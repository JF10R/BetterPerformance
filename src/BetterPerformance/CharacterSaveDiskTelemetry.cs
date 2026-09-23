using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;

namespace BetterPerformance
{
    // Breaks PlayerProfile.SavePlayerToDisk into its dominant phases: the pre-save cloud
    // quota/backup checks, the SHA-512 package hash, the FileWriter span (construction
    // through Finish, which covers the inline binary writes between them), the atomic
    // file replace and the auto-backup check. ZPackage.GenerateHash and FileWriter
    // also serve the world-save path; a thread-static flag, set only by this method's own
    // prefix/finalizer, keeps those other calls from being attributed here. Capture only;
    // never changes save scheduling, file layout or content.
    internal static class CharacterSaveDiskTelemetry
    {
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".CharacterSaveDiskTelemetry");
        // Set only on the thread running SavePlayerToDisk; a different thread (e.g. the
        // world-save worker) always reads its own, unset slot and is never attributed here.
        [ThreadStatic] private static bool active;
        [ThreadStatic] private static long writeStarted;
        [ThreadStatic] private static bool writeStartValid;
        private static long packageBytes;
        private static long probeFailures;
        private static string fileSource = "none";

        internal static bool Installed { get; private set; }
        internal static string Status { get; private set; } = "disabled";

