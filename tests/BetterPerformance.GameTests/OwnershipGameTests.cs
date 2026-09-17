using System.Reflection;
using System.Reflection.Emit;

internal static class OwnershipGameTests
{
    internal static int Run(Assembly game, Assembly plugin)
    {
        try { return Verify(game, plugin); }
        catch (TypeLoadException exception) when (exception.Message.Contains("Non-abstract, non-.cctor method in an interface"))
        {
            Console.WriteLine("UNAVAILABLE Ownership/host/network game-contract checks: standalone .NET Framework cannot load native socket interfaces; use Unity runtime validation.");
            return 0;
        }
    }

    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static int Verify(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Ownership/host/network telemetry: " + message);
            checks++;
        }
        Type TypeOf(string name) => game.GetType(name, true)!;

        // 1. Hooked ownership and replication entry points.
        var zdo = TypeOf("ZDO"); var zdoMan = TypeOf("ZDOMan");
        var drop = TypeOf("ItemDrop"); var container = TypeOf("Container"); var zdoId = TypeOf("ZDOID");
        var hooks = new (Type Owner, string Name, Type[] Parameters)[] {
            (zdo, "SetOwner", new[] { typeof(long) }),
            (zdoMan, "RPC_RequestZDO", new[] { typeof(long), zdoId }),
            (drop, "RPC_RequestOwn", new[] { typeof(long) }),
            (container, "RPC_RequestOpen", new[] { typeof(long), typeof(long) })
        };
        foreach (var (owner, name, parameters) in hooks)
        {
            var method = owner.GetMethod(name, Declared, null, parameters, null);
            Check(method != null && !method.IsStatic && method.ReturnType == typeof(void) && method.GetMethodBody() != null,
                owner.Name + "." + name + ": installed game has the exact instance signature");
        }

        // 2. ZDOMan replication counters and queues.
        foreach (string name in new[] { "m_zdosSent", "m_zdosRecv", "m_zdosSentLastSec", "m_zdosRecvLastSec" })
        {
            var field = zdoMan.GetField(name, Declared);
            Check(field != null && !field.IsStatic && field.FieldType == typeof(int), "ZDOMan." + name + " is an instance int");
        }
        foreach (string name in new[] { "m_clientChangeQueue", "m_deadZDOs" })
        {
            var field = zdoMan.GetField(name, Declared);
            Check(field != null && !field.IsStatic && !field.FieldType.IsValueType, "ZDOMan." + name + " is a reference collection");
            var count = field!.FieldType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
            Check(count != null && count.PropertyType == typeof(int), "ZDOMan." + name + " exposes a public int Count");
        }
        var instance = zdoMan.GetProperty("instance", BindingFlags.Public | BindingFlags.Static);
        Check(instance != null && instance.PropertyType == zdoMan, "ZDOMan.instance is a public static accessor");

