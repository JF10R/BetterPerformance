using System.Reflection;
using Mono.Cecil;
using Cil = Mono.Cecil.Cil;

// Game contracts behind PositionJumpSync, the post-arrival window of TeleportLoadingTelemetry and the
// 0.4.19 engine and zone labels; Mono.Cecil metadata only, never calls the game.
internal static class PositionJumpGameTests
{
    internal static int Run(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Position jump sync: " + message);
            checks++;
        }

        string managed = Path.GetDirectoryName(game.Location)!;
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managed);
        using var gameModule = ModuleDefinition.ReadModule(game.Location, new ReaderParameters { AssemblyResolver = resolver });
        TypeDefinition Require(string name) => gameModule.GetType(name)
            ?? throw new InvalidOperationException("Position jump sync: " + name + " is missing.");
        MethodDefinition Method(TypeDefinition type, string name, params string[] parameters) =>
            type.Methods.SingleOrDefault(m => m.Name == name && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters))
            ?? throw new InvalidOperationException("Position jump sync: " + type.Name + "." + name + "(" + string.Join(", ", parameters) + ") is missing.");
        IEnumerable<Cil.Instruction> Body(MethodDefinition method) => method.HasBody ? method.Body.Instructions : Enumerable.Empty<Cil.Instruction>();
        bool Calls(MethodDefinition method, string declaring, string name) => Body(method).Any(i =>
            (i.OpCode == Cil.OpCodes.Call || i.OpCode == Cil.OpCodes.Callvirt) && i.Operand is MethodReference called &&
            called.Name == name && called.DeclaringType.Name == declaring);
        bool Reads(MethodDefinition method, string field) => Body(method).Any(i => i.Operand is FieldReference f && f.Name == field &&
            (i.OpCode == Cil.OpCodes.Ldfld || i.OpCode == Cil.OpCodes.Ldsfld || i.OpCode == Cil.OpCodes.Ldflda));
        bool Stores(MethodDefinition method, string field) => Body(method).Any(i => i.OpCode == Cil.OpCodes.Stfld && i.Operand is FieldReference f && f.Name == field);
        bool LoadsString(MethodDefinition method, string value) => Body(method).Any(i => i.OpCode == Cil.OpCodes.Ldstr && (string)i.Operand == value);
        bool LoadsFloat(MethodDefinition method, float value) => Body(method).Any(i => i.OpCode == Cil.OpCodes.Ldc_R4 && (float)i.Operand == value);

        const string Vector3 = "UnityEngine.Vector3", List = "System.Collections.Generic.List`1<ZDO>";
        TypeDefinition net = Require("ZNet"), peer = Require("ZNetPeer"), zdoMan = Require("ZDOMan"), player = Require("Player");

        // 1. The client's position leaves every 2 s through SendServerSyncPlayerData; the server's list every 2 s.
        MethodDefinition periodic = Method(net, "SendPeriodicData", "System.Single");
        Check(LoadsFloat(periodic, 2f) && Calls(periodic, "ZNet", "SendServerSyncPlayerData") && Calls(periodic, "ZNet", "SendPlayerList"),
            "SendPeriodicData still sends the position and the player list every 2 s");
        MethodDefinition send = Method(net, "SendServerSyncPlayerData", "ZNetPeer");
        Check(!send.IsStatic && send.ReturnType.FullName == "System.Void" && Reads(send, "m_referencePosition") && LoadsString(send, "ServerSyncedPlayerData"),
            "SendServerSyncPlayerData(ZNetPeer) sends m_referencePosition as ServerSyncedPlayerData");
        MethodDefinition list = Method(net, "SendPlayerList");
        Check(!list.IsStatic && list.ReturnType.FullName == "System.Void" && LoadsString(list, "PlayerList") && Calls(list, "ZNet", "UpdatePlayerList"),
            "SendPlayerList() rebuilds and sends PlayerList");
        MethodDefinition receive = Method(net, "RPC_ServerSyncedPlayerData", "ZRpc", "ZPackage");
        Check(receive.ReturnType.FullName == "System.Void" && Calls(receive, "ZNet", "GetPeer") && Stores(receive, "m_refPos"),
            "RPC_ServerSyncedPlayerData stores the peer's m_refPos");
        MethodDefinition getPeer = Method(net, "GetPeer", "ZRpc");
        Check(!getPeer.IsStatic && getPeer.ReturnType.FullName == "ZNetPeer", "ZNet.GetPeer(ZRpc) returns ZNetPeer");
        Check(Method(net, "GetServerPeer").IsPublic && Method(net, "GetReferencePosition").IsPublic, "GetServerPeer and GetReferencePosition are public");
        Check(peer.Fields.Any(f => f.Name == "m_refPos" && f.FieldType.FullName == Vector3 && f.IsPublic) &&
            peer.Fields.Any(f => f.Name == "m_uid" && f.FieldType.FullName == "System.Int64" && f.IsPublic), "ZNetPeer.m_refPos and m_uid are public");
        Check(Method(peer, "IsReady").IsPublic, "ZNetPeer.IsReady() is public");

        // 2. Why the delay matters: the server streams objects around that position, the map reads the list,
        // and the owner's LateUpdate writes the moved position into the reference every frame.
        Check(Calls(Method(zdoMan, "CreateSyncList", "ZDOMan/ZDOPeer", List), "ZNetPeer", "GetRefPos"), "ZDOMan.CreateSyncList selects around the peer's reported position");
        Check(Calls(Method(Require("Minimap"), "UpdatePlayerPins", "System.Single"), "ZNet", "GetOtherPublicPlayers"), "Minimap player pins come from the player list");
        Check(Calls(Method(player, "LateUpdate"), "ZNet", "SetReferencePosition"), "Player.LateUpdate writes the reference position");

        // 3. The post-arrival pending count and the zone attribution.
        MethodDefinition create = Method(Require("ZNetScene"), "CreateObjects", List, List);
        Check(!create.IsStatic && create.ReturnType.FullName == "System.Void", "ZNetScene.CreateObjects(List<ZDO>, List<ZDO>) is an instance void");
        Check(Require("ZDO").Properties.Any(p => p.Name == "Created" && p.GetMethod != null && p.GetMethod.IsPublic), "ZDO.Created has a public getter");
        MethodDefinition zonePos = Method(Require("ZoneSystem"), "GetZonePos", "Vector2s");
        // Vector2s lives in another assembly; its public x/y are bound when the plugin compiles against the game.
        Check(zonePos.IsStatic && zonePos.IsPublic && zonePos.ReturnType.FullName == Vector3, "ZoneSystem.GetZonePos(Vector2s) is a public static Vector3");

        // 4. The frame-limit input labelled by EngineTelemetry: the fps limit comes from the Background graphics mode.
        TypeDefinition graphics = Require("GraphicsSettingsManager");
        Check(graphics.Fields.Any(f => f.Name == "m_isInBackground" && !f.IsStatic && f.FieldType.FullName == "System.Boolean"),
            "GraphicsSettingsManager.m_isInBackground is an instance bool");
        Check(Reads(Method(graphics, "GetCurrentGraphicsMode", "System.Boolean"), "m_isInBackground") &&
            Calls(Method(graphics, "RequestTargetFrameRateFromPreset"), "PresentManager", "RequestTargetFrameRate"),
            "the requested frame rate follows the Background graphics mode");

        // 5. The pure policy.
        Type policy = plugin.GetType("BetterPerformance.Core.PositionJumpPolicy", true)!;
        var isJump = policy.GetMethod("IsJump", BindingFlags.Public | BindingFlags.Static)!;
        Check((bool)isJump.Invoke(null, new object[] { 3000.0, 64.0 })! && !(bool)isJump.Invoke(null, new object[] { 10.0, 64.0 })!, "a portal is a jump, walking is not");
        return checks;
    }
}