        internal struct PhaseState
        {
            internal CaptureSession? Session;
            internal long Started;
        }

        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            if (!config.Bind("Diagnostics", "CharacterSaveDiskPhases", true,
                "Break PlayerProfile.SavePlayerToDisk into its cloud-check/hash/write/replace/backup phases, " +
                "attributed only inside that call. Capture only; never changes save behavior or scheduling. Requires restart.").Value)
            { Status = "disabled"; return; }
            try
            {
                MethodInfo saveToDisk = AccessTools.DeclaredMethod(typeof(PlayerProfile), "SavePlayerToDisk", Type.EmptyTypes)
                    ?? throw new InvalidOperationException("PlayerProfile.SavePlayerToDisk is unavailable.");
                if (saveToDisk.IsStatic || saveToDisk.ReturnType != typeof(bool))
                    throw new InvalidOperationException("Unsupported PlayerProfile.SavePlayerToDisk signature.");
                // CloudStorageFileGrouping lives in Splatform.dll, which this plugin does not
                // reference at compile time; resolve it the same way CloudWriteOptimization
                // resolves Splatform.Steam types.
                Type grouping = ResolveSplatformType("Splatform.CloudStorageFileGrouping")
                    ?? throw new InvalidOperationException("Splatform.CloudStorageFileGrouping is unavailable.");
                MethodInfo cloudChecks = AccessTools.DeclaredMethod(typeof(SaveSystem), "PreSaveCloudChecksAndOperations",
                    new[] { typeof(string), typeof(SaveDataType), typeof(FileHelpers.FileSource).MakeByRefType(),
                        typeof(bool).MakeByRefType(), typeof(bool).MakeByRefType(), typeof(int), typeof(World) })
                    ?? throw new InvalidOperationException("SaveSystem.PreSaveCloudChecksAndOperations is unavailable.");
                if (!cloudChecks.IsStatic || cloudChecks.ReturnType != typeof(void))
                    throw new InvalidOperationException("Unsupported SaveSystem.PreSaveCloudChecksAndOperations signature.");
                MethodInfo generateHash = AccessTools.DeclaredMethod(typeof(ZPackage), "GenerateHash", Type.EmptyTypes)
                    ?? throw new InvalidOperationException("ZPackage.GenerateHash is unavailable.");
                if (generateHash.IsStatic || generateHash.ReturnType != typeof(byte[]))
                    throw new InvalidOperationException("Unsupported ZPackage.GenerateHash signature.");
                ConstructorInfo writerCtor = AccessTools.Constructor(typeof(FileWriter),
                    new[] { typeof(string), grouping, typeof(FileHelpers.FileHelperType), typeof(FileHelpers.FileSource) })
                    ?? throw new InvalidOperationException("FileWriter constructor is unavailable.");
                MethodInfo finish = AccessTools.DeclaredMethod(typeof(FileWriter), "Finish", Type.EmptyTypes)
                    ?? throw new InvalidOperationException("FileWriter.Finish is unavailable.");
                if (finish.IsStatic || finish.ReturnType != typeof(void))
                    throw new InvalidOperationException("Unsupported FileWriter.Finish signature.");
                MethodInfo replace = AccessTools.DeclaredMethod(typeof(FileHelpers), "ReplaceOldFile",
                    new[] { typeof(string), typeof(string), typeof(string), grouping, typeof(FileHelpers.FileSource) })
                    ?? throw new InvalidOperationException("FileHelpers.ReplaceOldFile is unavailable.");
                if (!replace.IsStatic || replace.ReturnType != typeof(void))
                    throw new InvalidOperationException("Unsupported FileHelpers.ReplaceOldFile signature.");
                MethodInfo autoBackup = AccessTools.DeclaredMethod(typeof(ZNet), "ConsiderAutoBackup",
                    new[] { typeof(string), typeof(SaveDataType), typeof(DateTime) })
                    ?? throw new InvalidOperationException("ZNet.ConsiderAutoBackup is unavailable.");
                if (!autoBackup.IsStatic || autoBackup.ReturnType != typeof(bool))
                    throw new InvalidOperationException("Unsupported ZNet.ConsiderAutoBackup signature.");

                Patches.Patch(saveToDisk,
                    prefix: new HarmonyMethod(typeof(CharacterSaveDiskTelemetry), nameof(BeforeSave)),
                    finalizer: new HarmonyMethod(typeof(CharacterSaveDiskTelemetry), nameof(AfterSave)));
                Patches.Patch(cloudChecks,
                    prefix: new HarmonyMethod(typeof(CharacterSaveDiskTelemetry), nameof(BeforePhase)),
                    finalizer: new HarmonyMethod(typeof(CharacterSaveDiskTelemetry), nameof(AfterCloudChecks)));
                Patches.Patch(generateHash,
                    prefix: new HarmonyMethod(typeof(CharacterSaveDiskTelemetry), nameof(BeforePhase)),
                    finalizer: new HarmonyMethod(typeof(CharacterSaveDiskTelemetry), nameof(AfterHash)));
                // GetArray itself must stay unpatched: MapCompressionCache.Compatible() refuses
                // any owner on it, even this plugin's, and would fall back on every save.
                Patches.Patch(writerCtor, postfix: new HarmonyMethod(typeof(CharacterSaveDiskTelemetry), nameof(AfterWriterConstructed)));
                Patches.Patch(finish,
                    prefix: new HarmonyMethod(typeof(CharacterSaveDiskTelemetry), nameof(BeforeWrite)),
                    finalizer: new HarmonyMethod(typeof(CharacterSaveDiskTelemetry), nameof(AfterWrite)));
                Patches.Patch(replace,
                    prefix: new HarmonyMethod(typeof(CharacterSaveDiskTelemetry), nameof(BeforePhase)),
                    finalizer: new HarmonyMethod(typeof(CharacterSaveDiskTelemetry), nameof(AfterReplace)));
                Patches.Patch(autoBackup,
                    prefix: new HarmonyMethod(typeof(CharacterSaveDiskTelemetry), nameof(BeforePhase)),
                    finalizer: new HarmonyMethod(typeof(CharacterSaveDiskTelemetry), nameof(AfterBackup)));
                Installed = true;
                Status = "installed";
            }
            catch (Exception exception)
            {
                try { Patches.UnpatchSelf(); } catch { }
                Installed = false;
                Status = "unavailable";
                logger.LogWarning("Character save disk phase attribution unavailable: " + exception.GetType().Name);
            }
        }

        // Same fallback CloudWriteOptimization.Resolve uses for Splatform.Steam types: the
        // type's own assembly is not referenced by this plugin, so a name search across
        // already-loaded assemblies is tried first, then an explicit load by simple name.
        private static Type? ResolveSplatformType(string fullName)
        {
            try
            {
                return AccessTools.TypeByName(fullName) ?? Assembly.Load("Splatform").GetType(fullName, false);
            }
            catch { return null; }
        }

        private static void BeforeSave(out bool __state)
        {
            __state = active;
            if (!Installed) return;
            active = true;
        }

        // Void finalizer preserves the native return value and exception; it only restores
        // the scope flag so a nested/failed call can never leave phase attribution stuck on.
        private static void AfterSave(bool __state)
        {
            active = __state;
            writeStartValid = false;
        }

        private static void BeforePhase(out PhaseState __state)
        {
            __state = default;
            if (!active) return;
            CaptureSession? session = Volatile.Read(ref TimingHooks.Current);
            if (session == null) return;
            __state = new PhaseState { Session = session, Started = Stopwatch.GetTimestamp() };
        }

        private static void Record(PhaseState state, Metric metric, Exception? exception)
        {
            if (state.Session == null) return;
            try
            {
                state.Session.Book.Record(metric, (Stopwatch.GetTimestamp() - state.Started) * 1000.0 / Stopwatch.Frequency, exception != null);
            }
            catch { state.Session.RecordProbeFailure(); }
        }

        private static void AfterCloudChecks(PhaseState __state, Exception? __exception) => Record(__state, Metric.CharacterSaveCloudChecks, __exception);
        private static void AfterHash(ZPackage __instance, PhaseState __state, Exception? __exception)
        {
            Record(__state, Metric.CharacterSaveHash, __exception);
            if (!active || __instance == null) return;
            // The hashed package is the file payload; Size() only flushes what GetArray already flushed.
            try { RecordMaximum(ref packageBytes, __instance.Size()); }
            catch { Interlocked.Increment(ref probeFailures); }
        }
        private static void AfterReplace(PhaseState __state, Exception? __exception) => Record(__state, Metric.CharacterSaveReplace, __exception);
        private static void AfterBackup(PhaseState __state, Exception? __exception) => Record(__state, Metric.CharacterSaveBackup, __exception);

        // The interesting cost is bounded by construction (open the file/cloud buffer) through
        // Finish (flush, then the cloud chunk upload or local file flush); the inline
        // fileWriter.m_binary.Write(...) calls between them run against that same buffer and
        // are covered by starting the clock here rather than at Finish's own entry.
        private static void AfterWriterConstructed(FileWriter __instance)
        {
            if (!active) return;
            try
            {
                writeStarted = Stopwatch.GetTimestamp();
                writeStartValid = true;
                Interlocked.Exchange(ref fileSource, __instance.m_fileSource == FileHelpers.FileSource.Cloud ? "cloud" : "local");
            }
            catch { Interlocked.Increment(ref probeFailures); }
        }

        private static void BeforeWrite(out PhaseState __state)
        {
            __state = default;
            bool started = writeStartValid;
            writeStartValid = false;
            if (!active || !started) return;
            CaptureSession? session = Volatile.Read(ref TimingHooks.Current);
            if (session == null) return;
            __state = new PhaseState { Session = session, Started = writeStarted };
        }

        private static void AfterWrite(PhaseState __state, Exception? __exception) => Record(__state, Metric.CharacterSaveWrite, __exception);

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

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            gauges.Add(new NumberValue("character_save_package_bytes", Interlocked.Exchange(ref packageBytes, 0), "bytes"));
            gauges.Add(new NumberValue("character_save_disk_probe_failures", Interlocked.Exchange(ref probeFailures, 0), "calls"));
            labels.Add(new TextValue("character_save_disk_status", Status));
            labels.Add(new TextValue("character_save_file_source", Interlocked.Exchange(ref fileSource, "none")));
            labels.Add(new TextValue("character_save_disk_semantics",
                "phases_attributed_only_inside_saveplayertodisk_on_its_own_thread; " +
                "generatehash_getarray_filewriter_also_serve_world_saves_and_are_excluded_there; " +
                "write_spans_filewriter_construction_through_finish; package_bytes_is_interval_max_not_per_call; " +
                "file_source_is_none_when_no_save_completed_this_interval"));
        }

        internal static void Reset()
        {
            Interlocked.Exchange(ref packageBytes, 0);
            Interlocked.Exchange(ref probeFailures, 0);
            Interlocked.Exchange(ref fileSource, "none");
        }

        internal static void Uninstall()
        {
            Installed = false;
            Status = "disabled";
            Reset();
            try { Patches.UnpatchSelf(); }
            catch (Exception exception) { Status = "unpatch_failed_" + exception.GetType().Name; }
        }
    }
}
