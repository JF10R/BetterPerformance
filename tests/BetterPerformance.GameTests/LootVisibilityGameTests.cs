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

        // 1b. v3 t0 is the destroyed area's centre, read in a prefix because the handler's
        //     UpdateMesh deactivates the area collider, whose bounds are then empty.
        FieldDefinition hitAreas = rock.Fields.SingleOrDefault(f => f.Name == "m_hitAreas")
            ?? throw new InvalidOperationException("Loot visibility: MineRock5.m_hitAreas is missing.");
        Check(!hitAreas.IsStatic && hitAreas.FieldType.FullName == "System.Collections.Generic.List`1<MineRock5/HitArea>",
            "MineRock5.m_hitAreas is still the instance list of hit areas the area index reads");
        TypeDefinition hitArea = rock.NestedTypes.Single(t => t.Name == "HitArea");
        Check(hitArea.Fields.Any(f => f.Name == "m_collider" && !f.IsStatic && f.FieldType.FullName == "UnityEngine.Collider"),
            "MineRock5.HitArea.m_collider is still the area's Collider");
        Check(damageArea.Body.Instructions.Any(i => (i.Operand as MethodReference)?.Name == "get_bounds") &&
              damageArea.Body.Instructions.Any(i => (i.Operand as FieldReference)?.Name == "m_dropItems"),
            "DamageArea still places drops from m_dropItems at the area collider's bounds centre");
        Check(areaHealth.Body.Instructions.Any(i => (i.Operand as MethodReference)?.Name == "UpdateMesh"),
            "RPC_SetAreaHealth still ends in UpdateMesh, which is why the centre is read before it");
        TypeDefinition routed = gameModule.GetType("ZNet")!;
        Check(routed.Methods.Any(m => m.HasBody &&
                m.Body.Instructions.Any(i => (i.Operand as MethodReference)?.Name == "SetUID") &&
                m.Body.Instructions.Any(i => (i.Operand as MethodReference)?.Name == "GetSessionID")),
            "the routed-RPC sender id is still the ZDO session id, so sender == GetSessionID marks this process's own destruction");

        // 1c. Drop tables and spawn prefabs each source kind's destruction reads.
        foreach (var (type, field, fieldType) in new[]
        {
            ("MineRock5", "m_dropItems", "DropTable"), ("MineRock", "m_dropItems", "DropTable"),
            ("DropOnDestroyed", "m_dropWhenDestroyed", "DropTable"), ("TreeLog", "m_dropWhenDestroyed", "DropTable"),
            ("TreeBase", "m_dropWhenDestroyed", "DropTable"), ("TreeBase", "m_logPrefab", "UnityEngine.GameObject"),
            ("TreeBase", "m_logSpawnPoint", "UnityEngine.Transform"), ("TreeLog", "m_subLogPrefab", "UnityEngine.GameObject"),
            ("TreeLog", "m_subLogPoints", "UnityEngine.Transform[]"), ("TreeLog", "m_spawnDistance", "System.Single"),
            ("Destructible", "m_spawnWhenDestroyed", "UnityEngine.GameObject"),
            ("DropTable", "m_drops", "System.Collections.Generic.List`1<DropTable/DropData>"),
        })
            Check(gameModule.GetType(type)?.Fields.Any(f => f.Name == field && !f.IsStatic && f.IsPublic && f.FieldType.FullName == fieldType) == true,
                type + "." + field + " is still a public " + fieldType);
        MethodDefinition logDestroy = gameModule.GetType("TreeLog")!.Methods.Single(m => m.Name == "Destroy" && m.Parameters.Count == 2);
        Check(logDestroy.Body.Instructions.Any(i => (i.Operand as MethodReference)?.Name == "CheckDropConversion"),
            "TreeLog.Destroy still converts drops, so its table includes Game.m_damageTypeDropConversions results");
        MethodDefinition dropAwake = gameModule.GetType("ItemDrop")!.Methods.Single(m => m.Name == "Awake" && m.Parameters.Count == 0);
        Check(dropAwake.Body.Instructions.Any(i => (i.Operand as FieldReference)?.Name == "s_spawnTime" &&
                (i.Operand as FieldReference)?.DeclaringType.FullName == "ZDOVars") &&
              gameModule.GetType("ZDO")!.Methods.Any(m => m.Name == "GetLong" && m.ReturnType.FullName == "System.Int64" &&
                m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "System.Int32", "System.Int64" })),
            "ItemDrop.Awake still stamps ZDOVars.s_spawnTime, which the stale-drop filter reads through ZDO.GetLong(int, long)");
        MethodDefinition breakRock = gameModule.GetType("Destructible")!.Methods.Single(m => m.Name == "Destroy" && m.Parameters.Count == 1);
        Check(breakRock.Body.Instructions.Any(i => (i.Operand as MethodReference)?.Name == "Damage" &&
                (i.Operand as MethodReference)?.DeclaringType.FullName == "MineRock5"),
            "Destructible.Destroy still damages its spawned MineRock5 at once, before any client has that instance");

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

        // 3. t2: scene registration of the object, which is the one point both a network
        //    creation and this process's own Instantiate pass through, and the accessors the
        //    hook reads. Asserted from both sides: the method's shape, and ZNetView.Awake
        //    still calling it, so a build that stops calling it fails here.
        TypeDefinition netScene = gameModule.GetType("ZNetScene")!;
        MethodDefinition addInstance = netScene.Methods.SingleOrDefault(m => m.Name == "AddInstance" &&
            m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "ZDO", "ZNetView" }))
            ?? throw new InvalidOperationException("Loot visibility: ZNetScene.AddInstance(ZDO, ZNetView) is missing.");
        Check(!addInstance.IsStatic && addInstance.IsPublic && addInstance.ReturnType.FullName == "System.Void",
            "ZNetScene.AddInstance(ZDO, ZNetView) is a public instance void method");
        Check(viewAwake.Body.Instructions.Any(i => (i.Operand as MethodReference)?.Resolve() == addInstance),
            "ZNetView.Awake still registers every view through ZNetScene.AddInstance, local creation included");
        Check(netView.BaseType?.FullName == "UnityEngine.MonoBehaviour",
            "ZNetView is still a MonoBehaviour, which supplies the gameObject and GetComponent the hook reads");
        TypeDefinition zdo = gameModule.GetType("ZDO")!;
        foreach (var (name, returns) in new[] { ("GetPosition", "UnityEngine.Vector3"), ("IsOwner", "System.Boolean"), ("GetPrefab", "System.Int32") })
            Check(zdo.Methods.Any(m => m.Name == name && m.Parameters.Count == 0 && m.ReturnType.FullName == returns),
                "ZDO." + name + "() still returns " + returns);

        // 3b. t0 for trees, logs, plain rocks and destructibles: the owner's local destroy
        //     and the remote removal, plus the instance map the remote hook reads.
        MethodDefinition destroy = netScene.Methods.SingleOrDefault(m => m.Name == "Destroy" &&
            m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "UnityEngine.GameObject")
            ?? throw new InvalidOperationException("Loot visibility: ZNetScene.Destroy(GameObject) is missing.");
        Check(!destroy.IsStatic && destroy.ReturnType.FullName == "System.Void", "ZNetScene.Destroy(GameObject) is an instance void method");
        Check(destroy.Body.Instructions.Any(i => (i.Operand as MethodReference)?.Name == "DestroyZDO"),
            "ZNetScene.Destroy still destroys the owned ZDO, which is what makes it a destruction rather than an unload");
        MethodDefinition zdoDestroyed = netScene.Methods.SingleOrDefault(m => m.Name == "OnZDODestroyed" &&
            m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "ZDO")
            ?? throw new InvalidOperationException("Loot visibility: ZNetScene.OnZDODestroyed(ZDO) is missing.");
        Check(!zdoDestroyed.IsStatic && zdoDestroyed.ReturnType.FullName == "System.Void", "ZNetScene.OnZDODestroyed(ZDO) is an instance void handler");
        FieldDefinition instanceMap = netScene.Fields.SingleOrDefault(f => f.Name == "m_instances")
            ?? throw new InvalidOperationException("Loot visibility: ZNetScene.m_instances is missing.");
        Check(!instanceMap.IsStatic && instanceMap.FieldType.FullName.StartsWith("System.Collections.Generic.Dictionary`2<ZDO,ZNetView>"),
            "ZNetScene.m_instances still maps ZDO to ZNetView");
        MethodDefinition removeObjects = netScene.Methods.Single(m => m.Name == "RemoveObjects");
        Check(!removeObjects.Body.Instructions.Any(i => (i.Operand as MethodReference)?.Resolve() == destroy),
            "zone unload does not go through ZNetScene.Destroy, so an unload is never recorded as a destruction");
        foreach (string component in new[] { "TreeBase", "TreeLog", "MineRock", "Destructible", "DropOnDestroyed" })
            Check(gameModule.GetType(component) != null, component + " still exists for drop-source classification");
        FieldDefinition dropInstances = gameModule.GetType("ItemDrop")!.Fields.SingleOrDefault(f => f.Name == "s_instances");
        Check(dropInstances == null || (dropInstances.IsStatic && dropInstances.FieldType.FullName.StartsWith("System.Collections.Generic.List`1<ItemDrop>")),
            "ItemDrop.s_instances, when present, is the static list the population gauge reads");

        // 3b'. v4 owner t0 before the drops. Each source's own order is contract: the hook is
        //      placed before the first drop, and the ZNetScene.Destroy that follows is skipped.
        int Position(MethodDefinition method, Func<Cil.Instruction, bool> match, string what)
        {
            int index = method.Body.Instructions.ToList().FindIndex(i => match(i));
            Check(index >= 0, method.DeclaringType.Name + "." + method.Name + " still " + what);
            return index;
        }
        bool Calls(Cil.Instruction i, string type, string name) =>
            i.Operand is MethodReference called && called.Name == name && called.DeclaringType.FullName == type;
        bool Reads(Cil.Instruction i, string name) => (i.Operand as FieldReference)?.Name == name;
        Check(!breakRock.IsStatic && breakRock.ReturnType.FullName == "System.Void" && breakRock.Parameters[0].ParameterType.FullName == "HitData",
            "Destructible.Destroy(HitData) is an instance void method");
        int netDestroy = Position(breakRock, i => Calls(i, "ZNetScene", "Destroy"), "ends in ZNetScene.Destroy");
        Check(Position(breakRock, i => Reads(i, "m_spawnWhenDestroyed"), "spawns m_spawnWhenDestroyed") < netDestroy &&
              Position(breakRock, i => Reads(i, "m_onDestroyed"), "invokes m_onDestroyed") < netDestroy,
            "Destructible.Destroy spawns its fracture and runs m_onDestroyed (DropOnDestroyed) before ZNetScene.Destroy");
        Check(gameModule.GetType("DropOnDestroyed")!.Methods.Any(m => m.Name == "Awake" && m.HasBody &&
                m.Body.Instructions.Any(i => Reads(i, "m_onDestroyed") && (i.Operand as FieldReference)?.DeclaringType.FullName == "Destructible")),
            "DropOnDestroyed still drops through Destructible.m_onDestroyed");
        TypeDefinition tree = gameModule.GetType("TreeBase")!;
        MethodDefinition spawnLog = tree.Methods.SingleOrDefault(m => m.Name == "SpawnLog" &&
            m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "UnityEngine.Vector3" }))
            ?? throw new InvalidOperationException("Loot visibility: TreeBase.SpawnLog(Vector3) is missing.");
        Check(!spawnLog.IsStatic && spawnLog.ReturnType.FullName == "System.Void", "TreeBase.SpawnLog(Vector3) is an instance void method");
        MethodDefinition treeDamage = tree.Methods.Single(m => m.Name == "RPC_Damage");
        Check(tree.Methods.Count(m => m.HasBody && m.Body.Instructions.Any(i => (i.Operand as MethodReference)?.Resolve() == spawnLog)) == 1,
            "TreeBase.SpawnLog has a single caller, RPC_Damage");
        int treeLog = Position(treeDamage, i => (i.Operand as MethodReference)?.Resolve() == spawnLog, "calls SpawnLog");
        int treeDrops = Position(treeDamage, i => Calls(i, "DropTable", "GetDropList"), "reads its drop list");
        int treeDestroy = treeDamage.Body.Instructions.ToList().FindLastIndex(i => Calls(i, "ZNetView", "Destroy"));
        Check(treeLog < treeDrops && treeDrops < treeDestroy, "TreeBase.RPC_Damage calls SpawnLog, then drops, then destroys");
        TypeDefinition plainRock = gameModule.GetType("MineRock")!;
        MethodDefinition hide = plainRock.Methods.SingleOrDefault(m => m.Name == "RPC_Hide" &&
            m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "System.Int64", "System.Int32" }))
            ?? throw new InvalidOperationException("Loot visibility: MineRock.RPC_Hide(long, int) is missing.");
        Check(!hide.IsStatic && hide.ReturnType.FullName == "System.Void", "MineRock.RPC_Hide(long, int) is an instance void handler");
        Check(plainRock.Methods.Any(m => m.HasBody && m.Body.Instructions.Any(i => i.OpCode == Cil.OpCodes.Ldstr && (string)i.Operand == "Hide") &&
                m.Body.Instructions.Any(i => i.OpCode == Cil.OpCodes.Ldftn && (i.Operand as MethodReference)?.Resolve() == hide)),
            "MineRock still registers RPC_Hide as Hide, so the owner's own broadcast runs it at once");
        Check(plainRock.Methods.Any(m => m.Name == "AllDestroyed" && !m.IsStatic && m.Parameters.Count == 0 && m.ReturnType.FullName == "System.Boolean") &&
              plainRock.Fields.Any(f => f.Name == "m_removeWhenDestroyed" && f.IsPublic && f.FieldType.FullName == "System.Boolean"),
            "MineRock.AllDestroyed() and m_removeWhenDestroyed still decide the rock's removal");
        MethodDefinition rockHit = plainRock.Methods.Single(m => m.Name == "RPC_Hit");
        int healthSaved = Position(rockHit, i => Calls(i, "ZDO", "Set"), "saves the area's health");
        int hideSent = Position(rockHit, i => i.OpCode == Cil.OpCodes.Ldstr && (string)i.Operand == "Hide", "broadcasts Hide");
        int rockDrops = Position(rockHit, i => Calls(i, "DropTable", "GetDropList"), "reads its drop list");
        int rockDestroy = Position(rockHit, i => Calls(i, "ZNetView", "Destroy"), "destroys the rock");
        Check(healthSaved < hideSent && hideSent < rockDrops && rockDrops < rockDestroy,
            "MineRock.RPC_Hit saves health, broadcasts Hide, drops, then destroys");
        MethodDefinition logBreak = gameModule.GetType("TreeLog")!.Methods.Single(m => m.Name == "Destroy" && m.Parameters.Count == 2);
        Check(Position(logBreak, i => Calls(i, "ZNetScene", "Destroy"), "calls ZNetScene.Destroy") <
              Position(logBreak, i => Calls(i, "DropTable", "GetDropList"), "reads its drop list"),
            "TreeLog.Destroy destroys before dropping, so ZNetScene.Destroy stays its t0");

        // 3c. Send legs: SendZDOs records every ZDO it wrote in the peer's sent map, and
        //     RPC_ZDOData deserializes (so knows the prefab) only after creating the ZDO.
        MethodDefinition sendZdos = zdoMan.Methods.SingleOrDefault(m => m.Name == "SendZDOs" &&
            m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "ZDOMan/ZDOPeer", "System.Boolean" }))
            ?? throw new InvalidOperationException("Loot visibility: ZDOMan.SendZDOs(ZDOPeer, bool) is missing.");
        Check(!sendZdos.IsStatic && sendZdos.ReturnType.FullName == "System.Boolean", "ZDOMan.SendZDOs is an instance method returning bool");
        var sendBody = sendZdos.Body.Instructions.ToList();
        Check(sendBody.Any(i => (i.Operand as FieldReference)?.Name == "m_zdos") &&
              sendBody.Any(i => (i.Operand as MethodReference)?.Name == "set_Item"),
            "SendZDOs still writes each sent ZDO into the peer's m_zdos map the send leg reads");
        Check(zdoData.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "ZRpc", "ZPackage" }) &&
              zdoData.ReturnType.FullName == "System.Void", "ZDOMan.RPC_ZDOData(ZRpc, ZPackage) is a void handler");
        var dataBody = zdoData.Body.Instructions.ToList();
        int created = dataBody.FindIndex(i => (i.Operand as MethodReference)?.Resolve() == createNew);
        int deserialized = dataBody.FindIndex(i => (i.Operand as MethodReference)?.Name == "Deserialize");
        Check(created >= 0 && deserialized > created, "RPC_ZDOData deserializes the prefab after creating the ZDO, so the server classifies in a postfix");

        // 4. The plugin side is postfixes and prefixes that return void; none can replace
        //    or skip native work.
        TypeDefinition module = pluginModule.GetType("BetterPerformance.LootVisibilityTelemetry")!;
        foreach (var (hook, types) in new[]
        {
            ("BeforeSetAreaHealth", new[] { "MineRock5", "System.Int64", "System.Int32", "System.Single" }),
            ("AfterCreateNewZDO", new[] { "ZDOID", "UnityEngine.Vector3", "System.Int32", "ZDO" }),
            ("AfterAddInstance", new[] { "ZDO", "ZNetView" }),
            ("BeforeDestroy", new[] { "UnityEngine.GameObject" }),
            ("BeforeZdoDestroyed", new[] { "ZNetScene", "ZDO" }),
            ("BeforeDestructibleDestroy", new[] { "Destructible" }),
            ("BeforeSpawnLog", new[] { "TreeBase" }),
            ("BeforeRockHide", new[] { "MineRock", "System.Int64" }),
            ("AfterSendZdos", new[] { "System.Object", "System.Boolean" }),
            ("BeforeZdoData", new string[0]),
            ("AfterZdoData", new string[0]),
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
