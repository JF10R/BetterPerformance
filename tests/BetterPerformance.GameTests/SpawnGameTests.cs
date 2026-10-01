using System.Collections;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Mono.Cecil;

// Spawn species/refusal observers. Signatures are read from metadata with Mono.Cecil because
// SpawnSystem methods take Player, which a standalone CLR cannot type-load. The runtime part
// installs what this CLR can resolve and proves every installed patch is observational.
internal static class SpawnGameTests
{
    private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
    private const string Data = "SpawnSystem/SpawnData", Vector = "UnityEngine.Vector3", Prefab = "UnityEngine.GameObject";

    // Same order as SpawnTelemetry.Targets(): owner, method, static, return, parameters.
    private static readonly (string Type, string Method, bool Static, string Returns, string[] Parameters)[] Contracts =
    {
        ("SpawnSystem", "UpdateSpawnList", false, "System.Void",
            new[] { "System.Collections.Generic.List`1<" + Data + ">", "System.DateTime", "System.Boolean", "System.String" }),
        ("SpawnSystem", "Spawn", false, "System.Void", new[] { Data, Vector, "System.Boolean" }),
        ("SpawnSystem", "GetNrOfZDOInstances", true, "System.Int32", new[] { Prefab, "System.Collections.Generic.List`1<ZDO>", "System.Boolean" }),
        ("SpawnSystem", "FindBaseSpawnPoint", false, "System.Boolean",
            new[] { Data, "System.Collections.Generic.List`1<Player>", Vector + "&", "Player&" }),
        ("SpawnSystem", "IsSpawnPointGood", false, "System.Boolean", new[] { Data, Vector + "&" }),
        ("EffectArea", "IsPointInsideArea", true, "EffectArea", new[] { Vector, "EffectArea/Type", "System.Single" }),
        ("SpawnSystem", "HaveInstanceInRange", true, "System.Boolean", new[] { Prefab, Vector, "System.Single" })
    };

    // The Mountain table and the day/night split read these members.
    private static readonly string[] DataFields =
    {
        "m_name", "m_enabled", "m_prefab", "m_biome", "m_maxSpawned", "m_spawnInterval", "m_spawnChance",
        "m_requiredGlobalKey", "m_requiredEnvironments", "m_requiredPersistentEvent", "m_spawnAtDay", "m_spawnAtNight",
        "m_insidePlayerBase", "m_groupSizeMin", "m_groupSizeMax", "m_minAltitude", "m_maxAltitude",
        "m_spawnRadiusMin", "m_spawnRadiusMax", "m_spawnDistance"
    };

    private static readonly string[] Statuses = { "listStatus", "speciesStatus", "capStatus", "pointStatus", "rejectStatus", "baseStatus", "crowdStatus" };

