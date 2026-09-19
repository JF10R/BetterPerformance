using System.Reflection;
using Mono.Cecil;
using Cil = Mono.Cecil.Cil;

// The send gate lives next to ZSteamSocket, whose native socket interfaces a standalone CLR
// cannot type-load, so every game and Steamworks contract here is read from Mono.Cecil
// metadata. The module's own behaviour is exercised by reflection afterwards, and that part
// stands down with a STATIC ONLY note when the offline CLR refuses the types.
internal static class NetworkFlowGameTests
{
    internal static int Run(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Network flow: " + message);
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
            ?? throw new InvalidOperationException("Network flow: " + name + " is missing.");
        MethodDefinition Method(TypeDefinition type, string name, params string[] parameterTypes) =>
            type.Methods.SingleOrDefault(m => m.Name == name &&
                m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameterTypes))
            ?? throw new InvalidOperationException("Network flow: " + type.Name + "." + name + " with the expected parameters is missing.");
        FieldDefinition Field(TypeDefinition type, string name) => type.Fields.SingleOrDefault(f => f.Name == name)
            ?? throw new InvalidOperationException("Network flow: " + type.FullName + "." + name + " is missing.");
        // Ldc_I4 has three encodings; a constant read only from the widest form is missed.
        int? Int32Of(Cil.Instruction instruction)
        {
            if (instruction.OpCode == Cil.OpCodes.Ldc_I4) return (int)instruction.Operand;
            if (instruction.OpCode == Cil.OpCodes.Ldc_I4_S) return (sbyte)instruction.Operand;
            int index = new[]
            {
                Cil.OpCodes.Ldc_I4_0, Cil.OpCodes.Ldc_I4_1, Cil.OpCodes.Ldc_I4_2, Cil.OpCodes.Ldc_I4_3,
                Cil.OpCodes.Ldc_I4_4, Cil.OpCodes.Ldc_I4_5, Cil.OpCodes.Ldc_I4_6, Cil.OpCodes.Ldc_I4_7,
                Cil.OpCodes.Ldc_I4_8
            }.ToList().IndexOf(instruction.OpCode);
            return index < 0 ? (int?)null : index;
        }

        TypeDefinition zdoMan = Require(gameModule, "ZDOMan"), zdoPeer = Require(gameModule, "ZDOMan/ZDOPeer");
        TypeDefinition netPeer = Require(gameModule, "ZNetPeer"), net = Require(gameModule, "ZNet");
        TypeDefinition socketInterface = Require(gameModule, "ISocket"), steamSocket = Require(gameModule, "ZSteamSocket");

        // 1. The rewritten method and the argument the rewrite pushes in place of 10240.
        MethodDefinition sendZdos = Method(zdoMan, "SendZDOs", "ZDOMan/ZDOPeer", "System.Boolean");
        Check(!sendZdos.IsStatic && sendZdos.ReturnType.FullName == "System.Boolean" && sendZdos.HasBody,
            "ZDOMan.SendZDOs(ZDOPeer, bool) is an instance bool method with a body");
        Check(sendZdos.Parameters[0].Index == 0,
            "the ZDOPeer is the first parameter, so ldarg.1 loads it inside the instance method");
        FieldDefinition peerField = Field(zdoPeer, "m_peer");
        Check(!peerField.IsStatic && peerField.FieldType.FullName == "ZNetPeer",
            "ZDOMan.ZDOPeer.m_peer is the instance ZNetPeer the window is keyed from");
        FieldDefinition uid = Field(netPeer, "m_uid");
        Check(!uid.IsStatic && uid.FieldType.FullName == "System.Int64", "ZNetPeer.m_uid is the instance long key");
        FieldDefinition socket = Field(netPeer, "m_socket");
        Check(!socket.IsStatic && socket.FieldType.FullName == "ISocket", "ZNetPeer.m_socket is an instance ISocket");

