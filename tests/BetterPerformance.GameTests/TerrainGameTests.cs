using System.Collections;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using Mono.Cecil;

// Verifies the terrain regeneration attribution probes against the installed game
// assemblies. Nothing here starts the game. Heightmap and TerrainComp.PaintCleared cannot be
// type-loaded by a standalone CLR (Unity interfaces carry default methods), so their IL is
// read from metadata with Mono.Cecil instead of through reflection. The neighbour-save
// coalescing contract that used to live here went with its module in 0.4.10: Valheim 1.0.15
// removed the per-texel neighbour save natively.
internal static class TerrainGameTests
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private const string SpreadPrefix = "<PaintCleared>g__spread|";

    internal static int Run(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Terrain: " + message);
            checks++;
        }

        Type terrain = game.GetType("TerrainComp", true)!;
        MethodInfo paintCleared = terrain.GetMethods(Declared)
            .Single(method => method.Name == "PaintCleared" && method.GetParameters().Length == 3);
        Check(!paintCleared.IsStatic && paintCleared.ReturnType == typeof(void),
            "TerrainComp.PaintCleared is a void instance method with three arguments");

        // Metadata-only IL. Reflection cannot open PaintCleared's body in this process.
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(game.Location)!);
        using (ModuleDefinition module = ModuleDefinition.ReadModule(game.Location,
            new ReaderParameters { AssemblyResolver = resolver }))
        {
            TypeDefinition compiler = module.GetType("TerrainComp")!;
            MethodDefinition paintDefinition = compiler.Methods.Single(method =>
                method.Name == "PaintCleared" && method.Parameters.Count == 3);
            MethodDefinition spreadDefinition = compiler.Methods.Single(method =>
                method.Name.StartsWith(SpreadPrefix, StringComparison.Ordinal));
            Check(Calls(paintDefinition, spreadDefinition.Name) >= 1,
                "PaintCleared still reaches the neighbour through the spread local function");
            // 1.0.15 replaced the per-texel neighbour Save with a modified-paint flag. A build
            // that still saves per texel (1.0.14 or earlier, or a future regression) is the
            // one case where the removed coalescing module would have applied: name it in the
            // output so a check-game-update run shows it, without failing an older server that
            // is simply not updated yet.
            int neighbourSaves = Calls(spreadDefinition, "Save");
            Console.WriteLine(neighbourSaves == 0
                ? "Terrain: spread does not save the neighbour per painted edge vertex (1.0.15 shape)"
                : "Terrain: NOTE this build still saves the neighbour per painted edge vertex (" + neighbourSaves +
                  " call) — the pre-1.0.15 shape the removed TerrainSaveCoalescing addressed");

            TypeDefinition heightmap = module.GetType("Heightmap")!;
            MethodDefinition pokeDefinition = heightmap.Methods.Single(method => method.Name == "Poke");
            Check(pokeDefinition.Parameters.Count == 2 &&
                pokeDefinition.Parameters[0].ParameterType.FullName == "System.Int32" &&
                pokeDefinition.Parameters[1].ParameterType.FullName == "System.Boolean" &&
                pokeDefinition.ReturnType.FullName == "System.Void",
                "Heightmap.Poke(int, bool) still takes the delay and paint-only flag");
            Check(heightmap.Methods.Any(method => method.Name == "Regenerate" && method.Parameters.Count == 0) &&
                heightmap.Methods.Any(method => method.Name == "OnEnable" && method.Parameters.Count == 0),
                "Heightmap exposes the parameterless Regenerate and OnEnable the attribution hooks use");
            Check(Calls(heightmap.Methods.Single(method => method.Name == "OnEnable"), "Regenerate") == 1,
                "Heightmap.OnEnable regenerates, which is why component enable is an attribution reason");
        }

        MethodInfo spawnZone = game.GetType("ZoneSystem", true)!.GetMethods(Declared)
            .Single(method => method.Name == "SpawnZone" && method.GetParameters().Length == 3);
        ParameterInfo[] zoneParameters = spawnZone.GetParameters();
        Check(spawnZone.ReturnType == typeof(bool) && !spawnZone.IsStatic &&
            zoneParameters[0].ParameterType.Name == "Vector2s" &&
            zoneParameters[1].ParameterType == game.GetType("ZoneSystem+SpawnMode", true) &&
            zoneParameters[2].ParameterType.IsByRef &&
            zoneParameters[2].ParameterType.GetElementType()!.Name == "GameObject",
            "ZoneSystem.SpawnZone(Vector2s, SpawnMode, out GameObject) is the ghost/full spawn boundary");
        Check(Enum.GetNames(game.GetType("ZoneSystem+SpawnMode", true)!).SequenceEqual(new[] { "Full", "Client", "Ghost" }),
            "SpawnMode still distinguishes Ghost from the spawning modes that keep their root");

        Type attribution = plugin.GetType("BetterPerformance.TerrainTelemetry", true)!;
        foreach (MethodInfo hook in attribution.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(method => method.Name.StartsWith("Before", StringComparison.Ordinal) ||
                method.Name.StartsWith("After", StringComparison.Ordinal)))
        {
            Check(hook.ReturnType == typeof(void), "attribution hook " + hook.Name + " cannot skip or replace a native call");
            Check(hook.GetParameters().All(parameter => parameter.Name == "__state" || !parameter.ParameterType.IsByRef),
                "attribution hook " + hook.Name + " does not mutate game arguments");
        }

        var config = new ConfigFile(Path.Combine(Path.GetTempPath(),
            "bp-terrain-" + Guid.NewGuid().ToString("N") + ".cfg"), false) { SaveOnConfigSet = false };
        var log = new ManualLogSource("TerrainGameTests");
        Method(attribution, "Install").Invoke(null, new object[] { config, log });
        string attributionStatus = (string?)attribution.GetProperty("Status", Declared)!.GetValue(null) ?? "";
        Check(attributionStatus == "installed" || attributionStatus.StartsWith("unavailable", StringComparison.Ordinal),
            "attribution reports either an installed or an unavailable probe, never a silent partial install");
        if (attributionStatus.StartsWith("unavailable", StringComparison.Ordinal))
            Console.WriteLine("STATIC ONLY terrain attribution install: Heightmap cannot be type-loaded outside Unity");
        Method(attribution, "Uninstall").Invoke(null, null);

        Type number = plugin.GetType("BetterPerformance.Core.NumberValue", true)!;
        Type text = plugin.GetType("BetterPerformance.Core.TextValue", true)!;
        var gauges = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(number))!;
        var labels = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(text))!;
        Method(attribution, "Reset").Invoke(null, null);
        Method(attribution, "Sample").Invoke(null, new object[] { gauges, labels });
        PropertyInfo gaugeName = number.GetProperty("Name")!, gaugeValue = number.GetProperty("Value")!;
        PropertyInfo labelName = text.GetProperty("Name")!;
        double Gauge(string name) => (double)(gaugeValue.GetValue(gauges.Cast<object>()
            .Single(entry => (string?)gaugeName.GetValue(entry) == name)) ?? -1);
        foreach (string name in new[] { "heightmap_regen_terrain_op", "heightmap_regen_zone_spawn_full",
            "heightmap_regen_zone_spawn_ghost", "heightmap_regen_enable", "heightmap_regen_other",
            "heightmap_regen_frames_with_1", "heightmap_regen_frames_with_2_3", "heightmap_regen_frames_with_4_plus",
            "heightmap_regen_max_per_frame", "terrain_ops_total", "terrain_op_rpc_dispatches_total",
            "terrain_ops_per_frame_max", "terrain_op_neighbours_max", "terrain_op_neighbours_total",
            "heightmap_regen_other_thread_skips", "heightmap_regen_probe_failures" })
            Check(Gauge(name) == 0, name + " starts at zero after a reset");
        foreach (string name in new[] { "terrain_regen_scope", "terrain_telemetry_status", "terrain_op_neighbours_semantics" })
            Check(labels.Cast<object>().Any(entry => (string?)labelName.GetValue(entry) == name),
                name + " documents the export in the capture itself");

        Console.WriteLine("Terrain: " + checks + " installed-game checks; attribution status=" + attributionStatus +
            "; the per-texel neighbour save is gone since 1.0.15.");
        return checks;
    }

    private static int Calls(MethodDefinition method, string name) =>
        method.HasBody ? method.Body.Instructions.Count(instruction =>
            instruction.Operand is MethodReference called && called.Name == name) : -1;

    private static MethodInfo Method(Type type, string name, Type[]? parameters = null) =>
        (parameters == null ? type.GetMethod(name, Declared) : type.GetMethod(name, Declared, null, parameters, null))
        ?? throw new InvalidOperationException("Terrain: " + type.Name + "." + name + " is missing.");
}
