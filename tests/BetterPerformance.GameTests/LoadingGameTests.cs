using System.Reflection;
using System.Reflection.Emit;

internal static class LoadingGameTests
{
    internal static int Run(Assembly game, Assembly plugin)
    {
        try { return Verify(game, plugin); }
        catch (TypeLoadException exception) when (exception.Message.Contains("Non-abstract, non-.cctor method in an interface"))
        {
            Console.WriteLine("UNAVAILABLE loading game-contract checks on standalone .NET Framework; use modern CLR metadata checks and Unity validation.");
            return 0;
        }
    }
    private static int Verify(Assembly assembly, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception("Loading: " + message); checks++; }
        var game = assembly.GetType("Game", true)!;
        var scene = assembly.GetType("ZNetScene", true)!;
        var hud = assembly.GetType("Hud", true)!;
        var startup = assembly.GetType("FejdStartup", true)!;
        var player = assembly.GetType("Player", true)!;
        var spawn = Method(game, "SpawnPlayer"); var find = Method(game, "FindSpawnPoint");
        Check(Method(startup, "TransitionToMainScene").ReturnType == typeof(void), "transition hook is exact void boundary");
        Check(spawn.ReturnType == player && spawn.GetParameters().Select(p => p.ParameterType.Name).SequenceEqual(new[] { "Vector3", "Boolean" }), "spawn signature");
        Check(find.ReturnType == typeof(bool) && find.GetParameters().Select(p => p.ParameterType.Name).SequenceEqual(new[] { "Vector3&", "Boolean&", "Single" }), "readiness signature");
        Check(Field(game, "m_firstSpawn").FieldType == typeof(bool) && Field(game, "m_requestRespawn").FieldType == typeof(bool), "native episode fields");
        var updateReferences = References(Method(game, "UpdateRespawn")).ToList();
        Check(updateReferences.IndexOf(find) >= 0 && updateReferences.IndexOf(spawn) > updateReferences.IndexOf(find), "readiness precedes player construction");
        Check(updateReferences.Contains(Field(game, "m_requestRespawn")), "completion predicate is native UpdateRespawn state");
        var area = Method(scene, "IsAreaReady");
        Check(area.ReturnType == typeof(bool) && References(find).Contains(area), "area check is nested in native FindSpawnPoint");
        Check(References(find).Contains(Field(game, "m_respawnWait")) && References(find).Contains(Field(game, "m_respawnLoadDuration")), "native delay uses accumulated game time");
        var black = Method(hud, "UpdateBlackScreen");
        Check(black.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { player, typeof(float) }), "HUD scope has actual local player argument");
        var screen = Field(hud, "m_loadingScreen");
        Check(screen.FieldType.Name == "CanvasGroup" && References(black).Contains(screen), "verified loading CanvasGroup");
        Check(References(black).OfType<MethodInfo>().Any(m => m.Name == "set_alpha") &&
            References(black).OfType<MethodInfo>().Any(m => m.Name == "SetActive"), "native HUD drives alpha and active state");
        var module = plugin.GetType("BetterPerformance.LoadingTelemetry", true)!;
        foreach (string name in new[] { "ConnectionStarted", "BeforeScene", "BeforeRequest", "BeforeRespawn", "BeforeFind", "AfterFind", "BeforeArea", "AfterArea", "BeforeSpawn", "AfterSpawn", "BeforeUpdate", "AfterUpdate", "BeforeDestroy", "AfterBlackScreen" })
        {
            var observer = Method(module, name);
            Check(observer.ReturnType == typeof(void), name + " does not replace native result or exception");
            Check(observer.GetParameters().All(p => p.Name == "__state" || !p.ParameterType.IsByRef), name + " cannot mutate native parameters");
            Check(!References(observer).OfType<MethodInfo>().Any(m => m.DeclaringType?.Assembly == assembly &&
                new[] { "SpawnPlayer", "RequestRespawn", "FindSpawnPoint", "IsAreaReady", "Save", "InvokeRPC", "TeleportTo" }.Contains(m.Name)), name + " does not trigger game work");
        }
        Console.WriteLine("Loading telemetry: " + checks + " metadata-only contract checks; real HUD transition requires Unity validation.");
        return checks;
    }
    private const BindingFlags Flags = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static MethodInfo Method(Type type, string name) => type.GetMethod(name, Flags)!;
    private static FieldInfo Field(Type type, string name) => type.GetField(name, Flags)!;
    private static IEnumerable<MemberInfo> References(MethodInfo method)
    {
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!).ToDictionary(op => op.Value);
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
