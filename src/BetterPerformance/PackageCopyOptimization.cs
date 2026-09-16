using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;

namespace BetterPerformance
{
    // The only optimized caller owns two fresh local packages. General RPC/package
    // writes remain native because their source lifetime/ownership is less constrained.
    internal static class PackageCopyOptimization
    {
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".PackageCopyOptimization");
        private static readonly MethodInfo WritePackage = AccessTools.DeclaredMethod(typeof(ZPackage), "Write", new[] { typeof(ZPackage) });
        private static readonly MethodInfo GetArray = AccessTools.DeclaredMethod(typeof(ZPackage), "GetArray", Type.EmptyTypes);
        private static readonly MethodInfo SerializeZdo = AccessTools.DeclaredMethod(typeof(ZDO), "Serialize", new[] { typeof(ZPackage) });
        private static readonly MethodInfo ClearPackage = AccessTools.DeclaredMethod(typeof(ZPackage), "Clear", Type.EmptyTypes);
        private static readonly ConstructorInfo PackageConstructor = AccessTools.Constructor(typeof(ZPackage), Type.EmptyTypes);
        // Constructor/Clear patches can retain or expose the otherwise local source,
        // invalidating exclusive ownership even when serialization itself is native.
        private static readonly MethodBase[] ConflictMethods = { WritePackage, GetArray, SerializeZdo, ClearPackage, PackageConstructor };
        private static AccessTools.FieldRef<ZPackage, BinaryWriter>? writer;
        private static AccessTools.FieldRef<ZPackage, MemoryStream>? stream;
        [ThreadStatic] private static bool scopeAllowed;
        private static volatile bool enabled;
        private static long calls, observedBytes, copiedCalls, copiedBytes, fallbacks, failures, conflictScopes;
        internal static bool Enabled { get => enabled; set => enabled = value; }
        internal static bool Installed { get; private set; }
        internal static string Status { get; private set; } = "disabled";
        internal struct ScopeState { internal bool Previous; }

        internal static void Install(ManualLogSource logger)
        {
            try
            {
                ValidateContracts();
                var method = AccessTools.DeclaredMethod(typeof(ZDOMan), "SendZDOs", new[] { AccessTools.Inner(typeof(ZDOMan), "ZDOPeer"), typeof(bool) });
                if (method == null || method.ReturnType != typeof(bool)) throw new InvalidOperationException("Unsupported SendZDOs method.");
                Patches.Patch(method, prefix: new HarmonyMethod(typeof(PackageCopyOptimization), nameof(BeforeSend)),
                    transpiler: new HarmonyMethod(typeof(PackageCopyOptimization), nameof(Transpile)),
                    finalizer: new HarmonyMethod(typeof(PackageCopyOptimization), nameof(AfterSend)));
                Installed = true;
                Status = "installed";
                logger.LogInfo("Local ZDO package copy optimization installed; runtime gate remains opt-in.");
            }
            catch (Exception exception)
            {
                Enabled = Installed = false;
                Status = "unavailable";
                Patches.UnpatchSelf();
                logger.LogWarning("Local package copy unavailable; native writes retained: " + exception.GetType().Name);
            }
        }

        private static void ValidateContracts()
        {
            var writerField = AccessTools.DeclaredField(typeof(ZPackage), "m_writer");
            var streamField = AccessTools.DeclaredField(typeof(ZPackage), "m_stream");
            if (writerField?.FieldType != typeof(BinaryWriter) || streamField?.FieldType != typeof(MemoryStream) ||
                ClearPackage == null || PackageConstructor == null)
                throw new InvalidOperationException("Unsupported package fields.");
            var copy = PatchProcessor.GetOriginalInstructions(WritePackage);
            var copyOps = new[] { OpCodes.Ldarg_1, OpCodes.Callvirt, OpCodes.Stloc_0, OpCodes.Ldarg_0,
                OpCodes.Ldfld, OpCodes.Ldloc_0, OpCodes.Ldlen, OpCodes.Conv_I4, OpCodes.Callvirt,
                OpCodes.Ldarg_0, OpCodes.Ldfld, OpCodes.Ldloc_0, OpCodes.Callvirt, OpCodes.Ret };
            var array = PatchProcessor.GetOriginalInstructions(GetArray);
            var arrayOps = new[] { OpCodes.Ldarg_0, OpCodes.Ldfld, OpCodes.Callvirt, OpCodes.Ldarg_0,
                OpCodes.Ldfld, OpCodes.Callvirt, OpCodes.Ldarg_0, OpCodes.Ldfld, OpCodes.Callvirt, OpCodes.Ret };
            if (!copy.Select(i => i.opcode).SequenceEqual(copyOps) || !array.Select(i => i.opcode).SequenceEqual(arrayOps) ||
                !copy[1].Calls(GetArray) || !Equals(copy[4].operand, writerField) || !Equals(copy[10].operand, writerField) ||
                !copy[8].Calls(AccessTools.Method(typeof(BinaryWriter), "Write", new[] { typeof(int) })) ||
                !copy[12].Calls(AccessTools.Method(typeof(BinaryWriter), "Write", new[] { typeof(byte[]) })) ||
                !Equals(array[1].operand, writerField) || !Equals(array[4].operand, streamField) || !Equals(array[7].operand, streamField) ||
                !array[2].Calls(AccessTools.Method(typeof(BinaryWriter), "Flush", Type.EmptyTypes)) ||
                !array[5].Calls(AccessTools.Method(typeof(Stream), "Flush", Type.EmptyTypes)) ||
                !array[8].Calls(AccessTools.Method(typeof(MemoryStream), "ToArray", Type.EmptyTypes)))
                throw new InvalidOperationException("Native package copy contract changed.");
            writer = AccessTools.FieldRefAccess<ZPackage, BinaryWriter>(writerField);
            stream = AccessTools.FieldRefAccess<ZPackage, MemoryStream>(streamField);
        }