        // 2. The exact vanilla shape the transpiler assumes: two 10240 constants (the gate
        //    and the batch budget), one 2048 minimum, one send-queue read. If any of these
        //    counts moves, the rewrite must refuse rather than guess which site is which.
        var sendBody = sendZdos.Body.Instructions;
        Check(sendBody.Count(i => Int32Of(i) == 10240) == 2,
            "ZDOMan.SendZDOs still carries exactly two 10240 send-gate constants");
        Check(sendBody.Count(i => Int32Of(i) == 2048) == 1,
            "ZDOMan.SendZDOs still carries exactly one 2048 minimum-batch constant");
        Check(sendBody.Count(i => (i.Operand as MethodReference)?.Name == "GetSendQueueSize") == 1,
            "ZDOMan.SendZDOs still reads the peer send queue exactly once");
        MethodDefinition queueSize = Method(socketInterface, "GetSendQueueSize");
        Check(queueSize.ReturnType.FullName == "System.Int32" && !queueSize.IsStatic,
            "ISocket.GetSendQueueSize() is the instance int the gate compares against");
        Check(steamSocket.Interfaces.Any(i => i.InterfaceType.FullName == "ISocket"), "ZSteamSocket still implements ISocket");
        FieldDefinition connection = Field(steamSocket, "m_con");
        Check(!connection.IsStatic && connection.IsPrivate && connection.FieldType.FullName == "Steamworks.HSteamNetConnection",
            "ZSteamSocket.m_con is the private HSteamNetConnection the policy addresses");
        FieldDefinition handle = Field(Require(steamModule, "Steamworks.HSteamNetConnection"), "m_HSteamNetConnection");
        Check(!handle.IsStatic && handle.FieldType.FullName == "System.UInt32",
            "HSteamNetConnection.m_HSteamNetConnection is the uint passed as the per-connection config scope");

        // 3. The peer lifetime hooks: the disconnect the module forgets on, and the list the
        //    interval reconciliation walks for a peer that left another way.
        MethodDefinition disconnect = Method(net, "Disconnect", "ZNetPeer");
        Check(!disconnect.IsStatic && disconnect.IsPublic && disconnect.ReturnType.FullName == "System.Void",
            "ZNet.Disconnect(ZNetPeer) is a public instance void method");
        FieldDefinition peers = Field(net, "m_peers");
        Check(!peers.IsStatic && peers.FieldType.FullName == "System.Collections.Generic.List`1<ZNetPeer>",
            "ZNet.m_peers is the instance List<ZNetPeer> the reconciliation reads");
        MethodDefinition dedicatedCheck = Method(net, "IsDedicated");
        Check(!dedicatedCheck.IsStatic && dedicatedCheck.ReturnType.FullName == "System.Boolean",
            "ZNet.IsDedicated() is the predicate that picks the client or game-server Steam API");

        // 4. Both Steamworks entry points, with the exact signatures the module calls.
        TypeDefinition clientSockets = Require(steamModule, "Steamworks.SteamNetworkingSockets");
        TypeDefinition serverSockets = Require(steamModule, "Steamworks.SteamGameServerNetworkingSockets");
        TypeDefinition clientUtils = Require(steamModule, "Steamworks.SteamNetworkingUtils");
        TypeDefinition serverUtils = Require(steamModule, "Steamworks.SteamGameServerNetworkingUtils");
        string[] statusParameters =
        {
            "Steamworks.HSteamNetConnection", "Steamworks.SteamNetConnectionRealTimeStatus_t&",
            "System.Int32", "Steamworks.SteamNetConnectionRealTimeLaneStatus_t&"
        };
        foreach (TypeDefinition api in new[] { clientSockets, serverSockets })
        {
            MethodDefinition status = Method(api, "GetConnectionRealTimeStatus", statusParameters);
            Check(status.IsStatic && status.IsPublic && status.ReturnType.FullName == "Steamworks.EResult",
                api.Name + ".GetConnectionRealTimeStatus returns EResult with the expected parameters");
            MethodDefinition info = Method(api, "GetConnectionInfo", "Steamworks.HSteamNetConnection", "Steamworks.SteamNetConnectionInfo_t&");
            Check(info.IsStatic && info.IsPublic && info.ReturnType.FullName == "System.Boolean",
                api.Name + ".GetConnectionInfo is the public static bool the relay test reads");
        }
        string[] configParameters =
        {
            "Steamworks.ESteamNetworkingConfigValue", "Steamworks.ESteamNetworkingConfigScope",
            "System.IntPtr", "Steamworks.ESteamNetworkingConfigDataType", "System.IntPtr"
        };
        foreach (TypeDefinition api in new[] { clientUtils, serverUtils })
        {
            MethodDefinition set = Method(api, "SetConfigValue", configParameters);
            Check(set.IsStatic && set.IsPublic && set.ReturnType.FullName == "System.Boolean",
                api.Name + ".SetConfigValue is the public static bool the rate policy writes through");
        }

