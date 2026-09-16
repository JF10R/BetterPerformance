using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

internal static class PackageCopyGameTests
{
    internal static int Run(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("PackageCopy: " + message);
            checks++;
        }
        Type package = game.GetType("ZPackage", true)!;
        Type module = plugin.GetType("BetterPerformance.PackageCopyOptimization", true)!;
        MethodInfo contract = AccessTools.DeclaredMethod(module, "ValidateContracts");
        contract.Invoke(null, null);
        MethodInfo send = AccessTools.DeclaredMethod(game.GetType("ZDOMan", true)!, "SendZDOs");
        MethodInfo transpile = AccessTools.DeclaredMethod(module, "Transpile");
        var original = PatchProcessor.GetOriginalInstructions(send);
        List<CodeInstruction> Apply(List<CodeInstruction> input) =>
            ((IEnumerable<CodeInstruction>)transpile.Invoke(null, new object[] { input })!).ToList();
        var transformed = Apply(original);
        var nativeWrite = AccessTools.DeclaredMethod(package, "Write", new[] { package });
        int copyAt = original.FindIndex(i => i.Calls(nativeWrite));
        Check(transformed.Count == original.Count, "one call replaces one call");
        for (int index = 0; index < original.Count; index++)
            if (index != copyAt) Check(ReferenceEquals(original[index], transformed[index]), "outside IL/labels/EH retained at " + index);
        Check(transformed[copyAt].opcode == OpCodes.Call && transformed[copyAt].operand is MethodInfo wrapper &&
            wrapper.Name == "CopyLocalPackage", "only exact package call replaced");
        void Reject(Action<List<CodeInstruction>> alter, string name)
        {
            var changed = original.Select(i => new CodeInstruction(i)).ToList();
            alter(changed);
            try { Apply(changed); throw new Exception("accepted " + name); }
            catch (TargetInvocationException exception) when (exception.InnerException is InvalidOperationException) { checks++; }
        }
        Reject(code => code[copyAt - 2].opcode = OpCodes.Ldloc_1, "wrong destination");
        Reject(code => code[copyAt - 1].opcode = OpCodes.Ldloca_S, "source address escape");
        Reject(code => code.Insert(copyAt, new CodeInstruction(code[copyAt])), "duplicate copy site");
        Reject(code => code[copyAt].labels.Add(new DynamicMethod("label", typeof(void), Type.EmptyTypes).GetILGenerator().DefineLabel()), "branch into copy");
        int initializer = original.FindIndex(i => i.opcode == OpCodes.Stloc_2);
        Reject(code => code[initializer - 1].opcode = OpCodes.Ldnull, "destination not fresh");
        Reject(code => code[0] = new CodeInstruction(original[copyAt - 1]), "source outside allowed operations");

