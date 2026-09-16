using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using BetterPerformance.Core;
using BepInEx.Logging;
using HarmonyLib;

namespace BetterPerformance
{
    // Cache only the already-serialized complete native map payload. Pin/mod changes
    // participate in equality automatically; no hooks attempt to maintain dirty state.
    internal static class MapCompressionCache
    {
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".MapCompressionCache");
        private static readonly ExactByteCache Cache = new ExactByteCache(24 * 1024 * 1024);
        private static readonly MethodInfo Compress = AccessTools.DeclaredMethod(typeof(ZPackage), "WriteCompressed", new[] { typeof(ZPackage) });
        private static readonly MethodInfo GetArray = AccessTools.DeclaredMethod(typeof(ZPackage), "GetArray", Type.EmptyTypes);
        private static readonly MethodInfo NativeCompress = AccessTools.DeclaredMethod(typeof(Utils), "Compress", new[] { typeof(byte[]) });
        private static readonly FieldInfo Stream = AccessTools.DeclaredField(typeof(ZPackage), "m_stream");
        private static readonly FieldInfo Writer = AccessTools.DeclaredField(typeof(ZPackage), "m_writer");
        private static int mainThread;
        private static bool busy;
        private static ZNet? world;
        private static long lookups, hits, misses, fallbacks, inputBytes, avoidedBytes;
        private static double lookupMs, storeMs, compressionMs;
        internal static bool Enabled { get; set; }
        internal static bool Installed { get; private set; }
        internal static string Status { get; private set; } = "disabled";

        internal static void Install(ManualLogSource logger)
        {
            try
            {
                mainThread = Thread.CurrentThread.ManagedThreadId;
                ValidateContracts();
                Patches.Patch(AccessTools.DeclaredMethod(typeof(Minimap), "GetMapData", Type.EmptyTypes),
                    transpiler: new HarmonyMethod(typeof(MapCompressionCache), nameof(Transpile)));
                Installed = true; Status = "installed";
            }
            catch (Exception exception)
            {
                Patches.UnpatchSelf(); Installed = Enabled = false; Status = "unavailable";
                logger.LogWarning("Map compression cache unavailable: " + exception.GetType().Name);
            }
        }
        private static void ValidateContracts()
        {
                if (Stream?.FieldType != typeof(MemoryStream) || Writer?.FieldType != typeof(BinaryWriter) ||
                    Compress == null || GetArray == null || NativeCompress == null || !Compatible())
                    throw new InvalidOperationException("Unsupported or patched compression contract.");
                // Exact native calls prove the encoded output consists only of compressed
                // payload length and bytes; never cache arbitrary side effects of a replacement.
                var body = PatchProcessor.GetOriginalInstructions(Compress);
                var shape = new[] { OpCodes.Ldarg_1, OpCodes.Callvirt, OpCodes.Call, OpCodes.Stloc_0,
                    OpCodes.Ldarg_0, OpCodes.Ldfld, OpCodes.Ldloc_0, OpCodes.Ldlen, OpCodes.Conv_I4,
                    OpCodes.Callvirt, OpCodes.Ldarg_0, OpCodes.Ldfld, OpCodes.Ldloc_0, OpCodes.Callvirt, OpCodes.Ret };
                if (body.Count != shape.Length || body.Where((i, n) => i.opcode != shape[n]).Any() ||
                    !Equals(body[5].operand, Writer) || !Equals(body[11].operand, Writer) ||
                    !body[1].Calls(GetArray) || !body[2].Calls(NativeCompress) ||
                    !body[9].Calls(AccessTools.Method(typeof(BinaryWriter), "Write", new[] { typeof(int) })) ||
                    !body[13].Calls(AccessTools.Method(typeof(BinaryWriter), "Write", new[] { typeof(byte[]) })))
                    throw new InvalidOperationException("Compression writer shape changed.");
                var array = PatchProcessor.GetOriginalInstructions(GetArray);
                var arrayShape = new[] { OpCodes.Ldarg_0, OpCodes.Ldfld, OpCodes.Callvirt, OpCodes.Ldarg_0,
                    OpCodes.Ldfld, OpCodes.Callvirt, OpCodes.Ldarg_0, OpCodes.Ldfld, OpCodes.Callvirt, OpCodes.Ret };
                if (!array.Select(i => i.opcode).SequenceEqual(arrayShape) ||
                    !Equals(array[1].operand, Writer) || !Equals(array[4].operand, Stream) || !Equals(array[7].operand, Stream) ||
                    !array[2].Calls(AccessTools.Method(typeof(BinaryWriter), "Flush", Type.EmptyTypes)) ||
                    !array[5].Calls(AccessTools.Method(typeof(System.IO.Stream), "Flush", Type.EmptyTypes)) ||
                    !array[8].Calls(AccessTools.Method(typeof(MemoryStream), "ToArray", Type.EmptyTypes)))
                    throw new InvalidOperationException("GetArray side-effect contract changed.");
                ValidateNativeCompressor();
        }