        // 5. The status fields the window is computed from, and the info fields the relay
        //    test reads. A silent type change here would compute a window from nonsense.
        TypeDefinition realTime = Require(steamModule, "Steamworks.SteamNetConnectionRealTimeStatus_t");
        var statusFields = new[]
        {
            ("m_nPing", "System.Int32"), ("m_nSendRateBytesPerSecond", "System.Int32"),
            ("m_cbPendingReliable", "System.Int32"), ("m_cbPendingUnreliable", "System.Int32"),
            ("m_cbSentUnackedReliable", "System.Int32"), ("m_flConnectionQualityLocal", "System.Single")
        };
        string wrongType = string.Join(", ", statusFields
            .Where(f => Field(realTime, f.Item1).FieldType.FullName != f.Item2).Select(f => f.Item1));
        Check(wrongType.Length == 0, "SteamNetConnectionRealTimeStatus_t fields changed type: " + wrongType);
        TypeDefinition connectionInfo = Require(steamModule, "Steamworks.SteamNetConnectionInfo_t");
        Check(Field(connectionInfo, "m_nFlags").FieldType.FullName == "System.Int32",
            "SteamNetConnectionInfo_t.m_nFlags is the int the relayed bit is masked out of");
        Check(connectionInfo.Fields.Any(f => f.Name == "m_idPOPRelay"),
            "SteamNetConnectionInfo_t.m_idPOPRelay is the second relay signal the test reads");

        // 6. The config enumeration members the policy names, with their wire values.
        TypeDefinition scope = Require(steamModule, "Steamworks.ESteamNetworkingConfigScope");
        FieldDefinition perConnection = Field(scope, "k_ESteamNetworkingConfig_Connection");
        Check(perConnection.HasConstant && Convert.ToInt32(perConnection.Constant) == 4,
            "ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection is still 4");
        TypeDefinition configValue = Require(steamModule, "Steamworks.ESteamNetworkingConfigValue");
        foreach (string member in new[] { "k_ESteamNetworkingConfig_SendRateMin", "k_ESteamNetworkingConfig_SendRateMax", "k_ESteamNetworkingConfig_SendBufferSize" })
            Check(configValue.Fields.Any(f => f.Name == member), "ESteamNetworkingConfigValue." + member + " exists");
        Check(Field(Require(steamModule, "Steamworks.ESteamNetworkingConfigDataType"), "k_ESteamNetworkingConfig_Int32").HasConstant,
            "ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32 exists");

