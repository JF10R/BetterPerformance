using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;

namespace BetterPerformance
{
    // Only gates the two verified BitArray loops. Pins, package version, compression
    // and the complete native save lifecycle remain in the original method.
    internal static class FastMapSerialization
    {
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".FastMapSerialization");
        private static volatile bool enabled;
        internal static bool Enabled { get => enabled; set => enabled = value; }
        internal static bool Installed { get; private set; }
        internal static string Status { get; private set; } = "disabled";

        // Checked on the main thread before a speculative snapshot leaves it. Install has
        // verified the original boolean writer; foreign patches require the native loop.
        internal static bool CanWriteSnapshotBits => Installed &&
            (Harmony.GetPatchInfo(AccessTools.DeclaredMethod(typeof(ZPackage), "Write", new[] { typeof(bool) }))?.Owners.Count ?? 0) == 0;

        internal static void Install(ManualLogSource logger)
        {
            try
            {
                var method = AccessTools.DeclaredMethod(typeof(Minimap), "GetMapData", Type.EmptyTypes);
                if (method == null || method.ReturnType != typeof(byte[]) || method.IsStatic)
                    throw new InvalidOperationException("Unsupported map method.");
                var booleanWrite = AccessTools.DeclaredMethod(typeof(ZPackage), "Write", new[] { typeof(bool) });
                var writer = AccessTools.DeclaredField(typeof(ZPackage), "m_writer");
                if (booleanWrite == null || writer == null || writer.FieldType != typeof(BinaryWriter))
                    throw new InvalidOperationException("Unsupported package writer.");
                // Bypassing an altered boolean writer could bypass another mod's semantics.
                var patches = Harmony.GetPatchInfo(booleanWrite);
                if (patches != null && patches.Owners.Count != 0)
                    throw new InvalidOperationException("Boolean package writer already patched.");
                var body = PatchProcessor.GetOriginalInstructions(booleanWrite);
                if (body.Count != 5 || body[0].opcode != OpCodes.Ldarg_0 ||
                    body[1].opcode != OpCodes.Ldfld || !Equals(body[1].operand, writer) ||
                    body[2].opcode != OpCodes.Ldarg_1 || body[3].opcode != OpCodes.Callvirt ||
                    !Equals(body[3].operand, AccessTools.Method(typeof(BinaryWriter), "Write", new[] { typeof(bool) })) ||
                    body[4].opcode != OpCodes.Ret)
                    throw new InvalidOperationException("Boolean writer implementation changed.");
                Patches.Patch(method, transpiler: new HarmonyMethod(typeof(FastMapSerialization), nameof(Transpile)));
                Installed = true;
                Status = "installed";
                logger.LogInfo("Fast map serialization installed; runtime gate remains disabled until explicitly enabled.");
            }
            catch (Exception exception)
            {
                Patches.UnpatchSelf();
                Installed = Enabled = false;
                Status = "unavailable";
                logger.LogWarning("Fast map serialization unavailable; vanilla loops retained: " + exception.GetType().Name);
            }
        }

        internal static void Uninstall()
        {
            Enabled = false;
            Patches.UnpatchSelf();
            Installed = false;
            Status = "disabled";
        }

        private static bool WriteIfEnabled(BinaryWriter writer, BitArray bits, int count)
        {
            // A custom stream can distinguish native one-byte boolean writes from
            // bulk chunks even when the BinaryWriter itself is unmodified.
            if (!Enabled || writer == null || writer.GetType() != typeof(BinaryWriter) ||
                writer.BaseStream.GetType() != typeof(MemoryStream)) return false;
            var patches = Harmony.GetPatchInfo(AccessTools.DeclaredMethod(typeof(ZPackage), "Write", new[] { typeof(bool) }));
            if (patches != null && patches.Owners.Count != 0)
            {
                Enabled = false;
                Status = "boolean_writer_patched";
                return false;
            }
            return MapBitWriter.TryWrite(writer, bits, count);
        }

        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var input = instructions.ToList();
            var explored = AccessTools.DeclaredField(typeof(Minimap), "m_explored");
            var others = AccessTools.DeclaredField(typeof(Minimap), "m_exploredOthers");
            var writer = AccessTools.DeclaredField(typeof(ZPackage), "m_writer");
            if (explored == null || others == null || explored.FieldType != typeof(BitArray) ||
                others.FieldType != typeof(BitArray) || writer == null || writer.FieldType != typeof(BinaryWriter))
                throw new InvalidOperationException("Unsupported map fields.");
            var matches = new List<int>();
            for (int index = 0; index + 18 < input.Count; index++)
                if (Matches(input, index, explored, explored) || Matches(input, index, others, explored)) matches.Add(index);
            if (input.Count(i => i.Calls(AccessTools.PropertyGetter(typeof(BitArray), "Item"))) != 2 ||
                matches.Count != 2 || matches[1] != matches[0] + 18 ||
                !Matches(input, matches[0], explored, explored) || !Matches(input, matches[1], others, explored))
                throw new InvalidOperationException("Expected exactly two consecutive native map loops.");

