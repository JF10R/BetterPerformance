using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

internal static class FastMapSerializationTests
{
    internal static int Run(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FastMapSerialization: " + message);
            checks++;
        }
        Type map = game.GetType("Minimap", true)!;
        Type patch = plugin.GetType("BetterPerformance.FastMapSerialization", true)!;
        MethodInfo method = AccessTools.DeclaredMethod(map, "GetMapData", Type.EmptyTypes);
        MethodInfo transpile = AccessTools.DeclaredMethod(patch, "Transpile");
        var generator = new DynamicMethod("MapShapeProbe", typeof(void), Type.EmptyTypes).GetILGenerator();
        List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(method, generator);
        var snapshot = original.Select(i => (i.opcode, i.operand, labels: i.labels.ToArray(), blocks: i.blocks.ToArray())).ToArray();
        List<CodeInstruction> Apply(List<CodeInstruction> input) =>
            ((IEnumerable<CodeInstruction>)transpile.Invoke(null, new object[] { input, generator })!).ToList();
        var transformed = Apply(original);
        Check(transformed.Count == original.Count + 20, "exactly two 9-instruction gates and landing nops");
        Check(transformed.Where(original.Contains).SequenceEqual(original), "all native instructions retained in order");
        for (int index = 0; index < original.Count; index++)
        {
            var before = snapshot[index]; var after = original[index];
            Check(before.opcode == after.opcode && Equals(before.operand, after.operand) &&
                before.labels.SequenceEqual(after.labels) && before.blocks.SequenceEqual(after.blocks),
                "input unchanged at " + index);
        }
        var calls = transformed.Where(i => i.operand is MethodInfo called && called.DeclaringType == patch).ToArray();
        Check(calls.Length == 2 && calls.All(i => ((MethodInfo)i.operand).Name == "WriteIfEnabled"), "two isolated gates");
        foreach (var call in calls)
        {
            int index = transformed.IndexOf(call);
            Check(transformed[index + 1].opcode == OpCodes.Brtrue && transformed[index + 1].operand is Label,
                "successful write bypasses native loop");
            var target = (Label)transformed[index + 1].operand;
            Check(transformed[index + 20].opcode == OpCodes.Nop && transformed[index + 20].labels.Contains(target),
                "gate lands after exactly 18 original loop instructions");
        }
        int read = original.FindIndex(i => i.operand is MethodInfo called && called.DeclaringType == typeof(BitArray) && called.Name == "get_Item");
        Check(read >= 7, "current installed map contains expected BitArray serialization");
        int start = read - 7;
        void Reject(Action<List<CodeInstruction>> alter, string name)
        {
            var changed = original.Select(i => new CodeInstruction(i)).ToList();
            alter(changed);
            try { Apply(changed); throw new Exception("accepted unsupported " + name); }
            catch (TargetInvocationException exception) when (exception.InnerException is InvalidOperationException) { checks++; }
        }
        Reject(code => code[start + 11].opcode = OpCodes.Sub, "increment");
        Reject(code => code[start + 33].operand = AccessTools.DeclaredField(map, "m_exploredOthers"), "shared-loop bound");
        Reject(code => code[start + 5].operand = AccessTools.DeclaredField(map, "m_textureSize"), "source field");
        Reject(code => code[start + 1].operand = generator.DeclareLocal(typeof(long)), "counter type");
        Reject(code => code[start + 4].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock)), "exception boundary");
        Reject(code => code[0] = new CodeInstruction(OpCodes.Br, code[start + 2].operand), "external jump");
        Reject(code => code[0] = new CodeInstruction(OpCodes.Switch, new[] { (Label)code[start + 17].operand }), "external switch");
        Reject(code => code[start].labels.Add(generator.DefineLabel()), "additional entry label");
        Reject(code => code[0] = new CodeInstruction(OpCodes.Ldloc_S, code[start + 1].operand), "counter read outside loop");

        PropertyInfo enabled = patch.GetProperty("Enabled", BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo gate = AccessTools.DeclaredMethod(patch, "WriteIfEnabled");
        var bits = new BitArray(new byte[] { 0x81, 0xfe, 0x55, 0xaa, 0x31 });
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            enabled.SetValue(null, false);
            Check(!(bool)gate.Invoke(null, new object[] { writer, bits, 33 })! && stream.Length == 0,
                "disabled gate leaves native fallback untouched");
            try
            {
                enabled.SetValue(null, true);
                using (var customStream = new MemoryStream())
                using (var customWriter = new CustomWriter(customStream))
                    Check(!(bool)gate.Invoke(null, new object[] { customWriter, bits, 33 })! && customStream.Length == 0,
                        "custom BinaryWriter retains its virtual boolean semantics via native fallback");
                using (var customStream = new ChunkSensitiveStream())
                using (var ordinaryWriter = new BinaryWriter(customStream))
                {
                    Check(!(bool)gate.Invoke(null, new object[] { ordinaryWriter, bits, 33 })! && customStream.Length == 0,
                        "ordinary BinaryWriter over custom stream must fall back before writing");
                    for (int index = 0; index < 33; index++) ordinaryWriter.Write(bits[index]);
                    Check(customStream.ToArray().SequenceEqual(Enumerable.Range(0, 33).Select(i => bits[i] ? (byte)1 : (byte)0)),
                        "fallback preserves native single-boolean stream behavior");
                    customStream.SetLength(0); customStream.Position = 0;
                    ordinaryWriter.Write(new byte[33]);
                    Check(customStream.ToArray().All(value => value == 42),
                        "fixture proves custom bulk writes differ from native boolean writes");
                }
                Check((bool)gate.Invoke(null, new object[] { writer, bits, 33 })!, "enabled gate accepts current framework BitArray");
                Check(stream.ToArray().SequenceEqual(Enumerable.Range(0, 33).Select(i => bits[i] ? (byte)1 : (byte)0)),
                    "actual plugin gate emits native boolean bytes without length prefix");
            }
            finally { enabled.SetValue(null, false); }
        }
        var foreign = new Harmony("BetterPerformance.Tests.LateBooleanWriterPatch");
        var booleanWrite = AccessTools.DeclaredMethod(game.GetType("ZPackage", true)!, "Write", new[] { typeof(bool) });
        try
        {
            foreign.Patch(booleanWrite,
                prefix: new HarmonyMethod(typeof(FastMapSerializationTests), nameof(ObserveWrite)));
            enabled.SetValue(null, true);
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            Check(!(bool)gate.Invoke(null, new object[] { writer, bits, 33 })! && stream.Length == 0,
                "late foreign boolean writer patch forces untouched native fallback");
            Check(!(bool)enabled.GetValue(null)!, "late conflict disables future fast writes");
        }
        finally { enabled.SetValue(null, false); foreign.Unpatch(booleanWrite, HarmonyPatchType.All, foreign.Id); }
        Console.WriteLine("Fast map serialization: " + checks + " offline checks; native full-save parity still requires Unity.");
        return checks;
    }

    private static void ObserveWrite() { }

    private sealed class CustomWriter : BinaryWriter
    {
        internal CustomWriter(Stream stream) : base(stream) { }
        public override void Write(bool value) => base.Write((byte)(value ? 2 : 3));
    }

    private sealed class ChunkSensitiveStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count <= 1) { base.Write(buffer, offset, count); return; }
            for (int index = 0; index < count; index++) base.WriteByte(42);
        }
    }
}
