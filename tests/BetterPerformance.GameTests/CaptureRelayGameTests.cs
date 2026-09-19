using System.Reflection;
using HarmonyLib;
using Mono.Cecil;
using Cil = Mono.Cecil.Cil;

// Modeled on NetworkCompressionGameTests: every game-side contract is read from Mono.Cecil
// metadata rather than loaded reflectively. BetterPerformance.Core is compiled directly into
// the plugin assembly (see BetterPerformance.csproj: "the distributable remains one DLL"), so
// its types are read from pluginModule too, exactly as CompressionFrame already is there -
// there is no separate BetterPerformance.Core.dll to load. The reflection block at the end is
// attempted and degraded, never required.
internal static class CaptureRelayGameTests
{
    internal static int Run(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Capture relay: " + message);
            checks++;
        }

        string managed = Path.GetDirectoryName(game.Location)!;
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managed);
        var parameters = new ReaderParameters { AssemblyResolver = resolver };
        using var gameModule = ModuleDefinition.ReadModule(game.Location, parameters);
        using var pluginModule = ModuleDefinition.ReadModule(plugin.Location, parameters);

        TypeDefinition Require(ModuleDefinition module, string name) => module.GetType(name)
            ?? throw new InvalidOperationException("Capture relay: " + name + " is missing.");
        MethodDefinition Method(TypeDefinition type, string name, params string[] parameterTypes) =>
            type.Methods.SingleOrDefault(m => m.Name == name &&
                m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameterTypes))
            ?? throw new InvalidOperationException("Capture relay: " + type.Name + "." + name + " with the expected parameters is missing.");
        FieldDefinition Field(TypeDefinition type, string name) => type.Fields.SingleOrDefault(f => f.Name == name)
            ?? throw new InvalidOperationException("Capture relay: " + type.FullName + "." + name + " is missing.");
        static bool CallsNamed(MethodDefinition method, string name) =>
            method.HasBody && method.Body.Instructions.Any(i => (i.Operand as MethodReference)?.Name == name);

        TypeDefinition net = Require(gameModule, "ZNet"), peer = Require(gameModule, "ZNetPeer");
        TypeDefinition rpcType = Require(gameModule, "ZRpc"), package = Require(gameModule, "ZPackage");
        TypeDefinition socket = Require(gameModule, "ISocket");

        // 1. Where the offer/chunk RPCs are registered, and the server predicate that decides
        //    which side offers first.
        MethodDefinition newConnection = Method(net, "OnNewConnection", "ZNetPeer");
        Check(!newConnection.IsStatic && !newConnection.IsPublic && newConnection.ReturnType.FullName == "System.Void",
            "ZNet.OnNewConnection(ZNetPeer) is a private instance void method");
        Check(newConnection.Body.Instructions.Any(i => (i.Operand as MethodReference)?.Name == "Register" &&
                (i.Operand as MethodReference)?.DeclaringType.FullName == "ZRpc"),
            "ZNet.OnNewConnection still registers RPCs through ZRpc.Register, the hook point AfterNewConnection patches");
        MethodDefinition disconnect = Method(net, "Disconnect", "ZNetPeer");
        Check(!disconnect.IsStatic && disconnect.IsPublic && disconnect.ReturnType.FullName == "System.Void",
            "ZNet.Disconnect(ZNetPeer) is a public instance void method");
        MethodDefinition isServer = Method(net, "IsServer");
        Check(!isServer.IsStatic && isServer.IsPublic && isServer.ReturnType.FullName == "System.Boolean",
            "ZNet.IsServer() is a public instance bool method, the branch that decides who offers first");

        // 2. The peer fields the hooks read directly.
        FieldDefinition peerRpc = Field(peer, "m_rpc"), peerSocket = Field(peer, "m_socket"), peerServer = Field(peer, "m_server");
        Check(!peerRpc.IsStatic && peerRpc.FieldType.FullName == "ZRpc", "ZNetPeer.m_rpc is the instance ZRpc field the relay registers and invokes on");
        Check(!peerSocket.IsStatic && peerSocket.FieldType.FullName == "ISocket", "ZNetPeer.m_socket is the instance ISocket field the pump gates on");
        Check(!peerServer.IsStatic && peerServer.FieldType.FullName == "System.Boolean", "ZNetPeer.m_server is the instance bool field AfterNewConnection reads on the client side");

        // 3. The one Register overload the chunk handler uses, the sender, and the dispatch
        //    that makes an unregistered RPC name safe to send at a vanilla or older peer.
        //    Serialize is asserted to still route a ZPackage argument through ZPackage.Write(ZPackage)
        //    and to have no byte[] case - that absence is why the module frames chunks as one
        //    ZPackage RPC parameter instead of a raw byte[] parameter.
        MethodDefinition registerTyped = rpcType.Methods.SingleOrDefault(m => m.Name == "Register" &&
            m.GenericParameters.Count == 1 && m.Parameters.Count == 2 &&
            m.Parameters[1].ParameterType.FullName == "System.Action`2<ZRpc,T>")
            ?? throw new InvalidOperationException("Capture relay: ZRpc.Register<T>(string, Action<ZRpc,T>) is missing.");
        Check(registerTyped.IsPublic && !registerTyped.IsStatic,
            "ZRpc.Register<T>(string, Action<ZRpc,T>) is the public instance overload the relay registers its chunk handler with");
        MethodDefinition invoke = Method(rpcType, "Invoke", "System.String", "System.Object[]");
        Check(invoke.IsPublic && !invoke.IsStatic && invoke.ReturnType.FullName == "System.Void" &&
            invoke.Parameters[1].CustomAttributes.Any(a => a.AttributeType.Name == "ParamArrayAttribute"),
            "ZRpc.Invoke(string, params object[]) is the public sender the relay offers and chunks over");
        MethodDefinition handle = Method(rpcType, "HandlePackage", "ZPackage");
        Check(CallsNamed(handle, "TryGetValue"),
            "ZRpc.HandlePackage still looks the RPC name up with TryGetValue, so an unregistered relay RPC is dropped silently on a vanilla or older peer");
        MethodDefinition serialize = rpcType.Methods.SingleOrDefault(m => m.Name == "Serialize" && m.IsStatic &&
            m.Parameters.Count == 2 && m.Parameters[0].ParameterType.FullName == "System.Object[]" &&
            m.Parameters[1].ParameterType.FullName == "ZPackage&")
            ?? throw new InvalidOperationException("Capture relay: ZRpc.Serialize(object[], ref ZPackage) is missing.");
        Check(serialize.Body.Instructions.Any(i => (i.Operand as MethodReference)?.Name == "Write" &&
                (i.Operand as MethodReference)?.Parameters.Count == 1 &&
                (i.Operand as MethodReference)?.Parameters[0].ParameterType.FullName == "ZPackage"),
            "ZRpc.Serialize still writes a ZPackage argument through ZPackage.Write(ZPackage)");
        Check(!serialize.Body.Instructions.Any(i => (i.Operand as MethodReference)?.Name == "Write" &&
                (i.Operand as MethodReference)?.Parameters.Count == 1 &&
                (i.Operand as MethodReference)?.Parameters[0].ParameterType.FullName == "System.Byte[]"),
            "ZRpc.Serialize still has no byte[] case, so a raw chunk cannot be sent as its own RPC parameter and must ride inside a ZPackage");

        // 4. The ZPackage members the chunk framing writes and reads.
        MethodDefinition packageCtor = Method(package, ".ctor");
        Check(packageCtor.IsPublic && packageCtor.Parameters.Count == 0, "ZPackage() is the public parameterless constructor OnChunk and the offer build packages with");
        foreach (string type in new[] { "System.String", "System.Byte", "System.Int32", "System.Boolean", "System.Byte[]" })
        {
            MethodDefinition write = Method(package, "Write", type);
            Check(write.IsPublic && !write.IsStatic && write.ReturnType.FullName == "System.Void",
                "ZPackage.Write(" + type + ") is a public instance void overload");
        }
        MethodDefinition readString = Method(package, "ReadString");
        Check(readString.IsPublic && !readString.IsStatic && readString.ReturnType.FullName == "System.String", "ZPackage.ReadString() returns string");
        MethodDefinition readByte = Method(package, "ReadByte");
        Check(readByte.IsPublic && !readByte.IsStatic && readByte.ReturnType.FullName == "System.Byte", "ZPackage.ReadByte() returns byte");
        MethodDefinition readInt = Method(package, "ReadInt");
        Check(readInt.IsPublic && !readInt.IsStatic && readInt.ReturnType.FullName == "System.Int32", "ZPackage.ReadInt() returns int");
        MethodDefinition readBool = Method(package, "ReadBool");
        Check(readBool.IsPublic && !readBool.IsStatic && readBool.ReturnType.FullName == "System.Boolean", "ZPackage.ReadBool() returns bool");
        MethodDefinition readByteArray = Method(package, "ReadByteArray");
        Check(readByteArray.IsPublic && !readByteArray.IsStatic && readByteArray.ReturnType.FullName == "System.Byte[]", "ZPackage.ReadByteArray() returns byte[]");

        // 5. The socket predicates the pump gates on before it ever dequeues a chunk.
        MethodDefinition sendQueueSize = Method(socket, "GetSendQueueSize");
        Check(sendQueueSize.ReturnType.FullName == "System.Int32", "ISocket.GetSendQueueSize() returns int, the idle-socket gate the pump reads");
        MethodDefinition isConnected = Method(socket, "IsConnected");
        Check(isConnected.ReturnType.FullName == "System.Boolean", "ISocket.IsConnected() returns bool, the liveness check the pump and hooks read");

        // 6. The plugin's own hooks and negotiation surface, from pluginModule.
        TypeDefinition module = Require(pluginModule, "BetterPerformance.CaptureRelay");
        MethodDefinition Own(string name) => module.Methods.SingleOrDefault(m => m.Name == name)
            ?? throw new InvalidOperationException("Capture relay: CaptureRelay." + name + " is missing.");
        MethodDefinition afterNewConnection = Own("AfterNewConnection");
        Check(afterNewConnection.IsStatic && afterNewConnection.ReturnType.FullName == "System.Void" &&
            afterNewConnection.Parameters.Count == 2 &&
            afterNewConnection.Parameters[0].Name == "__instance" && afterNewConnection.Parameters[0].ParameterType.FullName == "ZNet" &&
            afterNewConnection.Parameters[1].Name == "peer" && afterNewConnection.Parameters[1].ParameterType.FullName == "ZNetPeer",
            "AfterNewConnection is a static void postfix taking the ZNet instance and the new peer");
        MethodDefinition afterDisconnect = Own("AfterDisconnect");
        Check(afterDisconnect.IsStatic && afterDisconnect.ReturnType.FullName == "System.Void" &&
            afterDisconnect.Parameters.Count == 1 && afterDisconnect.Parameters[0].ParameterType.FullName == "ZNetPeer",
            "AfterDisconnect is a static void postfix taking the disconnected peer");
        MethodDefinition onChunk = Own("OnChunk");
        Check(onChunk.IsStatic && onChunk.ReturnType.FullName == "System.Void" &&
            onChunk.Parameters.Count == 2 &&
            onChunk.Parameters[0].ParameterType.FullName == "ZRpc" && onChunk.Parameters[1].ParameterType.FullName == "ZPackage",
            "OnChunk is a static void RPC handler taking (ZRpc, ZPackage)");
        MethodDefinition pump = Own("Pump");
        Check(pump.IsStatic && pump.ReturnType.FullName == "System.Void" && pump.Parameters.Count == 0,
            "Pump is the static void method the plugin's Update calls once per frame");
        MethodDefinition tee = Own("Tee");
        Check(tee.IsStatic && tee.Parameters.Count == 1 && tee.Parameters[0].ParameterType.FullName == "System.String" &&
            tee.ReturnType.FullName == "System.Action`1<System.Byte[]>",
            "Tee(string) returns the Action<byte[]> the capture writer tees its encoded lines through");
        MethodDefinition captureFinished = Own("CaptureFinished");
        Check(captureFinished.IsStatic && captureFinished.Parameters.Count == 1 &&
            captureFinished.Parameters[0].ParameterType.FullName == "System.String" && captureFinished.ReturnType.FullName == "System.Void",
            "CaptureFinished(string) is a static void method the capture session calls once its writer has ended");
        FieldDefinition version = Field(module, "ProtocolVersion");
        Check(version.IsStatic && version.HasConstant && version.FieldType.FullName == "System.Int32" && (int)version.Constant! == 1,
            "CaptureRelay.ProtocolVersion is the const int 1");
        var bodies = module.Methods.Concat(module.NestedTypes.SelectMany(t => t.Methods)).Where(m => m.HasBody);
        var literals = new HashSet<string>(bodies.SelectMany(m => m.Body.Instructions)
            .Where(i => i.OpCode == Cil.OpCodes.Ldstr).Select(i => (string)i.Operand));
        Check(literals.Contains("BP_RelayOffer") && literals.Contains("BP_RelayChunk"),
            "the offer and chunk RPC names BP_RelayOffer/BP_RelayChunk still appear as literals in CaptureRelay's methods");

        // 7. The Core protocol types, compiled into pluginModule alongside the plugin itself.
        TypeDefinition relayNames = Require(pluginModule, "BetterPerformance.Core.RelayNames");
        MethodDefinition isValid = Method(relayNames, "IsValid", "System.String", "BetterPerformance.Core.RelayStreamKind");
        Check(isValid.IsPublic && isValid.IsStatic && isValid.ReturnType.FullName == "System.Boolean",
            "RelayNames.IsValid(string, RelayStreamKind) is public static bool");
        TypeDefinition relaySink = Require(pluginModule, "BetterPerformance.Core.RelaySink");
        MethodDefinition accept = Method(relaySink, "Accept", "BetterPerformance.Core.RelayChunk");
        Check(accept.IsPublic && !accept.IsStatic && accept.ReturnType.FullName == "BetterPerformance.Core.RelayAccept",
            "RelaySink.Accept(RelayChunk) is a public instance method returning RelayAccept");
        TypeDefinition relayOutbox = Require(pluginModule, "BetterPerformance.Core.RelayOutbox");
        MethodDefinition enqueue = Method(relayOutbox, "Enqueue", "System.String", "BetterPerformance.Core.RelayStreamKind", "System.Byte[]");
        Check(enqueue.IsPublic && !enqueue.IsStatic && enqueue.ReturnType.FullName == "System.Boolean",
            "RelayOutbox.Enqueue(string, RelayStreamKind, byte[]) is a public instance method returning bool");

        // 8. Whatever this CLR can actually run. CaptureRelay's own static initializer touches
        //    Harmony and the game's ZRpc/ZNetPeer types, none of which are native like
        //    ZSteamSocket, so this is expected to succeed; degrade only on an offline JIT limit.
        Type? live = null;
        try { live = plugin.GetType("BetterPerformance.CaptureRelay", true); }
        catch (Exception exception) when (exception is TypeLoadException || exception is FileNotFoundException)
        { Console.WriteLine("STATIC ONLY capture relay runtime block: " + exception.GetType().Name + "; every contract check above still ran"); }
        if (live != null)
        {
            const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
            try
            {
                Check((string)live.GetProperty("Status", PrivateStatic)!.GetValue(null)! == "disabled" &&
                    !(bool)live.GetProperty("Installed", PrivateStatic)!.GetValue(null)!,
                    "CaptureRelay starts uninstalled and disabled before Verify or Install ever run");
                live.GetMethod("Verify", PrivateStatic)!.Invoke(null, null);
                Check(true, "Verify() ran against the installed game build without throwing");
            }
            catch (Exception exception) when (Offline(exception))
            {
                Console.WriteLine("STATIC ONLY capture relay runtime block: " + exception.GetType().Name + "; every contract check above still ran");
            }
        }

        Console.WriteLine("Capture relay: " + checks + " static game-contract checks; the mirrored file requires a live two-peer session to validate.");
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