            var output = new List<CodeInstruction>(input.Count + 20);
            for (int index = 0; index < input.Count; index++)
            {
                if (!matches.Contains(index)) { output.Add(input[index]); continue; }
                var end = generator.DefineLabel();
                // Gate loads have no side effects and the original loop is the fallback.
                output.Add(new CodeInstruction(OpCodes.Ldloc_1));
                output.Add(new CodeInstruction(OpCodes.Ldfld, writer));
                output.Add(new CodeInstruction(OpCodes.Ldarg_0));
                output.Add(new CodeInstruction(OpCodes.Ldfld, index == matches[0] ? explored : others));
                output.Add(new CodeInstruction(OpCodes.Ldarg_0));
                output.Add(new CodeInstruction(OpCodes.Ldfld, explored));
                output.Add(new CodeInstruction(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(BitArray), "Length")));
                output.Add(new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(FastMapSerialization), nameof(WriteIfEnabled))));
                output.Add(new CodeInstruction(OpCodes.Brtrue, end));
                output.AddRange(input.GetRange(index, 18));
                var landing = new CodeInstruction(OpCodes.Nop);
                landing.labels.Add(end);
                output.Add(landing);
                index += 17;
            }
            return output;
        }

        private static bool Matches(List<CodeInstruction> code, int start, FieldInfo source, FieldInfo bound)
        {
            var ops = new[] { OpCodes.Ldc_I4_0, OpCodes.Stloc_S, OpCodes.Br_S, OpCodes.Ldloc_1,
                OpCodes.Ldarg_0, OpCodes.Ldfld, OpCodes.Ldloc_S, OpCodes.Callvirt, OpCodes.Callvirt,
                OpCodes.Ldloc_S, OpCodes.Ldc_I4_1, OpCodes.Add, OpCodes.Stloc_S, OpCodes.Ldloc_S,
                OpCodes.Ldarg_0, OpCodes.Ldfld, OpCodes.Callvirt, OpCodes.Blt_S };
            for (int j = 0; j < ops.Length; j++)
                // Harmony expands short branches before passing instructions to transpilers.
                if ((code[start + j].opcode != ops[j] && !(j == 2 && code[start + j].opcode == OpCodes.Br) &&
                    !(j == 17 && code[start + j].opcode == OpCodes.Blt)) || code[start + j].blocks.Count != 0 ||
                    code[start + j].labels.Count != (j == 3 || j == 13 ? 1 : 0)) return false;
            if (!Equals(code[start + 5].operand, source) || !Equals(code[start + 15].operand, bound) ||
                !Equals(code[start + 7].operand, AccessTools.PropertyGetter(typeof(BitArray), "Item")) ||
                !Equals(code[start + 8].operand, AccessTools.DeclaredMethod(typeof(ZPackage), "Write", new[] { typeof(bool) })) ||
                !Equals(code[start + 16].operand, AccessTools.PropertyGetter(typeof(BitArray), "Length"))) return false;
            object counter = code[start + 1].operand;
            if (!(counter is LocalBuilder local) || local.LocalType != typeof(int) ||
                local.LocalIndex != (source.Name == "m_explored" ? 4 : 5)) return false;
            foreach (int j in new[] { 6, 9, 12, 13 }) if (!Equals(code[start + j].operand, counter)) return false;
            if (!(code[start + 2].operand is Label condition) || !code[start + 13].labels.Contains(condition) ||
                !(code[start + 17].operand is Label body) || !code[start + 3].labels.Contains(body)) return false;
            var inside = new HashSet<Label>(code.Skip(start).Take(18).SelectMany(i => i.labels));
            // Strictly reject extra labels and any outside branch/switch into this loop.
            if (inside.Count != 2 || !inside.Contains(condition) || !inside.Contains(body)) return false;
            for (int j = 0; j < code.Count; j++)
            {
                if (j >= start && j < start + 18) continue;
                if (code[j].operand is Label target && inside.Contains(target)) return false;
                if (code[j].operand is Label[] targets && targets.Any(inside.Contains)) return false;
                // The fast path skips counter initialization/increments. A future or
                // modified body must not observe that local outside its native loop.
                if (code[j].operand is LocalBuilder otherLocal && otherLocal.LocalIndex == local.LocalIndex) return false;
                if (code[j].operand is LocalVariableInfo variable && variable.LocalIndex == local.LocalIndex) return false;
                if (code[j].opcode.Name != null && code[j].opcode.Name!.Contains("loc") &&
                    ((code[j].operand is byte slot && slot == local.LocalIndex) ||
                     (code[j].operand is int longSlot && longSlot == local.LocalIndex))) return false;
            }
            return true;
        }
    }
}
