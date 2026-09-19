using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

internal static class SectorInvalidationGameTests
{
    internal static int Run(Assembly game, Assembly plugin)
    {
        try { return Verify(game, plugin); }
        catch (TypeLoadException exception) when (exception.Message.Contains("Non-abstract, non-.cctor method in an interface"))
        {
            Console.WriteLine("UNAVAILABLE Sector invalidation contract checks: standalone .NET Framework cannot load native socket interfaces; use Unity runtime validation.");
            return 0;
        }
    }

    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

    private static int Verify(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Sector invalidation: " + message);
            checks++;
        }
        Type TypeOf(string name) => game.GetType(name, true)!;
        Type zdo = TypeOf("ZDO"), zdoMan = TypeOf("ZDOMan"), zdoId = TypeOf("ZDOID"), zone = TypeOf("ZoneSystem");
        Type scene = TypeOf("ZNetScene"), gameType = TypeOf("Game"), sectorIndex = TypeOf("ZoneSystem+SectorIndex");
        Type fix = plugin.GetType("BetterPerformance.SectorInvalidationFix", true)!;

        // 1. The patched method and the two members whose ORDER inside it is the defect.
        //    UnityEngine is not referenced here, so Vector3 comes from the game signature.
        var setPosition = zdo.GetMethods(Declared).SingleOrDefault(m => m.Name == "InternalSetPosition" && m.GetParameters().Length == 1);
        Check(setPosition != null && !setPosition.IsStatic && setPosition.IsPublic && setPosition.ReturnType == typeof(void),
            "ZDO.InternalSetPosition(Vector3) is a public instance void method");
        Type vector3 = setPosition!.GetParameters()[0].ParameterType;
        Check(vector3.FullName == "UnityEngine.Vector3", "InternalSetPosition still takes a UnityEngine.Vector3");
        var setSector = zdo.GetMethod("SetSector", Declared, null, new[] { sectorIndex }, null);
        Check(setSector != null && !setSector.IsStatic && setSector.ReturnType == typeof(void),
            "ZDO.SetSector(SectorIndex) is an instance void method");
        var position = zdo.GetField("m_position", Declared);
        Check(position != null && !position.IsStatic && position.FieldType == vector3, "ZDO.m_position is the instance position field");

        // 2. The defect itself: SetSector, and the peer invalidation it triggers, still
        //    run BEFORE the new position is stored. If this ever reverses, the game has
        //    fixed the bug and this module is dead weight.
        var body = PatchProcessor.GetOriginalInstructions(setPosition!);
        int call = body.FindIndex(i => i.Calls(setSector!));
        int store = body.FindIndex(i => i.opcode == System.Reflection.Emit.OpCodes.Stfld && Equals(i.operand, position));
        Check(call >= 0, "InternalSetPosition still calls SetSector");
        Check(store >= 0, "InternalSetPosition still stores m_position");
        Check(call < store, "SetSector still precedes the m_position store; if this fails the game writes the position first and DELETE this module as superseded");

        // 3. The module's own order test agrees, and rejects both the fixed order and a
        //    body it no longer recognises.
        var order = fix.GetMethod("SetsSectorBeforePosition", PrivateStatic)!;
        Check((bool)order.Invoke(null, new object[] { body, setSector!, position! })!, "the module detects the defective order in the live body");
        var reversed = Enumerable.Reverse(body).ToList();
        Check(!(bool)order.Invoke(null, new object[] { reversed, setSector!, position! })!, "the module reports the fixed order when the store precedes the call");
        Check(Rejects(order, new object[] { body.Where(i => !i.Calls(setSector!)).ToList(), setSector!, position! }),
            "a body without the SetSector call is rejected instead of patched");

