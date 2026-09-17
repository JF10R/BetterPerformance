using System.Reflection;
using Mono.Cecil;
using Cil = Mono.Cecil.Cil;

// Every game contract here is read from Mono.Cecil metadata: MineRock5 and ZNetScene reach
// Unity interfaces a standalone CLR cannot type-load, and this module's configuration
// defaults to enabled, so invoking its Install would patch them in the test process.
internal static class LootVisibilityGameTests
{
    internal static int Run(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Loot visibility: " + message);
            checks++;
        }

        string managed = Path.GetDirectoryName(game.Location)!;
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managed);
        var parameters = new ReaderParameters { AssemblyResolver = resolver };
        using var gameModule = ModuleDefinition.ReadModule(game.Location, parameters);
        using var pluginModule = ModuleDefinition.ReadModule(plugin.Location, parameters);

        // 1. t0: the handler that makes a destroyed hit area disappear on every client.
        TypeDefinition rock = gameModule.GetType("MineRock5")
            ?? throw new InvalidOperationException("Loot visibility: MineRock5 is missing.");
        MethodDefinition areaHealth = rock.Methods.SingleOrDefault(m => m.Name == "RPC_SetAreaHealth" &&
            m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "System.Int64", "System.Int32", "System.Single" }))
            ?? throw new InvalidOperationException("Loot visibility: MineRock5.RPC_SetAreaHealth(long, int, float) is missing.");
        Check(!areaHealth.IsStatic && areaHealth.IsPrivate && areaHealth.ReturnType.FullName == "System.Void",
            "MineRock5.RPC_SetAreaHealth(long, int, float) is a private instance void handler");
        MethodDefinition awake = rock.Methods.Single(m => m.Name == "Awake" && m.Parameters.Count == 0);
        Check(awake.Body.Instructions.Any(i => i.OpCode == Cil.OpCodes.Ldstr && (string)i.Operand == "RPC_SetAreaHealth"),
            "MineRock5.Awake still registers RPC_SetAreaHealth, so every client runs it");
        MethodDefinition damageArea = rock.Methods.Single(m => m.Name == "DamageArea" &&
            m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "System.Int32", "HitData" }));
        Check(damageArea.Body.Instructions.Any(i => i.OpCode == Cil.OpCodes.Ldstr && (string)i.Operand == "RPC_SetAreaHealth"),
            "the owner still broadcasts RPC_SetAreaHealth when an area reaches zero health");

        // 2. t1: the single creation point for a ZDO this process has never seen. The third
        //    parameter is the discriminator the module depends on, so its default and the
        //    two call sites are contract, not incidental.
        TypeDefinition zdoMan = gameModule.GetType("ZDOMan")
            ?? throw new InvalidOperationException("Loot visibility: ZDOMan is missing.");
        MethodDefinition createNew = zdoMan.Methods.SingleOrDefault(m => m.Name == "CreateNewZDO" &&
            m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "ZDOID", "UnityEngine.Vector3", "System.Int32" }))
            ?? throw new InvalidOperationException("Loot visibility: ZDOMan.CreateNewZDO(ZDOID, Vector3, int) is missing.");
        Check(!createNew.IsStatic && createNew.IsPrivate && createNew.ReturnType.FullName == "ZDO",
            "ZDOMan.CreateNewZDO(ZDOID, Vector3, int) is a private instance method returning ZDO");
        Check(createNew.Parameters[2].HasDefault && createNew.Parameters[2].Constant is int fallback && fallback == 0,
            "its prefab-hash parameter still defaults to zero");
        MethodDefinition zdoData = zdoMan.Methods.Single(m => m.Name == "RPC_ZDOData");
        var arrival = zdoData.Body.Instructions.Where(i => (i.Operand as MethodReference)?.Resolve() == createNew).ToList();
        Check(arrival.Count == 1, "RPC_ZDOData reaches that overload exactly once, when the incoming ZDO is unknown");
        Check(arrival[0].Previous.OpCode == Cil.OpCodes.Ldc_I4_0,
            "the network arrival still passes prefab hash zero, which is what separates it from local creation");
        MethodDefinition createPublic = zdoMan.Methods.Single(m => m.Name == "CreateNewZDO" &&
            m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "UnityEngine.Vector3", "System.Int32" }));
        Check(createPublic.Body.Instructions.Any(i => (i.Operand as MethodReference)?.Resolve() == createNew),
            "local creation still routes through the same overload, carrying its own hash");
        TypeDefinition netView = gameModule.GetType("ZNetView")!;
        MethodDefinition viewAwake = netView.Methods.Single(m => m.Name == "Awake" && m.Parameters.Count == 0);
        Check(viewAwake.Body.Instructions.Any(i => (i.Operand as MethodReference)?.Resolve() == createPublic),
            "ZNetView.Awake still creates its ZDO through the public overload, never the private one");

        // 3. t2: local creation of the object, and the accessors the hook reads.
        TypeDefinition netScene = gameModule.GetType("ZNetScene")!;
        MethodDefinition createObject = netScene.Methods.SingleOrDefault(m => m.Name == "CreateObject" &&
            m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "ZDO")
            ?? throw new InvalidOperationException("Loot visibility: ZNetScene.CreateObject(ZDO) is missing.");
        Check(!createObject.IsStatic && createObject.ReturnType.FullName == "UnityEngine.GameObject",
            "ZNetScene.CreateObject(ZDO) is an instance method returning a GameObject");
        TypeDefinition zdo = gameModule.GetType("ZDO")!;
        foreach (var (name, returns) in new[] { ("GetPosition", "UnityEngine.Vector3"), ("IsOwner", "System.Boolean"), ("GetPrefab", "System.Int32") })
            Check(zdo.Methods.Any(m => m.Name == name && m.Parameters.Count == 0 && m.ReturnType.FullName == returns),
                "ZDO." + name + "() still returns " + returns);

        // 4. The plugin side is three postfixes; none can replace or skip native work.
        TypeDefinition module = pluginModule.GetType("BetterPerformance.LootVisibilityTelemetry")!;
        foreach (var (hook, types) in new[]
        {
            ("AfterSetAreaHealth", new[] { "MineRock5", "System.Single" }),
            ("AfterCreateNewZDO", new[] { "ZDOID", "UnityEngine.Vector3", "System.Int32" }),
            ("AfterCreateObject", new[] { "ZDO", "UnityEngine.GameObject" }),
        })
        {
            MethodDefinition postfix = module.Methods.Single(m => m.Name == hook);
            Check(postfix.IsStatic && postfix.ReturnType.FullName == "System.Void" &&
                postfix.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(types) &&
                postfix.Parameters.All(p => !p.ParameterType.IsByReference),
                hook + " is a static void postfix taking its arguments by value");
        }

        Console.WriteLine("Loot visibility: " + checks + " static game-contract checks from metadata; the measured durations require a play session.");
        return checks;
    }
}
