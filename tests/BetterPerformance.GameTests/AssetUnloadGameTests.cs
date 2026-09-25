using System.Reflection;
using Mono.Cecil;
using Cil = Mono.Cecil.Cil;

// Game contracts behind AssetUnloadDeferral, read from Mono.Cecil metadata; never calls the game.
internal static class AssetUnloadGameTests
{
    internal static int Run(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Asset unload deferral: " + message);
            checks++;
        }

        string managed = Path.GetDirectoryName(game.Location)!;
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managed);
        using var gameModule = ModuleDefinition.ReadModule(game.Location, new ReaderParameters { AssemblyResolver = resolver });
        TypeDefinition Require(string name) => gameModule.GetType(name)
            ?? throw new InvalidOperationException("Asset unload deferral: " + name + " is missing.");
        MethodDefinition Method(TypeDefinition type, string name, params string[] parameters) =>
            type.Methods.SingleOrDefault(m => m.Name == name && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters))
            ?? throw new InvalidOperationException("Asset unload deferral: " + type.Name + "." + name + "(" + string.Join(", ", parameters) + ") is missing.");
        bool Calls(MethodDefinition method, string declaring, string name) => method.HasBody && method.Body.Instructions.Any(i =>
            (i.OpCode == Cil.OpCodes.Call || i.OpCode == Cil.OpCodes.Callvirt) && i.Operand is MethodReference called &&
            called.Name == name && called.DeclaringType.Name == declaring);
        bool LoadsString(MethodDefinition method, string value) => method.HasBody &&
            method.Body.Instructions.Any(i => i.OpCode == Cil.OpCodes.Ldstr && (string)i.Operand == value);
        bool LoadsDouble(MethodDefinition method, double value) => method.HasBody &&
            method.Body.Instructions.Any(i => i.OpCode == Cil.OpCodes.Ldc_R8 && (double)i.Operand == value);

        TypeDefinition gameType = Require("Game");

        // 1. The patched hourly check and the state it reads.
        MethodDefinition periodic = Method(gameType, "CollectResourcesCheckPeriodic");
        Check(!periodic.IsStatic && periodic.ReturnType.FullName == "System.Void", "Game.CollectResourcesCheckPeriodic() is an instance void");
        Check(Calls(periodic, "Game", "CollectResources") && LoadsDouble(periodic, 3599.0), "the hourly check still unloads past 3599 s");
        FieldDefinition last = gameType.Fields.SingleOrDefault(f => f.Name == "m_lastCollectResources")
            ?? throw new InvalidOperationException("Asset unload deferral: Game.m_lastCollectResources is missing.");
        Check(!last.IsStatic && last.FieldType.FullName == "System.DateTime", "Game.m_lastCollectResources is an instance DateTime");
        MethodDefinition collect = Method(gameType, "CollectResources", "System.Boolean");
        Check(collect.IsPublic && !collect.IsStatic && Calls(collect, "Resources", "UnloadUnusedAssets") &&
            collect.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "m_lastCollectResources"),
            "Game.CollectResources(bool) unloads and stamps m_lastCollectResources");

        // 2. What the deferral relies on: the hourly schedule, and the native calm-moment checks.
        Check(gameType.Methods.Any(m => LoadsString(m, "CollectResourcesCheckPeriodic") && Calls(m, "MonoBehaviour", "InvokeRepeating")),
            "Game still schedules CollectResourcesCheckPeriodic with InvokeRepeating");
        MethodDefinition check = Method(gameType, "CollectResourcesCheck");
        Check(check.IsPublic && Calls(check, "Game", "CollectResources") && LoadsDouble(check, 1200.0), "Game.CollectResourcesCheck() still unloads past 1200 s");
        Check(Calls(Method(Require("SleepText"), "CollectResources"), "Game", "CollectResourcesCheck"), "sleeping still runs the native check");
        Check(gameType.Methods.Count(m => Calls(m, "Game", "CollectResourcesCheck")) >= 2, "respawn and the idle pause still run the native check");

        // 3. The policy the prefix applies.
        Type policy = plugin.GetType("BetterPerformance.Core.AssetUnloadPolicy", true)!;
        MethodInfo decide = policy.GetMethod("Periodic", BindingFlags.Public | BindingFlags.Static)!;
        string Decide(double since, bool dedicated, int peers) => decide.Invoke(null, new object[] { since, double.PositiveInfinity, 7200.0, dedicated, peers })!.ToString()!;
        Check(Decide(3700, false, 1) == "Defer" && Decide(3700, true, 0) == "RunNative" && Decide(7300, true, 3) == "RunCapped" &&
            Decide(100, true, 0) == "NativeSkips", "the policy defers in play, runs on an empty server and at the cap");
        return checks;
    }
}