    internal static void Run(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Spawn telemetry: " + message);
            checks++;
        }

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(game.Location)!);
        using (ModuleDefinition module = ModuleDefinition.ReadModule(game.Location, new ReaderParameters { AssemblyResolver = resolver }))
        {
            foreach (var (typeName, methodName, isStatic, returns, parameters) in Contracts)
            {
                TypeDefinition? owner = module.GetType(typeName);
                Check(owner != null, typeName + " is present");
                Check(owner!.Methods.Count(m => m.Name == methodName && m.IsStatic == isStatic && m.HasBody &&
                    m.ReturnType.FullName == returns &&
                    m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters)) == 1,
                    typeName + "." + methodName + " keeps its exact native signature");
            }
            TypeDefinition spawn = module.GetType("SpawnSystem")!;
            TypeDefinition data = spawn.NestedTypes.Single(t => t.Name == "SpawnData");
            foreach (string field in DataFields)
                Check(data.Fields.Any(f => f.Name == field && f.IsPublic && !f.IsStatic), "SpawnData." + field + " is a public field");
            Check(data.Fields.Single(f => f.Name == "m_prefab").FieldType.FullName == Prefab, "SpawnData.m_prefab is the prefab GameObject");
            Check(spawn.Fields.Any(f => f.Name == "m_spawnLists" && f.IsPublic) &&
                spawn.Fields.Any(f => f.Name == "m_nospawn" && f.IsStatic && f.FieldType.FullName == "System.Boolean"),
                "SpawnSystem.m_spawnLists and static m_nospawn exist");
            Check(module.GetType("SpawnSystemList")!.Fields.Any(f => f.Name == "m_spawners" &&
                f.FieldType.FullName == "System.Collections.Generic.List`1<" + Data + ">"), "SpawnSystemList.m_spawners lists SpawnData");
            Check(module.GetType("EnvMan")!.Methods.Any(m => m.Name == "IsNight" && m.IsStatic && m.Parameters.Count == 0 &&
                m.ReturnType.FullName == "System.Boolean"), "EnvMan.IsNight() is static bool");
            Check(module.GetType("EffectArea")!.NestedTypes.Single(t => t.Name == "Type").Fields.Any(f => f.Name == "PlayerBase"),
                "EffectArea.Type.PlayerBase exists");
            // The cap derivation holds only if the cap break is the sole exit between the two calls.
            MethodDefinition list = spawn.Methods.Single(m => m.Name == "UpdateSpawnList");
            var calls = list.Body.Instructions.Where(i => i.Operand is MethodReference).Select(i => ((MethodReference)i.Operand).Name).ToList();
            int cap = calls.IndexOf("GetNrOfZDOInstances"), search = calls.IndexOf("FindBaseSpawnPoint");
            Check(cap >= 0 && search == cap + 1, "UpdateSpawnList calls FindBaseSpawnPoint right after GetNrOfZDOInstances (no call between)");
        }

        Type telemetry = plugin.GetType("BetterPerformance.SpawnTelemetry", true)!;
        var harmony = new Harmony("jf10r.BetterPerformance.SpawnGameTests");
        var availability = (IList)plugin.GetType("BetterPerformance.TimingHooks", true)!.GetField("Availability", StaticPrivate)!.GetValue(null)!;
        int initialAvailabilityCount = availability.Count;
        var offline = new List<string>();
        try
        {
            MethodInfo?[] targets;
            try { targets = (MethodInfo?[])telemetry.GetMethod("Targets", StaticPrivate)!.Invoke(null, null)!; }
            catch (TargetInvocationException exception) when (OfflineInterfaceLimitation(exception))
            { targets = new MethodInfo?[Contracts.Length]; offline.Add("Targets:type_load"); }
            Check(targets.Length == Contracts.Length, "plugin target list keeps its shape");
            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i] == null) { offline.Add(Contracts[i].Method + ":unresolved"); continue; }
                Check(targets[i]!.Name == Contracts[i].Method && targets[i]!.DeclaringType!.Name == Contracts[i].Type &&
                    targets[i]!.GetParameters().Length == Contracts[i].Parameters.Length, Contracts[i].Method + ": plugin resolves the contract method");
            }
            telemetry.GetMethod("Install", StaticPrivate)!.Invoke(null, new object[] { harmony, new ManualLogSource("SpawnGameTests"), true });
            int enabled = 0;
            foreach (string name in Statuses)
            {
                string value = (string)telemetry.GetField(name, StaticPrivate)!.GetValue(null)!;
                Check(value == "enabled" || value == "unavailable" || value == "patch_failed", name + " reports a known status");
                if (value == "enabled") enabled++;
                else offline.Add(name + ":" + value);
            }
            foreach (MethodInfo? method in targets)
            {
                if (method == null) continue;
                var patches = Harmony.GetPatchInfo(method);
                if (patches == null) continue;
                Check(!patches.Transpilers.Any(p => p.owner == harmony.Id), method.Name + ": no transpiler");
                // Void return: a prefix cannot skip, a finalizer cannot swallow; no by-ref __result: no result change.
                foreach (Patch patch in patches.Prefixes.Concat(patches.Postfixes).Concat(patches.Finalizers).Where(p => p.owner == harmony.Id))
                    Check(patch.PatchMethod.ReturnType == typeof(void) &&
                        !patch.PatchMethod.GetParameters().Any(p => p.Name == "__result" && p.ParameterType.IsByRef),
                        method.Name + ": " + patch.PatchMethod.Name + " is observational");
            }
            // Every observer is void and keeps native results. Read from metadata: hooks taking
            // SpawnSystem cannot be reflected on a standalone CLR.
            var pluginResolver = new DefaultAssemblyResolver();
            pluginResolver.AddSearchDirectory(Path.GetDirectoryName(game.Location)!);
            using (ModuleDefinition pluginModule = ModuleDefinition.ReadModule(plugin.Location, new ReaderParameters { AssemblyResolver = pluginResolver }))
            {
                TypeDefinition hooks = pluginModule.GetType("BetterPerformance.SpawnTelemetry")!;
                foreach (string hook in new[] { "ListPrefix", "ListFinalizer", "SpawnPrefix", "CapPostfix", "SearchPrefix", "SearchPostfix",
                    "PointPrefix", "PointFinalizer", "BasePostfix", "CrowdPostfix" })
                {
                    MethodDefinition[] found = hooks.Methods.Where(m => m.Name == hook).ToArray();
                    Check(found.Length == 1 && found[0].IsStatic && found[0].ReturnType.FullName == "System.Void" &&
                        !found[0].Parameters.Any(p => p.Name == "__result" && p.ParameterType.IsByReference),
                        hook + " cannot skip the original or change a native result");
                }
            }
            // No capture and no armed point check: nothing may be counted.
            Invoke(telemetry, "CapPostfix", new object?[] { null });
            Invoke(telemetry, "CrowdPostfix", new object?[] { null, true });
            Invoke(telemetry, "SearchPostfix", new object?[] { false, -1 });
            Invoke(telemetry, "PointFinalizer", new object?[] { false, -1, null });
            try { Invoke(telemetry, "BasePostfix", new object?[] { null, null }); }
            catch (TargetInvocationException exception) when (exception.InnerException is TypeLoadException) { offline.Add("BasePostfix:invoke"); }
            catch (TypeLoadException) { offline.Add("BasePostfix:invoke"); }
            Type number = plugin.GetType("BetterPerformance.Core.NumberValue", true)!;
            Type text = plugin.GetType("BetterPerformance.Core.TextValue", true)!;
            var gauges = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(number))!;
            var labels = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(text))!;
            telemetry.GetMethod("Sample", StaticPrivate)!.Invoke(null, new object[] { gauges, labels });
            PropertyInfo gaugeName = number.GetProperty("Name")!, gaugeValue = number.GetProperty("Value")!, labelName = text.GetProperty("Name")!;
            var rows = gauges.Cast<object>().Select(g => ((string)gaugeName.GetValue(g)!, (double)gaugeValue.GetValue(g)!)).ToArray();
            Check(rows.Length == 9 && rows.All(r => r.Item2 == 0 && r.Item1.StartsWith("spawn_", StringComparison.Ordinal)),
                "inactive hooks export exactly the nine zero totals");
            Check(labels.Cast<object>().Count(l => ((string)labelName.GetValue(l)!).EndsWith("_probe_status", StringComparison.Ordinal)) == 7,
                "every spawn probe reports its own status");
            Console.WriteLine("PASS spawn telemetry: " + checks + " checks; " + enabled + "/7 probes installed on this CLR");
            if (offline.Count > 0)
                Console.WriteLine("STATIC ONLY spawn probes " + string.Join(", ", offline) +
                    ": signature verified from metadata; offline CLR cannot load Player, so install is proven only in Unity");
        }
        finally
        {
            try { harmony.UnpatchSelf(); }
            catch (Exception exception) when (OfflineInterfaceLimitation(exception))
            { Console.WriteLine("STATIC ONLY spawn cleanup: standalone CLR cannot JIT Unity interface defaults; no game method invoked"); }
            telemetry.GetMethod("Reset", StaticPrivate)!.Invoke(null, null);
            while (availability.Count > initialAvailabilityCount) availability.RemoveAt(availability.Count - 1);
        }
    }

    private static void Invoke(Type telemetry, string name, object?[] arguments) =>
        telemetry.GetMethod(name, StaticPrivate)!.Invoke(null, arguments);

    private static bool OfflineInterfaceLimitation(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
            if (current is TypeLoadException && current.Message.IndexOf("non-abstract", StringComparison.OrdinalIgnoreCase) >= 0
                && current.Message.IndexOf("interface", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }
}