        private static void ValidateNativeCompressor()
        {
            var code = PatchProcessor.GetOriginalInstructions(NativeCompress);
            // Harmony can widen native short branches. Normalize only equivalent encodings.
            OpCode Normalize(OpCode op) => op == OpCodes.Leave_S ? OpCodes.Leave : op == OpCodes.Brfalse_S ? OpCodes.Brfalse : op;
            var shape = new[] { OpCodes.Newobj, OpCodes.Stloc_0, OpCodes.Ldloc_0, OpCodes.Ldc_I4_1,
                OpCodes.Newobj, OpCodes.Stloc_1, OpCodes.Ldloc_1, OpCodes.Ldarg_0, OpCodes.Ldc_I4_0,
                OpCodes.Ldarg_0, OpCodes.Ldlen, OpCodes.Conv_I4, OpCodes.Callvirt, OpCodes.Leave,
                OpCodes.Ldloc_1, OpCodes.Brfalse, OpCodes.Ldloc_1, OpCodes.Callvirt, OpCodes.Endfinally,
                OpCodes.Ldloc_0, OpCodes.Callvirt, OpCodes.Stloc_2, OpCodes.Leave, OpCodes.Ldloc_0,
                OpCodes.Brfalse, OpCodes.Ldloc_0, OpCodes.Callvirt, OpCodes.Endfinally, OpCodes.Ldloc_2, OpCodes.Ret };
            bool Branch(int from, int to) => code[from].operand is Label target && code[to].labels.Contains(target);
            var gzip = typeof(System.IO.Compression.GZipStream);
            var body = NativeCompress.GetMethodBody();
            if (!NativeCompress.IsStatic || NativeCompress.ReturnType != typeof(byte[]) ||
                !code.Select(i => Normalize(i.opcode)).SequenceEqual(shape) || body == null ||
                !body.LocalVariables.Select(v => v.LocalType).SequenceEqual(new[] { typeof(MemoryStream), gzip, typeof(byte[]) }) ||
                !Equals(code[0].operand, typeof(MemoryStream).GetConstructor(Type.EmptyTypes)) ||
                !Equals(code[4].operand, gzip.GetConstructor(new[] { typeof(System.IO.Stream), typeof(System.IO.Compression.CompressionLevel) })) ||
                !code[12].Calls(AccessTools.Method(typeof(System.IO.Stream), "Write", new[] { typeof(byte[]), typeof(int), typeof(int) })) ||
                !code[17].Calls(AccessTools.Method(typeof(IDisposable), "Dispose", Type.EmptyTypes)) ||
                !code[20].Calls(AccessTools.Method(typeof(MemoryStream), "ToArray", Type.EmptyTypes)) ||
                !code[26].Calls(AccessTools.Method(typeof(IDisposable), "Dispose", Type.EmptyTypes)) ||
                !Branch(13, 19) || !Branch(15, 18) || !Branch(22, 28) || !Branch(24, 27))
                throw new InvalidOperationException("Native compressor pure-data contract changed.");
            // Dispose scopes are part of the contract, not just the instruction stream.
            bool Finally(int start, int length, int handler, int handlerLength) => body.ExceptionHandlingClauses.Any(c =>
                c.Flags == ExceptionHandlingClauseOptions.Finally && c.TryOffset == start && c.TryLength == length &&
                c.HandlerOffset == handler && c.HandlerLength == handlerLength);
            if (body.ExceptionHandlingClauses.Count != 2 || !Finally(14, 13, 27, 10) || !Finally(6, 40, 46, 10))
                throw new InvalidOperationException("Native compressor disposal contract changed.");
        }
        private static bool Compatible() => new[] { Compress, GetArray, NativeCompress }.All(m =>
            m != null && (Harmony.GetPatchInfo(m)?.Owners.Count ?? 0) == 0);

        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            if (code.Count(i => i.Calls(Compress)) != 1) throw new InvalidOperationException("Expected one map compression call.");
            return code.Select(i => i.Calls(Compress)
                ? new CodeInstruction(i) { opcode = OpCodes.Call, operand = AccessTools.DeclaredMethod(typeof(MapCompressionCache), nameof(Write)) } : i);
        }

        private static void Write(ZPackage destination, ZPackage source)
        {
            if (!Enabled || busy || Thread.CurrentThread.ManagedThreadId != mainThread)
            { destination.WriteCompressed(source); return; }
            // Compatibility is checked per save, including patches installed after us.
            if (!Compatible()) { Cache.Clear(); fallbacks++; Status = "foreign_compressor_patch"; destination.WriteCompressed(source); return; }
            if (!ReferenceEquals(world, ZNet.instance)) { Cache.Clear(); world = ZNet.instance; }
            var sourceStream = Stream.GetValue(source) as MemoryStream;
            var destinationStream = Stream.GetValue(destination) as MemoryStream;
            var sourceWriter = Writer.GetValue(source) as BinaryWriter;
            var destinationWriter = Writer.GetValue(destination) as BinaryWriter;
            if (sourceStream == null || destinationStream == null || sourceWriter == null || destinationWriter == null ||
                sourceStream.GetType() != typeof(MemoryStream) || destinationStream.GetType() != typeof(MemoryStream) ||
                sourceWriter.GetType() != typeof(BinaryWriter) || destinationWriter.GetType() != typeof(BinaryWriter) ||
                !ReferenceEquals(sourceWriter.BaseStream, sourceStream) || !ReferenceEquals(destinationWriter.BaseStream, destinationStream) ||
                ReferenceEquals(sourceStream, destinationStream) || !sourceStream.CanRead || !destinationStream.CanWrite ||
                sourceStream.Length > 16 * 1024 * 1024 || destinationStream.Position != destinationStream.Length ||
                !sourceStream.TryGetBuffer(out var sourceBuffer) || !destinationStream.TryGetBuffer(out var destinationBuffer) ||
                ReferenceEquals(sourceBuffer.Array, destinationBuffer.Array))
            { fallbacks++; destination.WriteCompressed(source); return; }
            busy = true;
            try
            {
                // GetArray's native flush semantics remain observable on hits.
                sourceWriter.Flush(); sourceStream.Flush();
                var input = new ArraySegment<byte>(sourceBuffer.Array!, sourceBuffer.Offset, checked((int)sourceStream.Length));
                lookups++; inputBytes += input.Count;
                long started = Stopwatch.GetTimestamp();
                bool hit = Cache.TryWrite(input, destinationWriter);
                lookupMs += (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                if (hit) { hits++; avoidedBytes += input.Count; return; }
                misses++;
                long offset = destinationStream.Position;
                long compressStarted = Stopwatch.GetTimestamp();
                destination.WriteCompressed(source);
                compressionMs += (Stopwatch.GetTimestamp() - compressStarted) * 1000.0 / Stopwatch.Frequency;
                started = Stopwatch.GetTimestamp();
                try
                {
                    if (destinationStream.TryGetBuffer(out var output))
                        Cache.Store(input, new ArraySegment<byte>(output.Array!, checked(output.Offset + (int)offset), checked((int)(destinationStream.Position - offset))));
                    else Cache.Clear();
                }
                catch (OutOfMemoryException) { Cache.Clear(); fallbacks++; }
                storeMs += (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            }
            finally { busy = false; }
        }
        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            labels.Add(new TextValue("map_cache_status", Status));
            labels.Add(new TextValue("map_cache_enabled", Enabled ? "true" : "false"));
            gauges.Add(new NumberValue("map_cache_lookups_total", lookups, "calls"));
            gauges.Add(new NumberValue("map_cache_hits_total", hits, "calls"));
            gauges.Add(new NumberValue("map_cache_misses_total", misses, "calls"));
            gauges.Add(new NumberValue("map_cache_fallbacks_total", fallbacks, "calls"));
            gauges.Add(new NumberValue("map_cache_input_bytes_total", inputBytes, "bytes"));
            gauges.Add(new NumberValue("map_cache_compression_input_avoided_total", avoidedBytes, "bytes"));
            gauges.Add(new NumberValue("map_cache_lookup_ms_total", lookupMs, "ms"));
            gauges.Add(new NumberValue("map_cache_store_ms_total", storeMs, "ms"));
            gauges.Add(new NumberValue("map_cache_native_compression_ms_total", compressionMs, "ms"));
            gauges.Add(new NumberValue("map_cache_retained_bytes", Cache.RetainedBytes, "bytes"));
        }
        internal static void Clear() { Cache.Clear(); world = null; }
        internal static void Uninstall() { Enabled = false; Patches.UnpatchSelf(); Installed = false; Clear(); }
    }
}
