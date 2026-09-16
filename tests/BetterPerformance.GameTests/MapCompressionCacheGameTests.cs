using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;

internal static class MapCompressionCacheGameTests
{
    internal static int Run(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("MapCompressionCache: " + message);
            checks++;
        }
        Type package = game.GetType("ZPackage", true)!;
        Type module = plugin.GetType("BetterPerformance.MapCompressionCache", true)!;
        var enabled = module.GetProperty("Enabled", BindingFlags.NonPublic | BindingFlags.Static)!;
        var write = AccessTools.DeclaredMethod(module, "Write");
        var clear = AccessTools.DeclaredMethod(module, "Clear");
        var nativeWrite = AccessTools.DeclaredMethod(package, "WriteCompressed", new[] { package });
        var getArray = AccessTools.DeclaredMethod(package, "GetArray");
        var streamField = AccessTools.DeclaredField(package, "m_stream");
        var writerField = AccessTools.DeclaredField(package, "m_writer");
        var mainThread = AccessTools.DeclaredField(module, "mainThread");
        var busy = AccessTools.DeclaredField(module, "busy");
        bool savedEnabled = (bool)enabled.GetValue(null)!;
        int savedThread = (int)mainThread.GetValue(null)!;
        bool savedBusy = (bool)busy.GetValue(null)!;
        object Make(byte[] bytes) => Activator.CreateInstance(package, new object[] { bytes })!;
        MemoryStream StreamOf(object value) => (MemoryStream)streamField.GetValue(value)!;
        long Count(string field) => (long)AccessTools.DeclaredField(module, field).GetValue(null)!;
        void Invoke(MethodInfo method, object? target, object destination, object source) =>
            method.Invoke(target, method == write ? new[] { destination, source } : new[] { source });
        void Parity(object source, bool expectedHit, string message)
        {
            object expected = Make(new byte[] { 1, 2, 3 }), actual = Make(new byte[] { 1, 2, 3 });
            StreamOf(expected).Position = StreamOf(actual).Position = 3;
            long hits = Count("hits"), sourcePosition = StreamOf(source).Position;
            nativeWrite.Invoke(expected, new[] { source });
            write.Invoke(null, new[] { actual, source });
            Check(StreamOf(expected).ToArray().SequenceEqual(StreamOf(actual).ToArray()), message + " encoded bytes");
            Check(StreamOf(expected).Position == StreamOf(actual).Position && StreamOf(source).Position == sourcePosition,
                message + " source and destination position");
            Check((Count("hits") > hits) == expectedHit, message + " hit expectation");
        }
        try
        {
            Check(!savedEnabled, "cache defaults off in fresh offline process");
            mainThread.SetValue(null, Thread.CurrentThread.ManagedThreadId);
            enabled.SetValue(null, true);
            busy.SetValue(null, false);
            var validate = AccessTools.DeclaredMethod(module, "ValidateContracts");
            Check(validate != null, "native contract validator exists");
            validate!.Invoke(null, null);

            clear.Invoke(null, null);
            byte[] offsetBacking = new byte[180]; new Random(79).NextBytes(offsetBacking);
            object offsetSource = Make(Array.Empty<byte>());
            var offsetStream = new MemoryStream(offsetBacking, 17, 128, true, true);
            streamField.SetValue(offsetSource, offsetStream); writerField.SetValue(offsetSource, new BinaryWriter(offsetStream));
            offsetStream.Position = 37;
            Parity(offsetSource, false, "nonzero source buffer origin");
            offsetBacking[0] ^= 255; // Outside the source stream's visible contents.
            Parity(offsetSource, true, "unrelated backing bytes excluded from equality");

            clear.Invoke(null, null);
            object expectedCustom = Make(Array.Empty<byte>()), actualCustom = Make(Array.Empty<byte>());
            object customSource = Make(new byte[64]);
            writerField.SetValue(expectedCustom, new CustomWriter(StreamOf(expectedCustom)));
            writerField.SetValue(actualCustom, new CustomWriter(StreamOf(actualCustom)));
            for (int pass = 0; pass < 2; pass++)
            {
                nativeWrite.Invoke(expectedCustom, new[] { customSource });
                write.Invoke(null, new[] { actualCustom, customSource });
                Check(StreamOf(expectedCustom).ToArray().SequenceEqual(StreamOf(actualCustom).ToArray()),
                    "custom byte-array writer keeps native virtual dispatch pass=" + pass);
            }
            clear.Invoke(null, null);
            var transpile = AccessTools.DeclaredMethod(module, "Transpile");
            var original = PatchProcessor.GetOriginalInstructions(AccessTools.DeclaredMethod(game.GetType("Minimap", true)!, "GetMapData"));
            List<CodeInstruction> Apply(List<CodeInstruction> code) =>
                ((IEnumerable<CodeInstruction>)transpile.Invoke(null, new object[] { code })!).ToList();
            var transformed = Apply(original);
            int at = original.FindIndex(i => i.Calls(nativeWrite));
            Check(at >= 0 && transformed.Count == original.Count, "one native compression call replaced");
            for (int i = 0; i < original.Count; i++)
                if (i != at) Check(ReferenceEquals(original[i], transformed[i]), "surrounding IL retained " + i);
            Check(transformed[at].opcode == OpCodes.Call && Equals(transformed[at].operand, write) &&
                transformed[at].labels.SequenceEqual(original[at].labels) && transformed[at].blocks.SequenceEqual(original[at].blocks),
                "replacement keeps branch and exception metadata");
            foreach (bool duplicate in new[] { false, true })
            {
                var modified = original.Select(i => new CodeInstruction(i)).ToList();
                if (duplicate) modified.Insert(at, new CodeInstruction(modified[at])); else modified.RemoveAt(at);
                try { Apply(modified); throw new Exception("Accepted unexpected compression site count."); }
                catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { checks++; }
            }
            foreach (int size in new[] { 0, 1, 257, 65537 })
            {
                clear.Invoke(null, null);
                byte[] bytes = new byte[size]; new Random(size + 910).NextBytes(bytes);
                object source = Make(bytes);
                StreamOf(source).Position = size / 2;
                Parity(source, false, "miss size=" + size);
                Parity(source, true, "hit size=" + size);
                if (size > 0)
                {
                    StreamOf(source).Position = size - 1;
                    StreamOf(source).WriteByte((byte)(bytes[size - 1] ^ 255));
                    Parity(source, false, "last-byte mutation size=" + size);
                    Parity(source, true, "new-content hit size=" + size);
                }
            }

            object bypassSource = Make(new byte[64]);
            clear.Invoke(null, null);
            enabled.SetValue(null, false); Parity(bypassSource, false, "disabled");
            enabled.SetValue(null, true);
            busy.SetValue(null, true); Parity(bypassSource, false, "reentrant"); busy.SetValue(null, false);
            mainThread.SetValue(null, -1); Parity(bypassSource, false, "wrong thread");
            mainThread.SetValue(null, Thread.CurrentThread.ManagedThreadId);

            // Distinct stream instances can alias the same array. Miss storage must not
            // associate source bytes overwritten by output with compression of the old bytes.
            (object Source, object Destination) Aliased()
            {
                byte[] backing = new byte[512]; new Random(91).NextBytes(backing);
                var sourceStream = new MemoryStream(backing, 0, 128, true, true);
                var destinationStream = new MemoryStream(backing, 0, backing.Length, true, true);
                destinationStream.SetLength(0);
                object source = Make(Array.Empty<byte>()), destination = Make(Array.Empty<byte>());
                streamField.SetValue(source, sourceStream); writerField.SetValue(source, new BinaryWriter(sourceStream));
                streamField.SetValue(destination, destinationStream); writerField.SetValue(destination, new BinaryWriter(destinationStream));
                return (source, destination);
            }
            clear.Invoke(null, null);
            var expectedAlias = Aliased(); var actualAlias = Aliased();
            for (int pass = 0; pass < 2; pass++)
            {
                nativeWrite.Invoke(expectedAlias.Destination, new[] { expectedAlias.Source });
                write.Invoke(null, new[] { actualAlias.Destination, actualAlias.Source });
                Check(StreamOf(expectedAlias.Destination).ToArray().SequenceEqual(StreamOf(actualAlias.Destination).ToArray()),
                    "shared backing array parity pass=" + pass);
            }

            clear.Invoke(null, null);
            object expectedMismatch = Make(Array.Empty<byte>()), actualMismatch = Make(Array.Empty<byte>());
            using (var expectedWriterStream = new MemoryStream())
            using (var actualWriterStream = new MemoryStream())
            {
                writerField.SetValue(expectedMismatch, new BinaryWriter(expectedWriterStream));
                writerField.SetValue(actualMismatch, new BinaryWriter(actualWriterStream));
                for (int pass = 0; pass < 2; pass++)
                {
                    nativeWrite.Invoke(expectedMismatch, new[] { bypassSource });
                    write.Invoke(null, new[] { actualMismatch, bypassSource });
                    Check(expectedWriterStream.ToArray().SequenceEqual(actualWriterStream.ToArray()),
                        "writer stream differs from package stream pass=" + pass);
                }
            }

            clear.Invoke(null, null);
            object selfExpected = Make(new byte[] { 7, 8, 9 }), selfActual = Make(new byte[] { 7, 8, 9 });
            StreamOf(selfExpected).Position = StreamOf(selfActual).Position = 3;
            nativeWrite.Invoke(selfExpected, new[] { selfExpected }); write.Invoke(null, new[] { selfActual, selfActual });
            Check(StreamOf(selfExpected).ToArray().SequenceEqual(StreamOf(selfActual).ToArray()), "self source/destination native fallback");

            var foreign = new Harmony("BetterPerformance.Tests.LateCompressionArrayPatch");
            try
            {
                foreign.Patch(getArray, prefix: new HarmonyMethod(typeof(MapCompressionCacheGameTests), nameof(ReplaceArray)));
                Parity(bypassSource, false, "late GetArray patch executes");
            }
            finally { foreign.Unpatch(getArray, HarmonyPatchType.All, foreign.Id); }
            clear.Invoke(null, null);
            Parity(bypassSource, false, "native contract restored after foreign patch removal");
            Parity(bypassSource, true, "cache works after foreign patch removal");

            object FixedDestination()
            {
                object destination = Make(Array.Empty<byte>());
                var stream = new MemoryStream(new byte[1], 0, 1, true, true);
                stream.Position = stream.Length;
                streamField.SetValue(destination, stream); writerField.SetValue(destination, new BinaryWriter(stream));
                return destination;
            }
            Type FailureOf(MethodInfo method)
            {
                object destination = FixedDestination();
                try { Invoke(method, method == write ? null : destination, destination, bypassSource); throw new Exception("Expected fixed-capacity write failure."); }
                catch (TargetInvocationException e) { return e.InnerException!.GetType(); }
            }
            clear.Invoke(null, null);
            Check(FailureOf(nativeWrite) == FailureOf(write), "native destination-write exception preserved");
            Check(!(bool)busy.GetValue(null)!, "failure does not retain busy state");
        }
        finally
        {
            clear.Invoke(null, null);
            enabled.SetValue(null, savedEnabled); mainThread.SetValue(null, savedThread); busy.SetValue(null, savedBusy);
        }
        Console.WriteLine("Map compression cache: " + checks + " offline native package checks; live map/save lifecycle remains unmeasured.");
        return checks;
    }

    private static bool ReplaceArray(ref byte[] __result) { __result = new byte[] { 7, 9 }; return false; }

    private sealed class CustomWriter : BinaryWriter
    {
        internal CustomWriter(Stream stream) : base(stream) { }
        public override void Write(byte[] value) { base.Write((byte)42); base.Write(value); }
    }
}