        // 7. Vanilla still pins the send rate globally, which is the reason a per-connection
        //    override is needed at all. A NOTE, not a failure, when the game stops.
        MethodDefinition register = Method(steamSocket, "RegisterGlobalCallbacks");
        var registerBody = register.Body.Instructions;
        int sendRateMin = Convert.ToInt32(Field(configValue, "k_ESteamNetworkingConfig_SendRateMin").Constant);
        int sendRateMax = Convert.ToInt32(Field(configValue, "k_ESteamNetworkingConfig_SendRateMax").Constant);
        bool pinsRate = registerBody.Any(i => Int32Of(i) == sendRateMin) && registerBody.Any(i => Int32Of(i) == sendRateMax) &&
            registerBody.Any(i => Int32Of(i) == 153600);
        Console.WriteLine(pinsRate
            ? "NOTE: ZSteamSocket.RegisterGlobalCallbacks still pins SendRateMin/SendRateMax to 153600 B/s globally; the per-connection policy is required"
            : "NOTE: ZSteamSocket.RegisterGlobalCallbacks no longer pins the send rate to 153600 B/s; re-read the rate policy, it may now be redundant");
        Check(registerBody.Count(i => (i.Operand as MethodReference)?.Name == "SetConfigValue") >= 2,
            "RegisterGlobalCallbacks still configures Steam through SetConfigValue");

