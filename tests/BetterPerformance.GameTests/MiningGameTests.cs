using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using Mono.Cecil;
using Cil = Mono.Cecil.Cil;

// MineRock5 reaches Unity interfaces a standalone CLR cannot type-load, so every game
// signature and IL check here is read from Mono.Cecil metadata. Reflection is used only
// for the plugin's own configuration, counters and status.
internal static class MiningGameTests
{
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

    internal static int Run(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Mined-drop placement: " + message);
            checks++;
        }

        string managed = Path.GetDirectoryName(game.Location)!;
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managed);
        var parameters = new ReaderParameters { AssemblyResolver = resolver };
        using var gameModule = ModuleDefinition.ReadModule(game.Location, parameters);
        using var utilsModule = ModuleDefinition.ReadModule(Path.Combine(managed, "assembly_utils.dll"), parameters);
        using var pluginModule = ModuleDefinition.ReadModule(plugin.Location, parameters);
        TypeDefinition rock = gameModule.GetType("MineRock5")
            ?? throw new InvalidOperationException("Mined-drop placement: MineRock5 is missing.");
        TypeDefinition hitData = gameModule.GetType("HitData")
            ?? throw new InvalidOperationException("Mined-drop placement: HitData is missing.");

        // 1. The prefab field the module overrides, and the two positions it selects between.
        FieldDefinition center = rock.Fields.SingleOrDefault(f => f.Name == "m_hitEffectAreaCenter")
            ?? throw new InvalidOperationException("Mined-drop placement: MineRock5.m_hitEffectAreaCenter is missing.");
        Check(center.IsPublic && !center.IsStatic && center.FieldType.FullName == "System.Boolean",
            "MineRock5.m_hitEffectAreaCenter is a public instance bool");
        FieldDefinition point = hitData.Fields.SingleOrDefault(f => f.Name == "m_point")
            ?? throw new InvalidOperationException("Mined-drop placement: HitData.m_point is missing.");
        Check(!point.IsStatic && point.FieldType.FullName == "UnityEngine.Vector3",
            "HitData.m_point is the hit position the override selects");

        // 2. The lifecycle method the postfix hooks is the one that registers RPC_Damage.
        MethodDefinition awake = rock.Methods.SingleOrDefault(m => m.Name == "Awake" && m.Parameters.Count == 0)
            ?? throw new InvalidOperationException("Mined-drop placement: MineRock5.Awake() is missing.");
        Check(!awake.IsStatic && awake.IsPrivate && awake.ReturnType.FullName == "System.Void" && awake.HasBody,
            "MineRock5.Awake() is a private instance void lifecycle method");
        Check(awake.Body.Instructions.Any(i => i.OpCode == Cil.OpCodes.Ldstr && (string)i.Operand == "RPC_Damage"),
            "MineRock5.Awake still registers RPC_Damage, so the override precedes any damage");
        MethodDefinition rpc = rock.Methods.SingleOrDefault(m => m.Name == "RPC_Damage" &&
            m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "System.Int64", "HitData", "System.Int32" }))
            ?? throw new InvalidOperationException("Mined-drop placement: MineRock5.RPC_Damage(long, HitData, int) is missing.");
        Check(!rpc.IsStatic && rpc.IsPrivate && rpc.ReturnType.FullName == "System.Void",
            "MineRock5.RPC_Damage(long, HitData, int) is a private instance void handler");

        // 3. DamageArea still chooses between the collider bounds centre and the hit point.
        MethodDefinition damage = rock.Methods.SingleOrDefault(m => m.Name == "DamageArea" &&
            m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "System.Int32", "HitData" }))
            ?? throw new InvalidOperationException("Mined-drop placement: MineRock5.DamageArea(int, HitData) is missing.");
        Check(!damage.IsStatic && damage.ReturnType.FullName == "System.Boolean" && damage.HasBody,
            "MineRock5.DamageArea(int, HitData) is an instance method returning bool");
        var body = damage.Body.Instructions;
        Check(body.Any(i => (i.Operand as FieldReference)?.Name == "m_hitEffectAreaCenter"),
            "DamageArea still reads m_hitEffectAreaCenter");
        Check(body.Any(i => (i.Operand as FieldReference)?.Name == "m_point" &&
                (i.Operand as FieldReference)?.DeclaringType.FullName == "HitData"),
            "DamageArea still reads HitData.m_point for the alternative position");
        Check(body.Any(i => (i.Operand as MethodReference)?.Name == "get_center" &&
                (i.Operand as MethodReference)?.DeclaringType.FullName == "UnityEngine.Bounds"),
            "DamageArea still reads the collider bounds centre for the native position");
        Check(body.Any(i => (i.Operand as MethodReference)?.Name == "GetDropList"),
            "DamageArea still spawns the drop list at the position it selected");

        // 4. The structural-collapse path builds its own HitData at the chunk centre, so its
        //    drops are unchanged by the override.
        MethodDefinition support = rock.Methods.SingleOrDefault(m => m.Name == "CheckSupport" && m.Parameters.Count == 0)
            ?? throw new InvalidOperationException("Mined-drop placement: MineRock5.CheckSupport() is missing.");
        Check(support.Body.Instructions.Any(i => (i.Operand as MethodReference)?.Resolve() == damage),
            "CheckSupport still calls DamageArea with its own HitData");
        Check(support.Body.Instructions.Any(i => (i.Operand as FieldReference)?.Name == "m_point"),
            "CheckSupport still sets its own HitData.m_point, which the override never touches");

        // 5. The prefab-name helper the module uses to match configured names.
        TypeDefinition utils = utilsModule.GetType("Utils")
            ?? throw new InvalidOperationException("Mined-drop placement: Utils is missing from assembly_utils.");
        MethodDefinition prefabName = utils.Methods.SingleOrDefault(m => m.Name == "GetPrefabName" &&
            m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "System.String" }))
            ?? throw new InvalidOperationException("Mined-drop placement: Utils.GetPrefabName(string) is missing.");
        Check(prefabName.IsStatic && prefabName.IsPublic && prefabName.ReturnType.FullName == "System.String",
            "Utils.GetPrefabName(string) is a public static string method");

        // 6. The plugin hook is a postfix that cannot replace the lifecycle method.
        TypeDefinition module = pluginModule.GetType("BetterPerformance.MiningDropPlacement")!;
        MethodDefinition postfix = module.Methods.Single(m => m.Name == "AfterAwake");
        Check(postfix.IsStatic && postfix.ReturnType.FullName == "System.Void" && postfix.Parameters.Count == 1 &&
            postfix.Parameters[0].ParameterType.FullName == "MineRock5" && !postfix.Parameters[0].ParameterType.IsByReference,
            "AfterAwake is a static void postfix taking the MineRock5 instance by value");
        Check(module.Methods.Single(m => m.Name == "Override").Body.Instructions
                .Any(i => (i.Operand as FieldReference)?.Name == "m_hitEffectAreaCenter"),
            "the module writes only m_hitEffectAreaCenter, never a position or a drop table");

        // 7. Default configuration installs nothing, patches nothing and names one prefab.
        Type reflected = plugin.GetType("BetterPerformance.MiningDropPlacement", true)!;
        var config = new ConfigFile(Path.Combine(Path.GetTempPath(), "bp-mining-" + Guid.NewGuid().ToString("N") + ".cfg"), false)
        { SaveOnConfigSet = false };
        var log = new BepInEx.Logging.ManualLogSource("MiningVerification");
        log.LogEvent += (_, entry) => Console.WriteLine(entry.Data);
        reflected.GetMethod("Install", PrivateStatic)!.Invoke(null, new object[] { config, log });
        var option = (ConfigEntry<bool>)config[new ConfigDefinition("Mining", "DropAtHitPointEnabled")];
        Check(!option.Value && !(bool)option.DefaultValue, "DropAtHitPointEnabled defaults to false");
        var names = (ConfigEntry<string>)config[new ConfigDefinition("Mining", "DropAtHitPointPrefabs")];
        Check((string)names.DefaultValue == "rock4_copper_frac", "DropAtHitPointPrefabs defaults to the single researched prefab");
        Check(!(bool)reflected.GetProperty("Installed", PrivateStatic)!.GetValue(null)!, "default configuration installs no patch");
        Check(!(bool)reflected.GetProperty("Enabled", PrivateStatic)!.GetValue(null)!, "default configuration stays disabled");
        Check((string)reflected.GetProperty("Status", PrivateStatic)!.GetValue(null)! == "disabled", "default status reports disabled");
        Check((int)reflected.GetProperty("TrackedCount", PrivateStatic)!.GetValue(null)! == 0, "default configuration tracks no instance");
        Check(Harmony.GetAllPatchedMethods().SelectMany(m => Harmony.GetPatchInfo(m)!.Owners)
                .All(owner => !owner.EndsWith("MiningDropPlacement")),
            "default configuration leaves every method unpatched by this module");

        // 8. Counters and labels exist whether or not the module installed.
        Type number = plugin.GetType("BetterPerformance.Core.NumberValue", true)!, text = plugin.GetType("BetterPerformance.Core.TextValue", true)!;
        var gauges = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(number))!;
        var labels = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(text))!;
        reflected.GetMethod("Sample", PrivateStatic)!.Invoke(null, new object[] { gauges, labels });
        var exported = gauges.Cast<object>().Select(g => (string)number.GetProperty("Name")!.GetValue(g)!).ToList();
        foreach (string name in new[] { "mining_hitpoint_instances_applied", "mining_hitpoint_instances_restored",
            "mining_hitpoint_instances_seen", "mining_hitpoint_probe_failures" })
            Check(exported.Contains(name), name + " is exported");
        var labelNames = labels.Cast<object>().Select(l => (string)text.GetProperty("Name")!.GetValue(l)!).ToList();
        Check(labelNames.Contains("mining_hitpoint_status") && labelNames.Contains("mining_hitpoint_prefabs"),
            "the status and configured-prefab labels are exported");

        reflected.GetMethod("Uninstall", PrivateStatic)!.Invoke(null, null);
        Console.WriteLine("Mined-drop placement: " + checks + " static game-contract checks from metadata; the hit-effect look requires a disposable-world A/B.");
        return checks;
    }
}
