using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using Mono.Cecil;

// InventoryGui reaches Unity interfaces a standalone CLR cannot type-load, so every game
// signature and IL check here is read from Mono.Cecil metadata. Reflection is used only
// for the plugin's own configuration, counters and status.
internal static class GuiSoundGameTests
{
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

    internal static int Run(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("GUI group sound: " + message);
            checks++;
        }

        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(game.Location)!);
        var parameters = new ReaderParameters { AssemblyResolver = resolver };
        using var gameModule = ModuleDefinition.ReadModule(game.Location, parameters);
        using var pluginModule = ModuleDefinition.ReadModule(plugin.Location, parameters);
        TypeDefinition gui = gameModule.GetType("InventoryGui")
            ?? throw new InvalidOperationException("GUI group sound: InventoryGui is missing.");

        // 1. The exact private method the prefix hooks.
        MethodDefinition[] overloads = gui.Methods.Where(m => m.Name == "SetActiveGroup").ToArray();
        Check(overloads.Length == 2, "InventoryGui still declares exactly the two SetActiveGroup overloads");
        MethodDefinition target = overloads.Single(m => m.Parameters.Select(p => p.ParameterType.FullName)
            .SequenceEqual(new[] { "System.Int32", "System.Boolean" }));
        Check(!target.IsStatic && target.IsPrivate && target.ReturnType.FullName == "System.Void" && target.HasBody,
            "SetActiveGroup(int, bool) is a private instance void method with a managed body");

        // 2. The overload that must not be patched forwards to it, so patching both would
        //    evaluate the same call twice.
        MethodDefinition forwarding = overloads.Single(m => m != target);
        Check(forwarding.Parameters.Count == 2 && forwarding.Parameters[0].ParameterType.Name == "UIGroupHandler" &&
            forwarding.Parameters[1].ParameterType.FullName == "System.Boolean" && !forwarding.IsStatic,
            "SetActiveGroup(UIGroupHandler, bool) is the second overload");
        Check(forwarding.Body.Instructions.Any(i => (i.Operand as MethodReference)?.Resolve() == target),
            "SetActiveGroup(UIGroupHandler, bool) forwards to the int overload");

        // 3. The fields the prefix reads and the effect list it prevents from being created.
        FieldDefinition Field(string name) => gui.Fields.SingleOrDefault(f => f.Name == name)
            ?? throw new InvalidOperationException("GUI group sound: InventoryGui." + name + " is missing.");
        Check(Field("m_activeGroup").FieldType.FullName == "System.Int32" && !Field("m_activeGroup").IsStatic,
            "InventoryGui.m_activeGroup is an instance int");
        Check(Field("m_inventoryGroupCycling").FieldType.FullName == "System.Boolean" && !Field("m_inventoryGroupCycling").IsStatic,
            "InventoryGui.m_inventoryGroupCycling is an instance bool");
        FieldDefinition groups = Field("m_uiGroups");
        Check(groups.FieldType.IsArray && groups.FieldType.GetElementType().Name == "UIGroupHandler",
            "InventoryGui.m_uiGroups is a UIGroupHandler array");
        Check(Field("m_setActiveGroupEffects").FieldType.FullName == "EffectList",
            "InventoryGui.m_setActiveGroupEffects is an EffectList");

        // 4. The three click paths still route through a SetActiveGroup overload.
        foreach (string caller in new[] { "OnTabCraftPressed", "OnTabUpgradePressed", "OnSelectedRecipe" })
        {
            MethodDefinition method = gui.Methods.SingleOrDefault(m => m.Name == caller)
                ?? throw new InvalidOperationException("GUI group sound: InventoryGui." + caller + " is missing.");
            Check(method.HasBody, caller + " ships a managed body");
            Check(method.Body.Instructions.Any(i => (i.Operand as MethodReference)?.Name == "SetActiveGroup"),
                caller + " still calls a SetActiveGroup overload");
        }

        // 5. The int overload still reads m_activeGroup and still creates the group effect.
        var body = target.Body.Instructions;
        Check(body.Any(i => (i.Operand as FieldReference)?.Name == "m_activeGroup"),
            "SetActiveGroup(int, bool) still reads or writes m_activeGroup");
        Check(body.Any(i => (i.Operand as FieldReference)?.Name == "m_setActiveGroupEffects"),
            "SetActiveGroup(int, bool) still references m_setActiveGroupEffects");
        Check(body.Any(i => (i.Operand as MethodReference)?.Name == "Create" &&
                (i.Operand as MethodReference)?.DeclaringType.FullName == "EffectList"),
            "the group sound is still created through EffectList.Create");
        Check(body.Any(i => (i.Operand as MethodReference)?.Name == "Clamp"),
            "the non-cycling path still clamps the requested index, which the prefix reproduces");

        // 6. The prefix may only change playSound; it cannot skip the original.
        TypeDefinition module = pluginModule.GetType("BetterPerformance.GuiSoundDeduplication")!;
        MethodDefinition prefix = module.Methods.Single(m => m.Name == "Prefix");
        Check(prefix.IsStatic && prefix.ReturnType.FullName == "System.Void",
            "the plugin prefix is a static void method and cannot skip the original");
        Check(prefix.Parameters.Count(p => p.ParameterType.IsByReference) == 1 &&
            prefix.Parameters.Single(p => p.ParameterType.IsByReference).ParameterType.FullName == "System.Boolean&",
            "the prefix takes exactly one by-reference bool, the playSound argument");
        Check(prefix.Parameters.Any(p => p.ParameterType.FullName == "InventoryGui"), "the prefix receives the InventoryGui instance");
        // 7. Default configuration installs nothing and patches nothing.
        Type reflected = plugin.GetType("BetterPerformance.GuiSoundDeduplication", true)!;
        var config = new ConfigFile(Path.Combine(Path.GetTempPath(), "bp-guisound-" + Guid.NewGuid().ToString("N") + ".cfg"), false)
        { SaveOnConfigSet = false };
        var log = new BepInEx.Logging.ManualLogSource("GuiSoundVerification");
        log.LogEvent += (_, entry) => Console.WriteLine(entry.Data);
        reflected.GetMethod("Install", PrivateStatic)!.Invoke(null, new object[] { config, log });
        var option = (ConfigEntry<bool>)config[new ConfigDefinition("Gui", "DeduplicateGroupSoundEnabled")];
        Check(!option.Value && !(bool)option.DefaultValue, "DeduplicateGroupSoundEnabled defaults to false");
        Check(!(bool)reflected.GetProperty("Installed", PrivateStatic)!.GetValue(null)!, "default configuration installs no patch");
        Check(!(bool)reflected.GetProperty("Enabled", PrivateStatic)!.GetValue(null)!, "default configuration stays disabled");
        Check((string)reflected.GetProperty("Status", PrivateStatic)!.GetValue(null)! == "disabled", "default status reports disabled");
        Check(Harmony.GetAllPatchedMethods().SelectMany(m => Harmony.GetPatchInfo(m)!.Owners)
                .All(owner => !owner.EndsWith("GuiSoundDeduplication")),
            "default configuration leaves every method unpatched by this module");

        // 8. The counters and status label exist whether or not the module installed.
        Type number = plugin.GetType("BetterPerformance.Core.NumberValue", true)!, text = plugin.GetType("BetterPerformance.Core.TextValue", true)!;
        var gauges = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(number))!;
        var labels = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(text))!;
        reflected.GetMethod("Sample", PrivateStatic)!.Invoke(null, new object[] { gauges, labels });
        var names = gauges.Cast<object>().Select(g => (string)number.GetProperty("Name")!.GetValue(g)!).ToList();
        foreach (string name in new[] { "gui_group_sound_suppressed", "gui_group_sound_kept", "gui_sound_probe_failures" })
            Check(names.Contains(name), name + " is exported");
        var status = labels.Cast<object>().First(l => (string)text.GetProperty("Name")!.GetValue(l)! == "gui_sound_dedup_status");
        Check(new[] { "disabled", "installed", "unavailable", "patch_failed" }.Contains((string)text.GetProperty("Value")!.GetValue(status)!),
            "gui_sound_dedup_status reports one of the four defined states");

        reflected.GetMethod("Uninstall", PrivateStatic)!.Invoke(null, null);
        Console.WriteLine("GUI group sound: " + checks + " static game-contract checks from metadata; the audible result requires Unity runtime validation.");
        return checks;
    }
}
