using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;

namespace BetterPerformance
{
    // TerrainComp.PaintCleared spreads a painted zone-edge texel into the neighbouring
    // compiler and serializes that neighbour once per texel. The final bytes only ever
    // reflect the last write, so the intermediate serialize+compress passes are
    // redundant. This defers them to one write per neighbour per operation while
    // replicating the native paint-hash gate exactly, so m_lastHash, the skip decision
    // and the saved bytes match vanilla. Poke is never intercepted: Heightmap already
    // coalesces through m_doLateUpdate, so the same neighbours are poked in the same
    // frame as vanilla.
    internal static class TerrainSaveCoalescing
    {
        private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        private const string SpreadPrefix = "<PaintCleared>g__spread|";

        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".TerrainSaveCoalescing");
        private static readonly Dictionary<short, OpCode> Opcodes = BuildOpcodes();

        private static ManualLogSource logger = null!;
        private static MethodInfo? saveMethod, paintClearedMethod, spreadMethod, computeHashMethod;
        private static AccessTools.FieldRef<TerrainComp, int>? lastHash;
        private static AccessTools.FieldRef<TerrainComp, bool>? initialized;
        private static AccessTools.FieldRef<TerrainComp, ZNetView>? view;
        private static Func<TerrainComp, int>? computeHash;
        private static Action<TerrainComp, bool>? nativeSave;
        private static bool failed;

        [ThreadStatic] private static int depth;
        [ThreadStatic] private static TerrainComp? owner;
        [ThreadStatic] private static List<TerrainComp>? pending;

        private static long batches, deferred, flushed, gateSkips, nativeInsideBatch, fallbacks;

        internal static bool Installed { get; private set; }
        internal static bool Enabled { get; set; }
        internal static string Status { get; private set; } = "disabled";

        internal struct BatchState { internal bool Entered; internal TerrainComp? Previous; }

