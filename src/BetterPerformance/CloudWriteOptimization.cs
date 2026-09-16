using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;
using Steamworks;

namespace BetterPerformance
{
    // Splatform.Steam.SteamCloud.WriteFile allocates a fixed 100 MiB scratch chunk buffer
    // for every cloud file write, whatever the payload. Only a prefix of the payload is
    // ever copied into it and only that prefix length is handed to Steam, so a buffer
    // sized to the payload emits byte-identical chunks. The transpiler replaces the single
    // newarr and nothing else; every other instruction is preserved.
    internal static class CloudWriteOptimization
    {
        private const string TypeName = "Splatform.Steam.SteamCloud";
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".CloudWriteOptimization");
        private static readonly Harmony TelemetryPatches = new Harmony(Plugin.PluginId + ".CloudWriteTelemetry");
        private static readonly MethodInfo? ChunkWrite = AccessTools.Method(typeof(SteamRemoteStorage), "FileWriteStreamWriteChunk");
        private static readonly MethodInfo? ArrayCopy = AccessTools.Method(typeof(Array), "Copy",
            new[] { typeof(Array), typeof(int), typeof(Array), typeof(int), typeof(int) });
        private static ConfigEntry<bool>? requested;
        private static long writes, writeBytes, maxWriteBytes, elapsedTicks, maxElapsedTicks, avoidedBytes, allocations;
        internal static bool Installed { get; private set; }
        internal static bool Enabled { get; set; }
        internal static string Status { get; private set; } = "disabled";
        internal static string TelemetryStatus { get; private set; } = "not_installed";

        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            requested = config.Bind("CharacterSave", "CloudWriteBufferSizedToPayload", false,
                "Size the Steam cloud writer's scratch chunk buffer to the payload instead of a fixed 100 MiB. " +
                "Bytes sent to Steam are identical by construction; only the scratch allocation changes. " +
                "Avoids about 100 MiB of allocated and zeroed scratch per cloud file write, and a character save performs three. " +
                "Requires restart to install; reverts to the native allocation if the writer's IL does not match.");
            MethodInfo? method = Resolve(logger);
            if (method == null) { Status = "type_unavailable"; TelemetryStatus = "type_unavailable"; return; }
            InstallTelemetry(method, logger);
            if (!requested.Value) { Status = "disabled"; return; }
            try
            {
                Validate(PatchProcessor.GetOriginalInstructions(method), method);
                Patches.Patch(method, transpiler: new HarmonyMethod(typeof(CloudWriteOptimization), nameof(Transpile)));
                Installed = Enabled = true;
                Status = "installed";
                logger.LogInfo("Steam cloud write buffer sized to payload; chunk bytes are unchanged.");
            }
            catch (Exception exception)
            {
                Installed = Enabled = false;
                Status = exception is InvalidOperationException || exception.InnerException is InvalidOperationException
                    ? "unsupported_layout" : "unavailable";
                Remove(Patches, failure => Status = "unpatch_failed:" + failure);
                logger.LogWarning("Steam cloud write buffer unchanged (" + Status + "): " + exception.GetType().Name);
            }
        }

        // Harmony walks every method it knows about, and on a standalone CLR that can throw
        // while loading a Unity type. Removal must never escape install, sample or uninstall.
        private static void Remove(Harmony patches, Action<string> failed)
        {
            try { patches.UnpatchSelf(); }
            catch (Exception exception) { failed(exception.GetType().Name); }
        }

        // The Steam backend assembly is absent on the dedicated server and may not be
        // loaded yet on a client; neither case is an error.
        internal static MethodInfo? Resolve(ManualLogSource? logger)
        {
            Type? type = null;
            try { type = AccessTools.TypeByName(TypeName) ?? Assembly.Load("Splatform.Steam").GetType(TypeName, false); }
            catch (Exception exception) { logger?.LogInfo("Steam cloud writer unavailable: " + exception.GetType().Name); }
            if (type == null) return null;
            try
            {
                return type.GetMethods(AccessTools.all)
                    .FirstOrDefault(candidate => candidate.Name == "WriteFile" && Signature(candidate));
            }
            catch (Exception exception) { logger?.LogInfo("Steam cloud writer unavailable: " + exception.GetType().Name); return null; }
        }

        internal static bool Signature(MethodBase method)
        {
            var parameters = method.GetParameters();
            return !method.IsStatic && method is MethodInfo info && info.ReturnType == typeof(bool) &&
                parameters.Length == 4 && parameters[0].ParameterType == typeof(string) &&
                parameters[1].ParameterType == typeof(byte[]) && parameters[1].Name == "data" &&
                parameters[2].ParameterType.IsEnum && parameters[3].ParameterType == typeof(bool);
        }

        private static void InstallTelemetry(MethodInfo method, ManualLogSource logger)
        {
            try
            {
                TelemetryPatches.Patch(method,
                    prefix: new HarmonyMethod(typeof(CloudWriteOptimization), nameof(Before)),
                    finalizer: new HarmonyMethod(typeof(CloudWriteOptimization), nameof(After)));
                TelemetryStatus = "enabled";
            }
            catch (Exception exception)
            {
                TelemetryStatus = "patch_failed";
                Remove(TelemetryPatches, failure => TelemetryStatus = "unpatch_failed:" + failure);
                logger.LogWarning("Steam cloud write telemetry unavailable: " + exception.GetType().Name);
            }
        }

        private static void Before(byte[] data, out long __state)
        {
            __state = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref writes);
            long length = data == null ? 0 : data.Length;
            Interlocked.Add(ref writeBytes, length);
            RecordMaximum(ref maxWriteBytes, length);
        }

        private static void After(long __state)
        {
            long ticks = Stopwatch.GetTimestamp() - __state;
            if (ticks < 0) ticks = 0;
            Interlocked.Add(ref elapsedTicks, ticks);
            RecordMaximum(ref maxElapsedTicks, ticks);
        }

        private static void RecordMaximum(ref long target, long value)
        {
            long seen = Interlocked.Read(ref target);
            while (value > seen)
            {
                long previous = Interlocked.CompareExchange(ref target, value, seen);
                if (previous == seen) return;
                seen = previous;
            }
        }

        // Called in place of the native newarr. chunkSize is the writer's own local, so a
        // changed native constant cannot silently shrink the buffer below one chunk.
        internal static byte[] AllocateChunkBuffer(int chunkSize, byte[] data)
        {
            if (!Enabled || data == null) return new byte[chunkSize];
            int length = CloudChunkBuffer.BufferLength(chunkSize, data.Length);
            Interlocked.Increment(ref allocations);
            Interlocked.Add(ref avoidedBytes, chunkSize - length);
            return new byte[length];
        }

        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var code = instructions.ToList();
            int allocation = Validate(code, __originalMethod);
            code[allocation] = new CodeInstruction(code[allocation])
            {
                opcode = OpCodes.Call,
                operand = AccessTools.DeclaredMethod(typeof(CloudWriteOptimization), nameof(AllocateChunkBuffer))
            };
            // Validate proved the payload is argument 2 and that this site carries no
            // labels or exception blocks, so the insert cannot move a branch target.
            code.Insert(allocation, new CodeInstruction(OpCodes.Ldarg_2));
            return code;
        }

        // Proves the whole chunk loop, not just the allocation: the buffer is written only
        // by Array.Copy and read only by FileWriteStreamWriteChunk, the copy length is
        // either the chunk size or payload length modulo it, and the loop index is
        // non-negative and strictly below the chunk count on entry. Those together bound
        // every copy by min(chunkSize, payload length).
        internal static int Validate(IList<CodeInstruction> code, MethodBase original)
        {
            if (!Signature(original)) throw new InvalidOperationException("Unsupported cloud writer signature.");
            if (ChunkWrite == null || ArrayCopy == null) throw new InvalidOperationException("Unsupported Steam remote storage contract.");
            int data = 2;
            int constant = Single(code, i => Constant(code[i]) == CloudChunkBuffer.NativeChunkSize, "chunk size constant");
            int size = StoredLocal(code, constant + 1, "chunk size");
            int allocation = Single(code, i => code[i].opcode == OpCodes.Newarr && Equals(code[i].operand, typeof(byte)), "chunk buffer allocation");
            int buffer = StoredLocal(code, allocation + 1, "chunk buffer");
            int copy = Single(code, i => code[i].Calls(ArrayCopy), "chunk copy");
            int write = Single(code, i => code[i].Calls(ChunkWrite), "chunk write");
            if (constant + 1 >= allocation || allocation >= copy || copy >= write || allocation < 1)
                throw new InvalidOperationException("Unexpected cloud writer instruction order.");
            Assign(code, size, 1, "chunk size");
            Assign(code, buffer, 1, "chunk buffer");
            if (!Loads(code[allocation - 1], size)) throw new InvalidOperationException("Chunk buffer is not sized by the chunk size local.");
            // The buffer must not escape: its only uses are the copy destination and the write argument.
            if (!Accesses(code, buffer).SequenceEqual(new[] { allocation + 1, copy - 3, write - 2 }))
                throw new InvalidOperationException("Chunk buffer escapes the copy and write sites.");
            int count = StoredLocal(code, allocation + 9, "chunk count");
            Assign(code, count, 1, "chunk count");
            Shape(code, allocation + 2, "chunk count", Arg(data), Op(OpCodes.Ldlen), Op(OpCodes.Conv_I4), Load(size), Op(OpCodes.Div), Value(1), Op(OpCodes.Add));
            int index = StoredLocal(code, allocation + 11, "chunk index");
            Assign(code, index, 2, "chunk index");
            Shape(code, allocation + 10, "chunk index start", Value(0));
            int step = Accesses(code, index).Last(i => Stores(code[i], index));
            Shape(code, step - 3, "chunk index step", Load(index), Value(1), Op(OpCodes.Add));
            Shape(code, step + 1, "chunk loop test", Load(index), Load(count), Op(OpCodes.Blt));
            if (!Branches(code, allocation + 12, OpCodes.Br, step + 1) || !Branches(code, step + 3, OpCodes.Blt, allocation + 13))
                throw new InvalidOperationException("Chunk loop is not a pre-tested count loop.");
            int length = StoredLocal(code, copy - 8, "chunk length");
            Assign(code, length, 1, "chunk length");
            Shape(code, copy - 20, "chunk length select", Load(index), Value(1), Op(OpCodes.Add), Load(count), Op(OpCodes.Beq),
                Load(size), Op(OpCodes.Br), Arg(data), Op(OpCodes.Ldlen), Op(OpCodes.Conv_I4), Load(size), Op(OpCodes.Rem));
            if (!Branches(code, copy - 16, OpCodes.Beq, copy - 13) || !Branches(code, copy - 14, OpCodes.Br, copy - 8))
                throw new InvalidOperationException("Chunk length select does not follow the native branches.");
            Shape(code, copy - 7, "chunk copy arguments", Arg(data), Load(index), Load(size), Op(OpCodes.Mul), Load(buffer), Value(0), Load(length));
            Shape(code, write - 3, "chunk write arguments", Load(StoredLocal(code, allocation - 2, "stream handle")), Load(buffer), Load(length));
            if (code[allocation].labels.Count != 0 || code[allocation].blocks.Count != 0)
                throw new InvalidOperationException("Chunk allocation site carries branch or exception metadata.");
            return allocation;
        }

        private static Func<CodeInstruction, bool> Op(OpCode opcode) => instruction => Normalize(instruction.opcode) == Normalize(opcode);
        private static Func<CodeInstruction, bool> Load(int local) => instruction => Loads(instruction, local);
        private static Func<CodeInstruction, bool> Arg(int argument) => instruction => ArgumentIndex(instruction) == argument;
        private static Func<CodeInstruction, bool> Value(int value) => instruction => Constant(instruction) == value;

        private static int ArgumentIndex(CodeInstruction instruction)
        {
            var opcode = instruction.opcode;
            if (opcode == OpCodes.Ldarg_0) return 0;
            if (opcode == OpCodes.Ldarg_1) return 1;
            if (opcode == OpCodes.Ldarg_2) return 2;
            if (opcode == OpCodes.Ldarg_3) return 3;
            if (opcode != OpCodes.Ldarg && opcode != OpCodes.Ldarg_S) return -1;
            return instruction.operand is ParameterInfo parameter ? parameter.Position + 1 : Slot(instruction.operand);
        }

        private static void Shape(IList<CodeInstruction> code, int at, string what, params Func<CodeInstruction, bool>[] expected)
        {
            if (at < 0 || at + expected.Length > code.Count)
                throw new InvalidOperationException("Cloud writer " + what + " block is out of range.");
            for (int offset = 0; offset < expected.Length; offset++)
                if (!expected[offset](code[at + offset]))
                    throw new InvalidOperationException("Cloud writer " + what + " block differs at " + offset + ".");
        }

        private static int Single(IList<CodeInstruction> code, Func<int, bool> match, string what)
        {
            var found = Enumerable.Range(0, code.Count).Where(match).ToArray();
            if (found.Length != 1) throw new InvalidOperationException("Expected exactly one cloud writer " + what + ".");
            return found[0];
        }

        private static int StoredLocal(IList<CodeInstruction> code, int at, string what)
        {
            int local = at < code.Count ? LocalIndex(code[at], store: true) : -1;
            if (local < 0) throw new InvalidOperationException("Cloud writer " + what + " is not stored in a local.");
            return local;
        }

        private static void Assign(IList<CodeInstruction> code, int local, int expected, string what)
        {
            if (Accesses(code, local).Count(i => Stores(code[i], local)) != expected)
                throw new InvalidOperationException("Cloud writer " + what + " local is not assigned exactly " + expected + " time(s).");
        }

        // Any address-taking access is reported as index -1 and fails the ordered comparison.
        private static int[] Accesses(IList<CodeInstruction> code, int local) => Enumerable.Range(0, code.Count)
            .Where(i => Loads(code[i], local) || Stores(code[i], local) || Address(code[i], local)).ToArray();

        private static bool Branches(IList<CodeInstruction> code, int from, OpCode opcode, int to) =>
            from >= 0 && from < code.Count && to >= 0 && to < code.Count &&
            Normalize(code[from].opcode) == Normalize(opcode) && code[from].operand is Label label && code[to].labels.Contains(label);

        private static OpCode Normalize(OpCode opcode) =>
            opcode == OpCodes.Br_S ? OpCodes.Br : opcode == OpCodes.Beq_S ? OpCodes.Beq :
            opcode == OpCodes.Blt_S ? OpCodes.Blt : opcode == OpCodes.Brtrue_S ? OpCodes.Brtrue :
            opcode == OpCodes.Brfalse_S ? OpCodes.Brfalse : opcode;

        private static bool Loads(CodeInstruction instruction, int local) => local >= 0 && LocalIndex(instruction, store: false) == local;
        private static bool Stores(CodeInstruction instruction, int local) => local >= 0 && LocalIndex(instruction, store: true) == local;
        private static bool Address(CodeInstruction instruction, int local) =>
            (instruction.opcode == OpCodes.Ldloca || instruction.opcode == OpCodes.Ldloca_S) && Slot(instruction.operand) == local;

        private static int LocalIndex(CodeInstruction instruction, bool store)
        {
            var opcode = instruction.opcode;
            if (store)
            {
                if (opcode == OpCodes.Stloc_0) return 0;
                if (opcode == OpCodes.Stloc_1) return 1;
                if (opcode == OpCodes.Stloc_2) return 2;
                if (opcode == OpCodes.Stloc_3) return 3;
                return opcode == OpCodes.Stloc || opcode == OpCodes.Stloc_S ? Slot(instruction.operand) : -1;
            }
            if (opcode == OpCodes.Ldloc_0) return 0;
            if (opcode == OpCodes.Ldloc_1) return 1;
            if (opcode == OpCodes.Ldloc_2) return 2;
            if (opcode == OpCodes.Ldloc_3) return 3;
            return opcode == OpCodes.Ldloc || opcode == OpCodes.Ldloc_S ? Slot(instruction.operand) : -1;
        }

        private static int Slot(object? operand) =>
            operand is LocalBuilder builder ? builder.LocalIndex :
            operand is LocalVariableInfo info ? info.LocalIndex :
            operand is int value ? value : operand is byte small ? small : operand is sbyte signed ? signed : -1;

        private static long Constant(CodeInstruction instruction)
        {
            var opcode = instruction.opcode;
            if (opcode == OpCodes.Ldc_I4) return instruction.operand is int value ? value : long.MinValue;
            if (opcode == OpCodes.Ldc_I4_S) return instruction.operand is sbyte small ? small :
                instruction.operand is byte unsigned ? unsigned : instruction.operand is int widened ? widened : long.MinValue;
            if (opcode == OpCodes.Ldc_I4_M1) return -1;
            if (opcode == OpCodes.Ldc_I4_0) return 0;
            if (opcode == OpCodes.Ldc_I4_1) return 1;
            if (opcode == OpCodes.Ldc_I4_2) return 2;
            if (opcode == OpCodes.Ldc_I4_3) return 3;
            if (opcode == OpCodes.Ldc_I4_4) return 4;
            if (opcode == OpCodes.Ldc_I4_5) return 5;
            if (opcode == OpCodes.Ldc_I4_6) return 6;
            if (opcode == OpCodes.Ldc_I4_7) return 7;
            if (opcode == OpCodes.Ldc_I4_8) return 8;
            return long.MinValue;
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            gauges.Add(new NumberValue("cloud_writes", Interlocked.Exchange(ref writes, 0), "calls"));
            gauges.Add(new NumberValue("cloud_write_bytes", Interlocked.Exchange(ref writeBytes, 0), "bytes"));
            gauges.Add(new NumberValue("cloud_write_max_bytes", Interlocked.Exchange(ref maxWriteBytes, 0), "bytes"));
            gauges.Add(new NumberValue("cloud_write_elapsed_sum", Milliseconds(Interlocked.Exchange(ref elapsedTicks, 0)), "ms"));
            gauges.Add(new NumberValue("cloud_write_elapsed_max", Milliseconds(Interlocked.Exchange(ref maxElapsedTicks, 0)), "ms"));
            gauges.Add(new NumberValue("cloud_write_sized_allocations", Interlocked.Exchange(ref allocations, 0), "calls"));
            gauges.Add(new NumberValue("cloud_write_scratch_bytes_avoided", Interlocked.Exchange(ref avoidedBytes, 0), "bytes"));
            labels.Add(new TextValue("cloud_write_optimization_status", Status));
            labels.Add(new TextValue("cloud_write_optimization_enabled", Enabled ? "true" : "false"));
            labels.Add(new TextValue("cloud_write_telemetry_status", TelemetryStatus));
            labels.Add(new TextValue("cloud_write_scope",
                "steam_remote_storage_only; bytes_are_payload_not_quota; no_file_names_exported"));
        }

        private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        internal static void Uninstall()
        {
            Enabled = Installed = false;
            Status = "disabled";
            TelemetryStatus = "not_installed";
            Remove(Patches, failure => Status = "unpatch_failed:" + failure);
            Remove(TelemetryPatches, failure => TelemetryStatus = "unpatch_failed:" + failure);
        }
    }
}