        // 8. Our side of the contract, read from the built plugin.
        TypeDefinition flow = Require(pluginModule, "BetterPerformance.NetworkFlow");
        MethodDefinition window = Method(flow, "Window", "System.Object");
        Check(window.IsStatic && window.ReturnType.FullName == "System.Int32",
            "NetworkFlow.Window(object) is the static int the rewritten site calls");
        MethodDefinition transpile = flow.Methods.SingleOrDefault(m => m.Name == "Transpile")
            ?? throw new InvalidOperationException("Network flow: NetworkFlow.Transpile is missing.");
        var transpileBody = transpile.Body.Instructions;
        Check(transpileBody.Any(i => (i.Operand as FieldReference)?.Name == "Ldarg_1"),
            "the transpiler pushes the ZDOPeer argument with ldarg.1");
        foreach (string moved in new[] { "labels", "blocks" })
            Check(transpileBody.Any(i => (i.Operand as MethodReference)?.DeclaringType.Name == "CodeInstruction" &&
                    (i.Operand as MethodReference)!.Name == "get_" + moved) ||
                transpileBody.Any(i => (i.Operand as FieldReference)?.Name == moved),
                "the transpiler carries the original instruction's " + moved + " onto the replacement");
        var literals = new HashSet<string>(flow.Methods.Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions)
            .Where(i => i.OpCode == Cil.OpCodes.Ldstr)
            .Select(i => (string)i.Operand));
        string missingGauges = string.Join(", ", new[]
        {
            "net_flow_window_min_bytes", "net_flow_window_max_bytes", "net_flow_updates", "net_flow_backoffs",
            "net_flow_recoveries", "net_flow_unknown_rate", "net_flow_peers", "net_flow_lan_links",
            "net_flow_wan_links", "net_flow_rate_sets", "net_flow_failures"
        }.Where(name => !literals.Contains(name)));
        Check(missingGauges.Length == 0, "gauges no longer exported: " + missingGauges);
        string missingLabels = string.Join(", ", new[] { "net_flow_status", "net_flow_enabled", "net_flow_scope" }
            .Where(name => !literals.Contains(name)));
        Check(missingLabels.Length == 0, "labels no longer exported: " + missingLabels);
        string missingStatus = string.Join(", ", new[]
        {
            "yield_to_betternetworking", "foreign_patch:", "unavailable_unexpected_shape", "failed", "AdaptiveFlowEnabled"
        }.Where(name => !literals.Contains(name)));
        Check(missingStatus.Length == 0, "NetworkFlow can no longer report: " + missingStatus);
        Check(literals.Contains("CW_Jesse.BetterNetworking"), "the module still recognises BetterNetworking by plugin id and yields to it");

        // 9. The module's own shape guard, two-sided against the live body. Loading the
        //    game types this needs can fail outside Unity; the Cecil checks above stand.
        try { checks += Behaviour(game, plugin); }
        catch (Exception exception) when (Offline(exception))
        {
            Console.WriteLine("STATIC ONLY network flow behaviour: this CLR cannot load the native socket types; Cecil contract checks above still ran");
        }

        Console.WriteLine("Network flow: " + checks + " static game-contract checks; the adaptive window requires a live multiplayer session to validate.");
        return checks;
    }

    private static bool Offline(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
            if (current is TypeLoadException || current is FileNotFoundException || current is TypeInitializationException) return true;
        return false;
    }

    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

    private static int Behaviour(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Network flow: " + message);
            checks++;
        }
        Type flow = plugin.GetType("BetterPerformance.NetworkFlow", true)!;
        Type zdoMan = game.GetType("ZDOMan", true)!, peer = game.GetType("ZDOMan+ZDOPeer", true)!;
        var sendZdos = zdoMan.GetMethod("SendZDOs", Declared, null, new[] { peer, typeof(bool) }, null)!;
        var queueSize = game.GetType("ISocket", true)!.GetMethod("GetSendQueueSize", Declared, null, Type.EmptyTypes, null)!;

        // The guard accepts the live body and rejects each single-site mutation of it, so a
        // future body that merely resembles the gate is refused instead of rewritten.
        var body = HarmonyLib.PatchProcessor.GetOriginalInstructions(sendZdos);
        var guard = flow.GetMethod("RequireSendWindowShape", PrivateStatic)!;
        guard.Invoke(null, new object[] { body, queueSize });
        checks++;
        bool Window(HarmonyLib.CodeInstruction i) =>
            i.opcode == System.Reflection.Emit.OpCodes.Ldc_I4 && i.operand is int value && value == 10240;
        var withoutOneWindow = body.Where((i, index) => index != body.FindIndex(Window)).ToList();
        Check(Rejects(guard, new object[] { withoutOneWindow, queueSize }), "a body with only one 10240 constant is rejected");
        Check(Rejects(guard, new object[] { body.Concat(new[] { new HarmonyLib.CodeInstruction(System.Reflection.Emit.OpCodes.Ldc_I4, 10240) }).ToList(), queueSize }),
            "a body with a third 10240 constant is rejected");
        Check(Rejects(guard, new object[] { body.Where(i => !(i.opcode == System.Reflection.Emit.OpCodes.Ldc_I4 && Equals(i.operand, 2048))).ToList(), queueSize }),
            "a body without the 2048 minimum-batch constant is rejected");
        Check(Rejects(guard, new object[] { body.Where(i => !Equals(i.operand, queueSize)).ToList(), queueSize }),
            "a body that no longer reads the send queue is rejected");

        var hasConfig = flow.GetMethod("HasSetConfigValue", PrivateStatic)!;
        foreach (string api in new[] { "Steamworks.SteamNetworkingUtils", "Steamworks.SteamGameServerNetworkingUtils" })
            Check((bool)hasConfig.Invoke(null, new object[] { SteamType(plugin, api) })!, api + ".SetConfigValue is resolved by the module");
        Check(!(bool)hasConfig.Invoke(null, new object[] { typeof(string) })!, "a type without SetConfigValue is rejected");

        // Default configuration installs nothing, and telemetry exists either way.
        var config = new BepInEx.Configuration.ConfigFile(Path.Combine(Path.GetTempPath(), "bp-flow-" + Guid.NewGuid().ToString("N") + ".cfg"), false) { SaveOnConfigSet = false };
        var log = new BepInEx.Logging.ManualLogSource("NetworkFlowVerification");
        log.LogEvent += (_, entry) => Console.WriteLine(entry.Data);
        flow.GetMethod("Install", PrivateStatic)!.Invoke(null, new object[] { config, log });
        Check((string)flow.GetProperty("Status", PrivateStatic)!.GetValue(null)! == "disabled" &&
            HarmonyLib.Harmony.GetPatchInfo(sendZdos)?.Owners.Any(o => o.EndsWith("NetworkFlow")) != true,
            "the default (off) configuration installs nothing on SendZDOs");
        var option = (BepInEx.Configuration.ConfigEntry<bool>)config[new BepInEx.Configuration.ConfigDefinition("Network", "AdaptiveFlowEnabled")];
        Check(!option.Value && !(bool)option.DefaultValue, "Network.AdaptiveFlowEnabled defaults to false");
        option.Value = true;
        flow.GetMethod("Install", PrivateStatic)!.Invoke(null, new object[] { config, log });
        string status = (string)flow.GetProperty("Status", PrivateStatic)!.GetValue(null)!;
        bool installed = (bool)flow.GetProperty("Installed", PrivateStatic)!.GetValue(null)!;
        Check(status == "installed" || status == "unavailable_unexpected_shape" || status.StartsWith("foreign_patch:"),
            "Install with the switch on reports a status instead of throwing (actual=" + status + ")");
        Check(installed == (bool)flow.GetProperty("Enabled", PrivateStatic)!.GetValue(null)!, "Enabled follows Installed once the switch is on");
        Check(!installed || HarmonyLib.Harmony.GetPatchInfo(sendZdos)!.Prefixes.All(p => !p.owner.EndsWith("NetworkFlow")),
            "SendZDOs carries no prefix that could skip the original");

        // The hook returns the vanilla constant for anything it cannot key, and never throws.
        var windowHook = flow.GetMethod("Window", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        Check((int)windowHook.Invoke(null, new object?[] { null })! == 10240, "Window(null) returns the vanilla 10240 gate");
        Check((int)windowHook.Invoke(null, new object[] { "not a peer" })! == 10240, "Window of a non-peer returns the vanilla 10240 gate");

        Type number = plugin.GetType("BetterPerformance.Core.NumberValue", true)!, text = plugin.GetType("BetterPerformance.Core.TextValue", true)!;
        var sample = flow.GetMethod("Sample", PrivateStatic)!;
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
        foreach (string name in new[]
        {
            "net_flow_window_min_bytes", "net_flow_window_max_bytes", "net_flow_updates", "net_flow_backoffs",
            "net_flow_recoveries", "net_flow_unknown_rate", "net_flow_peers", "net_flow_lan_links",
            "net_flow_wan_links", "net_flow_rate_sets", "net_flow_failures"
        })
            Check(counters.ContainsKey(name), name + " is exported");
        foreach (string name in new[] { "net_flow_status", "net_flow_enabled", "net_flow_scope" })
            Check(labelNames.Contains(name), name + " is exported");
        Check(Numbers(out _).Values.All(value => value == 0), "Sample drains its interval counters");

        flow.GetMethod("Uninstall", PrivateStatic)!.Invoke(null, null);
        Check(!(bool)flow.GetProperty("Installed", PrivateStatic)!.GetValue(null)! &&
            (string)flow.GetProperty("Status", PrivateStatic)!.GetValue(null)! == "disabled", "Uninstall removes the hooks");
        // Harmony's unpatch rescans every patched method and a standalone CLR can throw there
        // on an unrelated Unity type; the module swallows that, so offline the patch may survive.
        if (HarmonyLib.Harmony.GetPatchInfo(sendZdos)?.Transpilers.Any(p => p.owner.EndsWith("NetworkFlow")) == true)
            Console.WriteLine("STATIC ONLY network flow unpatch: the rewrite survived UnpatchSelf on this CLR; contract checks above still ran");
        else checks++;
        return checks;
    }

    private static Type SteamType(Assembly plugin, string name)
    {
        foreach (var reference in plugin.GetReferencedAssemblies())
        {
            if (!reference.Name!.StartsWith("com.rlabrecque", StringComparison.Ordinal)) continue;
            Type? type = Assembly.Load(reference).GetType(name, false);
            if (type != null) return type;
        }
        throw new InvalidOperationException("Network flow: " + name + " is not reachable from the plugin's references.");
    }

    private static bool Rejects(MethodInfo guard, object[] arguments)
    {
        try { guard.Invoke(null, arguments); return false; }
        catch (TargetInvocationException exception) when (exception.InnerException is InvalidOperationException) { return true; }
    }
}