        // 2b. Caller-marker targets. Each one is the native method whose SetOwner writes the
        // split counters attribute; a changed signature must break here, not silently rebin.
        var zNetScene = TypeOf("ZNetScene");
        Type[] create = { typeof(List<>).MakeGenericType(zdo), typeof(int), typeof(int).MakeByRefType() };
        var scopes = new (Type Owner, string Name, Type[] Parameters)[] {
            (zdoMan, "ReleaseZDOS", new[] { typeof(float) }),
            (zdoMan, "RPC_ZDOData", new[] { TypeOf("ZRpc"), TypeOf("ZPackage") }),
            (zdoMan, "RemovePeer", new[] { TypeOf("ZNetPeer") }),
            (zNetScene, "CreateObjectsSorted", create),
            (zNetScene, "CreateDistantObjects", create)
        };
        foreach (var (owner, name, parameters) in scopes)
        {
            var method = owner.GetMethod(name, Declared, null, parameters, null);
            Check(method != null && !method.IsStatic && method.ReturnType == typeof(void) && method.GetMethodBody() != null,
                owner.Name + "." + name + ": installed game has the exact caller-marker signature");
        }
        // UnityEngine is not referenced by this project, so the pass signature is pinned by
        // name, arity and the parameter type's full name instead of a typeof.
        var passes = zdoMan.GetMethods(Declared).Where(method => method.Name == "ReleaseNearbyZDOS").ToArray();
        Check(passes.Length == 1 && !passes[0].IsStatic && passes[0].ReturnType == typeof(void) &&
            passes[0].GetParameters().Length == 2 &&
            passes[0].GetParameters()[0].ParameterType.FullName == "UnityEngine.Vector3" &&
            passes[0].GetParameters()[1].ParameterType == typeof(long),
            "ZDOMan.ReleaseNearbyZDOS(Vector3,long) is the single release pass the marker binds");
        var session = zdoMan.GetField("m_sessionID", Declared);
        Check(session != null && !session.IsStatic && session.FieldType == typeof(long),
            "ZDOMan.m_sessionID is the instance long that separates the server pass from a peer pass");
        var uid = zdo.GetField("m_uid", Declared);
        Check(uid != null && !uid.IsStatic && uid.FieldType == zdoId,
            "ZDO.m_uid is the ZDOID the release-cycle map keys on");
        var getOwner = zdo.GetMethod("GetOwner", Declared, null, Type.EmptyTypes, null);
        Check(getOwner != null && !getOwner.IsStatic && getOwner.ReturnType == typeof(long),
            "ZDO.GetOwner is the instance read the cycle map takes the pre-write owner from");

        // 3. Counting prefixes cannot observe, mutate or skip anything.
        Type ownership = plugin.GetType("BetterPerformance.OwnershipTelemetry", true)!;
        foreach (string name in new[] { "OnRequestZdo", "OnRequestOwn", "OnRequestOpen" })
        {
            var prefix = ownership.GetMethod(name, Declared);
            Check(prefix != null && prefix.IsStatic && prefix.ReturnType == typeof(void) && prefix.GetParameters().Length == 0,
                name + " is a parameterless void prefix that cannot skip the original");
        }
        // OnSetOwner reads the instance and the owner it is about to be given, so the split
        // counters can name the caller. Both are read-only: no by-ref parameter, no result.
        var setOwner = ownership.GetMethod("OnSetOwner", Declared);
        Check(setOwner != null && setOwner.IsStatic && setOwner.ReturnType == typeof(void),
            "OnSetOwner is a void prefix that cannot skip the original");
        Check(setOwner!.GetParameters().Length == 2 && setOwner.GetParameters()[0].ParameterType == zdo &&
            setOwner.GetParameters()[1].ParameterType == typeof(long),
            "OnSetOwner reads the ZDO and the incoming owner and nothing else");
        Check(!setOwner.GetParameters().Any(parameter => parameter.ParameterType.IsByRef),
            "OnSetOwner cannot mutate the owner the native call is about to write");
        foreach (string name in new[] { "BeforeReleaseZdos", "BeforeReleasePass", "BeforeZdoData",
            "BeforeDisconnectSweep", "BeforeInvalidPrefab", "AfterScope", "AfterReleaseZdos" })
        {
            var marker = ownership.GetMethod(name, Declared);
            Check(marker != null && marker.IsStatic && marker.ReturnType == typeof(void),
                name + " is a void marker that cannot skip or replace the original");
            Check(marker!.GetParameters().All(parameter => !parameter.ParameterType.IsByRef ||
                parameter.Name == "__state"),
                name + " moves only its own marker state");
        }

