using System.Reflection;
using Mono.Cecil;
using Cil = Mono.Cecil.Cil;

// ZSteamSocket reaches native socket interfaces a standalone CLR cannot type-load, so every
// game and Steamworks contract here is read from Mono.Cecil metadata, never through
// reflection on a loaded type.
internal static class HostNetGameTests
{
    internal static int Run(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Host net telemetry: " + message);
            checks++;
        }

        string managed = Path.GetDirectoryName(game.Location)!;
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managed);
        var parameters = new ReaderParameters { AssemblyResolver = resolver };
        using var gameModule = ModuleDefinition.ReadModule(game.Location, parameters);
        using var steamModule = ModuleDefinition.ReadModule(Path.Combine(managed, "com.rlabrecque.steamworks.net.dll"), parameters);
        using var pluginModule = ModuleDefinition.ReadModule(plugin.Location, parameters);

        TypeDefinition Require(ModuleDefinition module, string name) => module.GetType(name)
            ?? throw new InvalidOperationException("Host net telemetry: " + name + " is missing.");
        MethodDefinition Method(TypeDefinition type, string name, params string[] parameterTypes) =>
            type.Methods.SingleOrDefault(m => m.Name == name &&
                m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameterTypes))
            ?? throw new InvalidOperationException("Host net telemetry: " + type.Name + "." + name + " with the expected parameters is missing.");
        FieldDefinition Field(TypeDefinition type, string name) => type.Fields.SingleOrDefault(f => f.Name == name)
            ?? throw new InvalidOperationException("Host net telemetry: " + type.FullName + "." + name + " is missing.");

        TypeDefinition net = Require(gameModule, "ZNet"), peer = Require(gameModule, "ZNetPeer");
        TypeDefinition steamSocket = Require(gameModule, "ZSteamSocket"), zdoMan = Require(gameModule, "ZDOMan");
        TypeDefinition clientApi = Require(steamModule, "Steamworks.SteamNetworkingSockets");
        TypeDefinition serverApi = Require(steamModule, "Steamworks.SteamGameServerNetworkingSockets");
        TypeDefinition realTimeStatus = Require(steamModule, "Steamworks.SteamNetConnectionRealTimeStatus_t");

        // 1. The peer list the server path walks, and the two predicates that pick the API.
        FieldDefinition peers = Field(net, "m_peers");
        Check(!peers.IsStatic && peers.FieldType.FullName == "System.Collections.Generic.List`1<ZNetPeer>",
            "ZNet.m_peers is an instance List<ZNetPeer>");
        foreach (string predicate in new[] { "IsServer", "IsDedicated" })
        {
            MethodDefinition method = Method(net, predicate);
            Check(!method.IsStatic && method.ReturnType.FullName == "System.Boolean",
                "ZNet." + predicate + "() is an instance bool the sampler branches on");
        }

        // 2. The native call the fallback path keeps using.
        MethodDefinition netStats = Method(net, "GetNetStats",
            "System.Single&", "System.Single&", "System.Int32&", "System.Single&", "System.Single&");
        Check(!netStats.IsStatic && netStats.ReturnType.FullName == "System.Void" && netStats.HasBody,
            "ZNet.GetNetStats(out float, out float, out int, out float, out float) is the native fallback");

        // 3. Peer readiness and the socket the connection handle hides behind.
        FieldDefinition socket = Field(peer, "m_socket");
        Check(!socket.IsStatic && socket.FieldType.FullName == "ISocket", "ZNetPeer.m_socket is an instance ISocket");
        MethodDefinition ready = Method(peer, "IsReady");
        Check(!ready.IsStatic && ready.ReturnType.FullName == "System.Boolean", "ZNetPeer.IsReady() decides which peers count");
        Check(steamSocket.Interfaces.Any(i => i.InterfaceType.FullName == "ISocket"), "ZSteamSocket still implements ISocket");
        FieldDefinition connection = Field(steamSocket, "m_con");
        Check(!connection.IsStatic && connection.IsPrivate && connection.FieldType.FullName == "Steamworks.HSteamNetConnection",
            "ZSteamSocket.m_con is the private HSteamNetConnection the sampler reads");

        // 4. Both Steamworks entry points, with the signature the plugin calls.
        string[] statusParameters =
        {
            "Steamworks.HSteamNetConnection",
            "Steamworks.SteamNetConnectionRealTimeStatus_t&",
            "System.Int32",
            "Steamworks.SteamNetConnectionRealTimeLaneStatus_t&"
        };
        foreach (TypeDefinition api in new[] { clientApi, serverApi })
        {
            MethodDefinition status = Method(api, "GetConnectionRealTimeStatus", statusParameters);
            Check(status.IsStatic && status.IsPublic && status.ReturnType.FullName == "Steamworks.EResult",
                api.Name + ".GetConnectionRealTimeStatus returns EResult with the expected parameters");
        }
        var statusFields = new[]
        {
            ("m_nPing", "System.Int32"), ("m_flConnectionQualityLocal", "System.Single"),
            ("m_flConnectionQualityRemote", "System.Single"), ("m_flOutBytesPerSec", "System.Single"),
            ("m_flInBytesPerSec", "System.Single"), ("m_cbPendingReliable", "System.Int32"),
            ("m_cbPendingUnreliable", "System.Int32"), ("m_cbSentUnackedReliable", "System.Int32")
        };
        string wrongType = string.Join(", ", statusFields
            .Where(f => Field(realTimeStatus, f.Item1).FieldType.FullName != f.Item2).Select(f => f.Item1));
        Check(wrongType.Length == 0, "SteamNetConnectionRealTimeStatus_t fields changed type: " + wrongType);

        // 5. The quantity host_net_pending_bytes_max is compared against: the native send
        //    queue gate. GetSendQueueSize adds the same three pending counters, and
        //    ZDOMan.SendZDOs refuses a non-flush tick once the total passes the cap.
        MethodDefinition sendQueue = Method(steamSocket, "GetSendQueueSize");
        var queueBody = sendQueue.Body.Instructions;
        string missingPending = string.Join(", ", new[] { "m_cbPendingReliable", "m_cbPendingUnreliable", "m_cbSentUnackedReliable" }
            .Where(pending => !queueBody.Any(i => (i.Operand as FieldReference)?.Name == pending)));
        Check(missingPending.Length == 0,
            "ZSteamSocket.GetSendQueueSize no longer adds the counters the gauge sums: " + missingPending);
        MethodDefinition sendZdos = zdoMan.Methods.SingleOrDefault(m => m.Name == "SendZDOs")
            ?? throw new InvalidOperationException("Host net telemetry: ZDOMan.SendZDOs is missing.");
        var sendBody = sendZdos.Body.Instructions;
        Check(sendBody.Any(i => (i.Operand as MethodReference)?.Name == "GetSendQueueSize"),
            "ZDOMan.SendZDOs still reads the peer send queue before syncing");
        Check(sendBody.Any(i => i.OpCode == Cil.OpCodes.Ldc_I4 && (int)i.Operand == 10240),
            "ZDOMan.SendZDOs still uses the 10240-byte send-queue cap the gauge is read against");

        // 6. Two-sided observation of the vanilla defect this module works around. The
        //    dedicated build has no client Steam context, so a GetConnectionQuality that
        //    asks SteamNetworkingSockets yields zeros for every figure ZNet.GetNetStats
        //    aggregates there. The harness runs against both assemblies, so the NOTE below
        //    is how we learn that Iron Gate fixed it.
        MethodDefinition quality = Method(steamSocket, "GetConnectionQuality",
            "System.Single&", "System.Single&", "System.Int32&", "System.Single&", "System.Single&");
        var qualityCalls = quality.Body.Instructions
            .Select(i => i.Operand as MethodReference)
            .Where(m => m != null && m.Name == "GetConnectionRealTimeStatus")
            .Select(m => m!.DeclaringType.FullName)
            .Distinct()
            .ToList();
        Check(qualityCalls.Count == 1, "ZSteamSocket.GetConnectionQuality reads the link through exactly one Steam interface");
        bool fixedUpstream = qualityCalls[0] == "Steamworks.SteamGameServerNetworkingSockets";
        Console.WriteLine(fixedUpstream
            ? "NOTE: ZSteamSocket.GetConnectionQuality uses SteamGameServerNetworkingSockets on this build; the per-peer path is no longer needed on the dedicated server"
            : "NOTE: ZSteamSocket.GetConnectionQuality uses " + qualityCalls[0] + " on this build; ZNet.GetNetStats stays zero on a dedicated server and the per-peer path is required");
        Check(fixedUpstream || qualityCalls[0] == "Steamworks.SteamNetworkingSockets",
            "GetConnectionQuality uses one of the two known Steam networking interfaces");

        // 7. The plugin reads both interfaces, keeps the native fallback, and caches the
        //    contract decision instead of re-resolving it every interval.
        TypeDefinition host = Require(pluginModule, "BetterPerformance.HostTelemetry");
        MethodDefinition Own(string name) => host.Methods.SingleOrDefault(m => m.Name == name)
            ?? throw new InvalidOperationException("Host net telemetry: HostTelemetry." + name + " is missing.");
        MethodDefinition serverLinks = Own("ServerLinks"), tryRead = Own("TryRead");
        Check(host.Fields.Any(f => f.IsStatic && f.Name == "contractChecked" && f.FieldType.FullName == "System.Boolean"),
            "the contract outcome is cached in a static flag, not re-resolved per interval");
        var readCalls = tryRead.Body.Instructions.Select(i => i.Operand as MethodReference).Where(m => m != null).ToList();
        foreach (string api in new[] { "Steamworks.SteamGameServerNetworkingSockets", "Steamworks.SteamNetworkingSockets" })
            Check(readCalls.Any(m => m!.Name == "GetConnectionRealTimeStatus" && m.DeclaringType.FullName == api),
                "TryRead calls " + api + ".GetConnectionRealTimeStatus");
        Check(readCalls.Any(m => m!.Name == "SteamSocket" && m.DeclaringType.FullName == "BetterPerformance.SteamTelemetry"),
            "TryRead unwraps the ServerSync socket wrapper before reading m_con");
        Check(Own("Native").Body.Instructions.Any(i => (i.Operand as MethodReference)?.Name == "GetNetStats"),
            "the native GetNetStats path is retained for clients and for the contract fallback");

        // 8. The server path allocates nothing per peer beyond the exported gauges, and
        //    never takes ZNet.GetPeers, which copies the list.
        Check(serverLinks.Body.Instructions.Where(i => i.OpCode == Cil.OpCodes.Newobj)
                .All(i => (i.Operand as MethodReference)?.DeclaringType.FullName == "BetterPerformance.Core.NumberValue"),
            "ServerLinks allocates only the interval's gauge objects");
        Check(!serverLinks.Body.Instructions.Any(i => (i.Operand as MethodReference)?.Name == "GetPeers"),
            "ServerLinks walks m_peers directly instead of copying the peer list");

        // 9. Every exported name, so a rename cannot silently drop a gauge the summarizer reads.
        var literals = new HashSet<string>(host.Methods.Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions)
            .Where(i => i.OpCode == Cil.OpCodes.Ldstr)
            .Select(i => (string)i.Operand));
        string missingGauges = string.Join(", ", new[]
        {
            "host_net_ping_ms", "host_net_ping_max_ms", "host_net_quality_local", "host_net_quality_remote",
            "host_net_out_bytes_per_sec", "host_net_in_bytes_per_sec", "host_net_pending_bytes_max",
            "host_net_peers_ready", "host_net_peers_measured", "host_net_peers_unmeasured"
        }.Where(name => !literals.Contains(name)));
        Check(missingGauges.Length == 0, "gauges no longer exported: " + missingGauges);
        string missingStatus = string.Join(", ", new[]
        {
            "server_per_peer", "server_no_peers", "server_peers_unmeasured", "client_server_link", "native_fallback:"
        }.Where(status => !literals.Contains(status)));
        Check(missingStatus.Length == 0, "host_net_status can no longer report: " + missingStatus);

        Console.WriteLine("Host net telemetry: " + checks + " static game-contract checks; per-peer figures require a live multiplayer session to validate.");
        return checks;
    }
}