        // 4. The native call the postfix repeats, and the peer test that makes the
        //    repetition meaningful.
        var manInvalidated = zdoMan.GetMethod("ZDOSectorInvalidated", Declared, null, new[] { zdo }, null);
        Check(manInvalidated != null && !manInvalidated.IsStatic && manInvalidated.IsPublic && manInvalidated.ReturnType == typeof(void),
            "ZDOMan.ZDOSectorInvalidated(ZDO) is a public instance void method");
        var sectorBody = PatchProcessor.GetOriginalInstructions(setSector!);
        Check(sectorBody.Any(i => i.Calls(manInvalidated!)), "ZDO.SetSector still raises ZDOMan.ZDOSectorInvalidated");
        var requireInvalidation = fix.GetMethod("RequireSectorInvalidation", PrivateStatic)!;
        Check(Rejects(requireInvalidation, new object[] { sectorBody.Where(i => !i.Calls(manInvalidated!)).ToList(), manInvalidated! }),
            "a SetSector that no longer invalidates peers is rejected");

        Type peer = game.GetType("ZDOMan+ZDOPeer", true)!;
        var peerInvalidated = peer.GetMethod("ZDOSectorInvalidated", Declared, null, new[] { zdo }, null);
        Check(peerInvalidated != null && !peerInvalidated.IsStatic && peerInvalidated.ReturnType == typeof(void),
            "ZDOPeer.ZDOSectorInvalidated(ZDO) is an instance void method");
        var getPosition = zdo.GetMethod("GetPosition", Declared, null, Type.EmptyTypes, null);
        Check(getPosition != null && !getPosition.IsStatic && getPosition.IsPublic && getPosition.ReturnType == vector3,
            "ZDO.GetPosition() is the public position accessor the peer test reads");
        var inActiveArea = scene.GetMethod("InActiveArea", Declared, null, new[] { vector3, vector3 }, null);
        Check(inActiveArea != null && inActiveArea.IsStatic && inActiveArea.IsPublic && inActiveArea.ReturnType == typeof(bool),
            "ZNetScene.InActiveArea(Vector3, Vector3) is a public static bool");
        var peerBody = PatchProcessor.GetOriginalInstructions(peerInvalidated!);
        Check(peerBody.Any(i => i.Calls(getPosition!)), "the peer test still reads the ZDO position, so writing it first changes the outcome");
        Check(peerBody.Any(i => i.Calls(inActiveArea!)), "the peer test still compares that position against the peer's active area");
        var requireReads = fix.GetMethod("RequirePeerReadsPosition", PrivateStatic)!;
        Check(Rejects(requireReads, new object[] { new List<CodeInstruction>(), getPosition!, inActiveArea! }),
            "a peer test that no longer reads the position is rejected");

        // 5. The state the telemetry and the effect depend on.
        var invalidSector = peer.GetField("m_invalidSector", Declared);
        Check(invalidSector != null && invalidSector.FieldType == typeof(HashSet<>).MakeGenericType(zdoId),
            "ZDOPeer.m_invalidSector is the HashSet<ZDOID> sent as the invalid-sector prefix");
        var tracked = peer.GetField("m_zdos", Declared);
        Check(tracked != null && tracked.FieldType.IsGenericType && tracked.FieldType.GetGenericArguments()[0] == zdoId,
            "ZDOPeer.m_zdos is still keyed by ZDOID, the membership the invalidation removes");
        var peers = zdoMan.GetField("m_peers", Declared);
        Check(peers != null && !peers.IsStatic && peers.FieldType.IsGenericType && peers.FieldType.GetGenericArguments()[0] == peer,
            "ZDOMan.m_peers is the list of ZDOPeer the invalidation walks");