        // 4. Online backend and socket runtimes.
        var backend = TypeOf("ZNet").GetField("m_onlineBackend", Declared);
        Check(backend != null && backend.IsStatic && backend.FieldType.IsEnum && backend.FieldType.Name == "OnlineBackendType",
            "ZNet.m_onlineBackend is a static OnlineBackendType");
        foreach (string name in new[] { "Steamworks", "PlayFab", "None" })
            Check(Enum.IsDefined(backend!.FieldType, name), "OnlineBackendType defines " + name);
        var playFab = game.GetType("ZPlayFabSocket");
        var socket = game.GetType("ISocket", true)!;
        Check(playFab != null && socket.IsAssignableFrom(playFab), "ZPlayFabSocket is an ISocket runtime");
        Check(socket.IsAssignableFrom(TypeOf("ZSteamSocket")), "ZSteamSocket is an ISocket runtime");

        // 5. Steam connection description contract, and the verified Relayed flag value.
        var connection = TypeOf("ZSteamSocket").GetField("m_con", Declared)!;
        Assembly steam = connection.FieldType.Assembly;
        Type info = steam.GetType("Steamworks.SteamNetConnectionInfo_t", true)!;
        var flags = info.GetField("m_nFlags", Declared);
        Check(flags != null && flags.FieldType == typeof(int), "SteamNetConnectionInfo_t.m_nFlags is an int");
        var relayPop = info.GetField("m_idPOPRelay", Declared);
        Check(relayPop != null && relayPop.FieldType.Name == "SteamNetworkingPOPID", "SteamNetConnectionInfo_t.m_idPOPRelay is a POP id");
        Check(relayPop!.FieldType.GetField("m_SteamNetworkingPOPID", Declared)?.FieldType == typeof(uint),
            "SteamNetworkingPOPID packs its code in a uint");
        var relayed = steam.GetType("Steamworks.Constants", true)!.GetField("k_nSteamNetworkConnectionInfoFlags_Relayed", Declared);
        Check(relayed != null && (int)relayed.GetRawConstantValue()! == 16,
            "k_nSteamNetworkConnectionInfoFlags_Relayed is 16 (8 is _Fast)");
        foreach (string name in new[] { "Steamworks.SteamNetworkingSockets", "Steamworks.SteamGameServerNetworkingSockets" })
        {
            var method = steam.GetType(name, true)!.GetMethod("GetConnectionInfo", Declared,
                null, new[] { connection.FieldType, info.MakeByRefType() }, null);
            Check(method != null && method.IsStatic && method.ReturnType == typeof(bool),
                name + ".GetConnectionInfo(connection, out info) returns bool");
        }

        // 6. No peer identity or address may reach an export.
        Type steamTelemetry = plugin.GetType("BetterPerformance.SteamTelemetry", true)!;
        var forbidden = new[] { "m_addrRemote", "m_identityRemote", "m_szConnectionDescription_", "m_szConnectionDescription", "m_szEndDebug_", "m_szEndDebug", "m_peerID", "m_playerName", "m_playfabId" };
        foreach (var method in steamTelemetry.GetMethods(Declared))
            foreach (var member in References(method))
                Check(!forbidden.Contains(member.Name), method.Name + " does not read " + member.Name);

        // 7. Host telemetry stays read-only: no timer, priority or affinity writes.
        Type host = plugin.GetType("BetterPerformance.HostTelemetry", true)!;
        var banned = new[] { "timeBeginPeriod", "timeEndPeriod", "NtSetTimerResolution", "SetProcessAffinityMask", "SetThreadAffinityMask", "SetPriorityClass", "SetThreadPriority", "PowerSetActiveScheme", "PowerWriteFriendlyName" };
        foreach (var method in host.GetMethods(Declared))
        {
            Check(!banned.Contains(method.Name), "HostTelemetry declares no " + method.Name);
            foreach (var member in References(method))
                Check(!banned.Contains(member.Name) && !(member.Name == "set_PriorityClass" || member.Name == "set_ProcessorAffinity"),
                    method.Name + " does not call " + member.Name);
        }
        foreach (string name in new[] { "StartLabels", "Sample" })
            Check(host.GetMethod(name, Declared) != null, "HostTelemetry exposes " + name);

