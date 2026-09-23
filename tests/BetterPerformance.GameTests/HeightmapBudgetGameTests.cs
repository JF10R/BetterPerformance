using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using HarmonyLib;
using Mono.Cecil;
using Cil = Mono.Cecil.Cil;

// Heightmap implements IMonoUpdater, which a standalone CLR cannot type-load, so every game
// contract here is read from Mono.Cecil metadata. The deferral is safe only while the sets of
// readers and writers of the queue flag stay exactly the ones checked below; a game update that
// adds one fails here and needs a fresh safety review, not a wider check.
internal static class HeightmapBudgetGameTests
{
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

    internal static int Run(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Heightmap rebuild budget: " + message);
            checks++;
        }

        string managed = Path.GetDirectoryName(game.Location)!;
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managed);
        var parameters = new ReaderParameters { AssemblyResolver = resolver };
        using var gameModule = ModuleDefinition.ReadModule(game.Location, parameters);
        using var pluginModule = ModuleDefinition.ReadModule(plugin.Location, parameters);
        TypeDefinition Type(string name) => gameModule.GetType(name)
            ?? throw new InvalidOperationException("Heightmap rebuild budget: " + name + " is missing.");
        TypeDefinition heightmap = Type("Heightmap");
        MethodDefinition Method(TypeDefinition type, string name, params string[] parameterTypes) =>
            type.Methods.SingleOrDefault(m => m.Name == name &&
                m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameterTypes))
            ?? throw new InvalidOperationException("Heightmap rebuild budget: " + type.Name + "." + name +
                "(" + string.Join(", ", parameterTypes) + ") is missing.");
        bool Calls(MethodDefinition method, MethodDefinition target) =>
            method.HasBody && method.Body.Instructions.Any(i => (i.Operand as MethodReference)?.Resolve() == target);
        IEnumerable<MethodDefinition> AllMethods() => gameModule.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody);
        // Compiler-generated local functions carry a counter suffix; "<Outer>g__name|1_0" -> "Outer.name".
        string Name(MethodDefinition method)
        {
            var local = Regex.Match(method.Name, @"^<(\w+)>g__(\w+)\|");
            string type = method.DeclaringType.DeclaringType?.Name ?? method.DeclaringType.Name;
            return type + "::" + (local.Success ? local.Groups[1].Value + "." + local.Groups[2].Value : method.Name);
        }

        // 1. The queue flag and the members the hooks read.
        FieldDefinition flag = heightmap.Fields.SingleOrDefault(f => f.Name == "m_doLateUpdate")
            ?? throw new InvalidOperationException("Heightmap rebuild budget: Heightmap.m_doLateUpdate is missing.");
        Check(flag.IsPublic && !flag.IsStatic && flag.FieldType.FullName == "System.Int32", "m_doLateUpdate is a public instance int");
        foreach (var (field, type) in new[] { ("m_width", "System.Int32"), ("m_scale", "System.Single") })
            Check(heightmap.Fields.Any(f => f.Name == field && f.IsPublic && !f.IsStatic && f.FieldType.FullName == type),
                field + " is a public instance " + type);
        PropertyDefinition instances = heightmap.Properties.SingleOrDefault(p => p.Name == "Instances")
            ?? throw new InvalidOperationException("Heightmap rebuild budget: Heightmap.Instances is missing.");
        Check(instances.GetMethod != null && instances.GetMethod.IsStatic && instances.GetMethod.IsPublic &&
            instances.PropertyType.FullName == "System.Collections.Generic.List`1<IMonoUpdater>",
            "Heightmap.Instances is a public static List<IMonoUpdater>");
        PropertyDefinition distantLod = heightmap.Properties.SingleOrDefault(p => p.Name == "IsDistantLod")
            ?? throw new InvalidOperationException("Heightmap rebuild budget: Heightmap.IsDistantLod is missing.");
        Check(distantLod.PropertyType.FullName == "System.Boolean" && distantLod.GetMethod?.IsPublic == true,
            "IsDistantLod is a public bool property");
        MethodDefinition inside = Method(heightmap, "IsPointInside", "UnityEngine.Vector3", "System.Single");
        Check(!inside.IsStatic && inside.IsPublic && inside.ReturnType.FullName == "System.Boolean",
            "IsPointInside(Vector3, float) is the public square-overlap test ClutterSystem also uses");

        // 2. CustomLateUpdate runs Regenerate only when the flag is 2, and does nothing else.
        MethodDefinition lateUpdate = Method(heightmap, "CustomLateUpdate", "System.Single");
        Check(!lateUpdate.IsStatic && lateUpdate.IsPublic && lateUpdate.ReturnType.FullName == "System.Void",
            "CustomLateUpdate(float) is a public instance void method");
        MethodDefinition regenerate = Method(heightmap, "Regenerate");
        var late = lateUpdate.Body.Instructions;
        Check(late.Any(i => i.OpCode == Cil.OpCodes.Ldfld && (i.Operand as FieldReference)?.Resolve() == flag) &&
            late.Any(i => i.OpCode == Cil.OpCodes.Ldc_I4_2) &&
            late.Any(i => i.OpCode == Cil.OpCodes.Stfld && (i.Operand as FieldReference)?.Resolve() == flag),
            "CustomLateUpdate compares m_doLateUpdate with 2 and clears it");
        var lateCalls = late.Where(i => i.Operand is MethodReference).Select(i => ((MethodReference)i.Operand).Resolve()).ToList();
        Check(lateCalls.Count == 1 && lateCalls[0] == regenerate,
            "CustomLateUpdate makes exactly one call, Regenerate, so skipping it skips nothing else");
        MethodDefinition queued = Method(heightmap, "HaveQueuedRebuild");
        Check(!queued.IsStatic && queued.ReturnType.FullName == "System.Boolean" &&
            queued.Body.Instructions.Any(i => (i.Operand as FieldReference)?.Resolve() == flag) &&
            queued.Body.Instructions.Any(i => i.OpCode == Cil.OpCodes.Ldc_I4_2),
            "HaveQueuedRebuild() tests m_doLateUpdate == 2, so a deferred rebuild still reads as queued");

        // 3. Exactly these methods write and read the flag.
        var writers = AllMethods().Where(m => m.Body.Instructions.Any(i => i.OpCode == Cil.OpCodes.Stfld &&
            (i.Operand as FieldReference)?.Resolve() == flag)).Select(Name).OrderBy(n => n).ToList();
        Check(writers.SequenceEqual(new[] { "Heightmap::CustomLateUpdate", "Heightmap::LateUpdate", "Heightmap::Poke", "Heightmap::Regenerate" }),
            "only Poke sets the flag; LateUpdate, CustomLateUpdate and Regenerate clear it (actual: " + string.Join(", ", writers) + ")");
        var readers = AllMethods().Where(m => m.Body.Instructions.Any(i => i.OpCode != Cil.OpCodes.Stfld &&
            (i.Operand as FieldReference)?.Resolve() == flag)).Select(Name).OrderBy(n => n).ToList();
        Check(readers.SequenceEqual(new[] { "Heightmap::CustomLateUpdate", "Heightmap::HaveQueuedRebuild", "Heightmap::LateUpdate",
                "Heightmap::TrySetPaintOnlyRequest", "TerrainComp::PaintCleared.getMask" }),
            "the flag's readers are unchanged (actual: " + string.Join(", ", readers) + ")");

        // 4. Only TerrainModifier.PokeHeightmaps passes a non-constant delay (flag ? 2 : 0); every
        //    other Poke caller passes 0 (immediate) or 1 (Unity LateUpdate, not this queue).
        MethodDefinition poke = Method(heightmap, "Poke", "System.Int32", "System.Boolean");
        var variableDelay = new List<string>();
        foreach (MethodDefinition method in AllMethods())
            foreach (var call in method.Body.Instructions.Where(i => (i.Operand as MethodReference)?.Resolve() == poke))
            {
                // A constant delay is a literal 0 or 1 that no branch merges into.
                var delay = call.Previous?.Previous;
                bool constant = delay != null && (delay.OpCode == Cil.OpCodes.Ldc_I4_0 || delay.OpCode == Cil.OpCodes.Ldc_I4_1) &&
                    !method.Body.Instructions.Any(i => i.Operand == delay) &&
                    delay.Previous?.OpCode.FlowControl != Cil.FlowControl.Branch;
                if (!constant) variableDelay.Add(Name(method));
            }
        Check(variableDelay.Distinct().SequenceEqual(new[] { "TerrainModifier::PokeHeightmaps" }),
            "TerrainModifier.PokeHeightmaps is the only source of the late queue (actual: " + string.Join(", ", variableDelay) + ")");
        TypeDefinition modifier = Type("TerrainModifier");
        MethodDefinition pokeAll = modifier.Methods.Single(m => m.Name == "PokeHeightmaps");
        Check(Calls(Method(modifier, "Awake"), pokeAll) && Calls(Method(modifier, "OnDestroy"), pokeAll),
            "TerrainModifier.Awake and OnDestroy queue the rebuilds (location load and unload)");
        Check(pokeAll.Body.Instructions.Any(i => (i.Operand as MethodReference)?.Name == "GetAllHeightmaps"),
            "PokeHeightmaps reaches only GetAllHeightmaps, which excludes distant-LOD heightmaps");

        // 5. A zone's first build is synchronous in OnEnable, never through the queue.
        MethodDefinition enable = Method(heightmap, "OnEnable");
        Check(Calls(enable, regenerate) && !enable.Body.Instructions.Any(i => (i.Operand as FieldReference)?.Resolve() == flag),
            "OnEnable regenerates directly and never queues");

        // 6. The flushes that must still see a deferred rebuild as queued.
        MethodDefinition forceAll = Method(heightmap, "ForceGenerateAll");
        Check(forceAll.IsStatic && Calls(forceAll, queued) && Calls(forceAll, regenerate),
            "ForceGenerateAll regenerates every heightmap whose HaveQueuedRebuild is true");
        var forcers = AllMethods().Where(m => Calls(m, forceAll)).Select(Name).OrderBy(n => n).ToList();
        Check(forcers.SequenceEqual(new[] { "Ship::Awake", "SnapToGround::SnappAll", "Vagon::Awake" }),
            "ForceGenerateAll callers are unchanged (actual: " + string.Join(", ", forcers) + ")");
        MethodDefinition queuedNear = Method(heightmap, "HaveQueuedRebuild", "UnityEngine.Vector3", "System.Single");
        var waiters = AllMethods().Where(m => Calls(m, queuedNear)).Select(Name).ToList();
        Check(waiters.SequenceEqual(new[] { "ClutterSystem::IsHeightmapReady" }),
            "ClutterSystem.IsHeightmapReady is the only reader of the area test (actual: " + string.Join(", ", waiters) + ")");
        TypeDefinition clutter = Type("ClutterSystem");
        FieldDefinition clutterDistance = clutter.Fields.Single(f => f.Name == "m_distance");
        Check(clutterDistance.IsPublic && !clutterDistance.IsStatic && clutterDistance.FieldType.FullName == "System.Single" &&
            Method(clutter, "IsHeightmapReady").Body.Instructions.Any(i => (i.Operand as FieldReference)?.Resolve() == clutterDistance),
            "grass waits on queued heightmaps within ClutterSystem.m_distance, the radius the module keeps critical");

        // 7. The batch runs once per frame from MonoUpdaters.LateUpdate; nothing calls the method directly.
        MethodDefinition updaters = Method(Type("MonoUpdaters"), "LateUpdate");
        Check(updaters.Body.Instructions.Count(i => (i.Operand as MethodReference)?.Resolve() == instances.GetMethod) == 1,
            "MonoUpdaters.LateUpdate hands Heightmap.Instances to the late batch once per frame");
        Check(!AllMethods().Any(m => Calls(m, lateUpdate)), "no game method calls Heightmap.CustomLateUpdate directly");

        // 8. The plugin hooks: a skipping prefix and a void finalizer, neither naming Heightmap.
        TypeDefinition module = pluginModule.GetType("BetterPerformance.HeightmapRebuildBudget")
            ?? throw new InvalidOperationException("Heightmap rebuild budget: the module type is missing.");
        MethodDefinition before = module.Methods.Single(m => m.Name == "BeforeLateUpdate");
        Check(before.IsStatic && before.ReturnType.FullName == "System.Boolean" &&
            before.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "UnityEngine.MonoBehaviour", "System.Int64&" }),
            "BeforeLateUpdate is a static bool prefix (MonoBehaviour __instance, out long __state)");
        MethodDefinition after = module.Methods.Single(m => m.Name == "AfterLateUpdate");
        Check(after.IsStatic && after.ReturnType.FullName == "System.Void", "AfterLateUpdate is a void finalizer; it cannot swallow an exception");
        Check(module.Methods.All(m => m.ReturnType.FullName != "Heightmap" && m.Parameters.All(p => p.ParameterType.FullName != "Heightmap")) &&
            module.Fields.All(f => !f.FieldType.FullName.Contains("Heightmap>") && f.FieldType.FullName != "Heightmap"),
            "no module signature or field names Heightmap (standalone CLR type-load limit)");
        Check(!before.Body.Instructions.Any(i => i.OpCode == Cil.OpCodes.Stfld &&
                (i.Operand as FieldReference)?.Name == "m_doLateUpdate") &&
            !module.Methods.Any(m => m.HasBody && m.Body.Instructions.Any(i => (i.Operand as MethodReference)?.Name == "Regenerate")),
            "the module never writes the flag and never calls Regenerate itself");

        // 9. Default configuration installs nothing and patches nothing.
        Type reflected = plugin.GetType("BetterPerformance.HeightmapRebuildBudget", true)!;
        var config = new ConfigFile(Path.Combine(Path.GetTempPath(), "bp-heightmap-" + Guid.NewGuid().ToString("N") + ".cfg"), false)
        { SaveOnConfigSet = false };
        var log = new BepInEx.Logging.ManualLogSource("HeightmapBudgetVerification");
        log.LogEvent += (_, entry) => Console.WriteLine(entry.Data);
        reflected.GetMethod("Install", PrivateStatic)!.Invoke(null, new object[] { config, log });
        var enabled = (ConfigEntry<bool>)config[new ConfigDefinition("Terrain", "RebuildBudgetEnabled")];
        Check(!enabled.Value && !(bool)enabled.DefaultValue, "RebuildBudgetEnabled defaults to false");
        foreach (var (key, value, min, max) in new[] { ("RebuildBudgetMilliseconds", 4f, 1f, 50f),
            ("RebuildCriticalRadius", 80f, 32f, 512f), ("RebuildMaxDeferMilliseconds", 500f, 50f, 5000f) })
        {
            var entry = (ConfigEntry<float>)config[new ConfigDefinition("Terrain", key)];
            var range = (AcceptableValueRange<float>)entry.Description.AcceptableValues;
            Check((float)entry.DefaultValue == value && range.MinValue == min && range.MaxValue == max,
                key + " defaults to " + value + " within " + min + "-" + max);
        }
        Check(!config.ContainsKey(new ConfigDefinition("Terrain", "CoalesceNeighbourSavesEnabled")),
            "the stale CoalesceNeighbourSavesEnabled key is not reused");
        Check(!(bool)reflected.GetProperty("Installed", PrivateStatic)!.GetValue(null)!, "default configuration installs no patch");
        Check((string)reflected.GetProperty("Status", PrivateStatic)!.GetValue(null)! == "disabled", "default status reports disabled");
        Check(!(bool)reflected.GetMethod("SetEnabled", PrivateStatic)!.Invoke(null, new object[] { true })!,
            "the runtime toggle cannot enable a module that did not install");
        Check(Harmony.GetAllPatchedMethods().SelectMany(m => Harmony.GetPatchInfo(m)!.Owners)
                .All(owner => !owner.EndsWith("HeightmapRebuildBudget")),
            "default configuration leaves every method unpatched by this module");

        // 10. Counters and labels exist whether or not the module installed.
        Type number = plugin.GetType("BetterPerformance.Core.NumberValue", true)!, text = plugin.GetType("BetterPerformance.Core.TextValue", true)!;
        var gauges = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(number))!;
        var labels = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(text))!;
        reflected.GetMethod("Sample", PrivateStatic)!.Invoke(null, new object[] { gauges, labels });
        var exported = gauges.Cast<object>().Select(g => (string)number.GetProperty("Name")!.GetValue(g)!).ToList();
        Check(exported.SequenceEqual(new[] { "heightmap_budget_rebuilds_run", "heightmap_budget_deferred",
                "heightmap_budget_demoted_measured", "heightmap_budget_overdue_forced", "heightmap_budget_critical",
                "heightmap_budget_budgeted", "heightmap_budget_planned_frames", "heightmap_budget_frames_over_budget",
                "heightmap_budget_max_deferral_ms", "heightmap_budget_frame_spend_max_ms", "heightmap_budget_plan_ms_max",
                "heightmap_budget_queue_peak", "heightmap_budget_cost_estimate_ms", "heightmap_budget_probe_failures" }),
            "the gauge set is exactly the documented one (actual: " + string.Join(", ", exported) + ")");
        var labelNames = labels.Cast<object>().Select(l => (string)text.GetProperty("Name")!.GetValue(l)!).ToList();
        Check(labelNames.SequenceEqual(new[] { "heightmap_budget_status", "heightmap_budget_enabled", "heightmap_budget_ms" }),
            "the label set is exactly the documented one");

        reflected.GetMethod("Uninstall", PrivateStatic)!.Invoke(null, null);
        Console.WriteLine("Heightmap rebuild budget: " + checks + " static game-contract checks from metadata; the deferral itself is verified in play.");
        return checks;
    }
}
