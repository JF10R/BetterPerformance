using System.Collections;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using Mono.Cecil;

// Verifies the gameplay counter contract against the installed game assemblies.
// Nothing here starts the game or invokes a game method. Player, Character, Ship and
// ZSFX implement a Unity interface that carries default methods, which a standalone
// CLR cannot type-load, so every game-side signature is read from metadata with
// Mono.Cecil instead of through reflection.
internal static class GameplayGameTests
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly string[] Gauges =
    {
        "container_open_requests_received", "container_concurrent_open_conflicts", "container_open_granted",
        "container_open_requests_rejected_in_use", "container_changes", "container_gui_open_frames",
        "inventory_gui_open_frames", "inventory_moves", "inventory_item_stacks", "inventory_items_max",
        "smelter_updates", "smelter_catchup_items_sum", "smelter_catchup_items_max", "smelter_spawns",
        "fireplace_fuel_adds", "cooking_spawns", "beehive_extracts",
        "placement_ghost_updates", "placement_ghost_frames", "pieces_placed", "pieces_removed",
        "tree_damage_rpcs", "tree_logs_spawned", "rock_damage_rpcs", "rock_area_destroys",
        "destructible_destroys", "attacks_started", "hits_dealt", "drop_on_destroyed_events", "drops_spawned",
        "minimap_explore_updates", "minimap_explore_scans", "minimap_fog_applies",
        "minimap_fog_pixels_explored", "minimap_large_map_frames",
        "gameplay_observed_frames", "player_on_ship_frames", "ship_instances_max", "vagon_instances_max",
        "zsfx_instances_max", "item_drops_autostacked", "item_drop_slow_updates",
        "gameplay_other_thread_skips", "gameplay_probe_failures"
    };

    private static readonly string[] Labels =
    {
        "gameplay_telemetry_status", "gameplay_scope", "gameplay_probes_unavailable", "gameplay_skipped_counters"
    };

    // type, method, return type, parameter types. Every entry is a hook target or a
    // member the module reads; a change here is a broken counter, not a broken plugin.
    private static readonly (string Type, string Method, string Returns, string[] Parameters)[] Contracts =
    {
        ("Container", "RPC_RequestOpen", "System.Void", new[] { "System.Int64", "System.Int64" }),
        ("Container", "RPC_OpenResponse", "System.Void", new[] { "System.Int64", "System.Boolean" }),
        ("Container", "OnContainerChanged", "System.Void", new string[0]),
        ("Container", "IsInUse", "System.Boolean", new string[0]),
        ("Inventory", "MoveItemToThis", "System.Void", new[] { "Inventory", "ItemDrop/ItemData" }),
        ("Inventory", "AddItem", "System.Boolean", new[] { "ItemDrop/ItemData" }),
        ("Inventory", "NrOfItems", "System.Int32", new string[0]),
        ("Smelter", "UpdateSmelter", "System.Void", new string[0]),
        ("Smelter", "GetQueueSize", "System.Int32", new string[0]),
        ("Smelter", "GetProcessedQueueSize", "System.Int32", new string[0]),
        ("Smelter", "Spawn", "System.Void", new[] { "System.String", "System.Int32" }),
        ("Fireplace", "RPC_AddFuel", "System.Void", new[] { "System.Int64" }),
        ("CookingStation", "SpawnItem", "System.Void",
            new[] { "System.String", "System.Int32", "UnityEngine.Vector3", "System.Boolean" }),
        ("Beehive", "RPC_Extract", "System.Void", new[] { "System.Int64" }),
        ("Player", "UpdatePlacementGhost", "System.Void", new[] { "System.Boolean" }),
        ("Player", "PlacePiece", "System.Void",
            new[] { "Piece", "UnityEngine.Vector3", "UnityEngine.Quaternion", "System.Boolean", "System.Boolean" }),
        ("Player", "RemovePiece", "System.Boolean", new string[0]),
        ("Player", "IsAttachedToShip", "System.Boolean", new string[0]),
        ("TreeBase", "RPC_Damage", "System.Void", new[] { "System.Int64", "HitData" }),
        ("TreeBase", "SpawnLog", "System.Void", new[] { "UnityEngine.Vector3" }),
        ("MineRock5", "RPC_Damage", "System.Void", new[] { "System.Int64", "HitData", "System.Int32" }),
        ("MineRock5", "DamageArea", "System.Boolean", new[] { "System.Int32", "HitData" }),
        ("Destructible", "Destroy", "System.Void", new[] { "HitData" }),
        ("Character", "RPC_Damage", "System.Void", new[] { "System.Int64", "HitData" }),
        ("DropOnDestroyed", "OnDestroyed", "System.Void", new string[0]),
        ("DropTable", "GetDropList", "System.Collections.Generic.List`1<UnityEngine.GameObject>", new string[0]),
        ("Minimap", "UpdateExplore", "System.Void", new[] { "System.Single", "Player" }),
        ("Minimap", "Explore", "System.Void", new[] { "UnityEngine.Vector3", "System.Single" }),
        ("Minimap", "Explore", "System.Boolean", new[] { "System.Int32", "System.Int32" }),
        ("InventoryGui", "IsVisible", "System.Boolean", new string[0]),
        ("Hud", "Update", "System.Void", new string[0]),
        ("ItemDrop", "AutoStackItems", "System.Void", new string[0]),
        ("ItemDrop", "SlowUpdate", "System.Void", new string[0])
    };

    internal static int Run(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Gameplay counters: " + message);
            checks++;
        }

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(game.Location)!);
        using (ModuleDefinition module = ModuleDefinition.ReadModule(game.Location,
            new ReaderParameters { AssemblyResolver = resolver }))
        {
            foreach (var (typeName, methodName, returns, parameters) in Contracts)
            {
                TypeDefinition? owner = module.GetType(typeName);
                Check(owner != null, typeName + " is present in the installed game");
                MethodDefinition[] matches = owner!.Methods.Where(candidate =>
                    candidate.Name == methodName &&
                    candidate.IsStatic == (typeName == "InventoryGui" && methodName == "IsVisible") &&
                    candidate.ReturnType.FullName == returns &&
                    candidate.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(parameters))
                    .ToArray();
                Check(matches.Length == 1,
                    typeName + "." + methodName + " resolves to exactly one hook target with the expected shape");
            }

            // Attack.Start takes a Unity physics type this plugin never references, so
            // the module matches it on shape; the shape must stay unambiguous.
            MethodDefinition[] attacks = module.GetType("Attack")!.Methods.Where(candidate =>
                candidate.Name == "Start" && !candidate.IsStatic &&
                candidate.ReturnType.FullName == "System.Boolean" && candidate.Parameters.Count == 9).ToArray();
            Check(attacks.Length == 1, "Attack.Start is the single nine-argument bool instance method the shape match finds");

            FieldDefinition? ghost = module.GetType("Player")!.Fields.SingleOrDefault(field => field.Name == "m_placementGhost");
            Check(ghost != null && ghost.FieldType.FullName == "UnityEngine.GameObject",
                "Player.m_placementGhost is the GameObject the ghost gauge reads");
            FieldDefinition? mode = module.GetType("Minimap")!.Fields.SingleOrDefault(field => field.Name == "m_mode");
            Check(mode != null && mode.FieldType.FullName == "Minimap/MapMode",
                "Minimap.m_mode is the enum the large-map frame gauge reads");
            Check(module.GetType("Minimap/MapMode")!.Fields.Any(field => field.Name == "Large"),
                "Minimap.MapMode still declares the Large mode");
            FieldDefinition? current = module.GetType("InventoryGui")!.Fields
                .SingleOrDefault(field => field.Name == "m_currentContainer");
            Check(current != null && current.FieldType.FullName == "Container",
                "InventoryGui.m_currentContainer is the container the open-frame gauge reads");
            FieldDefinition? humanoidInventory = module.GetType("Humanoid")!.Fields
                .SingleOrDefault(field => field.Name == "m_inventory");
            Check(humanoidInventory != null && humanoidInventory.FieldType.FullName == "Inventory",
                "Humanoid.m_inventory is the inventory the size gauge reads");

            foreach (var (typeName, member, kind) in new[]
                { ("Ship", "Instances", "property"), ("ZSFX", "Instances", "property"), ("Vagon", "m_instances", "field") })
            {
                TypeDefinition owner = module.GetType(typeName)!;
                string? collection = kind == "property"
                    ? owner.Properties.SingleOrDefault(property => property.Name == member)?.PropertyType.FullName
                    : owner.Fields.SingleOrDefault(field => field.Name == member)?.FieldType.FullName;
                Check(collection != null && collection.StartsWith("System.Collections.Generic.List`1<", StringComparison.Ordinal),
                    typeName + "." + member + " is a generic list whose Count the occupancy gauge reads");
            }
        }

        Type telemetry = plugin.GetType("BetterPerformance.GameplayTelemetry", true)!;
        foreach (MethodInfo hook in telemetry.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(method => method.Name.StartsWith("Before", StringComparison.Ordinal) ||
                method.Name.StartsWith("After", StringComparison.Ordinal) ||
                method.Name.StartsWith("On", StringComparison.Ordinal)))
        {
            Check(hook.ReturnType == typeof(void),
                "hook " + hook.Name + " cannot skip a native call or replace its result or exception");
            Check(hook.GetParameters().All(parameter => parameter.Name == "__state" || !parameter.ParameterType.IsByRef),
                "hook " + hook.Name + " does not mutate game arguments");
            Check(!hook.GetParameters().Any(parameter => parameter.Name == "__result" && parameter.ParameterType.IsByRef),
                "hook " + hook.Name + " never takes a writable result");
        }

        var config = new ConfigFile(Path.Combine(Path.GetTempPath(),
            "bp-gameplay-" + Guid.NewGuid().ToString("N") + ".cfg"), false) { SaveOnConfigSet = false };
        var log = new ManualLogSource("GameplayGameTests");
        Method(telemetry, "Install").Invoke(null, new object[] { config, log });
        var entry = (ConfigEntry<bool>)config[new ConfigDefinition("Diagnostics", "GameplayCountersEnabled")];
        Check(entry.Value && (bool)entry.DefaultValue!,
            "diagnostics counters are on by default and bound under the Diagnostics section");
        string status = (string?)telemetry.GetProperty("Status", Declared)!.GetValue(null) ?? "";
        Check(status is "installed" or "partial" or "unavailable",
            "install reports a known state, never a silent partial install (actual=" + status + ")");
        // Offline, probes on types carrying Unity interfaces cannot install. Print the
        // refusals so a signature change can never hide behind an expected type-load.
        var unavailable = (IEnumerable<string>)telemetry.GetField("Missing", Declared)!.GetValue(null)!;
        string refusals = string.Join(", ", unavailable);
        Console.WriteLine("Gameplay counters: probes refused in this process: " + (refusals.Length == 0 ? "none" : refusals));
        // A type-load or a Harmony refusal is this process lacking Unity, and the
        // signatures above are already verified from metadata. An InvalidOperationException
        // is the module itself rejecting a game signature, which a game update would cause.
        Check(!unavailable.Any(entry => entry.EndsWith(":InvalidOperationException", StringComparison.Ordinal)),
            "no probe is refused for a changed game signature (" + refusals + ")");
        Method(telemetry, "Uninstall").Invoke(null, null);
        Check((string?)telemetry.GetProperty("Status", Declared)!.GetValue(null) == "disabled" &&
            telemetry.GetProperty("Installed", Declared)!.GetValue(null) is false,
            "removal releases the probes and reports the module as disabled");

        entry.Value = false;
        Method(telemetry, "Install").Invoke(null, new object[] { config, log });
        Check((string?)telemetry.GetProperty("Status", Declared)!.GetValue(null) == "disabled" &&
            telemetry.GetProperty("Installed", Declared)!.GetValue(null) is false,
            "an opted-out configuration installs nothing");

        Type number = plugin.GetType("BetterPerformance.Core.NumberValue", true)!;
        Type text = plugin.GetType("BetterPerformance.Core.TextValue", true)!;
        var gauges = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(number))!;
        var labels = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(text))!;
        telemetry.GetProperty("Enabled", Declared)!.SetValue(null, false);
        Method(telemetry, "Reset").Invoke(null, null);
        Method(telemetry, "Sample").Invoke(null, new object[] { gauges, labels });
        PropertyInfo gaugeName = number.GetProperty("Name")!, gaugeValue = number.GetProperty("Value")!;
        PropertyInfo labelName = text.GetProperty("Name")!;
        Check(gauges.Count == Gauges.Length, "a disabled sample exports exactly the documented gauge set");
        foreach (string name in Gauges)
        {
            object[] rows = gauges.Cast<object>().Where(row => (string?)gaugeName.GetValue(row) == name).ToArray();
            Check(rows.Length == 1, name + " is exported exactly once");
            Check((double)gaugeValue.GetValue(rows[0])! == 0, name + " starts at zero after a reset");
        }
        foreach (string name in Labels)
            Check(labels.Cast<object>().Any(row => (string?)labelName.GetValue(row) == name),
                name + " documents the export in the capture itself");
        Check(labels.Cast<object>().Any(row => (string?)labelName.GetValue(row) == "gameplay_telemetry_status" &&
            (string?)text.GetProperty("Value")!.GetValue(row) == "disabled"),
            "a disabled module reports itself as disabled in the capture");
        Method(telemetry, "Uninstall").Invoke(null, null);

        Console.WriteLine("Gameplay counters: " + checks + " installed-game checks; install status=" + status +
            "; probes refused offline=" + (refusals.Length == 0 ? "none" : refusals) +
            "; counts only, no timing, and no game method was invoked.");
        return checks;
    }

    private static MethodInfo Method(Type type, string name) => type.GetMethod(name, Declared)
        ?? throw new InvalidOperationException("Gameplay counters: missing method " + type.Name + "." + name);
}
