using System.Reflection;
using System.Reflection.Emit;

internal static class LoadingDetailsGameTests
{
    internal static int Run(Assembly game, Assembly plugin)
    {
        try { return Verify(game, plugin); }
        catch (TypeLoadException exception) when (exception.Message.Contains("Non-abstract, non-.cctor method in an interface"))
        {
            Console.WriteLine("UNAVAILABLE loading-details native contracts on standalone Framework; use modern CLR metadata checks.");
            return 0;
        }
    }
    private static int Verify(Assembly assembly, Assembly plugin)
    {
        int count = 0;
        void Check(bool valid, string message) { if (!valid) throw new Exception("Loading details: " + message); count++; }
        var biome = assembly.GetType("AltBiomeWorldData", true)!;
        var world = assembly.GetType("World", true)!;
        var verify = Method(biome, "VerifyBiomeData");
        var load = Method(biome, "TryLoadCache");
        var points = Method(biome, "GenerateBiomePoints");
        var sectors = Method(biome, "GenerateSectors");
        foreach (var method in new[] { verify, load, points })
            Check(method.IsStatic && method.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { world }), "static world-only biome boundary");
        Check(verify.ReturnType == typeof(void) && points.ReturnType == typeof(void) && load.ReturnType == typeof(bool), "biome result signatures");
        Check(!sectors.IsStatic && sectors.ReturnType == typeof(void) && sectors.GetParameters().Length == 0, "instance sector boundary");
        var calls = References(verify).ToList();
        Check(calls.Contains(load) && calls.Contains(points) && calls.Contains(sectors), "verification contains all measured subpaths");
        Check(calls.IndexOf(load) < calls.IndexOf(points) && calls.IndexOf(points) < calls.IndexOf(sectors), "native cache/generation/sector order");
        Check(calls.OfType<FieldInfo>().Any(f => f.Name == "m_worldVersion") && calls.OfType<MethodInfo>().Any(m => m.Name == "SaveCache"),
            "cache success alone cannot prove generation omitted");
        var game = assembly.GetType("Game", true)!;
        var scene = assembly.GetType("ZNetScene", true)!;
        var find = Method(game, "FindSpawnPoint");
        foreach (string field in new[] { "m_respawnWait", "m_respawnLoadDuration" })
            Check(game.GetField(field, Flags)!.FieldType == typeof(float) && References(find).OfType<FieldInfo>().Any(f => f.Name == field), "native game-time field " + field);
        Check(References(find).OfType<MethodInfo>().Count(m => m.Name == "HaveLogoutPoint") == 1 &&
            References(find).OfType<MethodInfo>().Count(m => m.Name == "HaveCustomSpawnPoint") == 1, "unique existing profile predicates");
        Check(References(Method(scene, "IsAreaReady")).OfType<MethodInfo>().Count(m => m.Name == "IsZoneLoaded" && m.GetParameters()[0].ParameterType.Name == "Vector2s") == 1,
            "unique native loaded-zone check");
        var evidence = plugin.GetType("BetterPerformance.LoadingWaitEvidence", true)!;
        string Classify(bool ready = false, bool failed = false, bool logout = false, bool custom = false, bool death = false,
            bool area = false, bool areaReady = false, int zone = 0, float wait = 2, float minimum = 2)
        {
            object value = Activator.CreateInstance(evidence)!;
            void Set(string name, object assigned) => evidence.GetField(name, Flags)!.SetValue(value, assigned);
            Set("Logout", logout); Set("Custom", custom); Set("AfterDeath", death); Set("AreaSeen", area);
            Set("AreaReady", areaReady); Set("Zone", zone); Set("WaitAtComparison", wait); Set("Minimum", minimum);
            return Method(evidence, "Classify").Invoke(value, new object[] { ready, failed })!.ToString()!;
        }
        Check(Classify(logout: true) == "MinimumDelayConsistent", "inclusive native minimum boundary");
        Check(Classify(logout: true, wait: 2.01f) == "FallbackOrOther", "above minimum cannot infer timer gating");
        Check(Classify(logout: true, death: true) == "FallbackOrOther", "death path ignores logout point");
        Check(Classify(custom: true, death: true) == "MinimumDelayConsistent", "custom point minimum still applies after death");
        Check(Classify(area: true, zone: 2) == "ZoneNotLoaded", "observed missing zone");
        Check(Classify(area: true, zone: 1) == "AreaNotReadyWithZoneLoaded", "observed loaded zone with missing area readiness");
        Check(Classify(area: true) == "AreaNotReadyUnknown", "missing zone evidence stays unknown");
        Check(Classify(area: true, areaReady: true, zone: 1) == "FallbackOrOther", "ready area followed by false find is not missing objects");
        Check(Classify(logout: true, minimum: float.NaN) == "FallbackOrOther", "nonfinite native fields never fabricate minimum timing");
        Check(Classify(ready: true, failed: true) == "Exception" && Classify(ready: true, area: true) == "Ready", "exception and successful find precedence");
        var module = plugin.GetType("BetterPerformance.LoadingDetailsTelemetry", true)!;
        foreach (string name in new[] { "BeforeCost", "AfterCost", "AfterCache", "BeforeFind", "AfterFind", "BeforeArea", "AfterArea" })
        {
            var observer = Method(module, name);
            Check(observer.ReturnType == typeof(void) && observer.GetParameters().All(p => p.Name == "__state" || !p.ParameterType.IsByRef),
                name + " cannot change native return, arguments or exception");
            Check(!References(observer).OfType<MethodInfo>().Any(m => m.DeclaringType?.Assembly == assembly), name + " does not invoke game work");
        }
        foreach (string name in new[] { "LogoutObserved", "CustomObserved", "ZoneObserved" })
        {
            var method = Method(module, name);
            Check(method.ReturnType == typeof(bool) && method.GetParameters().Single().ParameterType == typeof(bool), "identity observer stack contract");
            var bytes = method.GetMethodBody()!.GetILAsByteArray()!;
            Check(bytes[bytes.Length - 2] == OpCodes.Ldarg_0.Value && bytes[bytes.Length - 1] == OpCodes.Ret.Value,
                name + " returns original boolean unchanged");
        }
        Console.WriteLine("Loading details: " + count + " metadata/pure classification checks; native runtime validation still required.");
        return count;
    }
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
    private static MethodInfo Method(Type type, string name) => type.GetMethod(name, Flags)!;
    private static IEnumerable<MemberInfo> References(MethodInfo method)
    {
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(OpCode))
            .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(op => op.Value);
        var bytes = method.GetMethodBody()!.GetILAsByteArray()!;
        for (int offset = 0; offset < bytes.Length;)
        {
            int raw = bytes[offset++]; if (raw == 0xfe) raw = 0xfe00 | bytes[offset++];
            var opcode = opcodes[unchecked((short)raw)]; int length;
            switch (opcode.OperandType)
            {
                case OperandType.InlineNone: length = 0; break;
                case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: length = 1; break;
                case OperandType.InlineVar: length = 2; break;
                case OperandType.InlineI8: case OperandType.InlineR: length = 8; break;
                case OperandType.InlineSwitch: length = 4 + 4 * BitConverter.ToInt32(bytes, offset); break;
                default: length = 4; break;
            }
            if (opcode.OperandType == OperandType.InlineMethod || opcode.OperandType == OperandType.InlineField)
                yield return method.Module.ResolveMember(BitConverter.ToInt32(bytes, offset), method.DeclaringType!.GetGenericArguments(), method.GetGenericArguments())!;
            offset += length;
        }
    }
}
