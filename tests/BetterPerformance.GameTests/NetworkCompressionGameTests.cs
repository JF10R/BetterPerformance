using System.Reflection;
using HarmonyLib;
using Mono.Cecil;
using Cil = Mono.Cecil.Cil;

// ZSteamSocket reaches native socket interfaces a standalone CLR cannot type-load, so every
// game and plugin contract here is read from Mono.Cecil metadata, exactly as
// HostNetGameTests does. The reflection block at the end is attempted and degraded, never
// required.
internal static class NetworkCompressionGameTests
{
    internal static int Run(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Network compression: " + message);
            checks++;
        }

        string managed = Path.GetDirectoryName(game.Location)!;
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managed);
        var parameters = new ReaderParameters { AssemblyResolver = resolver };
        using var gameModule = ModuleDefinition.ReadModule(game.Location, parameters);
        using var pluginModule = ModuleDefinition.ReadModule(plugin.Location, parameters);

        TypeDefinition Require(ModuleDefinition module, string name) => module.GetType(name)
            ?? throw new InvalidOperationException("Network compression: " + name + " is missing.");
        MethodDefinition Method(TypeDefinition type, string name, params string[] parameterTypes) =>
            type.Methods.SingleOrDefault(m => m.Name == name &&
                m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameterTypes))
            ?? throw new InvalidOperationException("Network compression: " + type.Name + "." + name + " with the expected parameters is missing.");
        FieldDefinition Field(TypeDefinition type, string name) => type.Fields.SingleOrDefault(f => f.Name == name)
            ?? throw new InvalidOperationException("Network compression: " + type.FullName + "." + name + " is missing.");
        static bool CallsNamed(MethodDefinition method, string name) =>
            method.HasBody && method.Body.Instructions.Any(i => (i.Operand as MethodReference)?.Name == name);

        TypeDefinition socketType = Require(gameModule, "ZSteamSocket"), net = Require(gameModule, "ZNet");
        TypeDefinition peer = Require(gameModule, "ZNetPeer"), rpc = Require(gameModule, "ZRpc");
        TypeDefinition package = Require(gameModule, "ZPackage");

        // 1. The send path the prefix rewrites: one queued array becomes exactly one Steam
        //    message, so a frame is a whole packet and never a fragment of one.
        MethodDefinition sendQueued = Method(socketType, "SendQueuedPackages");
        Check(!sendQueued.IsStatic && !sendQueued.IsPublic && sendQueued.ReturnType.FullName == "System.Void" && sendQueued.HasBody,
            "ZSteamSocket.SendQueuedPackages() is a private instance void method with a body");
        Check(CallsNamed(sendQueued, "SendMessageToConnection"),
            "SendQueuedPackages still hands each queued array to SendMessageToConnection");
        FieldDefinition queue = Field(socketType, "m_sendQueue");
        Check(!queue.IsStatic && queue.FieldType.FullName == "System.Collections.Generic.Queue`1<System.Byte[]>",
            "ZSteamSocket.m_sendQueue is the instance Queue<byte[]> the prefix re-encodes");
        Check(sendQueued.Body.Instructions.Any(i => (i.Operand as FieldReference)?.Name == "m_sendQueue"),
            "SendQueuedPackages still drains m_sendQueue rather than another buffer");

        // 2. Send enqueues and flushes synchronously, which is what makes the postfix's
        //    empty-queue test mean "the Start message has left this socket".
        MethodDefinition send = Method(socketType, "Send", "ZPackage");
        Check(!send.IsStatic && send.IsPublic && send.ReturnType.FullName == "System.Void",
            "ZSteamSocket.Send(ZPackage) is a public instance void method");
        Check(CallsNamed(send, "SendQueuedPackages"), "Send still flushes through SendQueuedPackages");
        Check(CallsNamed(send, "Enqueue") && CallsNamed(send, "GetArray"),
            "Send still enqueues the package's own byte array");
        MethodDefinition connected = Method(socketType, "IsConnected");
        Check(!connected.IsStatic && connected.IsPublic && connected.ReturnType.FullName == "System.Boolean",
            "ZSteamSocket.IsConnected() is the public predicate the per-socket sweep reads");

        // 3. The receive path the postfix rewrites: one ZPackage per received message.
        MethodDefinition recv = Method(socketType, "Recv");
        Check(!recv.IsStatic && recv.IsPublic && recv.ReturnType.FullName == "ZPackage" && recv.HasBody,
            "ZSteamSocket.Recv() is a public instance method returning ZPackage");
        Check(CallsNamed(recv, "ReceiveMessagesOnConnection"),
            "Recv still reads its bytes from ReceiveMessagesOnConnection");
        Check(recv.Body.Instructions.Count(i => i.OpCode == Cil.OpCodes.Newobj &&
                (i.Operand as MethodReference)?.DeclaringType.FullName == "ZPackage") == 1,
            "Recv builds exactly one ZPackage per received message");

        // 4. The package the decoder rebuilds.
        MethodDefinition fromBytes = Method(package, ".ctor", "System.Byte[]");
        Check(fromBytes.IsPublic, "ZPackage(byte[]) is the public constructor the decoder rebuilds with");
        MethodDefinition getArray = Method(package, "GetArray");
        Check(!getArray.IsStatic && getArray.IsPublic && getArray.ReturnType.FullName == "System.Byte[]",
            "ZPackage.GetArray() returns the exact payload bytes");

        // 5. Where the negotiation is registered, and where the per-socket state is dropped.
        MethodDefinition newConnection = Method(net, "OnNewConnection", "ZNetPeer");
        Check(!newConnection.IsStatic && !newConnection.IsPublic && newConnection.ReturnType.FullName == "System.Void",
            "ZNet.OnNewConnection(ZNetPeer) is a private instance void method");
        Check(newConnection.Body.Instructions.Any(i => (i.Operand as MethodReference)?.Name == "Register" &&
                (i.Operand as MethodReference)?.DeclaringType.FullName == "ZRpc"),
            "OnNewConnection is still where per-peer RPCs are registered");
        MethodDefinition disconnect = Method(net, "Disconnect", "ZNetPeer");
        Check(!disconnect.IsStatic && disconnect.IsPublic && disconnect.ReturnType.FullName == "System.Void",
            "ZNet.Disconnect(ZNetPeer) is a public instance void method");
        FieldDefinition peerSocket = Field(peer, "m_socket"), peerRpc = Field(peer, "m_rpc"), uid = Field(peer, "m_uid");
        Check(!peerSocket.IsStatic && peerSocket.FieldType.FullName == "ISocket", "ZNetPeer.m_socket is an instance ISocket");
        Check(!peerRpc.IsStatic && peerRpc.FieldType.FullName == "ZRpc", "ZNetPeer.m_rpc is the instance ZRpc the offer travels on");
        Check(!uid.IsStatic && uid.FieldType.FullName == "System.Int64", "ZNetPeer.m_uid identifies the connection");
        Check(socketType.Interfaces.Any(i => i.InterfaceType.FullName == "ISocket"),
            "ZSteamSocket still implements ISocket, so the ServerSync unwrap can reach it");

        // 6. The one Register overload and the Invoke the negotiation uses. There is no
        //    acknowledgement RPC: the offer alone turns the offering peer's inbound
        //    direction on, so the no-argument Register overload is not part of this contract.
        MethodDefinition registerTyped = rpc.Methods.SingleOrDefault(m => m.Name == "Register" &&
            m.GenericParameters.Count == 1 && m.Parameters.Count == 2 &&
            m.Parameters[1].ParameterType.FullName == "System.Action`2<ZRpc,T>")
            ?? throw new InvalidOperationException("Network compression: ZRpc.Register<T>(string, Action<ZRpc,T>) is missing.");
        Check(registerTyped.IsPublic && !registerTyped.IsStatic, "ZRpc.Register<T>(string, Action<ZRpc,T>) carries the protocol version");
        MethodDefinition invoke = Method(rpc, "Invoke", "System.String", "System.Object[]");
        Check(invoke.IsPublic && !invoke.IsStatic && invoke.ReturnType.FullName == "System.Void" &&
            invoke.Parameters[1].CustomAttributes.Any(a => a.AttributeType.Name == "ParamArrayAttribute"),
            "ZRpc.Invoke(string, params object[]) is the public sender");

        // 7. An offer to a peer that does not know the name must be silently dropped, or the
        //    negotiation would break vanilla clients instead of leaving them uncompressed.
        MethodDefinition handle = Method(rpc, "HandlePackage", "ZPackage");
        Check(CallsNamed(handle, "TryGetValue"),
            "ZRpc.HandlePackage still looks the hash up with TryGetValue, so an unknown RPC name is ignored");

        // 8. The plugin's hooks: static, void, and shaped for the Harmony arguments they take.
        TypeDefinition module = Require(pluginModule, "BetterPerformance.NetworkCompression");
        MethodDefinition Own(string name) => module.Methods.SingleOrDefault(m => m.Name == name)
            ?? throw new InvalidOperationException("Network compression: NetworkCompression." + name + " is missing.");
        foreach (string name in new[] { "BeforeSend", "AfterSend" })
        {
            MethodDefinition hook = Own(name);
            Check(hook.IsStatic && hook.ReturnType.FullName == "System.Void" &&
                hook.Parameters.Count == 1 && hook.Parameters[0].Name == "__instance" &&
                hook.Parameters[0].ParameterType.FullName == "ZSteamSocket",
                name + " is a static void hook on the socket that cannot skip the original");
        }
        MethodDefinition afterRecv = Own("AfterRecv");
        Check(afterRecv.IsStatic && afterRecv.ReturnType.FullName == "System.Void" &&
            afterRecv.Parameters.Count == 2 &&
            afterRecv.Parameters[0].ParameterType.FullName == "ZSteamSocket" &&
            afterRecv.Parameters[1].Name == "__result" && afterRecv.Parameters[1].ParameterType.FullName == "ZPackage&",
            "AfterRecv replaces the received package through a by-reference __result");
        foreach (string name in new[] { "AfterNewConnection", "AfterDisconnect" })
        {
            MethodDefinition hook = Own(name);
            Check(hook.IsStatic && hook.ReturnType.FullName == "System.Void" &&
                hook.Parameters.Count == 1 && hook.Parameters[0].ParameterType.FullName == "ZNetPeer",
                name + " is a static void postfix taking the peer");
        }
        Check(!module.Methods.Any(m => m.Name.Contains("Transpile")),
            "the module rewrites no IL: there is no transpiler");
        Check(Own("AfterRecv").Body.Instructions.Any(i => (i.Operand as MethodReference)?.Name == "TryDecode") &&
            Own("EncodeOnce").Body.Instructions.Any(i => (i.Operand as MethodReference)?.Name == "Encode"),
            "the hooks go through CompressionFrame rather than compressing inline");
        // The decoder must not sit behind a per-socket flag: the frame identifies itself, so
        // the only per-socket lookup in AfterRecv may happen after the framing test.
        var recvBody = Own("AfterRecv").Body.Instructions.ToList();
        int framedAt = recvBody.FindIndex(i => (i.Operand as MethodReference)?.Name == "IsFramed");
        int lookupAt = recvBody.FindIndex(i => (i.Operand as MethodReference)?.Name == "Lookup");
        Check(framedAt >= 0 && (lookupAt < 0 || framedAt < lookupAt),
            "AfterRecv decides on the frame itself, never on per-socket negotiation state");
        Check(Own("OnOffer").Body.Instructions.Any(i => (i.Operand as MethodReference)?.Name == "SteamSocket" &&
                (i.Operand as MethodReference)?.DeclaringType.FullName == "BetterPerformance.SteamTelemetry"),
            "the offer handler unwraps the ServerSync wrapper before keying the socket state");

        // 9. The protocol constant and the strings the negotiation, the yield and the
        //    telemetry are made of. A rename here is a silent protocol or gauge break.
        FieldDefinition version = Field(module, "ProtocolVersion");
        Check(version.IsStatic && version.HasConstant && version.FieldType.FullName == "System.Int32" && (int)version.Constant == 1,
            "the protocol version constant is 1");
        var literals = new HashSet<string>(module.Methods.Where(m => m.HasBody)
            .Concat(module.NestedTypes.SelectMany(t => t.Methods).Where(m => m.HasBody))
            .SelectMany(m => m.Body.Instructions)
            .Where(i => i.OpCode == Cil.OpCodes.Ldstr)
            .Select(i => (string)i.Operand));
        string missingProtocol = string.Join(", ", new[]
        {
            "BP_NetCompressOffer", "Network", "CompressionEnabled",
            "CW_Jesse.BetterNetworking", "yield_to_betternetworking", "foreign_patch:",
            "unavailable_unexpected_shape", "installed", "failed"
        }.Where(name => !literals.Contains(name)));
        Check(missingProtocol.Length == 0, "protocol or status strings no longer present: " + missingProtocol);
        Check(!literals.Any(literal => literal.Contains("BP_NetCompressStart")),
            "no acknowledgement RPC survives: the offer alone starts a direction");
        string missingGauges = string.Join(", ", new[]
        {
            "net_compress_compress_packets", "net_compress_compress_raw_bytes", "net_compress_compress_wire_bytes",
            "net_compress_compress_kept_raw", "net_compress_decode_packets", "net_compress_decode_raw_bytes",
            "net_compress_decode_wire_bytes", "net_compress_decode_failures", "net_compress_unframed_received",
            "net_compress_peers_active", "net_compress_peers_offered", "net_compress_peers_incompatible",
            "net_compress_failures"
        }.Where(name => !literals.Contains(name)));
        Check(missingGauges.Length == 0, "gauges no longer exported: " + missingGauges);
        string missingLabels = string.Join(", ", new[]
        {
            "net_compress_status", "net_compress_enabled", "net_compress_version", "net_compress_scope",
            "steamworks_only; framed_deflate; peers_without_this_plugin_stay_raw"
        }.Where(name => !literals.Contains(name)));
        Check(missingLabels.Length == 0, "labels no longer exported: " + missingLabels);

        // 10. The frame the two sides agree on, read from the shipped Core code.
        TypeDefinition frame = Require(pluginModule, "BetterPerformance.Core.CompressionFrame");
        FieldDefinition maxRaw = Field(frame, "MaxRawLength");
        Check(maxRaw.HasConstant && (int)maxRaw.Constant == 524288, "CompressionFrame.MaxRawLength is the 512 KiB bound");
        foreach (string name in new[] { "Encode", "IsFramed", "TryDecode" })
            Check(frame.Methods.Any(m => m.Name == name && m.IsPublic && m.IsStatic),
                "CompressionFrame." + name + " is public and static");

        // 11. Whatever this CLR can actually run. ZSteamSocket cannot be type-loaded
        //     offline, so each of these is attempted and reported, never required.
        Type? live = null;
        try { live = plugin.GetType("BetterPerformance.NetworkCompression", true); }
        catch (Exception exception) when (exception is TypeLoadException || exception is FileNotFoundException)
        { Console.WriteLine("STATIC ONLY network compression runtime block: " + exception.GetType().Name + "; every contract check above still ran"); }
        if (live != null)
        {
            const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
            try
            {
                var guard = live.GetMethod("RequireSteamTransport", PrivateStatic)!;
                var empty = new List<CodeInstruction>();
                bool rejected = false;
                try { guard.Invoke(null, new object[] { empty, empty }); }
                catch (TargetInvocationException exception) when (exception.InnerException is InvalidOperationException) { rejected = true; }
                Check(rejected, "a socket that no longer sends one Steam message per queued array is rejected instead of patched");

                var config = new BepInEx.Configuration.ConfigFile(
                    Path.Combine(Path.GetTempPath(), "bp-netcompress-" + Guid.NewGuid().ToString("N") + ".cfg"), false) { SaveOnConfigSet = false };
                var log = new BepInEx.Logging.ManualLogSource("NetworkCompressionVerification");
                log.LogEvent += (_, entry) => Console.WriteLine(entry.Data);
                live.GetMethod("Install", PrivateStatic)!.Invoke(null, new object[] { config, log });
                Check((string)live.GetProperty("Status", PrivateStatic)!.GetValue(null)! == "disabled" &&
                    !(bool)live.GetProperty("Installed", PrivateStatic)!.GetValue(null)!,
                    "the default (off) configuration patches nothing");
                var option = (BepInEx.Configuration.ConfigEntry<bool>)config[
                    new BepInEx.Configuration.ConfigDefinition("Network", "CompressionEnabled")];
                Check(!option.Value && !(bool)option.DefaultValue, "Network.CompressionEnabled defaults to false");

                Type number = plugin.GetType("BetterPerformance.Core.NumberValue", true)!;
                Type text = plugin.GetType("BetterPerformance.Core.TextValue", true)!;
                var sample = live.GetMethod("Sample", PrivateStatic)!;
                Dictionary<string, double> Numbers(out List<string> labelNames)
                {
                    var gauges = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(number))!;
                    var labels = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(text))!;
                    sample.Invoke(null, new object[] { gauges, labels });
                    labelNames = labels.Cast<object>().Select(l => (string)text.GetProperty("Name")!.GetValue(l)!).ToList();
                    return gauges.Cast<object>().ToDictionary(
                        g => (string)number.GetProperty("Name")!.GetValue(g)!,
                        g => (double)number.GetProperty("Value")!.GetValue(g)!);
                }
                var counters = Numbers(out var exportedLabels);
                Check(counters.Count == 13 && counters.ContainsKey("net_compress_peers_active"),
                    "Sample exports the thirteen counters, including the active-peer count");
                foreach (string name in new[] { "net_compress_status", "net_compress_enabled", "net_compress_version", "net_compress_scope" })
                    Check(exportedLabels.Contains(name), name + " is exported");
                Check(Numbers(out _).Values.All(value => value == 0), "Sample drains its interval counters");

                live.GetMethod("Uninstall", PrivateStatic)!.Invoke(null, null);
                Check(!(bool)live.GetProperty("Installed", PrivateStatic)!.GetValue(null)! &&
                    (string)live.GetProperty("Status", PrivateStatic)!.GetValue(null)! == "disabled",
                    "Uninstall leaves the module disabled and unpatched");
            }
            catch (Exception exception) when (Offline(exception))
            {
                // Harmony's unpatch and this module's own types both reach ZSteamSocket, which a
                // standalone .NET Framework cannot JIT; the Cecil checks above cover the contract.
                Console.WriteLine("STATIC ONLY network compression runtime block: " + exception.GetType().Name + "; every contract check above still ran");
            }
        }

        Console.WriteLine("Network compression: " + checks + " static game-contract checks; the negotiated ratio requires a live two-peer session to validate.");
        return checks;
    }

    private static bool Offline(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is TypeLoadException || current is System.Security.SecurityException) return true;
            if (current is HarmonyException) return true;
        }
        return false;
    }
}