        var enabled = module.GetProperty("Enabled", BindingFlags.Static | BindingFlags.NonPublic)!;
        var before = AccessTools.DeclaredMethod(module, "BeforeSend");
        var after = AccessTools.DeclaredMethod(module, "AfterSend");
        var copy = AccessTools.DeclaredMethod(module, "CopyLocalPackage");
        var streamField = AccessTools.DeclaredField(package, "m_stream");
        MemoryStream StreamOf(object instance) => (MemoryStream)streamField.GetValue(instance)!;
        object Make(byte[] payload) => Activator.CreateInstance(package, new object[] { payload })!;
        void ScopedCopy(object destination, object source)
        {
            var state = new object?[] { null };
            before.Invoke(null, state);
            try { copy.Invoke(null, new[] { destination, source }); }
            finally { after.Invoke(null, state); }
        }
        try
        {
            foreach (bool active in new[] { false, true })
            foreach (int size in new[] { 0, 1, 257, 65537 })
            {
                enabled.SetValue(null, active);
                var bytes = new byte[size]; new Random(size + 491).NextBytes(bytes);
                object source = Make(bytes), expected = Make(new byte[9]), actual = Make(new byte[9]);
                StreamOf(source).Position = size / 2;
                StreamOf(expected).Position = StreamOf(actual).Position = 3;
                nativeWrite.Invoke(expected, new[] { source });
                ScopedCopy(actual, source);
                Check(StreamOf(expected).ToArray().SequenceEqual(StreamOf(actual).ToArray()) &&
                    StreamOf(expected).Position == StreamOf(actual).Position && StreamOf(source).Position == size / 2,
                    "native current-game package parity active=" + active + " size=" + size);
            }
            object selfExpected = Make(new byte[] { 2, 5, 8 }), selfActual = Make(new byte[] { 2, 5, 8 });
            nativeWrite.Invoke(selfExpected, new[] { selfExpected });
            ScopedCopy(selfActual, selfActual);
            Check(StreamOf(selfExpected).ToArray().SequenceEqual(StreamOf(selfActual).ToArray()), "self-copy preserves native snapshot behavior");
            var foreign = new Harmony("BetterPerformance.Tests.LatePackageArrayPatch");
            var getArray = AccessTools.DeclaredMethod(package, "GetArray");
            try
            {
                foreign.Patch(getArray, prefix: new HarmonyMethod(typeof(PackageCopyGameTests), nameof(ReplaceArray)));
                object destination = Make(Array.Empty<byte>());
                ScopedCopy(destination, Make(new byte[32]));
                Check(StreamOf(destination).ToArray().SequenceEqual(new byte[] { 2, 0, 0, 0, 7, 9 }),
                    "late GetArray patch executes through native fallback");
            }
            finally { foreign.Unpatch(getArray, HarmonyPatchType.All, foreign.Id); }
            var ownershipFence = new Harmony("BetterPerformance.Tests.PackageOwnershipFence");
            var scope = AccessTools.Field(module, "scopeAllowed");
            foreach (MethodBase target in new MethodBase[] {
                AccessTools.DeclaredMethod(package, "Clear", Type.EmptyTypes),
                AccessTools.Constructor(package, Type.EmptyTypes) })
            {
                object previousScope = scope.GetValue(null)!;
                try
                {
                    ownershipFence.Patch(target, prefix: new HarmonyMethod(typeof(PackageCopyGameTests), nameof(ObserveOwnershipBoundary)));
                    scope.SetValue(null, true); // Model an existing outer send scope.
                    object?[] state = { null };
                    before.Invoke(null, state);
                    try
                    {
                        Check(!(bool)scope.GetValue(null)!, "patched ownership boundary blocks direct-buffer scope: " + target.Name);
                        long fallbacks = (long)AccessTools.Field(module, "fallbacks").GetValue(null)!;
                        object source = Make(new byte[] { 6, 8, 10 });
                        object expected = Make(Array.Empty<byte>()), actual = Make(Array.Empty<byte>());
                        nativeWrite.Invoke(expected, new[] { source });
                        copy.Invoke(null, new[] { actual, source });
                        Check((long)AccessTools.Field(module, "fallbacks").GetValue(null)! == fallbacks + 1 &&
                            StreamOf(actual).ToArray().SequenceEqual(StreamOf(expected).ToArray()),
                            "ownership conflict executes exact native copy: " + target.Name);
                    }
                    finally { after.Invoke(null, state); }
                    Check((bool)scope.GetValue(null)!, "nested conflict restores previous scope: " + target.Name);
                }
                finally
                {
                    scope.SetValue(null, previousScope);
                    ownershipFence.Unpatch(target, HarmonyPatchType.All, ownershipFence.Id);
                }
            }
        }
        finally { enabled.SetValue(null, false); }
        Console.WriteLine("Package copy: " + checks + " offline checks; live SendZDOs performance remains unmeasured.");
        return checks;
    }

    private static bool ReplaceArray(ref byte[] __result) { __result = new byte[] { 7, 9 }; return false; }
    private static void ObserveOwnershipBoundary() { }
}