        internal static void Install(ConfigFile config, ManualLogSource log)
        {
            logger = log;
            var requested = config.Bind("Terrain", "CoalesceNeighbourSavesEnabled", false,
                "Experimental: write each neighbouring terrain compiler once per paint operation instead of once per painted zone-edge vertex. " +
                "Saved bytes and paint state are intended to be identical to vanilla; requires restart. Unmeasured.");
            if (!requested.Value) { Status = "disabled"; return; }
            try
            {
                Resolve();
                string? reason = Verify(saveMethod!, spreadMethod!, paintClearedMethod!, out bool paintClearedVerified);
                if (reason != null) throw new InvalidOperationException(reason);
                computeHash = AccessTools.MethodDelegate<Func<TerrainComp, int>>(computeHashMethod!);
                nativeSave = AccessTools.MethodDelegate<Action<TerrainComp, bool>>(saveMethod!);
                Patches.Patch(paintClearedMethod!,
                    prefix: new HarmonyMethod(typeof(TerrainSaveCoalescing), nameof(BeforePaintCleared)),
                    finalizer: new HarmonyMethod(typeof(TerrainSaveCoalescing), nameof(AfterPaintCleared)));
                Patches.Patch(saveMethod!, prefix: new HarmonyMethod(typeof(TerrainSaveCoalescing), nameof(BeforeSave)));
                Installed = Enabled = true;
                // A partial contract means this runtime could not read PaintCleared's body
                // (standalone CLR cannot type-load Heightmap); the spread and Save shapes
                // were still proven. Unity reads all three and reports "installed".
                Status = paintClearedVerified ? "installed" : "installed_partial_contract";
                logger.LogWarning("Experimental terrain neighbour save coalescing installed (" + Status + ").");
            }
            catch (Exception exception)
            {
                Installed = Enabled = false;
                Status = "unavailable";
                Release();
                logger.LogWarning("Terrain save coalescing unavailable; vanilla saves retained: " +
                    exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static void Resolve()
        {
            saveMethod = AccessTools.DeclaredMethod(typeof(TerrainComp), "Save", new[] { typeof(bool) });
            computeHashMethod = AccessTools.DeclaredMethod(typeof(TerrainComp), "ComputePaintMaskHash", Type.EmptyTypes);
            paintClearedMethod = FindPaintCleared();
            spreadMethod = FindSpread();
            if (saveMethod == null || computeHashMethod == null || paintClearedMethod == null || spreadMethod == null)
                throw new InvalidOperationException("Terrain paint methods are missing.");
            var hashField = AccessTools.DeclaredField(typeof(TerrainComp), "m_lastHash");
            var initializedField = AccessTools.DeclaredField(typeof(TerrainComp), "m_initialized");
            var viewField = AccessTools.DeclaredField(typeof(TerrainComp), "m_nview");
            if (hashField?.FieldType != typeof(int) || initializedField?.FieldType != typeof(bool) ||
                viewField?.FieldType != typeof(ZNetView))
                throw new InvalidOperationException("Unsupported terrain compiler fields.");
            lastHash = AccessTools.FieldRefAccess<TerrainComp, int>(hashField);
            initialized = AccessTools.FieldRefAccess<TerrainComp, bool>(initializedField);
            view = AccessTools.FieldRefAccess<TerrainComp, ZNetView>(viewField);
        }

        // PaintCleared's third parameter is a nested game type; resolve by shape so the
        // lookup does not depend on a type the standalone CLR may refuse to load.
        private static MethodInfo? FindPaintCleared()
        {
            foreach (MethodInfo candidate in typeof(TerrainComp).GetMethods(Declared))
            {
                if (candidate.Name != "PaintCleared" || candidate.IsStatic) continue;
                ParameterInfo[] parameters;
                try { if (candidate.ReturnType != typeof(void)) continue; parameters = candidate.GetParameters(); }
                catch { continue; }
                if (parameters.Length == 3 && parameters[0].ParameterType == typeof(UnityEngine.Vector3) &&
                    parameters[1].ParameterType == typeof(UnityEngine.Vector3) && !parameters[2].ParameterType.IsByRef)
                    return candidate;
            }
            return null;
        }

        private static MethodInfo? FindSpread()
        {
            foreach (MethodInfo candidate in typeof(TerrainComp).GetMethods(Declared))
            {
                if (!candidate.Name.StartsWith(SpreadPrefix, StringComparison.Ordinal) || candidate.IsStatic) continue;
                try
                {
                    if (candidate.ReturnType != typeof(bool)) continue;
                    ParameterInfo[] parameters = candidate.GetParameters();
                    if (parameters.Length < 3 || parameters[0].ParameterType != typeof(int) ||
                        parameters[1].ParameterType != typeof(int)) continue;
                    bool captured = true;
                    for (int index = 2; index < parameters.Length; index++)
                    {
                        Type type = parameters[index].ParameterType;
                        captured &= type.IsByRef && type.GetElementType()?.DeclaringType == typeof(TerrainComp);
                    }
                    if (captured) return candidate;
                }
                catch { continue; }
            }
            return null;
        }

        // Structural contract. Returns null when the native shape is the one this patch
        // was written against, otherwise the reason it is not.
        internal static string? Verify(MethodBase save, MethodBase spread, MethodBase paintCleared, out bool paintClearedVerified)
        {
            paintClearedVerified = false;
            var hashMethod = AccessTools.DeclaredMethod(typeof(TerrainComp), "ComputePaintMaskHash", Type.EmptyTypes);
            var hashField = AccessTools.DeclaredField(typeof(TerrainComp), "m_lastHash");
            if (hashMethod == null || hashField == null) return "ComputePaintMaskHash/m_lastHash are missing.";

            List<Step>? saveSteps = ReadIl(save);
            if (saveSteps == null) return "TerrainComp.Save body is unreadable.";
            int gate = IndexOfCall(saveSteps, hashMethod);
            if (gate < 0 || IndexOfCall(saveSteps, hashMethod, gate + 1) >= 0)
                return "Save does not compute the paint hash exactly once.";
            if (gate < 5 || gate + 12 >= saveSteps.Count) return "Save paint-hash gate is truncated.";
            if (!Is(saveSteps[gate - 5], OpCodes.Ldarg_1) || !IsBranch(saveSteps[gate - 4], OpCodes.Brtrue, OpCodes.Brtrue_S) ||
                !Is(saveSteps[gate - 3], OpCodes.Ldc_I4_0) || !IsBranch(saveSteps[gate - 2], OpCodes.Br, OpCodes.Br_S) ||
                !Is(saveSteps[gate - 1], OpCodes.Ldarg_0))
                return "Save does not select the paint hash from the paintOnly argument.";
            if (!Is(saveSteps[gate + 1], OpCodes.Stloc_0) || !Is(saveSteps[gate + 2], OpCodes.Ldloc_0) ||
                !Is(saveSteps[gate + 3], OpCodes.Ldarg_0) || !IsField(saveSteps[gate + 4], OpCodes.Ldfld, hashField) ||
                !Is(saveSteps[gate + 5], OpCodes.Ceq) || !Is(saveSteps[gate + 6], OpCodes.Ldarg_1) ||
                !Is(saveSteps[gate + 7], OpCodes.And) || !IsBranch(saveSteps[gate + 8], OpCodes.Brfalse, OpCodes.Brfalse_S) ||
                !Is(saveSteps[gate + 9], OpCodes.Ret) || !Is(saveSteps[gate + 10], OpCodes.Ldarg_0) ||
                !Is(saveSteps[gate + 11], OpCodes.Ldloc_0) || !IsField(saveSteps[gate + 12], OpCodes.Stfld, hashField))
                return "Save no longer skips an unchanged paint mask and latches m_lastHash.";
            if (Count(saveSteps, step => IsField(step, OpCodes.Stfld, hashField)) != 1)
                return "Save writes m_lastHash more than once.";
            if (!saveSteps.Exists(step => step.Code == OpCodes.Ldsfld && step.Member?.Name == "s_TCData"))
                return "Save no longer targets the terrain compiler ZDO key.";
            if (!saveSteps.Exists(step => step.Member is MethodBase called && called.Name == "Compress"))
                return "Save no longer compresses the serialized package.";

            List<Step>? spreadSteps = ReadIl(spread);
            if (spreadSteps == null) return "PaintCleared spread body is unreadable.";
            int call = IndexOfCall(spreadSteps, save);
            if (call < 0 || IndexOfCall(spreadSteps, save, call + 1) >= 0)
                return "spread does not save the neighbour exactly once.";
            if (call < 2 || !Is(spreadSteps[call - 2], OpCodes.Ldloc_0))
                return "spread saves an unexpected receiver.";
            if (call + 4 >= spreadSteps.Count || !Is(spreadSteps[call + 1], OpCodes.Ldloc_0) ||
                spreadSteps[call + 2].Code != OpCodes.Ldfld || spreadSteps[call + 2].Member?.Name != "m_hmap" ||
                !Is(spreadSteps[call + 3], OpCodes.Ldc_I4_1))
                return "spread no longer pokes the neighbour heightmap with a one-frame delay after saving.";

            List<Step>? paintSteps = ReadIl(paintCleared);
            if (paintSteps != null)
            {
                if (IndexOfCall(paintSteps, spread) < 0) return "PaintCleared does not call the spread local function.";
                if (IndexOfCall(paintSteps, save) >= 0) return "PaintCleared saves outside the spread local function.";
                paintClearedVerified = true;
            }
            return null;
        }

        private static void BeforePaintCleared(TerrainComp __instance, out BatchState __state)
        {
            __state = new BatchState { Entered = false, Previous = owner };
            if (!Enabled || !Installed || failed) return;
            __state.Entered = true;
            owner = __instance;
            if (++depth == 1) Interlocked.Increment(ref batches);
        }

        private static void AfterPaintCleared(BatchState __state)
        {
            if (!__state.Entered) return;
            owner = __state.Previous;
            if (--depth > 0) return;
            depth = 0;
            Flush();
        }

        // Replicates TerrainComp.Save's own guards and paint-hash gate, then skips only
        // the serialize/compress/ZDO write. The deferred write happens once per
        // neighbour when the operation's paint kernel finishes.
        private static bool BeforeSave(TerrainComp __instance, bool paintOnly)
        {
            if (depth <= 0) return true;
            if (ReferenceEquals(__instance, owner)) { Interlocked.Increment(ref nativeInsideBatch); return true; }
            try
            {
                ZNetView? instanceView = view!(__instance);
                if (!initialized!(__instance) || instanceView is null || !instanceView.IsValid() || !instanceView.IsOwner())
                    return true;
                int hash = paintOnly ? computeHash!(__instance) : 0;
                if (paintOnly && hash == lastHash!(__instance)) { Interlocked.Increment(ref gateSkips); return false; }
                lastHash!(__instance) = hash;
                var list = pending ?? (pending = new List<TerrainComp>(8));
                if (!list.Contains(__instance)) list.Add(__instance);
                Interlocked.Increment(ref deferred);
                return false;
            }
            catch (Exception exception) { Fail(exception); return true; }
        }

        // Runs at depth zero, so the prefix above lets these writes through. Save(false)
        // serializes exactly the bytes a paint-only save would and zeroes m_lastHash;
        // the value the native gate had latched is restored afterwards.
        private static void Flush()
        {
            var list = pending;
            if (list == null || list.Count == 0) return;
            if (nativeSave == null || lastHash == null) { list.Clear(); return; }
            for (int index = 0; index < list.Count; index++)
            {
                TerrainComp component = list[index];
                try
                {
                    int hash = lastHash!(component);
                    nativeSave!(component, false);
                    lastHash!(component) = hash;
                    Interlocked.Increment(ref flushed);
                }
                catch (Exception exception) { Fail(exception); }
            }
            list.Clear();
        }

        private static void Fail(Exception exception)
        {
            Interlocked.Increment(ref fallbacks);
            if (failed) return;
            failed = true;
            Enabled = false;
            Status = "failed";
            logger?.LogWarning("Terrain save coalescing disabled after " + exception.GetType().Name + "; vanilla saves resume.");
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            gauges.Add(new NumberValue("terrain_batches", Interlocked.Read(ref batches), "operations"));
            gauges.Add(new NumberValue("terrain_saves_deferred", Interlocked.Read(ref deferred), "calls"));
            gauges.Add(new NumberValue("terrain_saves_flushed", Interlocked.Read(ref flushed), "calls"));
            gauges.Add(new NumberValue("terrain_saves_gate_skipped", Interlocked.Read(ref gateSkips), "calls"));
            gauges.Add(new NumberValue("terrain_saves_native_inside_batch", Interlocked.Read(ref nativeInsideBatch), "calls"));
            gauges.Add(new NumberValue("terrain_coalesce_fallbacks", Interlocked.Read(ref fallbacks), "calls"));
            labels.Add(new TextValue("terrain_coalesce_status", Status));
            labels.Add(new TextValue("terrain_coalesce_enabled", Enabled ? "true" : "false"));
            labels.Add(new TextValue("terrain_coalesce_poke", "native_pass_through"));
        }

        internal static void Reset()
        {
            Interlocked.Exchange(ref batches, 0);
            Interlocked.Exchange(ref deferred, 0);
            Interlocked.Exchange(ref flushed, 0);
            Interlocked.Exchange(ref gateSkips, 0);
            Interlocked.Exchange(ref nativeInsideBatch, 0);
            Interlocked.Exchange(ref fallbacks, 0);
        }

        internal static void Uninstall()
        {
            Enabled = false;
            Flush();
            Installed = false;
            depth = 0;
            owner = null;
            pending?.Clear();
            Status = "disabled";
            Release();
        }

        // Harmony rescans every patched method in the process when it removes a patch,
        // so a shared standalone CLR can refuse the removal for an unrelated Unity type.
        // Report that instead of throwing over the caller's own failure.
        private static void Release()
        {
            try { Patches.UnpatchSelf(); }
            catch (Exception exception) { Status = Status + "_unpatch_failed_" + exception.GetType().Name; }
        }

        internal struct Step
        {
            internal OpCode Code;
            internal MemberInfo? Member;
        }

        private static bool Is(Step step, OpCode code) => step.Code == code;

        private static bool IsBranch(Step step, OpCode wide, OpCode compact) => step.Code == wide || step.Code == compact;

        private static bool IsField(Step step, OpCode code, FieldInfo field) =>
            step.Code == code && Equals(step.Member, field);

        private static int Count(List<Step> steps, Predicate<Step> predicate)
        {
            int total = 0;
            foreach (Step step in steps) if (predicate(step)) total++;
            return total;
        }

        private static int IndexOfCall(List<Step> steps, MethodBase target, int from = 0)
        {
            for (int index = from; index < steps.Count; index++)
                if ((steps[index].Code == OpCodes.Call || steps[index].Code == OpCodes.Callvirt) &&
                    Equals(steps[index].Member, target)) return index;
            return -1;
        }

        // Metadata-only IL reader. Operand tokens are resolved individually so a member
        // whose type this runtime cannot load leaves one unresolved step instead of
        // failing the whole contract check.
        internal static List<Step>? ReadIl(MethodBase method)
        {
            byte[] bytes;
            Type[] typeArguments, methodArguments;
            try
            {
                MethodBody? body = method.GetMethodBody();
                byte[]? raw = body?.GetILAsByteArray();
                if (raw == null) return null;
                bytes = raw;
                typeArguments = method.DeclaringType?.GetGenericArguments() ?? Type.EmptyTypes;
                methodArguments = method is MethodInfo info && info.IsGenericMethodDefinition
                    ? info.GetGenericArguments() : Type.EmptyTypes;
            }
            catch { return null; }
            var steps = new List<Step>(bytes.Length / 2);
            for (int offset = 0; offset < bytes.Length;)
            {
                int value = bytes[offset++];
                if (value == 0xfe)
                {
                    if (offset >= bytes.Length) return null;
                    value = 0xfe00 | bytes[offset++];
                }
                if (!Opcodes.TryGetValue(unchecked((short)value), out OpCode code)) return null;
                int length;
                switch (code.OperandType)
                {
                    case OperandType.InlineNone: length = 0; break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar: length = 1; break;
                    case OperandType.InlineVar: length = 2; break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR: length = 8; break;
                    case OperandType.InlineSwitch:
                        if (offset + 4 > bytes.Length) return null;
                        length = 4 + 4 * BitConverter.ToInt32(bytes, offset);
                        break;
                    default: length = 4; break;
                }
                if (length < 0 || offset + length > bytes.Length) return null;
                var step = new Step { Code = code };
                if (code.OperandType == OperandType.InlineMethod || code.OperandType == OperandType.InlineField)
                {
                    try { step.Member = method.Module.ResolveMember(BitConverter.ToInt32(bytes, offset), typeArguments, methodArguments); }
                    catch { step.Member = null; }
                }
                steps.Add(step);
                offset += length;
            }
            return steps;
        }

        private static Dictionary<short, OpCode> BuildOpcodes()
        {
            var map = new Dictionary<short, OpCode>();
            foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.FieldType != typeof(OpCode)) continue;
                var code = (OpCode)field.GetValue(null)!;
                map[code.Value] = code;
            }
            return map;
        }
    }
}
