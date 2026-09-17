using System.Collections;
using System.Reflection;
using Mono.Cecil;
using Cil = Mono.Cecil.Cil;

// Verifies the gameplay-loop probes against the installed assembly.
// Player, Character, Humanoid, Ship and every other IMonoUpdater implementor cannot be
// type-loaded by a standalone CLR, so every signature is read from Mono.Cecil metadata
// instead of reflection. Reflection is used only for the plugin's own state, and the
// registration of a probe is asserted at runtime only when its types actually load here.
internal static class GameplayProbesGameTests
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
    private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;

    private static int checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("GameplayProbesGameTests: " + message);
        checks++;
    }

    private readonly struct Contract
    {
        internal readonly string Type, Method, Metric, Return;
        internal readonly string[] Parameters;
        internal readonly bool Static;
        internal Contract(string type, string method, string metric, string[] parameters, string ret = "System.Void", bool isStatic = false)
        { Type = type; Method = method; Metric = metric; Parameters = parameters; Return = ret; Static = isStatic; }
    }

    private static readonly string[] None = new string[0];

    // Exact managed signatures the installer must target. Cecil spells a nested type with
    // '/', reflection with '+'; the comparison below normalises to '+'.
    private static readonly Contract[] Contracts = {
        new Contract("InventoryGui", "Update", "InventoryGuiUpdate", None),
        new Contract("InventoryGui", "UpdateInventory", "InventoryGridUpdate", new[] { "Player" }),
        new Contract("InventoryGui", "UpdateContainer", "ContainerGridUpdate", new[] { "Player" }),
        new Contract("InventoryGui", "Show", "InventoryGuiShow", new[] { "Container", "System.Int32" }),
        new Contract("Container", "Interact", "ContainerInteract",
            new[] { "Humanoid", "System.Boolean", "System.Boolean" }, "System.Boolean"),
        new Contract("Container", "OnContainerChanged", "ContainerChanged", None),
        new Contract("Container", "CheckForChanges", "ContainerCheckForChanges", None),
        new Contract("Inventory", "AddItem", "InventoryAddItem", new[] { "ItemDrop/ItemData" }, "System.Boolean"),
        new Contract("Inventory", "MoveItemToThis", "InventoryMoveItem", new[] { "Inventory", "ItemDrop/ItemData" }),
        new Contract("Player", "UpdatePlacementGhost", "PlacementGhostUpdate", new[] { "System.Boolean" }),
        new Contract("Player", "UpdatePlacement", "PlacementUpdate", new[] { "System.Boolean", "System.Single" }),
        new Contract("Player", "TryPlacePiece", "PiecePlace", new[] { "Piece" }, "System.Boolean"),
        new Contract("Hud", "UpdateBuild", "BuildGuiUpdate", new[] { "Player", "System.Boolean" }),
        new Contract("Minimap", "Update", "MinimapUpdate", None),
        new Contract("Minimap", "UpdateExplore", "MinimapExploreUpdate", new[] { "System.Single", "Player" }),
        new Contract("Minimap", "UpdateMap", "MinimapLargeMapUpdate", new[] { "Player", "System.Single", "System.Boolean" }),
        new Contract("Minimap", "SetMapMode", "MinimapSetMapMode", new[] { "Minimap/MapMode" }),
        new Contract("Ship", "CustomFixedUpdate", "ShipFixedUpdate", new[] { "System.Single" }),
        new Contract("Vagon", "Update", "VagonUpdate", None),
        new Contract("Vagon", "AttachTo", "VagonAttach", new[] { "UnityEngine.GameObject" }),
        new Contract("Vagon", "Detach", "VagonDetach", None),
        new Contract("TreeBase", "RPC_Damage", "TreeDamage", new[] { "System.Int64", "HitData" }),
        new Contract("TreeBase", "SpawnLog", "TreeSpawnLog", new[] { "UnityEngine.Vector3" }),
        new Contract("TreeLog", "RPC_Damage", "TreeLogDamage", new[] { "System.Int64", "HitData" }),
        new Contract("TreeLog", "Destroy", "TreeLogDestroy", new[] { "HitData", "System.Boolean" }),
        new Contract("MineRock5", "RPC_Damage", "MineRockDamage", new[] { "System.Int64", "HitData", "System.Int32" }),
        new Contract("MineRock5", "DamageArea", "MineRockDamageArea", new[] { "System.Int32", "HitData" }, "System.Boolean"),
        new Contract("Destructible", "RPC_Damage", "DestructibleDamage", new[] { "System.Int64", "HitData" }),
        new Contract("Destructible", "Destroy", "DestructibleDestroy", new[] { "HitData" }),
        new Contract("WearNTear", "RPC_Damage", "WearDamage", new[] { "System.Int64", "HitData" }),
        new Contract("Character", "RPC_Damage", "CharacterDamage", new[] { "System.Int64", "HitData" }),
        new Contract("Character", "ApplyDamage", "CharacterApplyDamage",
            new[] { "HitData", "System.Boolean", "System.Boolean", "HitData/DamageModifier" }),
        new Contract("Attack", "Start", "AttackStart", new[] {
            "Humanoid", "UnityEngine.Rigidbody", "ZSyncAnimation", "CharacterAnimEvent", "VisEquipment",
            "ItemDrop/ItemData", "Attack", "System.Single", "System.Single" }, "System.Boolean"),
        new Contract("Piece", "DropResources", "PieceDropResources", new[] { "HitData" }),
        new Contract("DropOnDestroyed", "OnDestroyed", "DropTableDrop", None),
        new Contract("Smelter", "Spawn", "SmelterSpawn", new[] { "System.String", "System.Int32" }),
        new Contract("ItemDrop", "SlowUpdate", "ItemDropSlowUpdate", None),
        new Contract("ItemDrop", "AutoStackItems", "ItemAutoStack", None),
        new Contract("Pickable", "Interact", "PickableInteract",
            new[] { "Humanoid", "System.Boolean", "System.Boolean" }, "System.Boolean"),
        new Contract("Player", "Update", "PlayerUpdate", None),
        new Contract("Player", "FixedUpdate", "PlayerFixedUpdate", None),
        new Contract("Hud", "Update", "HudUpdate", None),
        new Contract("Player", "RemovePiece", "PieceRemove", None, "System.Boolean"),
        new Contract("Player", "CopyPiece", "PieceCopy", None, "System.Boolean"),
        new Contract("BuildUi", "OpenBuildMenu", "BuildMenuOpen", None),
        new Contract("ClutterSystem", "LateUpdate", "ClutterLateUpdate", None),
        new Contract("ClutterSystem", "GeneratePatches", "ClutterGeneratePatches",
            new[] { "System.Boolean", "UnityEngine.Vector3" }),
        new Contract("ClutterSystem", "GenerateVegPatch", "ClutterGenerateVegPatch",
            new[] { "UnityEngine.Vector2Int", "System.Single" }, "ClutterSystem/PatchData"),
        new Contract("WaterVolume", "StaticUpdate", "WaterStaticUpdate", None, "System.Void", true)
    };

    // Native methods that exist but are deliberately left unhooked, with the reason.
    private static readonly (string Type, string Method, string Reason)[] Skipped = {
        ("Player", "PlacePiece", "runs inside TryPlacePiece; hooking both would double count"),
        ("InventoryGui", "OnSelectedItem", "UI event wrapper; Inventory.MoveItemToThis carries the move"),
        ("Minimap", "UpdatePins", "runs inside UpdateMap"),
        ("Minimap", "UpdateDynamicPins", "runs inside UpdateMap"),
        ("Smelter", "SpawnProcessed", "calls Spawn(string,int), which is hooked"),
        ("DropTable", "GetDropList", "runs inside DropOnDestroyed.OnDestroyed"),
        ("Character", "CustomFixedUpdate", "dispatched by the Character fixed-update batch")
    };

    internal static void Run(Assembly game, Assembly plugin)
    {
        checks = 0;
        Type timing = plugin.GetType("BetterPerformance.TimingHooks", true)!;
        Type metricType = plugin.GetType("BetterPerformance.Core.Metric", true)!;
        var registered = (IDictionary)timing.GetField("Metrics", StaticPrivate)!.GetValue(null)!;
        var availability = ((IEnumerable)timing.GetField("Availability", StaticPrivate)!.GetValue(null)!)
            .Cast<object>().ToDictionary(
                v => (string)v.GetType().GetProperty("Name")!.GetValue(v)!,
                v => (string)v.GetType().GetProperty("Value")!.GetValue(v)!);

        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(game.Location)!);
        using var module = ModuleDefinition.ReadModule(game.Location, new ReaderParameters { AssemblyResolver = resolver });

        // Every metric this stream adds must sit after the previously last member: the
        // exported histogram order is positional and older captures must stay comparable.
        int boundary = (int)Enum.Parse(metricType, "DungeonSpawn");
        var added = new List<string>(Contracts.Select(c => c.Metric));
        int gated = 0;
        foreach (var contract in Contracts)
        {
            string metric = contract.Metric;
            Check(Enum.IsDefined(metricType, metric), metric + ": metric is exportable");
            Check((int)Enum.Parse(metricType, metric) > boundary, metric + ": appended after the previous last metric");

            TypeDefinition owner = module.GetType(contract.Type)
                ?? throw new InvalidOperationException("GameplayProbesGameTests: " + contract.Type + " is missing.");
            var matches = owner.Methods.Where(m => m.Name == contract.Method &&
                m.Parameters.Select(p => Normalize(p.ParameterType.FullName)).SequenceEqual(contract.Parameters.Select(Normalize))).ToArray();
            Check(matches.Length == 1, metric + ": exactly one overload matches the expected parameter types");
            MethodDefinition definition = matches[0];
            Check(Normalize(definition.ReturnType.FullName) == Normalize(contract.Return), metric + ": expected return type");
            Check(definition.IsStatic == contract.Static, metric + ": expected static/instance shape");
            Check(definition.HasBody, metric + ": current game ships a managed body");

            Check(availability.ContainsKey("probe." + metric), metric + ": installer reported a status");
            string state = availability["probe." + metric];

            // The installer resolves every game type by name. Where a type cannot be loaded
            // in this process the probe must degrade, not guess.
            string[] gate = Gate(contract);
            if (gate.Any(name => !Loads(game, name)))
            {
                gated++;
                Check(state == "unavailable", metric + ": unloadable types must degrade to unavailable (actual=" + state + ")");
                Check(!registered.Values.Cast<object>().Any(v => v.ToString() == metric),
                    metric + ": no game method was registered while its types could not be loaded");
                Console.WriteLine("STATIC ONLY " + metric + ": signature verified from metadata; " +
                    "offline CLR cannot load " + string.Join(", ", gate.Where(name => !Loads(game, name))));
                continue;
            }

            Type reflected = game.GetType(contract.Type, true)!;
            var reflectedMatches = reflected.GetMethods(All).Where(m => m.Name == contract.Method &&
                m.GetParameters().Select(p => Normalize(p.ParameterType.FullName!)).SequenceEqual(contract.Parameters.Select(Normalize))).ToArray();
            Check(reflectedMatches.Length == 1, metric + ": reflection resolves the same single overload");
            Check(registered.Contains(reflectedMatches[0]) && registered[reflectedMatches[0]]!.ToString() == metric,
                metric + ": production installer maps the exact game method to this metric");
            // An offline patch of a body that reaches a Unity interface implementor fails to
            // JIT here. That is a verifier limitation, never a guessed signature.
            Check(state == "enabled" || state == "patch_failed",
                metric + ": installer reports success or an offline JIT limitation (actual=" + state + ")");
        }

        // Batch probes: one patch per MonoUpdaters dispatch method, the profiler-scope
        // string selects the metric. Each label must be one the shipped game actually passes.
        foreach (var (field, dispatch, parameters, prefixName) in new[] {
            ("FixedBatches", "CustomFixedUpdate", 4, "FixedBatchPrefix"),
            ("UpdateBatches", "CustomUpdate", 5, "UpdateBatchPrefix") })
        {
            var table = (IDictionary)timing.GetField(field, BindingFlags.Static | BindingFlags.NonPublic |
                BindingFlags.Public)!.GetValue(null)!;
            Check(table.Count > 0, field + ": batch table is populated");
            TypeDefinition updaters = module.GetType("MonoUpdatersExtra")!;
            MethodDefinition entry = updaters.Methods.Single(m => m.Name == dispatch);
            Check(entry.IsStatic && entry.Parameters.Count == parameters &&
                entry.Parameters[2].ParameterType.FullName == "System.String",
                dispatch + ": profiler scope is argument index 2, so the prefix binds __2");

            foreach (DictionaryEntry pair in table)
            {
                string label = (string)pair.Key!;
                string metric = pair.Value!.ToString()!;
                added.Add(metric);
                Check(Enum.IsDefined(metricType, metric), metric + ": batch metric is exportable");
                Check(metric == "CharacterFixedBatch" || (int)Enum.Parse(metricType, metric) > boundary,
                    metric + ": batch metric appended after the previous last metric");
                var callers = CallersOf(module, label);
                Check(callers.Count == 1, label + ": the batch literal occurs in exactly one method (found " + callers.Count + ")");
                Check(callers[0].Body.Instructions.Any(i => i.Operand is MethodReference call &&
                    call.Name == dispatch && call.DeclaringType.FullName == "MonoUpdatersExtra"),
                    label + ": the literal is passed to MonoUpdatersExtra." + dispatch);
                Check(availability.ContainsKey("probe." + metric), metric + ": installer reported a status");
            }

            MethodInfo prefix = timing.GetMethod(prefixName, StaticPrivate)!;
            ParameterInfo[] prefixParameters = prefix.GetParameters();
            Check(prefix.ReturnType == typeof(void) && prefixParameters.Length == 2,
                prefixName + ": cannot skip the original method");
            Check(prefixParameters[0].Name == "__2" && prefixParameters[0].ParameterType == typeof(string),
                prefixName + ": reads the profiler-scope string at argument index 2");
            Check(prefixParameters[1].IsOut, prefixName + ": only produces timing state");
        }
        Check(timing.GetMethod("BatchFinalizer", StaticPrivate)!.ReturnType == typeof(void),
            "BatchFinalizer cannot replace a result or exception");
        // The build-mode stall bucket is read off the same finalizer, which must stay a void
        // observer, and is drained by the gameplay counters as placement_update_over_10ms.
        Check(timing.GetMethod("Finalizer", StaticPrivate)!.ReturnType == typeof(void),
            "Finalizer cannot replace a result or exception");
        MethodInfo? drain = timing.GetMethod("DrainPlacementUpdateOver10Ms",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Check(drain != null && drain.ReturnType == typeof(long) && drain.GetParameters().Length == 0,
            "the 10 ms placement bucket is drained through a parameterless long accessor");
        Check(added.Distinct().Count() == added.Count, "no metric is claimed by two probes");

        // Deliberate omissions, and native methods the brief assumed that this build lacks.
        foreach (var (typeName, methodName, reason) in Skipped)
        {
            TypeDefinition owner = module.GetType(typeName)!;
            Check(owner.Methods.Any(m => m.Name == methodName), typeName + "." + methodName + ": still exists (" + reason + ")");
            Check(!registered.Keys.Cast<MethodBase>().Any(m => m.DeclaringType!.Name == typeName && m.Name == methodName),
                typeName + "." + methodName + ": not hooked (" + reason + ")");
        }
        TypeDefinition vagon = module.GetType("Vagon")!;
        Check(!vagon.Methods.Any(m => m.Name == "FixedUpdate" || m.Name == "CustomFixedUpdate"),
            "Vagon has no fixed-update entry in this build, so Vagon.Update is the registered equivalent");
        TypeDefinition tree = module.GetType("TreeBase")!;
        Check(!tree.Methods.Any(m => m.Name == "Destroy" || m.Name == "RPC_Destroy"),
            "TreeBase has no destroy entry, so TreeBase.SpawnLog is the registered log-spawn path");

        Console.WriteLine("PASS " + checks + " gameplay-loop checks; probes gated by unloadable types=" +
            gated + "; no game method invoked");
    }

    private static string Normalize(string name) => name.Replace('/', '+');

    private static string[] Gate(Contract contract)
    {
        var names = new List<string> { contract.Type };
        foreach (string parameter in contract.Parameters)
        {
            // Primitives and Unity engine types live outside assembly_valheim and are not
            // what the installer guards; UnityEngine.Rigidbody is not even referenced by it.
            if (parameter.StartsWith("System.") || parameter.StartsWith("UnityEngine.")) continue;
            string owner = Normalize(parameter);
            int nested = owner.IndexOf('+');
            names.Add(nested < 0 ? owner : owner.Substring(0, nested));
        }
        return names.Distinct().ToArray();
    }

    private static bool Loads(Assembly game, string name)
    {
        try { return game.GetType(Normalize(name), false) != null; }
        catch (TypeLoadException) { return false; }
        catch (FileNotFoundException) { return false; }
    }

    private static List<MethodDefinition> CallersOf(ModuleDefinition module, string literal)
    {
        var found = new List<MethodDefinition>();
        foreach (TypeDefinition type in module.GetTypes())
            foreach (MethodDefinition method in type.Methods)
            {
                if (!method.HasBody) continue;
                if (method.Body.Instructions.Any(i => i.OpCode == Cil.OpCodes.Ldstr && (string?)i.Operand == literal))
                    found.Add(method);
            }
        return found;
    }
}