        // 6. Sector identity: the cheap compare the hot path makes.
        Check(sectorIndex.IsValueType && !sectorIndex.IsEnum, "ZoneSystem.SectorIndex is a struct");
        var sector = sectorIndex.GetField("Sector", BindingFlags.Public | BindingFlags.Instance);
        Check(sector != null && sector.FieldType == typeof(uint), "SectorIndex exposes a public uint Sector");
        Check(sectorIndex.GetMethod("op_Equality", BindingFlags.Public | BindingFlags.Static) != null &&
            sectorIndex.GetMethod("op_Inequality", BindingFlags.Public | BindingFlags.Static) != null,
            "SectorIndex defines == and !=");
        var getSectorIndex = zone.GetMethod("GetSectorIndex", Declared, null, new[] { vector3 }, null);
        Check(getSectorIndex != null && getSectorIndex.IsStatic && getSectorIndex.IsPublic && getSectorIndex.ReturnType == sectorIndex,
            "ZoneSystem.GetSectorIndex(Vector3) is the public static sector projection");
        Check(zone.GetField("SectorZero", BindingFlags.Public | BindingFlags.Static)?.FieldType == sectorIndex,
            "ZoneSystem.SectorZero is the out-of-world sector");

        // 7. The native early-out the postfix mirrors: portal ZDOs keep their sector.
        Check(zdo.GetMethod("GetPrefab", Declared, null, Type.EmptyTypes, null)?.ReturnType == typeof(int),
            "ZDO.GetPrefab() identifies the prefab for the portal early-out");
        var portals = gameType.GetProperty("PortalPrefabHash", BindingFlags.Public | BindingFlags.Instance);
        Check(portals != null && portals.PropertyType == typeof(List<int>), "Game.PortalPrefabHash is the public List<int> SetSector skips");
        Check(sectorBody.Any(i => i.operand is MethodInfo getter && getter.Name == "get_PortalPrefabHash"),
            "SetSector still early-outs on portal prefabs, so the postfix must skip them too");

        // 8. Hooks cannot skip, replace or rewrite the native method.
        var before = fix.GetMethod("BeforePosition", PrivateStatic);
        Check(before != null && before.IsStatic && before.ReturnType == typeof(void), "BeforePosition is a static void prefix that cannot skip the original");
        var state = before!.GetParameters().SingleOrDefault(p => p.Name == "__state");
        Check(state != null && state.IsOut && state.ParameterType.GetElementType() == fix.GetNestedType("PositionState", BindingFlags.NonPublic | BindingFlags.Public),
            "the prefix hands the old position to the postfix through an out __state");
        var after = fix.GetMethod("AfterPosition", PrivateStatic);
        Check(after != null && after.IsStatic && after.ReturnType == typeof(void), "AfterPosition is a static void postfix");
        Check(after!.GetParameters().Any(p => p.Name == "__state" && !p.IsOut && p.ParameterType == fix.GetNestedType("PositionState", BindingFlags.NonPublic | BindingFlags.Public)),
            "the postfix consumes the captured state by value");

