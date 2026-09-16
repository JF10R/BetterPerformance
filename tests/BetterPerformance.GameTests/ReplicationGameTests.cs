using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using Mono.Cecil;
using BepInEx.Configuration;

// ZSyncTransform and RandomFlyingBird implement IMonoUpdater, which standalone .NET
// Framework cannot type-load ("Non-abstract, non-.cctor method in an interface"), so
// their contracts are read from metadata with Mono.Cecil. Everything on the ZDOMan
// send path loads normally and is checked by reflection.
internal static class ReplicationGameTests
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

    internal static int Run(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Replication cadence: " + message);
            checks++;
        }
        Type TypeOf(string name) => game.GetType(name, true)!;

        // 1. The send path this work hooks, and the private peer state it reads.
        Type zdoMan = TypeOf("ZDOMan"), zdo = TypeOf("ZDO"), zdoId = TypeOf("ZDOID");
        Type peerType = zdoMan.GetNestedType("ZDOPeer", BindingFlags.NonPublic)!;
        Check(peerType != null && !peerType.IsPublic, "ZDOMan.ZDOPeer is a private nested type");
        Type zdoList = typeof(List<>).MakeGenericType(zdo);
        var createSyncList = zdoMan.GetMethod("CreateSyncList", Declared, null, new[] { peerType!, zdoList }, null);
        var addForceSend = zdoMan.GetMethod("AddForceSendZdos", Declared, null, new[] { peerType!, zdoList }, null);
        var sendZdos = zdoMan.GetMethod("SendZDOs", Declared, null, new[] { peerType!, typeof(bool) }, null);
        Check(createSyncList != null && createSyncList.ReturnType == typeof(void) && !createSyncList.IsStatic,
            "ZDOMan.CreateSyncList(ZDOPeer, List<ZDO>) is the instance selection method");
        Check(zdoMan.GetMethods(Declared).Count(method => method.Name == "CreateSyncList") == 1,
            "CreateSyncList has no overloads: one postfix covers the client and server paths");
        Check(addForceSend != null && addForceSend.ReturnType == typeof(void),
            "ZDOMan.AddForceSendZdos(ZDOPeer, List<ZDO>) still inserts the forced entries");
        Check(sendZdos != null && sendZdos.ReturnType == typeof(bool), "ZDOMan.SendZDOs(ZDOPeer, bool) is the send entry point");

        var forceSend = peerType!.GetField("m_forceSend", Declared);
        var peerField = peerType.GetField("m_peer", Declared);
        var zdos = peerType.GetField("m_zdos", Declared);
        Check(forceSend != null && forceSend.FieldType == typeof(HashSet<>).MakeGenericType(zdoId), "ZDOPeer.m_forceSend is a HashSet<ZDOID>");
        Check(peerField != null && peerField.FieldType == TypeOf("ZNetPeer"), "ZDOPeer.m_peer is a ZNetPeer");
        Check(zdos != null && zdos.FieldType.IsGenericType && zdos.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>) &&
            zdos.FieldType.GetGenericArguments()[0] == zdoId, "ZDOPeer.m_zdos is a Dictionary keyed by ZDOID");
        Type infoType = zdos!.FieldType.GetGenericArguments()[1];
        var syncTime = infoType.GetField("m_syncTime", Declared);
        Check(infoType.IsValueType && syncTime != null && syncTime.FieldType == typeof(float) && !syncTime.IsStatic,
            "PeerZDOInfo.m_syncTime is an instance float on a value type");
        foreach (string name in new[] { "m_dataRevision", "m_ownerRevision" })
            Check(infoType.GetField(name, Declared) != null, "PeerZDOInfo." + name + " still carries the revision accounting this work never writes");

        // 2. m_zdosSent is assigned once inside the send loop, so the shortfall against
        //    the selected list counts byte-budget truncations exactly.
        var sentField = zdoMan.GetField("m_zdosSent", Declared);
        Check(sentField != null && sentField.FieldType == typeof(int) && !sentField.IsStatic, "ZDOMan.m_zdosSent is an instance int");
        var sendBody = PatchProcessor.GetOriginalInstructions(sendZdos!);
        Check(sendBody.Count(instruction => instruction.opcode == System.Reflection.Emit.OpCodes.Stfld && Equals(instruction.operand, sentField)) == 1,
            "SendZDOs assigns m_zdosSent exactly once");
        Check(sendBody.Any(instruction => instruction.operand is MethodInfo called && called.Name == "Size" && called.DeclaringType == TypeOf("ZPackage")),
            "SendZDOs still exits its loop on the serialized package size");

        // 3. The velocity contract, read from metadata: the owner writes s_velHash and
        //    the non-owner reads it, so a remote client needs no plugin of its own.
        checks += Metadata(game.Location);

        // 4. The reflective reader: a real Dictionary<ZDOID, PeerZDOInfo> is read back
        //    through the generated accessor without naming the private struct.
        Type access = plugin.GetType("BetterPerformance.ZdoPeerAccess", true)!;
        access.GetMethod("Resolve", PrivateStatic)!.Invoke(null, null);
        Check((bool)access.GetProperty("Resolved", PrivateStatic)!.GetValue(null)!, "the private peer layout resolves against the installed game");
        Check((Type)access.GetProperty("PeerType", PrivateStatic)!.GetValue(null)! == peerType, "the resolved peer type is ZDOMan.ZDOPeer");
        object reader = access.GetProperty("SyncTimes", PrivateStatic)!.GetValue(null)!;
        object map = Activator.CreateInstance(zdos.FieldType)!;
        object uid = Activator.CreateInstance(zdoId)!;
        object info = Activator.CreateInstance(infoType, new object[] { 7u, (ushort)3, 1.25f })!;
        zdos.FieldType.GetMethod("set_Item")!.Invoke(map, new[] { uid, info });
        object[] arguments = { map, uid, 0f };
        Check((bool)reader.GetType().GetMethod("TryGet")!.Invoke(reader, arguments)! && (float)arguments[2] == 1.25f,
            "the generated accessor reads m_syncTime from the private info struct");
        object peer = FormatterServices.GetUninitializedObject(peerType);
        zdos.SetValue(peer, map);
        forceSend!.SetValue(peer, Activator.CreateInstance(forceSend.FieldType));
        Check(ReferenceEquals(access.GetMethod("Zdos", PrivateStatic)!.Invoke(null, new[] { peer }), map), "the peer's sync map is read back by reference");
        Check(access.GetMethod("ForceSend", PrivateStatic)!.Invoke(null, new[] { peer }) != null, "the peer's forced-send set is read back");
        Check(access.GetMethod("NetPeer", PrivateStatic)!.Invoke(null, new[] { peer }) == null, "an absent ZNetPeer is reported rather than thrown");

        // 5. Default configuration installs nothing at all.
        Type cadence = plugin.GetType("BetterPerformance.ReplicationCadence", true)!;
        var logSource = new BepInEx.Logging.ManualLogSource("ReplicationVerification");
        ConfigFile Configuration() => new ConfigFile(Path.Combine(Path.GetTempPath(), "bp-replication-" + Guid.NewGuid().ToString("N") + ".cfg"), false) { SaveOnConfigSet = false };
        cadence.GetMethod("Install", PrivateStatic)!.Invoke(null, new object[] { Configuration(), logSource });
        Check(!(bool)cadence.GetProperty("Installed", PrivateStatic)!.GetValue(null)!, "default configuration installs no replication patch");
        Check(!(bool)cadence.GetProperty("Enabled", PrivateStatic)!.GetValue(null)!, "default configuration leaves the runtime gate closed");
        Check((string)cadence.GetProperty("Status", PrivateStatic)!.GetValue(null)! == "disabled", "default configuration reports disabled");
        // Other verifications share this process and leave their own probes on the
        // same method, so absence is asserted by owner, not by the method being clean.
        Check(Harmony.GetPatchInfo(createSyncList!)?.Owners.Any(owner => owner.EndsWith("ReplicationCadence")) != true,
            "default configuration adds no replication patch to CreateSyncList");

        // 6. Enabled, the postfix binds although the peer parameter is inaccessible.
        //    Bird velocity cannot install here: ZSyncTransform does not type-load
        //    offline, and a refused installer must leave the rest working.
        var enabledConfig = Configuration();
        enabledConfig.Bind("Replication", "CosmeticResendIntervalEnabled", false).Value = true;
        enabledConfig.Bind("Replication", "BirdVelocityEnabled", false).Value = true;
        try
        {
            cadence.GetMethod("Install", PrivateStatic)!.Invoke(null, new object[] { enabledConfig, logSource });
            var selection = Harmony.GetPatchInfo(createSyncList!);
            Check(selection != null && selection.Postfixes.Any(patch => patch.owner.EndsWith("ReplicationCadence")),
                "Harmony binds the private ZDOPeer parameter as object");
            Check(selection!.Prefixes.Concat(selection.Transpilers).All(patch => !patch.owner.EndsWith("ReplicationCadence")),
                "the selection hook is a postfix only: the native list is built unchanged");
            Check((bool)cadence.GetProperty("Installed", PrivateStatic)!.GetValue(null)!, "the enabled configuration installs");
            Check((string)cadence.GetProperty("Status", PrivateStatic)!.GetValue(null)! == "installed_resend_interval_only",
                "an installer refused by the offline runtime is contained and reported");
        }
        finally { cadence.GetMethod("Uninstall", PrivateStatic)!.Invoke(null, null); }
        Check(!(bool)cadence.GetProperty("Installed", PrivateStatic)!.GetValue(null)!, "uninstall removes the patches and closes the gate");
        Check(Harmony.GetPatchInfo(createSyncList!)?.Postfixes.Any(patch => patch.owner.EndsWith("ReplicationCadence")) != true,
            "uninstall leaves no selection postfix behind");

        // 7. Telemetry is independent of the switches and observational only.
        Type telemetry = plugin.GetType("BetterPerformance.ReplicationTelemetry", true)!;
        var telemetryConfig = Configuration();
        telemetryConfig.Bind("Diagnostics", "ReplicationCadenceTelemetryEnabled", true).Value = false;
        telemetry.GetMethod("Install", PrivateStatic)!.Invoke(null, new object[] { telemetryConfig, logSource });
        Check(!(bool)telemetry.GetProperty("Installed", PrivateStatic)!.GetValue(null)!, "telemetry honours its own diagnostics switch");
        try
        {
            telemetry.GetMethod("Install", PrivateStatic)!.Invoke(null, new object[] { Configuration(), logSource });
            var selection = Harmony.GetPatchInfo(createSyncList!);
            var send = Harmony.GetPatchInfo(sendZdos!);
            Check(selection != null && selection.Postfixes.Any(patch => patch.owner.EndsWith("ReplicationTelemetry")), "telemetry observes the selection pass");
            Check((bool)telemetry.GetProperty("Installed", PrivateStatic)!.GetValue(null)!, "telemetry installs without the optional switches");
            string status = (string)telemetry.GetProperty("Status", PrivateStatic)!.GetValue(null)!;
            // SendZDOs cannot always be detoured offline; losing the byte-budget
            // counters must not cost the selection histograms.
            Check(status == "installed" || status == "installed_without_send_counters", "telemetry reports which half installed, actual=" + status);
            Check(status == "installed_without_send_counters" ||
                (send != null && send.Prefixes.Concat(send.Postfixes).Any(patch => patch.owner.EndsWith("ReplicationTelemetry"))),
                "an installed send counter is a prefix and postfix on SendZDOs");
            Check(send == null || send.Transpilers.All(patch => !patch.owner.EndsWith("ReplicationTelemetry")), "telemetry never rewrites the send");
        }
        finally { telemetry.GetMethod("Uninstall", PrivateStatic)!.Invoke(null, null); }
        Console.WriteLine("PASS " + checks + " replication cadence checks; no game methods invoked");
        return checks;
    }

    private static int Metadata(string assemblyPath)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Replication cadence metadata: " + message);
            checks++;
        }
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(assemblyPath));
        using var module = ModuleDefinition.ReadModule(assemblyPath, new ReaderParameters { AssemblyResolver = resolver });
        TypeDefinition TypeOf(string name) => module.GetType(name) ?? throw new InvalidOperationException(name + " is missing.");
        bool Field(TypeDefinition type, string name, string fieldType) =>
            type.Fields.Any(field => field.Name == name && field.FieldType.FullName == fieldType);
        MethodDefinition Method(TypeDefinition type, string name) => type.Methods.Single(method => method.Name == name);
        bool Reads(MethodDefinition method, string field) =>
            method.HasBody && method.Body.Instructions.Any(instruction => instruction.Operand is FieldReference read && read.Name == field);
        bool Calls(MethodDefinition method, string declaring, string name) =>
            method.HasBody && method.Body.Instructions.Any(instruction =>
                instruction.Operand is MethodReference called && called.Name == name && called.DeclaringType.Name == declaring);

        TypeDefinition sync = TypeOf("ZSyncTransform"), bird = TypeOf("RandomFlyingBird"), vars = TypeOf("ZDOVars");
        Check(vars.Fields.Any(field => field.Name == "s_velHash" && field.IsPublic && field.IsStatic && field.FieldType.FullName == "System.Int32"),
            "ZDOVars.s_velHash is a public static int");
        MethodDefinition ownerSync = Method(sync, "OwnerSync"), syncPosition = Method(sync, "SyncPosition"), onDisable = Method(sync, "OnDisable");
        Check(ownerSync.ReturnType.FullName == "System.Void" && !ownerSync.HasParameters && !ownerSync.IsStatic,
            "ZSyncTransform.OwnerSync() is the owner-side publication");
        Check(onDisable.ReturnType.FullName == "System.Void" && !onDisable.HasParameters, "ZSyncTransform.OnDisable() is available for per-instance cleanup");
        Check(Reads(ownerSync, "s_velHash") && Calls(ownerSync, "ZDO", "Set"), "OwnerSync writes s_velHash through ZDO.Set");
        Check(Reads(syncPosition, "s_velHash") && Calls(syncPosition, "ZDO", "GetVec3"),
            "SyncPosition reads s_velHash as a Vec3 on the non-owner, with no plugin on the receiving client");
        foreach (var (name, type) in new[] { ("m_body", "UnityEngine.Rigidbody"), ("m_nview", "ZNetView"), ("m_syncPosition", "System.Boolean") })
            Check(Field(sync, name, type), "ZSyncTransform." + name + " is a " + type);
        Check(Method(sync, "GetVelocity").Body.Instructions.Any(instruction => instruction.OpCode.Name.StartsWith("ldc.r4") || instruction.Operand is MethodReference),
            "ZSyncTransform.GetVelocity has a body to fall through to zero");

        MethodDefinition fixedUpdate = Method(bird, "CustomFixedUpdate");
        Check(fixedUpdate.ReturnType.FullName == "System.Void" && fixedUpdate.Parameters.Count == 1 &&
            fixedUpdate.Parameters[0].ParameterType.FullName == "System.Single", "RandomFlyingBird.CustomFixedUpdate(float) is the movement step");
        Check(Field(bird, "m_speed", "System.Single"), "RandomFlyingBird.m_speed is a float");
        Check(!bird.Fields.Any(field => field.FieldType.FullName == "UnityEngine.Rigidbody"),
            "RandomFlyingBird holds no Rigidbody, which is why the vanilla published velocity is zero");
        Check(Calls(fixedUpdate, "Transform", "set_position"), "RandomFlyingBird assigns transform.position directly");
        Console.WriteLine("PASS " + checks + " replication metadata checks (Mono.Cecil; types that cannot load offline)");
        return checks;
    }
}
