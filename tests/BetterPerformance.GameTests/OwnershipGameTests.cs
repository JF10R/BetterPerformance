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

        // 3. Counting prefixes cannot observe, mutate or skip anything.
        Type ownership = plugin.GetType("BetterPerformance.OwnershipTelemetry", true)!;
        foreach (string name in new[] { "OnSetOwner", "OnRequestZdo", "OnRequestOwn", "OnRequestOpen" })
        {
            var prefix = ownership.GetMethod(name, Declared);
            Check(prefix != null && prefix.IsStatic && prefix.ReturnType == typeof(void) && prefix.GetParameters().Length == 0,
                name + " is a parameterless void prefix that cannot skip the original");
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
