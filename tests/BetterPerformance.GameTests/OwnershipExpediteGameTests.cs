using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

internal static class OwnershipExpediteGameTests
{
    internal static int Run(Assembly game, Assembly plugin)
    {
        try { return Verify(game, plugin); }
        catch (TypeLoadException exception) when (exception.Message.Contains("Non-abstract, non-.cctor method in an interface"))
        {
            Console.WriteLine("UNAVAILABLE Owner-grant expedite contract checks: standalone .NET Framework cannot load native socket interfaces; use Unity runtime validation.");
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
            if (!condition) throw new InvalidOperationException("Owner-grant expedite: " + message);
            checks++;
        }
        Type TypeOf(string name) => game.GetType(name, true)!;
        Type zdo = TypeOf("ZDO"), zdoMan = TypeOf("ZDOMan"), zdoId = TypeOf("ZDOID");
        Type net = TypeOf("ZNet"), peer = TypeOf("ZNetPeer"), rpc = TypeOf("ZRpc"), package = TypeOf("ZPackage");
        Type expedite = plugin.GetType("BetterPerformance.OwnershipExpedite", true)!;

        // 1. Exact game signatures the module hooks and calls.
        var incoming = zdoMan.GetMethod("RPC_ZDOData", Declared, null, new[] { rpc, package }, null);
        Check(incoming != null && !incoming.IsStatic && incoming.ReturnType == typeof(void) && incoming.GetMethodBody() != null,
            "ZDOMan.RPC_ZDOData(ZRpc, ZPackage) is an instance void handler");
        var apply = zdo.GetMethod("SetOwnerInternal", Declared, null, new[] { typeof(long) }, null);
        Check(apply != null && !apply.IsStatic && apply.ReturnType == typeof(void), "ZDO.SetOwnerInternal(long) is an instance void method");
        var forceSend = zdoMan.GetMethod("ForceSendZDO", Declared, null, new[] { typeof(long), zdoId }, null);
        Check(forceSend != null && !forceSend.IsStatic && forceSend.IsPublic && forceSend.ReturnType == typeof(void),
            "ZDOMan.ForceSendZDO(long, ZDOID) is the public per-peer forced send");
        var getOwner = zdo.GetMethod("GetOwner", Declared, null, Type.EmptyTypes, null);
        Check(getOwner != null && !getOwner.IsStatic && getOwner.ReturnType == typeof(long), "ZDO.GetOwner() returns the current owner id");
        var sessionId = zdoMan.GetMethod("GetSessionID", Declared, null, Type.EmptyTypes, null);
        Check(sessionId != null && sessionId.IsStatic && sessionId.ReturnType == typeof(long), "ZDOMan.GetSessionID() is a static long");
        var getPeer = net.GetMethod("GetPeer", Declared, null, new[] { typeof(long) }, null);
        Check(getPeer != null && !getPeer.IsStatic && getPeer.ReturnType == peer, "ZNet.GetPeer(long) resolves a connected peer");
        var getPeers = net.GetMethod("GetPeers", Declared, null, Type.EmptyTypes, null);
        Check(getPeers != null && !getPeers.IsStatic, "ZNet.GetPeers() exposes the peer list for sender resolution");
        Check(net.GetMethod("IsServer", Declared, null, Type.EmptyTypes, null)?.ReturnType == typeof(bool), "ZNet.IsServer() gates the module server-side");
        Check(peer.GetField("m_rpc", Declared)?.FieldType == rpc && peer.GetField("m_uid", Declared)?.FieldType == typeof(long),
            "ZNetPeer exposes m_rpc and m_uid for sender resolution");

        // 2. The forced-send queue still applies the native revision test, so an
        //    expedited ZDO is never sent to a peer that already holds the revision.
        var addForce = zdoMan.GetMethod("AddForceSendZdos", Declared);
        Check(addForce != null, "ZDOMan.AddForceSendZdos exists");
        var shouldSend = game.GetType("ZDOMan+ZDOPeer", true)!.GetMethod("ShouldSend", Declared);
        Check(shouldSend != null && shouldSend.ReturnType == typeof(bool), "ZDOPeer.ShouldSend(ZDO) is the native revision test");
        var forceInstructions = PatchProcessor.GetOriginalInstructions(addForce!);
        Check(forceInstructions.Any(i => i.Calls(shouldSend!)), "AddForceSendZdos still gates each forced id on ShouldSend");
        Check(forceInstructions.Any(i => i.operand is MethodInfo insert && insert.Name == "Insert"), "AddForceSendZdos still inserts at the head of the sync list");

        // 3. The owner-apply contract: exactly two SetOwnerInternal sites in RPC_ZDOData.
        var body = PatchProcessor.GetOriginalInstructions(incoming!);
        Check(body.Count(i => i.Calls(apply!)) == 2, "RPC_ZDOData applies an incoming owner at exactly two sites");
        var require = expedite.GetMethod("RequireApplySites", PrivateStatic)!;
        var malformed = body.Where(i => !i.Calls(apply!)).ToList();
        bool rejected = false;
        try { require.Invoke(null, new object[] { malformed, apply! }); }
        catch (TargetInvocationException exception) when (exception.InnerException is InvalidOperationException) { rejected = true; }
        Check(rejected, "a changed owner-apply layout is rejected instead of hooked");

        // 4. Hooks cannot skip, replace or rewrite the native handler.
        foreach (var (name, returns) in new[] { ("BeforeIncoming", typeof(void)), ("AfterIncoming", typeof(void)), ("BeforeSetOwner", typeof(void)), ("AfterSetOwner", typeof(void)) })
        {
            var hook = expedite.GetMethod(name, PrivateStatic);
            Check(hook != null && hook.IsStatic && hook.ReturnType == returns, name + " is a static void hook that cannot skip the original");
        }
        Check(expedite.GetMethod("AfterIncoming", PrivateStatic)!.GetParameters().All(p => p.Name != "__exception"),
            "the incoming finalizer never observes or swallows an exception");

        // 5. Default configuration: installed observers, no forced sends.
        var config = new ConfigFile(Path.Combine(Path.GetTempPath(), "bp-expedite-" + Guid.NewGuid().ToString("N") + ".cfg"), false) { SaveOnConfigSet = false };
        var log = new BepInEx.Logging.ManualLogSource("ExpediteVerification");
        log.LogEvent += (_, entry) => Console.WriteLine(entry.Data);
        expedite.GetMethod("Install", PrivateStatic)!.Invoke(null, new object[] { config, log });
        string status = (string)expedite.GetProperty("Status", PrivateStatic)!.GetValue(null)!;
        bool installed = (bool)expedite.GetProperty("Installed", PrivateStatic)!.GetValue(null)!;
        Check(status == "installed" || status == "unavailable", "Install reports installed or unavailable, never an exception (actual=" + status + ")");
        Check(!(bool)expedite.GetProperty("Enabled", PrivateStatic)!.GetValue(null)!, "default configuration performs no forced sends");
        var option = (ConfigEntry<bool>)config[new ConfigDefinition("Ownership", "ExpediteOwnerGrantsEnabled")];
        Check(!option.Value && !(bool)option.DefaultValue, "ExpediteOwnerGrantsEnabled defaults to false");
        var cap = (ConfigEntry<int>)config[new ConfigDefinition("Ownership", "ExpediteMaxPerSecond")];
        Check((int)cap.DefaultValue == 256, "the per-second forced-insert bound has a finite default");
        if (installed)
        {
            foreach (var target in new[] { incoming!, apply! })
            {
                var patches = Harmony.GetPatchInfo(target);
                Check(patches != null && !patches.Transpilers.Any(p => p.owner.EndsWith("OwnershipExpedite")), target.Name + ": no IL rewrite");
                Check(patches!.Prefixes.Where(p => p.owner.EndsWith("OwnershipExpedite")).All(p => p.PatchMethod.ReturnType == typeof(void)),
                    target.Name + ": prefix cannot skip the original");
            }
        }
        else Console.WriteLine("STATIC ONLY expedite install: offline .NET Framework cannot patch this handler; contract checks above still ran");

        // 6. The per-second bound and the duplicate guard, exercised directly.
        var admit = expedite.GetMethod("Admit", PrivateStatic)!;
        expedite.GetMethod("Reset", PrivateStatic)!.Invoke(null, null);
        cap.Value = 3;
        object MakeId(uint id) => Activator.CreateInstance(zdoId, new object[] { 7L, id })!;
        Check((bool)admit.Invoke(null, new[] { (object)11L, MakeId(1) })!, "first grant inside the bound is admitted");
        Check(!(bool)admit.Invoke(null, new[] { (object)11L, MakeId(1) })!, "the same peer/ZDO pair is not forced twice in one window");
        Check((bool)admit.Invoke(null, new[] { (object)12L, MakeId(1) })!, "the same ZDO for a different peer is a distinct forced send");
        Check((bool)admit.Invoke(null, new[] { (object)11L, MakeId(2) })!, "a different ZDO for the same peer is admitted");
        Check(!(bool)admit.Invoke(null, new[] { (object)11L, MakeId(3) })!, "the per-second bound stops further forced inserts");
        cap.Value = 0;
        Check(!(bool)admit.Invoke(null, new[] { (object)13L, MakeId(4) })!, "a non-positive bound admits nothing");

        // 7. Counters exist whether or not the optimization runs.
        Type number = plugin.GetType("BetterPerformance.Core.NumberValue", true)!, text = plugin.GetType("BetterPerformance.Core.TextValue", true)!;
        var sample = expedite.GetMethod("Sample", PrivateStatic)!;
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
        foreach (string name in new[] { "ownership_grants_observed", "ownership_grants_expedited", "ownership_grants_skipped_sender",
            "ownership_grants_skipped_offline", "ownership_grants_skipped_unchanged", "ownership_grants_skipped_capacity",
            "ownership_grants_skipped_duplicate", "ownership_expedite_failures" })
            Check(counters.ContainsKey(name), name + " is exported");
        Check(labelNames.Contains("ownership_expedite_status"), "ownership_expedite_status is exported");
        Check(counters["ownership_grants_skipped_capacity"] == 2, "capacity skips were counted by the bound above");
        Check(counters["ownership_grants_skipped_duplicate"] == 1, "the duplicate guard was counted");
        Check(Numbers(out _).Values.All(value => value == 0), "Sample drains its interval counters");

        // 8. The ZDO count gauge added to the existing ownership telemetry.
        var objects = zdoMan.GetField("m_objectsByID", Declared);
        Check(objects != null && !objects.IsStatic && objects.FieldType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance)?.PropertyType == typeof(int),
            "ZDOMan.m_objectsByID exposes a public int Count for zdoman_objects_total");

        expedite.GetMethod("Uninstall", PrivateStatic)!.Invoke(null, null);
        Check(!(bool)expedite.GetProperty("Installed", PrivateStatic)!.GetValue(null)! &&
            (string)expedite.GetProperty("Status", PrivateStatic)!.GetValue(null)! == "disabled", "Uninstall removes the observers");
        Check(Harmony.GetPatchInfo(apply!)?.Prefixes.Any(p => p.owner.EndsWith("OwnershipExpedite")) != true, "no expedite patch survives Uninstall");

        Console.WriteLine("Owner-grant expedite: " + checks + " static game-contract checks; forced-send effect requires Unity runtime validation.");
        return checks;
    }
}