        // 8. A reset sample exports the documented gauge and label set at zero, so a renamed
        // or dropped counter breaks here instead of leaving a silent hole in a capture.
        string[] ownershipGauges = {
            "zdo_set_owner_calls", "zdo_set_owner_calls_release_to_zero", "zdo_set_owner_calls_release_claim_peer",
            "zdo_set_owner_calls_release_server_pass", "zdo_set_owner_calls_zdo_data_reapply",
            "zdo_set_owner_calls_disconnect_sweep", "zdo_set_owner_calls_invalid_prefab_destroy",
            "zdo_set_owner_calls_other", "release_cycles", "release_cycle_released", "release_cycle_reclaimed",
            "release_cycle_net_changes", "release_cycle_capacity_skips", "zdo_request_rpcs",
            "item_request_own_rpcs", "container_open_requests", "ownership_other_thread_skips",
            "ownership_probe_failures"
        };
        string[] ownershipLabels = {
            "ownership_telemetry_status", "ownership_scope", "ownership_caller_split_status",
            "ownership_markers_unavailable", "zdoman_counters_status"
        };
        Type number = plugin.GetType("BetterPerformance.Core.NumberValue", true)!;
        Type text = plugin.GetType("BetterPerformance.Core.TextValue", true)!;
        var numbers = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(number))!;
        var texts = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(text))!;
        ownership.GetProperty("Enabled", Declared)!.SetValue(null, false);
        ownership.GetMethod("Reset", Declared)!.Invoke(null, null);
        ownership.GetMethod("Sample", Declared)!.Invoke(null, new object[] { numbers, texts });
        PropertyInfo gaugeName = number.GetProperty("Name")!, gaugeValue = number.GetProperty("Value")!;
        PropertyInfo labelName = text.GetProperty("Name")!;
        foreach (string name in ownershipGauges)
        {
            object[] rows = numbers.Cast<object>().Where(row => (string?)gaugeName.GetValue(row) == name).ToArray();
            Check(rows.Length == 1, name + " is exported exactly once by a reset sample");
            Check((double)gaugeValue.GetValue(rows[0])! == 0, name + " starts at zero after a reset");
        }
        foreach (string name in ownershipLabels)
            Check(texts.Cast<object>().Any(row => (string?)labelName.GetValue(row) == name),
                name + " documents the export in the capture itself");

        Console.WriteLine("Ownership/host/network telemetry: " + checks + " static game-contract checks; counters require Unity runtime validation.");
        return checks;
    }

    // Metadata-only reader; no native game method is invoked by these checks.
    private static IEnumerable<MemberInfo> References(MethodInfo method)
    {
        var body = method.GetMethodBody();
        var bytes = body?.GetILAsByteArray();
        if (bytes == null) yield break;
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!).ToDictionary(op => op.Value);
        for (int offset = 0; offset < bytes.Length;)
        {
            int raw = bytes[offset++]; if (raw == 0xfe) raw = 0xfe00 | bytes[offset++];
            if (!opcodes.TryGetValue(unchecked((short)raw), out var opcode)) yield break;
            int length;
            switch (opcode.OperandType)
            {
                case OperandType.InlineNone: length = 0; break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: length = 1; break;
                case OperandType.InlineVar: length = 2; break;
                case OperandType.InlineI8:
                case OperandType.InlineR: length = 8; break;
                case OperandType.InlineSwitch: length = 4 + 4 * BitConverter.ToInt32(bytes, offset); break;
                default: length = 4; break;
            }
            if (opcode.OperandType == OperandType.InlineMethod || opcode.OperandType == OperandType.InlineField)
            {
                MemberInfo? member = null;
                try { member = method.Module.ResolveMember(BitConverter.ToInt32(bytes, offset), method.DeclaringType!.GetGenericArguments(), method.GetGenericArguments()); }
                catch { }
                if (member != null) yield return member;
            }
            offset += length;
        }
    }
}