        // 9. Default configuration installs the contract but performs no re-issue.
        var verify = fix.GetMethod("Verify", PrivateStatic)!;
        Check(verify.Invoke(null, null) == null, "Verify accepts the live game shape and reports no supersession");
        var config = new ConfigFile(Path.Combine(Path.GetTempPath(), "bp-sector-" + Guid.NewGuid().ToString("N") + ".cfg"), false) { SaveOnConfigSet = false };
        var log = new BepInEx.Logging.ManualLogSource("SectorVerification");
        log.LogEvent += (_, entry) => Console.WriteLine(entry.Data);
        // Off means no patch at all, so the switch is set before Install to exercise the contract path.
        fix.GetMethod("Install", PrivateStatic)!.Invoke(null, new object[] { config, log });
        Check((string)fix.GetProperty("Status", PrivateStatic)!.GetValue(null)! == "disabled" &&
            Harmony.GetPatchInfo(setPosition!)?.Owners.Any(o => o.EndsWith("SectorInvalidationFix")) != true,
            "the default (off) configuration installs nothing on InternalSetPosition");
        var option = (ConfigEntry<bool>)config[new ConfigDefinition("Replication", "SectorInvalidationFixEnabled")];
        Check(!option.Value && !(bool)option.DefaultValue, "Replication.SectorInvalidationFixEnabled defaults to false");
        option.Value = true;
        fix.GetMethod("Install", PrivateStatic)!.Invoke(null, new object[] { config, log });
        string status = (string)fix.GetProperty("Status", PrivateStatic)!.GetValue(null)!;
        bool installed = (bool)fix.GetProperty("Installed", PrivateStatic)!.GetValue(null)!;
        Check(status == "installed" || status == "unavailable_unexpected_shape",
            "Install with the switch on reports a status instead of throwing (actual=" + status + ")");
        Check(installed == (bool)fix.GetProperty("Enabled", PrivateStatic)!.GetValue(null)!, "Enabled follows Installed once the switch is on");
        if (installed)
        {
            var patches = Harmony.GetPatchInfo(setPosition!);
            Check(patches != null && !patches.Transpilers.Any(p => p.owner.EndsWith("SectorInvalidationFix")), "InternalSetPosition: no IL rewrite");
            Check(patches!.Prefixes.Where(p => p.owner.EndsWith("SectorInvalidationFix")).All(p => p.PatchMethod.ReturnType == typeof(void)),
                "InternalSetPosition: the prefix cannot skip the original");
        }
        else Console.WriteLine("STATIC ONLY sector invalidation install: offline .NET Framework cannot patch this method; contract checks above still ran");

        // 10. Telemetry exists whether or not the fix runs, and drains per interval.
        Type number = plugin.GetType("BetterPerformance.Core.NumberValue", true)!, text = plugin.GetType("BetterPerformance.Core.TextValue", true)!;
        var sample = fix.GetMethod("Sample", PrivateStatic)!;
        Dictionary<string, double> Numbers(out List<string> names)
        {
            var gauges = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(number))!;
            var labels = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(text))!;
            sample.Invoke(null, new object[] { gauges, labels });
            names = labels.Cast<object>().Select(l => (string)text.GetProperty("Name")!.GetValue(l)!).ToList();
            return gauges.Cast<object>().ToDictionary(
                g => (string)number.GetProperty("Name")!.GetValue(g)!,
                g => (double)number.GetProperty("Value")!.GetValue(g)!);
        }
        var counters = Numbers(out var labelNames);
        foreach (string name in new[] { "sector_fix_sector_changes", "sector_fix_zone_jumps", "sector_fix_invalidations_added", "sector_fix_failures" })
            Check(counters.ContainsKey(name), name + " is exported");
        foreach (string name in new[] { "sector_fix_status", "sector_fix_enabled", "sector_fix_scope" })
            Check(labelNames.Contains(name), name + " is exported");
        Check(Numbers(out _).Values.All(value => value == 0), "Sample drains its interval counters");

        fix.GetMethod("Uninstall", PrivateStatic)!.Invoke(null, null);
        Check(!(bool)fix.GetProperty("Installed", PrivateStatic)!.GetValue(null)! &&
            (string)fix.GetProperty("Status", PrivateStatic)!.GetValue(null)! == "disabled", "Uninstall removes the hooks");
        // Harmony's unpatch rescans every patched method and a standalone CLR can throw there on an
        // unrelated Unity type; the module swallows that, so offline the patch may survive.
        if (Harmony.GetPatchInfo(setPosition!)?.Postfixes.Any(p => p.owner.EndsWith("SectorInvalidationFix")) == true)
            Console.WriteLine("STATIC ONLY sector invalidation unpatch: the hook survived UnpatchSelf on this CLR; contract checks above still ran");
        else checks++;

        Console.WriteLine("Sector invalidation: " + checks + " static game-contract checks; the removed ghost requires Unity runtime validation.");
        return checks;
    }

    private static bool Rejects(MethodInfo guard, object[] arguments)
    {
        try { guard.Invoke(null, arguments); return false; }
        catch (TargetInvocationException exception) when (exception.InnerException is InvalidOperationException) { return true; }
    }
}
