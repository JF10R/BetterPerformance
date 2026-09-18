using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using HarmonyLib;

internal static class InitialLoadingGameTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    // One token-independent fingerprint per method covers the client and the dedicated
    // server, and held unchanged across 1.0.14 and 1.0.15, where the four raw-byte hashes
    // it replaced all broke: those breaks were metadata renumbering, not logic.
    private const string CreateFingerprint = "9e86a318cf06f1b4adb27246d4a99ef5782f0b82f40ff54f6ab2ba1576c1f28c";
    private const string PokeFingerprint = "5c6f82835a71ea1bb30dcbe5b1c32454053607d9841bc1686bd4314b7024d313";

    internal static int Run(Assembly game, Assembly plugin)
    {
        int checks = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Initial loading: " + message); checks++; }
        var zone = game.GetType("ZoneSystem", true)!;
        var gameType = game.GetType("Game", true)!;
        var native = AccessTools.DeclaredMethod(zone, "CreateLocalZones");
        Check(native != null && native.GetParameters().Length == 1 &&
            native.GetParameters()[0].ParameterType.FullName == "UnityEngine.Vector3", "native vector parameter resolved without a compile-time Unity reference");
        var vector3 = native!.GetParameters()[0].ParameterType;
        var poke = AccessTools.DeclaredMethod(zone, "PokeLocalZone");
        Check(poke != null && poke.GetParameters().Length == 1 && poke.GetParameters()[0].ParameterType.FullName == "Vector2s",
            "native zone coordinate type resolved from its owning utility assembly");
        var update = AccessTools.DeclaredMethod(zone, "Update", Type.EmptyTypes);
        Check(native != null && !native.IsStatic && native.ReturnType == typeof(bool), "exact instance CreateLocalZones(Vector3) bool contract");
        Check(poke != null && !poke.IsStatic && poke.ReturnType == typeof(bool), "exact instance PokeLocalZone(Vector2s) bool contract");
        Check(update != null && !update.IsStatic && update.ReturnType == typeof(void), "exact Update boundary");
        foreach (string field in new[] { "m_firstSpawn", "m_requestRespawn" })
        {
            var member = AccessTools.DeclaredField(gameType, field);
            Check(member != null && !member.IsStatic && member.FieldType == typeof(bool), "native initial-respawn predicate " + field);
        }
        pluginAssembly = plugin;
        string createHash = Hash(native!), pokeHash = Hash(poke!);
        Check(createHash == CreateFingerprint && pokeHash == PokeFingerprint,
            "native fingerprints match the verified contract on this installation (computed create=" + createHash + " poke=" + pokeHash + ")");
        Check(native!.GetMethodBody()!.GetILAsByteArray()!.Length == 179 && poke!.GetMethodBody()!.GetILAsByteArray()!.Length == 97,
            "verified native body sizes");
        var module = plugin.GetType("BetterPerformance.InitialLoadingOptimization", true)!;
        var verify = module.GetMethod("VerifyNativeContracts", PrivateStatic)!;
        Check(verify != null && verify.GetParameters().Length == 0, "explicit no-argument production contract guard");
        verify!.Invoke(null, null);
        Check(true, "production native contract guard accepts verified installation");
        var verifyHash = module.GetMethod("VerifyHash", PrivateStatic)!;
        verifyHash.Invoke(null, new object[] { native, new[] { createHash.ToUpperInvariant() } });
        Check(true, "hash helper accepts the exact native body");
        void RejectHash(MethodInfo method, string[] allowed, string reason)
        {
            bool rejected = false;
            try { verifyHash.Invoke(null, new object[] { method, allowed }); }
            catch (TargetInvocationException exception) when (exception.InnerException is InvalidOperationException) { rejected = true; }
            Check(rejected, reason);
        }
        RejectHash(native, new[] { new string('0', 64) }, "wrong expected native hash rejected");
        RejectHash(typeof(InitialLoadingGameTests).GetMethod(nameof(ChangedContractFixture), PrivateStatic)!,
            new[] { CreateFingerprint.ToUpperInvariant() }, "changed synthetic body rejected without modifying game assemblies");
        var transpile = module.GetMethod("Transpile", PrivateStatic)!;
        var wrapper = module.GetMethod("CreateLocalZoneBurst", PrivateStatic)!;
        Check(wrapper != null && wrapper.IsStatic && wrapper.ReturnType == typeof(bool) &&
            wrapper.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { zone, vector3 }), "wrapper preserves native call stack signature");
        List<CodeInstruction> Apply(List<CodeInstruction> code) =>
            ((IEnumerable<CodeInstruction>)transpile.Invoke(null, new object[] { code })!).ToList();
        var original = PatchProcessor.GetOriginalInstructions(update!);
        Check(original.Count(i => i.Calls(native)) == 1, "actual native Update contains exactly one targeted call");
        var fixture = original.Select(i => new CodeInstruction(i)).ToList();
        int target = fixture.FindIndex(i => i.Calls(native));
        var generator = new DynamicMethod("InitialLoadingLabels", typeof(void), Type.EmptyTypes).GetILGenerator();
        fixture[target].labels.Add(generator.DefineLabel());
        fixture[target].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
        var snapshot = fixture.Select(i => (i.opcode, i.operand, labels: i.labels.ToArray(), blocks: i.blocks.ToArray())).ToArray();
        var patched = Apply(fixture);
        Check(patched.Count == snapshot.Length, "instruction count unchanged");
        for (int i = 0; i < patched.Count; i++)
        {
            Check(ReferenceEquals(patched[i], fixture[i]), "original instruction identity/order at " + i);
            Check(patched[i].labels.SequenceEqual(snapshot[i].labels) && patched[i].blocks.SequenceEqual(snapshot[i].blocks), "labels and exception blocks preserved at " + i);
            Check(i == target ? patched[i].opcode == OpCodes.Call && Equals(patched[i].operand, wrapper) :
                patched[i].opcode == snapshot[i].opcode && Equals(patched[i].operand, snapshot[i].operand), "only the unique call changes at " + i);
        }
        void Reject(List<CodeInstruction> code, string reason)
        {
            bool rejected = false;
            try { Apply(code); }
            catch (InvalidOperationException) { rejected = true; }
            catch (TargetInvocationException exception) when (exception.InnerException is InvalidOperationException) { rejected = true; }
            Check(rejected, reason);
        }
        Reject(original.Where(i => !i.Calls(native)).Select(i => new CodeInstruction(i)).ToList(), "zero native callsites rejected");
        var doubled = original.Select(i => new CodeInstruction(i)).ToList();
        doubled.Add(new CodeInstruction(OpCodes.Call, native));
        Reject(doubled, "multiple native callsites rejected");
        Console.WriteLine("Initial loading: " + checks + " fingerprint/native-contract/transpiler checks; no Unity zone creation invoked.");
        return checks;
    }
    // The plugin's own token-independent fingerprint, so the pin and the runtime guard
    // cannot drift apart; the raw-byte SHA-256 it replaced broke on two consecutive game
    // updates that left these bodies logically identical.
    private static Assembly? pluginAssembly;
    private static string Hash(MethodInfo method)
    {
        Type fingerprint = pluginAssembly!.GetType("BetterPerformance.IlFingerprint", true)!;
        return ((string)fingerprint.GetMethod("Compute", PrivateStatic)!.Invoke(null, new object[] { method })!).ToLowerInvariant();
    }
    private static bool ChangedContractFixture() => false;
}
