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
    // Optional probes extend this to the whole Game.SavePlayerProfile call (player data,
    // map data, derived gaps between hooked calls, unaccounted remainders, trigger); each
    // one that fails to install loses only its own timing and is named in probes_missing.
    internal static class CharacterSaveDiskTelemetry
    {
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".CharacterSaveDiskTelemetry");
        // Set only on the thread running SavePlayerToDisk; a different thread (e.g. the
        // world-save worker) always reads its own, unset slot and is never attributed here.
        [ThreadStatic] private static bool active;
        [ThreadStatic] private static long writeStarted;
        [ThreadStatic] private static bool writeStartValid;
        // Whole-save scope (Game.SavePlayerProfile) and the marks the derived gaps start from.
        [ThreadStatic] private static bool profileActive;
        [ThreadStatic] private static bool diskRan;
        [ThreadStatic] private static bool inPlayerData;
        [ThreadStatic] private static bool inMapData;
        [ThreadStatic] private static bool inReload;
        [ThreadStatic] private static long diskStarted;
        [ThreadStatic] private static long cloudEnded;
        [ThreadStatic] private static long hashEnded;
        [ThreadStatic] private static long reloadFiles;
        [ThreadStatic] private static CharacterSaveBreakdown? breakdown;
        private static long packageBytes;
        private static long playerDataBytes;
        private static long mapDataBytes;
        private static long catalogFiles;
        private static long directSaves;
        private static readonly long[] Triggers = new long[Enum.GetValues(typeof(CharacterSaveTrigger)).Length];
        private static long probeFailures;
        private static string fileSource = "none";
        private static readonly List<string> MissingProbes = new List<string>();
        private static bool profileProbe;
        private static AccessTools.FieldRef<Game, bool>? shuttingDown;
        private static AccessTools.FieldRef<PlayerProfile, byte[]>? playerData;

        internal static bool Installed { get; private set; }
        internal static string Status { get; private set; } = "disabled";

        internal struct PhaseState
        {
            internal CaptureSession? Session;
            internal long Started;
            internal long FinishStarted;
            internal bool Valid;
            internal bool PreviousFlag;
        }

        internal struct ProfileState
        {
            internal bool Tracking;
            internal long Started;
            internal int Trigger;
        }

        private static CharacterSaveBreakdown Breakdown => breakdown ??= new CharacterSaveBreakdown();

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
                    prefix: new HarmonyMethod(typeof(CharacterSaveDiskTelemetry), nameof(BeforeHash)),
                    finalizer: new HarmonyMethod(typeof(CharacterSaveDiskTelemetry), nameof(AfterHash)));
                // GetArray itself must stay unpatched: MapCompressionCache.Compatible() refuses
                // any owner on it, even this plugin's, and would fall back on every save.
                Patches.Patch(writerCtor,
                    prefix: new HarmonyMethod(typeof(CharacterSaveDiskTelemetry), nameof(BeforeWriterConstructed)),
                    postfix: new HarmonyMethod(typeof(CharacterSaveDiskTelemetry), nameof(AfterWriterConstructed)));
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
                try { PatchRemoval.UnpatchOwned(Patches); } catch { }
                Installed = false;
                Status = "unavailable";
                logger.LogWarning("Character save disk phase attribution unavailable: " + exception.GetType().Name);
                return;
            }
            InstallOptionalProbes(logger);
        }

        // Each optional probe validates its own target and, on any mismatch, is skipped alone.
        // Scope-gated probes still install without their parent but never fire: player_data and
        // map_data need profile, inventory/skills need player_data, map_data_bytes needs map_data,
        // catalog_files needs catalog_reload. probes_missing names the parent that failed.
        private static void InstallOptionalProbes(ManualLogSource logger)
        {
            MissingProbes.Clear();
            profileProbe = TryProbe("profile", () =>
            {
                MethodInfo method = AccessTools.DeclaredMethod(typeof(Game), "SavePlayerProfile", new[] { typeof(bool), typeof(bool) })
                    ?? throw new InvalidOperationException("Game.SavePlayerProfile is unavailable.");
                ParameterInfo[] parameters = method.GetParameters();
                if (method.IsStatic || method.ReturnType != typeof(void) ||
                    parameters[0].Name != "setLogoutPoint" || parameters[1].Name != "isFromRpc")
                    throw new InvalidOperationException("Unsupported Game.SavePlayerProfile signature.");
                FieldInfo? flag = AccessTools.DeclaredField(typeof(Game), "m_shuttingDown");
                if (flag == null || flag.IsStatic || flag.FieldType != typeof(bool))
                    throw new InvalidOperationException("Game.m_shuttingDown is unavailable.");
                shuttingDown = AccessTools.FieldRefAccess<Game, bool>(flag);
                Patches.Patch(method,
                    prefix: new HarmonyMethod(typeof(CharacterSaveDiskTelemetry), nameof(BeforeProfile)),
                    finalizer: new HarmonyMethod(typeof(CharacterSaveDiskTelemetry), nameof(AfterProfile)));
            });
            // Resolved outside the probe body: that body names Player, whose type load can fail first.
            playerData = ResolvePlayerData();
            TryProbe("player_data", () =>
            {
                if (playerData == null) throw new InvalidOperationException("PlayerProfile.m_playerData is unavailable.");
                MethodInfo method = AccessTools.DeclaredMethod(typeof(PlayerProfile), "SavePlayerData", new[] { typeof(Player) })
                    ?? throw new InvalidOperationException("PlayerProfile.SavePlayerData is unavailable.");
                if (method.IsStatic || method.ReturnType != typeof(void))
                    throw new InvalidOperationException("Unsupported PlayerProfile.SavePlayerData signature.");
                Patches.Patch(method,
                    prefix: new HarmonyMethod(typeof(CharacterSaveDiskTelemetry), nameof(BeforePlayerData)),
                    finalizer: new HarmonyMethod(typeof(CharacterSaveDiskTelemetry), nameof(AfterPlayerData)));
            });
            // Inventory.Save also runs for every container save; the prefix returns on its first
            // statement unless the calling thread is inside SavePlayerData.
            TryProbe("inventory", () => PatchNested(typeof(Inventory), nameof(AfterInventory)));
            TryProbe("skills", () => PatchNested(typeof(Skills), nameof(AfterSkills)));
            TryProbe("map_data", () =>
            {
                MethodInfo method = AccessTools.DeclaredMethod(typeof(Minimap), "SaveMapData", Type.EmptyTypes)
                    ?? throw new InvalidOperationException("Minimap.SaveMapData is unavailable.");
                if (method.IsStatic || method.ReturnType != typeof(void))
                    throw new InvalidOperationException("Unsupported Minimap.SaveMapData signature.");
                Patches.Patch(method,
                    prefix: new HarmonyMethod(typeof(CharacterSaveDiskTelemetry), nameof(BeforeMapData)),
                    finalizer: new HarmonyMethod(typeof(CharacterSaveDiskTelemetry), nameof(AfterMapData)));
            });
            TryProbe("map_data_bytes", () =>
            {
                MethodInfo method = AccessTools.DeclaredMethod(typeof(PlayerProfile), "SetMapData", new[] { typeof(byte[]) })
                    ?? throw new InvalidOperationException("PlayerProfile.SetMapData is unavailable.");
                if (method.IsStatic || method.ReturnType != typeof(void) || method.GetParameters()[0].Name != "data")
                    throw new InvalidOperationException("Unsupported PlayerProfile.SetMapData signature.");
                Patches.Patch(method, prefix: new HarmonyMethod(typeof(CharacterSaveDiskTelemetry), nameof(BeforeSetMapData)));
            });
            TryProbe("catalog_reload", () =>
            {
                MethodInfo method = AccessTools.DeclaredMethod(typeof(SaveCollection), "Reload", Type.EmptyTypes)
                    ?? throw new InvalidOperationException("SaveCollection.Reload is unavailable.");
                if (method.IsStatic || method.ReturnType != typeof(void))
                    throw new InvalidOperationException("Unsupported SaveCollection.Reload signature.");
                Patches.Patch(method,
                    prefix: new HarmonyMethod(typeof(CharacterSaveDiskTelemetry), nameof(BeforeReload)),
                    finalizer: new HarmonyMethod(typeof(CharacterSaveDiskTelemetry), nameof(AfterReload)));
            });
            TryProbe("catalog_files", () =>
            {
                MethodInfo method = AccessTools.DeclaredMethod(typeof(FileHelpers), "GetFiles",
                    new[] { typeof(FileHelpers.FileSource), typeof(string), typeof(string), typeof(string) })
                    ?? throw new InvalidOperationException("FileHelpers.GetFiles is unavailable.");
                if (!method.IsStatic || method.ReturnType != typeof(string[]))
                    throw new InvalidOperationException("Unsupported FileHelpers.GetFiles signature.");
                Patches.Patch(method, postfix: new HarmonyMethod(typeof(CharacterSaveDiskTelemetry), nameof(AfterGetFiles)));
            });
            TryProbe("backup_copy", () =>
            {
                MethodInfo method = AccessTools.DeclaredMethod(typeof(SaveSystem), "Copy",
                    new[] { typeof(SaveFile), typeof(string), typeof(FileHelpers.FileSource) })
                    ?? throw new InvalidOperationException("SaveSystem.Copy is unavailable.");
                if (!method.IsStatic || method.ReturnType != typeof(bool))
                    throw new InvalidOperationException("Unsupported SaveSystem.Copy signature.");
                Patches.Patch(method,
                    prefix: new HarmonyMethod(typeof(CharacterSaveDiskTelemetry), nameof(BeforePhase)),
                    finalizer: new HarmonyMethod(typeof(CharacterSaveDiskTelemetry), nameof(AfterBackupCopy)));
            });
            if (MissingProbes.Count != 0)
                logger.LogWarning("Character save breakdown probes unavailable: " + string.Join(",", MissingProbes.ToArray()));

            bool TryProbe(string name, Action install)
            {
                try { install(); return true; }
                catch { MissingProbes.Add(name); return false; }
            }
        }

        private static AccessTools.FieldRef<PlayerProfile, byte[]>? ResolvePlayerData()
        {
            try
            {
                FieldInfo? field = AccessTools.DeclaredField(typeof(PlayerProfile), "m_playerData");
                return field != null && !field.IsStatic && field.FieldType == typeof(byte[])
                    ? AccessTools.FieldRefAccess<PlayerProfile, byte[]>(field) : null;
            }
            catch { return null; }
        }

        private static void PatchNested(Type owner, string finalizer)
        {
            MethodInfo method = AccessTools.DeclaredMethod(owner, "Save", new[] { typeof(ZPackage) })
                ?? throw new InvalidOperationException(owner.Name + ".Save is unavailable.");
            if (method.IsStatic || method.ReturnType != typeof(void))
                throw new InvalidOperationException("Unsupported " + owner.Name + ".Save signature.");
            Patches.Patch(method,
                prefix: new HarmonyMethod(typeof(CharacterSaveDiskTelemetry), nameof(BeforeNested)),
                finalizer: new HarmonyMethod(typeof(CharacterSaveDiskTelemetry), finalizer));
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

        private static double Since(long started) => (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;

        private static void ResetMarks()
        {
            writeStartValid = false;
            cloudEnded = 0;
            hashEnded = 0;
        }

        // --- Whole-save scope: Game.SavePlayerProfile. ---
        private static void BeforeProfile(Game __instance, bool setLogoutPoint, bool isFromRpc, out ProfileState __state)
        {
            __state = default;
            if (!Installed || !profileProbe || profileActive) return;
            var trigger = CharacterSaveTrigger.Other;
            try
            {
                // Read before the body zeroes m_saveTimer; UpdateSaving calls only past the interval.
                trigger = CharacterSaveBreakdown.Classify(isFromRpc, shuttingDown != null && shuttingDown(__instance),
                    setLogoutPoint, __instance.m_saveTimer, Game.m_saveInterval);
            }
            catch { Interlocked.Increment(ref probeFailures); }
            Breakdown.Clear();
            profileActive = true;
            diskRan = false;
            __state = new ProfileState { Tracking = true, Started = Stopwatch.GetTimestamp(), Trigger = (int)trigger };
        }

        // A save the native body skipped (DontSaveCharacter flag, HardSaveBlock) never reached
        // SavePlayerToDisk and is neither timed as a remainder nor counted.
        private static void AfterProfile(ProfileState __state)
        {
            if (!__state.Tracking) return;
            try
            {
                if (diskRan)
                {
                    double total = Since(__state.Started);
                    RecordNow(Metric.CharacterSaveUnaccounted, Breakdown.ProfileUnaccounted(total));
                    Interlocked.Increment(ref Triggers[__state.Trigger]);
                }
            }
            catch { Interlocked.Increment(ref probeFailures); }
            profileActive = false;
            diskRan = false;
        }

        private static void BeforeSave(out bool __state)
        {
            __state = active;
            if (!Installed) return;
            active = true;
            ResetMarks();
            // A disk save outside SavePlayerProfile (e.g. from the character menu) gets its own breakdown.
            if (!profileActive) Breakdown.Clear();
            diskStarted = Stopwatch.GetTimestamp();
        }

        // Void finalizer preserves the native return value and exception; it only restores
        // the scope flag so a nested/failed call can never leave phase attribution stuck on.
        private static void AfterSave(bool __state)
        {
            if (active && diskStarted != 0)
            {
                try
                {
                    double toDisk = Since(diskStarted);
                    Breakdown.Add(CharacterSavePhase.ToDisk, toDisk);
                    RecordNow(Metric.CharacterSaveToDiskUnaccounted, Breakdown.ToDiskUnaccounted(toDisk));
                    if (profileActive) diskRan = true;
                    else Interlocked.Increment(ref directSaves);
                }
                catch { Interlocked.Increment(ref probeFailures); }
            }
            active = __state;
            diskStarted = 0;
            ResetMarks();
        }

        private static void BeforePhase(out PhaseState __state)
        {
            __state = default;
            if (!active) return;
            __state = new PhaseState { Session = Volatile.Read(ref TimingHooks.Current), Started = Stopwatch.GetTimestamp(), Valid = true };
        }

        private static double Record(PhaseState state, Metric metric, Exception? exception)
        {
            if (!state.Valid) return 0;
            double elapsed = Since(state.Started);
            Record(state.Session, metric, elapsed, exception != null);
            return elapsed;
        }

        private static void Attribute(PhaseState state, Metric metric, Exception? exception, CharacterSavePhase phase)
        {
            if (state.Valid) Breakdown.Add(phase, Record(state, metric, exception));
        }

        private static void Record(CaptureSession? session, Metric metric, double milliseconds, bool failed)
        {
            if (session == null) return;
            try { session.Book.Record(metric, milliseconds, failed); }
            catch { session.RecordProbeFailure(); }
        }

        private static void RecordNow(Metric metric, double milliseconds) =>
            Record(Volatile.Read(ref TimingHooks.Current), metric, milliseconds, false);

        // Derived gap: the time between the end of one hooked call and the start of the next.
        // The game-contract test pins the native call order that names each gap.
        private static void RecordGap(ref long mark, Metric metric, CharacterSavePhase phase)
        {
            long from = mark;
            mark = 0;
            if (from == 0) return;
            double elapsed = Since(from);
            Breakdown.Add(phase, elapsed);
            RecordNow(metric, elapsed);
        }

        private static void AfterCloudChecks(PhaseState __state, Exception? __exception)
        {
            Attribute(__state, Metric.CharacterSaveCloudChecks, __exception, CharacterSavePhase.CloudChecks);
            if (__state.Valid) cloudEnded = Stopwatch.GetTimestamp();
        }

        // Build = cloud checks end -> hash start: the ZPackage stat/world-data/player-data writes.
        private static void BeforeHash(out PhaseState __state)
        {
            if (active) RecordGap(ref cloudEnded, Metric.CharacterSaveBuild, CharacterSavePhase.Build);
            BeforePhase(out __state);
        }

        private static void AfterHash(ZPackage __instance, PhaseState __state, Exception? __exception)
        {
            Attribute(__state, Metric.CharacterSaveHash, __exception, CharacterSavePhase.Hash);
            if (!active || __instance == null) return;
            if (__state.Valid) hashEnded = Stopwatch.GetTimestamp();
            // The hashed package is the file payload; Size() only flushes what GetArray already flushed.
            try { RecordMaximum(ref packageBytes, __instance.Size()); }
            catch { Interlocked.Increment(ref probeFailures); }
        }
        private static void AfterReplace(PhaseState __state, Exception? __exception) =>
            Attribute(__state, Metric.CharacterSaveReplace, __exception, CharacterSavePhase.Replace);
        private static void AfterBackup(PhaseState __state, Exception? __exception) =>
            Attribute(__state, Metric.CharacterSaveBackup, __exception, CharacterSavePhase.Backup);

        // GetArray = hash end -> FileWriter construction start: the second full package copy.
        private static void BeforeWriterConstructed(out PhaseState __state)
        {
            if (active) RecordGap(ref hashEnded, Metric.CharacterSaveGetArray, CharacterSavePhase.GetArray);
            BeforePhase(out __state);
        }

        // The interesting cost is bounded by construction (open the file/cloud buffer) through
        // Finish (flush, then the cloud chunk upload or local file flush); the inline
        // fileWriter.m_binary.Write(...) calls between them run against that same buffer and
        // are covered by starting the clock here rather than at Finish's own entry.
        private static void AfterWriterConstructed(FileWriter __instance, PhaseState __state)
        {
            if (!active) return;
            try
            {
                // Open = File.Create + 256 KiB BufferedStream (local) or a MemoryStream (cloud).
                Attribute(__state, Metric.CharacterSaveOpen, null, CharacterSavePhase.Open);
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
            long now = Stopwatch.GetTimestamp();
            CaptureSession? session = Volatile.Read(ref TimingHooks.Current);
            // BufferWrite = construction end -> Finish start: the inline length/payload/hash writes.
            double buffered = (now - writeStarted) * 1000.0 / Stopwatch.Frequency;
            Breakdown.Add(CharacterSavePhase.BufferWrite, buffered);
            Record(session, Metric.CharacterSaveBufferWrite, buffered, false);
            __state = new PhaseState { Session = session, Started = writeStarted, FinishStarted = now, Valid = true };
        }

        // Write keeps its original span (construction through Finish); Finish alone is the
        // flush + flushToDisk (local) or chunked cloud upload, and close.
        private static void AfterWrite(PhaseState __state, Exception? __exception)
        {
            if (!__state.Valid) return;
            long now = Stopwatch.GetTimestamp();
            double finished = (now - __state.FinishStarted) * 1000.0 / Stopwatch.Frequency;
            Breakdown.Add(CharacterSavePhase.Finish, finished);
            Record(__state.Session, Metric.CharacterSaveWrite, (now - __state.Started) * 1000.0 / Stopwatch.Frequency, __exception != null);
            Record(__state.Session, Metric.CharacterSaveFinish, finished, __exception != null);
        }

        // --- Optional probes. Each is attributed only inside its parent's thread-static scope. ---
        private static void BeforePlayerData(out PhaseState __state)
        {
            __state = default;
            if (!profileActive) return;
            __state = new PhaseState { Session = Volatile.Read(ref TimingHooks.Current), Started = Stopwatch.GetTimestamp(), Valid = true, PreviousFlag = inPlayerData };
            inPlayerData = true;
        }

        private static void AfterPlayerData(PlayerProfile __instance, PhaseState __state, Exception? __exception)
        {
            if (!__state.Valid) return;
            inPlayerData = __state.PreviousFlag;
            Attribute(__state, Metric.CharacterSavePlayerData, __exception, CharacterSavePhase.PlayerData);
            try
            {
                byte[]? data = __instance != null && playerData != null ? playerData(__instance) : null;
                if (data != null) RecordMaximum(ref playerDataBytes, data.Length);
            }
            catch { Interlocked.Increment(ref probeFailures); }
        }

        private static void BeforeNested(out PhaseState __state)
        {
            __state = default;
            if (!inPlayerData) return;
            __state = new PhaseState { Session = Volatile.Read(ref TimingHooks.Current), Started = Stopwatch.GetTimestamp(), Valid = true };
        }

        private static void AfterInventory(PhaseState __state, Exception? __exception) => Record(__state, Metric.CharacterSaveInventory, __exception);
        private static void AfterSkills(PhaseState __state, Exception? __exception) => Record(__state, Metric.CharacterSaveSkills, __exception);

        private static void BeforeMapData(out PhaseState __state)
        {
            __state = default;
            if (!profileActive) return;
            __state = new PhaseState { Session = Volatile.Read(ref TimingHooks.Current), Started = Stopwatch.GetTimestamp(), Valid = true, PreviousFlag = inMapData };
            inMapData = true;
        }

        private static void AfterMapData(PhaseState __state, Exception? __exception)
        {
            if (!__state.Valid) return;
            inMapData = __state.PreviousFlag;
            Attribute(__state, Metric.CharacterSaveMapData, __exception, CharacterSavePhase.MapData);
        }

        // Reads only the argument SaveMapData passes: the compressed map payload GetMapData built.
        private static void BeforeSetMapData(byte[] data)
        {
            if (!inMapData || data == null) return;
            RecordMaximum(ref mapDataBytes, data.Length);
        }

        // SavePlayerToDisk invalidates the character catalog twice, so the next lookup
        // (ConsiderAutoBackup -> TryGetSaveByName) rescans the folder and stats every file.
        private static void BeforeReload(SaveCollection __instance, out PhaseState __state)
        {
            __state = default;
            if (!active || __instance == null || __instance.m_dataType != SaveDataType.Character) return;
            BeforePhase(out __state);
            __state.PreviousFlag = inReload;
            inReload = true;
            reloadFiles = 0;
        }

        private static void AfterReload(PhaseState __state, Exception? __exception)
        {
            if (!__state.Valid) return;
            inReload = __state.PreviousFlag;
            Record(__state, Metric.CharacterSaveCatalogReload, __exception);
            RecordMaximum(ref catalogFiles, reloadFiles);
        }

        private static void AfterGetFiles(string[] __result)
        {
            if (!inReload || __result == null) return;
            reloadFiles += __result.Length;
        }

        private static void AfterBackupCopy(PhaseState __state, Exception? __exception) => Record(__state, Metric.CharacterSaveBackupCopy, __exception);

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
            gauges.Add(new NumberValue("character_save_player_data_bytes", Interlocked.Exchange(ref playerDataBytes, 0), "bytes"));
            gauges.Add(new NumberValue("character_save_map_data_bytes", Interlocked.Exchange(ref mapDataBytes, 0), "bytes"));
            gauges.Add(new NumberValue("character_save_catalog_files", Interlocked.Exchange(ref catalogFiles, 0), "files"));
            long saves = 0;
            foreach (CharacterSaveTrigger trigger in Enum.GetValues(typeof(CharacterSaveTrigger)))
            {
                long count = Interlocked.Exchange(ref Triggers[(int)trigger], 0);
                saves += count;
                gauges.Add(new NumberValue("character_save_trigger_" + CharacterSaveBreakdown.Name(trigger), count, "saves"));
            }
            gauges.Add(new NumberValue("character_saves", saves, "saves"));
            gauges.Add(new NumberValue("character_save_direct_disk_saves", Interlocked.Exchange(ref directSaves, 0), "saves"));
            gauges.Add(new NumberValue("character_save_disk_probe_failures", Interlocked.Exchange(ref probeFailures, 0), "calls"));
            labels.Add(new TextValue("character_save_disk_status", Status));
            labels.Add(new TextValue("character_save_file_source", Interlocked.Exchange(ref fileSource, "none")));
            labels.Add(new TextValue("character_save_probes_missing",
                !Installed ? "n/a" : MissingProbes.Count == 0 ? "none" : string.Join(",", MissingProbes.ToArray())));
            labels.Add(new TextValue("character_save_disk_semantics",
                "phases_attributed_only_inside_saveplayertodisk_on_its_own_thread; " +
                "generatehash_getarray_filewriter_also_serve_world_saves_and_are_excluded_there; " +
                "write_spans_filewriter_construction_through_finish_and_overlaps_bufferwrite_plus_finish; " +
                "build_getarray_bufferwrite_are_gaps_between_hooked_calls; " +
                "inventory_skills_nest_in_playerdata_catalogreload_backupcopy_nest_in_cloudchecks_or_backup; " +
                "unaccounted_is_parent_minus_exclusive_children_and_absorbs_missing_probes; " +
                "bytes_and_catalog_files_are_interval_max_not_per_call; " +
                "file_source_is_none_when_no_save_completed_this_interval"));
        }

        internal static void Reset()
        {
            Interlocked.Exchange(ref packageBytes, 0);
            Interlocked.Exchange(ref playerDataBytes, 0);
            Interlocked.Exchange(ref mapDataBytes, 0);
            Interlocked.Exchange(ref catalogFiles, 0);
            Interlocked.Exchange(ref directSaves, 0);
            for (int i = 0; i < Triggers.Length; i++) Interlocked.Exchange(ref Triggers[i], 0);
            Interlocked.Exchange(ref probeFailures, 0);
            Interlocked.Exchange(ref fileSource, "none");
        }

        internal static void Uninstall()
        {
            Installed = false;
            profileProbe = false;
            Status = "disabled";
            Reset();
            MissingProbes.Clear();
            // Per method, so one target that cannot be rewritten does not strand the others patched.
            string? failure = null;
            foreach (MethodBase method in new List<MethodBase>(Patches.GetPatchedMethods()))
            {
                try { Patches.Unpatch(method, HarmonyPatchType.All, Patches.Id); }
                catch (Exception exception) { failure ??= exception.GetType().Name; }
            }
            if (failure != null) Status = "unpatch_failed_" + failure;
        }
    }
}
