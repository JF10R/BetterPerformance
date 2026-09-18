using System.Collections;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Verifies the terrain neighbour-save coalescing contract and the regeneration
// attribution probes against the installed game assemblies. Nothing here starts the
// game. Heightmap and TerrainComp.PaintCleared cannot be type-loaded by a standalone
// CLR (Unity interfaces carry default methods), so their IL is read from metadata with
// Mono.Cecil instead of through reflection.
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
        MethodInfo save = Method(terrain, "Save", new[] { typeof(bool) });
        Check(!save.IsStatic && save.ReturnType == typeof(void) && save.IsPrivate,
            "TerrainComp.Save(bool) is a private void instance method");
        MethodInfo hash = Method(terrain, "ComputePaintMaskHash", Type.EmptyTypes);
        Check(!hash.IsStatic && hash.ReturnType == typeof(int),
            "TerrainComp.ComputePaintMaskHash() is the int paint gate the deferred save replicates");
        Check(Field(terrain, "m_lastHash").FieldType == typeof(int) &&
            Field(terrain, "m_initialized").FieldType == typeof(bool) &&
            Field(terrain, "m_nview").FieldType.Name == "ZNetView",
            "the compiler state the deferred save reads has the expected field types");

        MethodInfo[] spreads = terrain.GetMethods(Declared)
            .Where(method => method.Name.StartsWith(SpreadPrefix, StringComparison.Ordinal)).ToArray();
        Check(spreads.Length == 1, "PaintCleared declares exactly one spread local function");
        MethodInfo spread = spreads[0];
        Check(!spread.IsStatic && spread.ReturnType == typeof(bool),
            "the spread local function is a bool instance method");
        ParameterInfo[] spreadParameters = spread.GetParameters();
        Check(spreadParameters.Length == 6 && spreadParameters[0].ParameterType == typeof(int) &&
            spreadParameters[1].ParameterType == typeof(int),
            "spread takes the two neighbour offsets followed by its captured state");
        Check(spreadParameters.Skip(2).All(parameter => parameter.ParameterType.IsByRef &&
            parameter.ParameterType.GetElementType()!.DeclaringType == terrain),
            "spread receives its captured scopes by reference from TerrainComp");

        MethodInfo paintCleared = terrain.GetMethods(Declared)
            .Single(method => method.Name == "PaintCleared" && method.GetParameters().Length == 3);
        Check(!paintCleared.IsStatic && paintCleared.ReturnType == typeof(void),
            "TerrainComp.PaintCleared is a void instance method with three arguments");
        MethodInfo doOperation = terrain.GetMethods(Declared)
            .Single(method => method.Name == "DoOperation" && method.GetParameters().Length == 3);

        // Valheim 1.0.15 removed the per-texel neighbour Save from spread: it now marks
        // m_modifiedPaint, writes the mask in memory and pokes, and a later Save serializes
        // the flags. That was the entire cost TerrainSaveCoalescing existed to remove, so on
        // 1.0.15 the module is superseded and must decline to install. Both shapes are
        // asserted strictly — this is not a widened contract: an installation on either build
        // gets exactly one expected outcome, and a client and a dedicated server can sit on
        // different builds while one of them is still updating.
        bool nativeSavesPerTexel = false;

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
            int spreadCalls = Calls(paintDefinition, spreadDefinition.Name);
            Check(spreadCalls >= 1, "PaintCleared reaches the neighbour through the spread local function");
            Check(Calls(paintDefinition, "Save") == 0, "PaintCleared itself never saves");
            int neighbourSaves = Calls(spreadDefinition, "Save");
            Check(neighbourSaves == 0 || neighbourSaves == 1,
                "spread saves the neighbour either once (up to 1.0.14) or never (1.0.15 and later)");
            nativeSavesPerTexel = neighbourSaves == 1;
            var instructions = spreadDefinition.Body.Instructions.ToList();
            Check(instructions.Any(instruction => instruction.Operand is MethodReference poke && poke.Name == "Poke"),
                "spread still pokes the neighbour heightmap");
            if (nativeSavesPerTexel)
            {
                int call = instructions.FindIndex(instruction =>
                    instruction.Operand is MethodReference called && called.Name == "Save");
                Check(call >= 2 && instructions[call - 2].OpCode == OpCodes.Ldloc_0,
                    "the saved receiver is the neighbouring compiler, not the operation's own compiler");
                Check(call + 5 < instructions.Count && instructions[call + 1].OpCode == OpCodes.Ldloc_0 &&
                    instructions[call + 2].OpCode == OpCodes.Ldfld &&
                    ((FieldReference)instructions[call + 2].Operand).Name == "m_hmap" &&
                    instructions[call + 3].OpCode == OpCodes.Ldc_I4_1,
                    "spread pokes the neighbour heightmap with a one-frame delay immediately after saving");
            }
            else
            {
                Check(instructions.Any(instruction => instruction.Operand is FieldReference modified &&
                        modified.Name == "m_modifiedPaint"),
                    "spread marks the neighbour's modified-paint flag instead of writing the ZDO");
            }
            Console.WriteLine("Terrain coalescing: PaintCleared reaches spread from " + spreadCalls +
                " edge and corner cases; per-texel neighbour save present=" + nativeSavesPerTexel);


            TypeDefinition heightmap = module.GetType("Heightmap")!;
            MethodDefinition pokeDefinition = heightmap.Methods.Single(method => method.Name == "Poke");
            Check(pokeDefinition.Parameters.Count == 2 &&
                pokeDefinition.Parameters[0].ParameterType.FullName == "System.Int32" &&
                pokeDefinition.Parameters[1].ParameterType.FullName == "System.Boolean" &&
                pokeDefinition.ReturnType.FullName == "System.Void",
                "Heightmap.Poke(int, bool) still takes the delay and paint-only flag this patch leaves native");
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

        Type coalescing = plugin.GetType("BetterPerformance.TerrainSaveCoalescing", true)!;
        Type attribution = plugin.GetType("BetterPerformance.TerrainTelemetry", true)!;
        MethodInfo verify = coalescing.GetMethod("Verify", BindingFlags.Static | BindingFlags.NonPublic)!;
        string? Verify(MethodBase saveCandidate, MethodBase spreadCandidate, MethodBase paintCandidate, out bool full)
        {
            object?[] arguments = { saveCandidate, spreadCandidate, paintCandidate, false };
            string? reason = (string?)verify.Invoke(null, arguments);
            full = (bool)arguments[3]!;
            return reason;
        }
        string? contractReason = Verify(save, spread, paintCleared, out bool paintVerified);
        if (nativeSavesPerTexel)
        {
            Check(contractReason == null, "the installed game matches the coalescing contract");
            Console.WriteLine("Terrain coalescing: contract holds; PaintCleared body readable by reflection=" +
                paintVerified + "; full contract is re-checked inside Unity where it always is");
        }
        else
        {
            Check(contractReason != null && contractReason.Contains("spread"),
                "a game without the per-texel neighbour save must be refused, not patched");
            Console.WriteLine("Terrain coalescing: superseded by the game — " + contractReason +
                " The module declines to install and vanilla behaviour is retained.");
        }
        Check(Verify(save, doOperation, paintCleared, out _) != null,
            "a spread local function that does not save the neighbour is rejected");
        Check(Verify(Method(terrain, "Load", Type.EmptyTypes), spread, paintCleared, out _) != null,
            "a Save without the paint-hash gate is rejected");
        Check(Verify(save, spread, doOperation, out _) != null,
            "a PaintCleared that does not reach the spread local function is rejected");

        foreach (string name in new[] { "BeforePaintCleared", "AfterPaintCleared", "Flush" })
            Check(Method(coalescing, name).ReturnType == typeof(void),
                "TerrainSaveCoalescing." + name + " cannot replace a native result");
        Check(Method(coalescing, "BeforeSave").ReturnType == typeof(bool),
            "only the Save prefix decides whether the native write runs");
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
        Method(coalescing, "Install").Invoke(null, new object[] { config, log });
        Check(coalescing.GetProperty("Installed", Declared)!.GetValue(null) is false &&
            coalescing.GetProperty("Enabled", Declared)!.GetValue(null) is false,
            "the default configuration installs no coalescing");
        Check((string?)coalescing.GetProperty("Status", Declared)!.GetValue(null) == "disabled",
            "the default configuration reports the optimization as disabled");
        Check(Harmony.GetPatchInfo(save) == null, "the default configuration leaves TerrainComp.Save unpatched");

        ((ConfigEntry<bool>)config[new ConfigDefinition("Terrain", "CoalesceNeighbourSavesEnabled")]).Value = true;
        Method(coalescing, "Install").Invoke(null, new object[] { config, log });
        string coalescingStatus = (string?)coalescing.GetProperty("Status", Declared)!.GetValue(null) ?? "";
        if (coalescingStatus.StartsWith("unavailable", StringComparison.Ordinal))
            // Patching PaintCleared needs its method body, which this CLR cannot open.
            Console.WriteLine("STATIC ONLY terrain coalescing install: contract verified from metadata; " +
                "Harmony installation requires the Unity runtime");
        else
        {
            Check(coalescing.GetProperty("Installed", Declared)!.GetValue(null) is true,
                "an opted-in configuration installs the coalescing patches");
            Check(Harmony.GetPatchInfo(save)?.Prefixes.Count > 0, "the opted-in configuration prefixes TerrainComp.Save");
        }
        Method(coalescing, "Uninstall").Invoke(null, null);
        Check(Harmony.GetPatchInfo(save)?.Prefixes.Any(patch =>
            patch.owner.EndsWith("TerrainSaveCoalescing", StringComparison.Ordinal)) != true,
            "removal releases TerrainComp.Save");

        Method(attribution, "Install").Invoke(null, new object[] { config, log });
        string attributionStatus = (string?)attribution.GetProperty("Status", Declared)!.GetValue(null) ?? "";
        Check(attributionStatus == "installed" || attributionStatus.StartsWith("unavailable", StringComparison.Ordinal),
            "attribution reports either an installed or an unavailable probe, never a silent partial install");
        if (attributionStatus.StartsWith("unavailable", StringComparison.Ordinal))
            Console.WriteLine("STATIC ONLY terrain attribution install: Heightmap cannot be type-loaded outside Unity");
        Method(attribution, "Uninstall").Invoke(null, null);

        // Scope bookkeeping without any game object: a batch opens, closes and drains an
        // empty deferred set. The deferring path itself needs a live compiler and Unity.
        PropertyInfo installedProperty = coalescing.GetProperty("Installed", Declared)!;
        PropertyInfo enabledProperty = coalescing.GetProperty("Enabled", Declared)!;
        MethodInfo openScope = Method(coalescing, "BeforePaintCleared");
        MethodInfo closeScope = Method(coalescing, "AfterPaintCleared");
        Method(coalescing, "Reset").Invoke(null, null);
        object?[] scope = { null, null };
        enabledProperty.SetValue(null, false);
        openScope.Invoke(null, scope);
        Check(scope[1]!.GetType().GetField("Entered", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(scope[1]) is false, "a disabled optimization never opens a batch scope");
        closeScope.Invoke(null, new[] { scope[1] });
        installedProperty.SetValue(null, true);
        enabledProperty.SetValue(null, true);
        openScope.Invoke(null, scope);
        closeScope.Invoke(null, new[] { scope[1] });
        installedProperty.SetValue(null, false);
        enabledProperty.SetValue(null, false);
        var scopeGauges = (IList)Activator.CreateInstance(typeof(List<>)
            .MakeGenericType(plugin.GetType("BetterPerformance.Core.NumberValue", true)!))!;
        var scopeLabels = (IList)Activator.CreateInstance(typeof(List<>)
            .MakeGenericType(plugin.GetType("BetterPerformance.Core.TextValue", true)!))!;
        Method(coalescing, "Sample").Invoke(null, new object[] { scopeGauges, scopeLabels });
        PropertyInfo scopeName = plugin.GetType("BetterPerformance.Core.NumberValue", true)!.GetProperty("Name")!;
        PropertyInfo scopeValue = plugin.GetType("BetterPerformance.Core.NumberValue", true)!.GetProperty("Value")!;
        double Scoped(string name) => (double)(scopeValue.GetValue(scopeGauges.Cast<object>()
            .Single(entry => (string?)scopeName.GetValue(entry) == name)) ?? -1);
        Check(Scoped("terrain_batches") == 1, "exactly one batch is counted for one enabled paint operation");
        Check(Scoped("terrain_saves_deferred") == 0 && Scoped("terrain_saves_flushed") == 0 &&
            Scoped("terrain_coalesce_fallbacks") == 0,
            "closing an empty batch defers nothing, flushes nothing and never falls back");

        Type number = plugin.GetType("BetterPerformance.Core.NumberValue", true)!;
        Type text = plugin.GetType("BetterPerformance.Core.TextValue", true)!;
        var gauges = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(number))!;
        var labels = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(text))!;
        Method(coalescing, "Reset").Invoke(null, null);
        Method(attribution, "Reset").Invoke(null, null);
        Method(coalescing, "Sample").Invoke(null, new object[] { gauges, labels });
        Method(attribution, "Sample").Invoke(null, new object[] { gauges, labels });
        PropertyInfo gaugeName = number.GetProperty("Name")!, gaugeValue = number.GetProperty("Value")!;
        PropertyInfo labelName = text.GetProperty("Name")!;
        double Gauge(string name) => (double)(gaugeValue.GetValue(gauges.Cast<object>()
            .Single(entry => (string?)gaugeName.GetValue(entry) == name)) ?? -1);
        foreach (string name in new[] { "terrain_batches", "terrain_saves_deferred", "terrain_saves_flushed",
            "terrain_saves_gate_skipped", "terrain_saves_native_inside_batch", "terrain_coalesce_fallbacks",
            "heightmap_regen_terrain_op", "heightmap_regen_zone_spawn_full", "heightmap_regen_zone_spawn_ghost",
            "heightmap_regen_enable", "heightmap_regen_other", "heightmap_regen_frames_with_1",
            "heightmap_regen_frames_with_2_3", "heightmap_regen_frames_with_4_plus", "heightmap_regen_max_per_frame",
            "terrain_ops_total", "terrain_op_rpc_dispatches_total", "terrain_ops_per_frame_max",
            "terrain_op_neighbours_max", "terrain_op_neighbours_total", "heightmap_regen_other_thread_skips",
            "heightmap_regen_probe_failures" })
            Check(Gauge(name) == 0, name + " starts at zero after a reset");
        foreach (string name in new[] { "terrain_coalesce_status", "terrain_coalesce_enabled", "terrain_coalesce_poke",
            "terrain_regen_scope", "terrain_telemetry_status", "terrain_op_neighbours_semantics" })
            Check(labels.Cast<object>().Any(entry => (string?)labelName.GetValue(entry) == name),
                name + " documents the export in the capture itself");

        Console.WriteLine("Terrain: " + checks + " installed-game checks; coalescing status=" + coalescingStatus +
            "; attribution status=" + attributionStatus + "; byte parity requires a Unity session.");
        return checks;
    }

    private static int Calls(MethodDefinition method, string name) =>
        method.HasBody ? method.Body.Instructions.Count(instruction =>
            instruction.Operand is MethodReference called && called.Name == name) : -1;

    private static MethodInfo Method(Type type, string name, Type[]? parameters = null) =>
        (parameters == null ? type.GetMethod(name, Declared) : type.GetMethod(name, Declared, null, parameters, null))
        ?? throw new InvalidOperationException("Terrain: missing method " + type.Name + "." + name);

    private static FieldInfo Field(Type type, string name) => type.GetField(name, Declared)
        ?? throw new InvalidOperationException("Terrain: missing field " + type.Name + "." + name);
}