        private static void BeforeSend(out ScopeState __state)
        {
            __state = new ScopeState { Previous = scopeAllowed };
            scopeAllowed = false;
            if (!Enabled || writer == null || stream == null) return;
            // Once per send operation, not once per serialized ZDO. Late method patches
            // conservatively restore the original Write/GetArray semantics.
            if (ConflictMethods.Any(method => Harmony.GetPatchInfo(method)?.Owners.Count > 0))
            {
                Interlocked.Increment(ref conflictScopes);
                Status = "native_due_to_method_patch";
                return;
            }
            scopeAllowed = true;
        }

        private static void AfterSend(ScopeState __state) { scopeAllowed = __state.Previous; }

        private static void CopyLocalPackage(ZPackage destination, ZPackage source)
        {
            Interlocked.Increment(ref calls);
            try
            {
                if (source != null && stream != null)
                {
                    var sourceStream = stream(source);
                    if (sourceStream != null && sourceStream.GetType() == typeof(MemoryStream) && sourceStream.CanRead)
                        Interlocked.Add(ref observedBytes, sourceStream.Length);
                }
                if (scopeAllowed && Enabled && destination != null && source != null &&
                    PackageCopy.TryWrite(writer!(destination), stream!(destination), writer(source), stream(source), out int copied) == PackageCopyOutcome.Copied)
                {
                    Interlocked.Increment(ref copiedCalls);
                    Interlocked.Add(ref copiedBytes, copied);
                    return;
                }
                Interlocked.Increment(ref fallbacks);
                destination!.Write(source);
            }
            catch { Interlocked.Increment(ref failures); throw; }
        }

        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var matches = code.Select((instruction, index) => new { instruction, index }).Where(x => x.instruction.Calls(WritePackage)).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("Expected one local package copy.");
            int at = matches[0].index;
            if (at < 2 || code[at - 2].opcode != OpCodes.Ldloc_2 || !IsLocal(code[at - 1], OpCodes.Ldloc_S, 5) ||
                !(code[at - 1].operand is LocalBuilder sourceLocal) || sourceLocal.LocalType != typeof(ZPackage) ||
                code[at].labels.Count != 0 || code[at].blocks.Count != 0)
                throw new InvalidOperationException("Unexpected local package copy operands.");
            var constructor = AccessTools.Constructor(typeof(ZPackage), Type.EmptyTypes);
            int destinationInit = code.FindIndex(i => i.opcode == OpCodes.Stloc_2);
            int sourceInit = code.FindIndex(i => IsLocal(i, OpCodes.Stloc_S, 5));
            if (destinationInit < 1 || sourceInit <= destinationInit || sourceInit >= at ||
                code[destinationInit - 1].opcode != OpCodes.Newobj || !Equals(code[destinationInit - 1].operand, constructor) ||
                code[sourceInit - 1].opcode != OpCodes.Newobj || !Equals(code[sourceInit - 1].operand, constructor) ||
                code.Count(i => i.opcode == OpCodes.Stloc_2) != 1 || code.Count(i => IsLocal(i, OpCodes.Stloc_S, 5)) != 1)
                throw new InvalidOperationException("Local packages must be constructed once in this method.");
            // The source can only be cleared, serialized into, and copied at this site.
            // Reject address-taking, local alias creation, storage, and other escapes.
            for (int index = 0; index < code.Count; index++)
            {
                if (!(code[index].operand is LocalBuilder local) || local.LocalIndex != 5 || index == sourceInit) continue;
                if (!IsLocal(code[index], OpCodes.Ldloc_S, 5) || index + 1 >= code.Count ||
                    !(code[index + 1].Calls(WritePackage) || code[index + 1].Calls(SerializeZdo) ||
                      code[index + 1].Calls(AccessTools.DeclaredMethod(typeof(ZPackage), "Clear", Type.EmptyTypes))))
                    throw new InvalidOperationException("Local source package escapes supported operations.");
            }
            var replacement = new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(PackageCopyOptimization), nameof(CopyLocalPackage)));
            code[at] = replacement;
            return code;
        }

        private static bool IsLocal(CodeInstruction instruction, OpCode opcode, int slot) =>
            instruction.opcode == opcode && instruction.operand is LocalBuilder local && local.LocalIndex == slot;

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            gauges.Add(new NumberValue("package_local_copy_calls_total", Interlocked.Read(ref calls), "calls"));
            gauges.Add(new NumberValue("package_local_copy_observed_bytes_total", Interlocked.Read(ref observedBytes), "bytes"));
            gauges.Add(new NumberValue("package_local_copy_fast_calls_total", Interlocked.Read(ref copiedCalls), "calls"));
            gauges.Add(new NumberValue("package_local_copy_avoided_payload_bytes_total", Interlocked.Read(ref copiedBytes), "bytes"));
            gauges.Add(new NumberValue("package_local_copy_fallback_calls_total", Interlocked.Read(ref fallbacks), "calls"));
            gauges.Add(new NumberValue("package_local_copy_failed_calls_total", Interlocked.Read(ref failures), "calls"));
            gauges.Add(new NumberValue("package_local_copy_conflict_scopes_total", Interlocked.Read(ref conflictScopes), "calls"));
            labels.Add(new TextValue("package_local_copy_status", Status));
            labels.Add(new TextValue("package_local_copy_enabled", Enabled ? "true" : "false"));
        }

        internal static void Uninstall()
        {
            Enabled = false;
            Patches.UnpatchSelf();
            Installed = false;
            Status = "disabled";
        }
    }
}
