using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;
using UnityEngine;

namespace BetterPerformance
{
    // Speculative pre-compression of the character map payload. A worker re-encodes a
    // main-thread snapshot with the game's own writer and compressor and parks the resulting
    // (input, encoded) pair; the main thread publishes it into the exact compression cache so
    // the synchronous Minimap.SaveMapData hits instead of compressing. Correctness never
    // depends on the worker: the cache compares the complete input before it emits anything,
    // so a stale or wrong speculation is a miss and the native path runs unchanged.
    internal static class MapPrecompression
    {
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".MapPrecompression");
        private static readonly object Gate = new object();
        private static ConfigEntry<bool>? option;
        private static AccessTools.FieldRef<Minimap, BitArray>? exploredField, othersField;
        private static AccessTools.FieldRef<Minimap, int>? textureSizeField;
        private static AccessTools.FieldRef<Minimap, List<Minimap.PinData>>? pinsField;
        private static FieldInfo? authorField;
        private static PrecompressionPolicy? policy;
        private static string policyLabel = "none";
        private static int mainThread;
        private static long changes;
        private static long runs, skippedDirty, skippedInFlight, published, stale, failures, saveAdoptions, bulkRuns;
        private static double snapshotMsMax, workerMsMax;
        private static double lastPumpSeconds = double.NegativeInfinity;
        private static byte[]? pendingInput, pendingEncoded;
        private static ZNet? pendingWorld;
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly FieldInfo PackageWriter = AccessTools.DeclaredField(typeof(ZPackage), "m_writer");

        // The pump only reconsiders at this cadence; the quiet window is measured in seconds,
        // so a per-frame evaluation would buy nothing and cost a pin scan every frame.
        private const double PumpIntervalSeconds = 0.25;

        internal static bool Installed { get; private set; }
        internal static bool Enabled => Installed && option != null && option.Value;
        internal static string Status { get; private set; } = "disabled";

        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            option = config.Bind("CharacterSave", "SpeculativeMapCompressionEnabled", false,
                "Compress the character map payload speculatively on a background thread and prime the exact compression cache, so the synchronous character save can reuse it. Requires MapSaving.ExactCompressionCacheEnabled. Output is only ever reused when the complete serialized input is byte-identical. Requires restart to install.");
            double quiet = config.Bind("CharacterSave", "SpeculativeMapQuietSeconds", 2.0,
                "Seconds the map payload must stop changing before a speculative run is dispatched.").Value;
            double interval = config.Bind("CharacterSave", "SpeculativeMapMinimumIntervalSeconds", 30.0,
                "Minimum seconds between two speculative runs.").Value;
            int cap = config.Bind("CharacterSave", "SpeculativeMapMaximumRunsPerMinute", 2,
                "Hard cap on speculative runs started per minute. Each run costs roughly one core for the duration of one map compression.").Value;
            policyLabel = "quiet=" + quiet.ToString("0.##", CultureInfo.InvariantCulture) + "s;interval="
                + interval.ToString("0.##", CultureInfo.InvariantCulture) + "s;cap=" + cap.ToString(CultureInfo.InvariantCulture) + "/min";
            if (!option.Value) { Status = "disabled"; return; }
            if (!MapCompressionCache.Installed || !MapCompressionCache.Enabled)
            {
                Release("unavailable_cache_disabled");
                logger.LogWarning("Speculative map compression needs the exact compression cache; native compression retained.");
                return;
            }
            try
            {
                quiet = quiet < 0.0 ? 0.0 : quiet;
                interval = interval < 0.0 ? 0.0 : interval;
                cap = cap < 1 ? 1 : cap;
                var targets = ValidateContracts();
                policy = new PrecompressionPolicy(quiet, interval, cap);
                mainThread = Thread.CurrentThread.ManagedThreadId;
                var mark = new HarmonyMethod(typeof(MapPrecompression), nameof(NoteExplored));
                Patches.Patch(targets.Explore, postfix: mark);
                Patches.Patch(targets.ExploreOthers, postfix: mark);
                Patches.Patch(targets.Reset, postfix: new HarmonyMethod(typeof(MapPrecompression), nameof(NoteReset)));
                Installed = true;
                Status = "installed";
                logger.LogInfo("Speculative map compression installed; " + policyLabel + ".");
            }
            catch (Exception exception)
            {
                try { Patches.UnpatchSelf(); } catch { Interlocked.Increment(ref failures); }
                Release("unavailable");
                logger.LogWarning("Speculative map compression unavailable; native compression retained: "
                    + exception.GetType().Name + ": " + exception.Message);
            }
        }

        // Every field the snapshot reads and every method that mutates it is pinned here, so a
        // changed game layout disables the module instead of publishing a wrong payload.
        private static (MethodInfo Explore, MethodInfo ExploreOthers, MethodInfo Reset) ValidateContracts()
        {
            var explore = AccessTools.DeclaredMethod(typeof(Minimap), "Explore", new[] { typeof(int), typeof(int) })
                ?? throw new InvalidOperationException("Minimap.Explore(int, int) is missing.");
            var exploreOthers = AccessTools.DeclaredMethod(typeof(Minimap), "ExploreOthers", new[] { typeof(int), typeof(int) })
                ?? throw new InvalidOperationException("Minimap.ExploreOthers(int, int) is missing.");
            var reset = AccessTools.DeclaredMethod(typeof(Minimap), "ResetAndExplore", new[] { typeof(BitArray), typeof(BitArray) })
                ?? throw new InvalidOperationException("Minimap.ResetAndExplore(BitArray, BitArray) is missing.");
            if (explore.ReturnType != typeof(bool) || exploreOthers.ReturnType != typeof(bool) || reset.ReturnType != typeof(void) ||
                explore.IsStatic || exploreOthers.IsStatic || reset.IsStatic)
                throw new InvalidOperationException("Unsupported minimap explore signatures.");
            if (AccessTools.DeclaredField(typeof(Minimap), "m_explored")?.FieldType != typeof(BitArray) ||
                AccessTools.DeclaredField(typeof(Minimap), "m_exploredOthers")?.FieldType != typeof(BitArray) ||
                AccessTools.DeclaredField(typeof(Minimap), "m_textureSize")?.FieldType != typeof(int) ||
                AccessTools.DeclaredField(typeof(Minimap), "m_pins")?.FieldType != typeof(List<Minimap.PinData>))
                throw new InvalidOperationException("Unsupported minimap map fields.");
            foreach (string name in new[] { "m_name", "m_pos", "m_type", "m_checked", "m_ownerID", "m_author", "m_save" })
                if (AccessTools.DeclaredField(typeof(Minimap.PinData), name) == null)
                    throw new InvalidOperationException("Minimap.PinData." + name + " is missing.");
            if (AccessTools.DeclaredField(typeof(Minimap.PinData), "m_name")!.FieldType != typeof(string) ||
                AccessTools.DeclaredField(typeof(Minimap.PinData), "m_pos")!.FieldType != typeof(Vector3) ||
                AccessTools.DeclaredField(typeof(Minimap.PinData), "m_checked")!.FieldType != typeof(bool) ||
                AccessTools.DeclaredField(typeof(Minimap.PinData), "m_save")!.FieldType != typeof(bool) ||
                AccessTools.DeclaredField(typeof(Minimap.PinData), "m_ownerID")!.FieldType != typeof(long) ||
                !AccessTools.DeclaredField(typeof(Minimap.PinData), "m_type")!.FieldType.IsEnum ||
                AccessTools.DeclaredField(typeof(Minimap.PinData), "m_author")!.FieldType.Name != "PlatformUserID")
                throw new InvalidOperationException("Unsupported minimap pin field types.");
            if (AccessTools.DeclaredMethod(typeof(ZNet), "IsReferencePositionPublic", Type.EmptyTypes)?.ReturnType != typeof(bool))
                throw new InvalidOperationException("ZNet.IsReferencePositionPublic() is missing.");
            if (AccessTools.DeclaredMethod(typeof(ZPackage), "WriteCompressed", new[] { typeof(ZPackage) }) == null ||
                AccessTools.DeclaredMethod(typeof(ZPackage), "GetArray", Type.EmptyTypes)?.ReturnType != typeof(byte[]))
                throw new InvalidOperationException("Unsupported package compression contract.");
            exploredField = AccessTools.FieldRefAccess<Minimap, BitArray>("m_explored");
            othersField = AccessTools.FieldRefAccess<Minimap, BitArray>("m_exploredOthers");
            textureSizeField = AccessTools.FieldRefAccess<Minimap, int>("m_textureSize");
            pinsField = AccessTools.FieldRefAccess<Minimap, List<Minimap.PinData>>("m_pins");
            authorField = AccessTools.DeclaredField(typeof(Minimap.PinData), "m_author");
            return (explore, exploreOthers, reset);
        }

        private static void NoteExplored(bool __result) { if (__result) Interlocked.Increment(ref changes); }
        private static void NoteReset() { Interlocked.Increment(ref changes); }

        // Main thread only. Called from the plugin's Update pump.
        internal static void Pump()
        {
            if (!Enabled || policy == null || Thread.CurrentThread.ManagedThreadId != mainThread) return;
            try
            {
                double now = Clock.Elapsed.TotalSeconds;
                if (now - lastPumpSeconds < PumpIntervalSeconds) return;
                lastPumpSeconds = now;
                if (TryAdoptPending()) return;
                var minimap = Minimap.instance;
                // A dedicated server has a Minimap but never saves a character profile.
                if (minimap == null || ZNet.instance == null || ZNet.instance.IsDedicated()) return;
                // The policy's in-flight flags are written by the worker under Gate; reading
                // and advancing them under the same lock keeps a stale read from turning
                // Dispatch's guard into a thrown exception that would disable the module.
                PrecompressionDecision decision;
                lock (Gate) { decision = policy.Evaluate(now, ChangeCounter(minimap)); }
                if (decision == PrecompressionDecision.Dirty) { skippedDirty++; return; }
                if (decision == PrecompressionDecision.InFlight) { skippedInFlight++; return; }
                if (decision != PrecompressionDecision.Run) return;
                long snapshotStarted = Stopwatch.GetTimestamp();
                var snapshot = Capture(minimap);
                snapshotMsMax = Math.Max(snapshotMsMax, (Stopwatch.GetTimestamp() - snapshotStarted) * 1000.0 / Stopwatch.Frequency);
                if (snapshot == null) return;
                lock (Gate) { policy.Dispatch(now, snapshot.Change); }
                runs++;
                if (snapshot.BulkBitsAllowed) bulkRuns++;
                var worker = new Thread(Encode) { IsBackground = true, Name = "BetterPerformance.MapPrecompression", Priority = System.Threading.ThreadPriority.BelowNormal };
                worker.Start(snapshot);
            }
            // A pump failure disables the module rather than risking a wrong publication.
            catch (Exception) { Interlocked.Increment(ref failures); Release("failed"); }
        }

        // Equality, not ordering: any difference in explored bits, saved pins or the reference
        // position flag yields a different value, so a run is never repeated on identical state.
        private static long ChangeCounter(Minimap minimap)
        {
            long value = Interlocked.Read(ref changes);
            var pins = pinsField == null ? null : pinsField(minimap);
            if (pins != null)
                for (int i = 0; i < pins.Count; i++)
                {
                    var pin = pins[i];
                    if (pin == null || !pin.m_save) continue;
                    unchecked
                    {
                        value = value * 1099511628211L ^ (pin.m_name == null ? 0 : pin.m_name.GetHashCode());
                        value = value * 1099511628211L ^ pin.m_pos.GetHashCode();
                        value = value * 1099511628211L ^ ((long)(int)pin.m_type << 2 | (pin.m_checked ? 2L : 0L) | 1L);
                        value = value * 1099511628211L ^ pin.m_ownerID;
                    }
                }
            var net = ZNet.instance;
            if (net != null && net.IsReferencePositionPublic()) unchecked { value = value * 1099511628211L ^ 0x5bf03635L; }
            return value;
        }

        // Two block copies of the bit array backing stores plus the saved pins flattened into
        // plain data. Nothing a worker touches afterwards is owned by the game.
        private static Snapshot? Capture(Minimap minimap)
        {
            if (exploredField == null || othersField == null || textureSizeField == null || pinsField == null || authorField == null) return null;
            var explored = exploredField(minimap);
            var others = othersField(minimap);
            var net = ZNet.instance;
            if (explored == null || others == null || net == null) return null;
            int length = explored.Length;
            // GetMapData bounds both loops by m_explored.Length; a shorter shared array would
            // make the native writer throw, so speculating on it would be meaningless.
            if (length <= 0 || others.Length < length) return null;
            int words = (length + 31) / 32;
            var exploredWords = new int[words];
            var othersWords = new int[words];
            explored.CopyTo(exploredWords, 0);
            others.CopyTo(othersWords, 0);
            var pins = pinsField(minimap);
            var saved = new List<Pin>(pins?.Count ?? 0);
            if (pins != null)
                for (int i = 0; i < pins.Count; i++)
                {
                    var pin = pins[i];
                    if (pin == null || !pin.m_save) continue;
                    saved.Add(new Pin
                    {
                        Name = pin.m_name,
                        X = pin.m_pos.x, Y = pin.m_pos.y, Z = pin.m_pos.z,
                        Type = (int)pin.m_type,
                        Checked = pin.m_checked,
                        Owner = pin.m_ownerID,
                        // m_author is a platform identity type this assembly does not
                        // reference; the native writer only ever uses its ToString().
                        Author = authorField!.GetValue(pin)?.ToString(),
                    });
                    if (saved[saved.Count - 1].Author == null) return null;
                }
            return new Snapshot
            {
                TextureSize = textureSizeField(minimap),
                Length = length,
                Explored = exploredWords,
                Others = othersWords,
                Pins = saved.ToArray(),
                ReferencePositionPublic = net.IsReferencePositionPublic(),
                World = net,
                Change = ChangeCounter(minimap),
                BulkBitsAllowed = FastMapSerialization.CanWriteSnapshotBits,
            };
        }

        // Worker thread. Plain managed data only: ZPackage is a MemoryStream plus a
        // BinaryWriter, and Utils.Compress, reached through WriteCompressed, is GZip over a
        // MemoryStream. No Unity API is called here.
        private static void Encode(object state)
        {
            var snapshot = (Snapshot)state;
            bool produced = false;
            try
            {
                long started = Stopwatch.GetTimestamp();
                var inner = BuildPayload(snapshot);
                var outer = new ZPackage();
                outer.WriteCompressed(inner);
                byte[] input = inner.GetArray();
                byte[] encoded = outer.GetArray();
                double elapsed = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                lock (Gate)
                {
                    workerMsMax = Math.Max(workerMsMax, elapsed);
                    pendingInput = input;
                    pendingEncoded = encoded;
                    pendingWorld = snapshot.World;
                    policy?.Complete(produced: true);
                    produced = true;
                }
            }
            catch (Exception) { Interlocked.Increment(ref failures); }
            finally { if (!produced) lock (Gate) { policy?.Complete(produced: false); } }
        }

        // The writer order of Minimap.GetMapData's inner package, sourced from the snapshot.
        internal static ZPackage BuildPayload(Snapshot snapshot)
        {
            var package = new ZPackage();
            package.Write(snapshot.TextureSize);
            WriteBits(package, snapshot.Explored, snapshot.Length, snapshot.BulkBitsAllowed);
            WriteBits(package, snapshot.Others, snapshot.Length, snapshot.BulkBitsAllowed);
            package.Write(snapshot.Pins.Length);
            for (int i = 0; i < snapshot.Pins.Length; i++)
            {
                var pin = snapshot.Pins[i];
                package.Write(pin.Name!);
                package.Write(new Vector3(pin.X, pin.Y, pin.Z));
                package.Write(pin.Type);
                package.Write(pin.Checked);
                package.Write(pin.Owner);
                package.Write(pin.Author!);
            }
            package.Write(snapshot.ReferencePositionPublic);
            return package;
        }

        private static bool Bit(int[] words, int index) => (words[index >> 5] & (1 << (index & 31))) != 0;

        private static void WriteBits(ZPackage package, int[] words, int count, bool allowBulk)
        {
            var writer = allowBulk ? PackageWriter?.GetValue(package) as BinaryWriter : null;
            if (writer != null && writer.GetType() == typeof(BinaryWriter) &&
                writer.BaseStream.GetType() == typeof(MemoryStream) && MapBitWriter.TryWritePacked(writer, words, count)) return;
            for (int i = 0; i < count; i++) package.Write(Bit(words, i));
        }

        // Publication stays on the main thread. Never wait for a worker, including when a
        // save polls this slot; complete-input comparison happens outside the publication lock.
        internal static bool TryAdoptPending(ArraySegment<byte>? savingInput = null)
        {
            if (savingInput.HasValue && !Enabled) return false;
            byte[]? input, encoded; ZNet? world;
            if (!Monitor.TryEnter(Gate)) return false;
            try
            {
                if (pendingInput == null || pendingEncoded == null) return false;
                input = pendingInput; encoded = pendingEncoded; world = pendingWorld;
            }
            finally { Monitor.Exit(Gate); }
            // A save can race the next 250 ms pump. Serve a finished worker immediately,
            // but never replace an already useful cache entry with a stale speculation.
            if (savingInput.HasValue)
            {
                var saving = savingInput.Value;
                if (saving.Count != input.Length) return false;
                for (int i = 0; i < saving.Count; i++)
                    if (saving.Array![saving.Offset + i] != input[i]) return false;
            }
            if (!Monitor.TryEnter(Gate)) return false;
            try
            {
                if (!ReferenceEquals(pendingInput, input) || !ReferenceEquals(pendingEncoded, encoded)) return false;
                pendingInput = pendingEncoded = null; pendingWorld = null;
                policy?.Adopted();
            }
            finally { Monitor.Exit(Gate); }
            bool adopted = MapCompressionCache.Adopt(input, encoded, world);
            // The worker increments failures too, so this counter must be atomic on both sides.
            if (adopted) Interlocked.Increment(ref published); else Interlocked.Increment(ref failures);
            if (adopted && savingInput.HasValue) saveAdoptions++;
            return true;
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            stale = MapCompressionCache.PrimedStale;
            gauges.Add(new NumberValue("map_precompress_runs", runs, "calls"));
            gauges.Add(new NumberValue("map_precompress_skipped_dirty", skippedDirty, "calls"));
            gauges.Add(new NumberValue("map_precompress_skipped_inflight", skippedInFlight, "calls"));
            gauges.Add(new NumberValue("map_precompress_snapshot_ms_max", snapshotMsMax, "ms"));
            gauges.Add(new NumberValue("map_precompress_worker_ms_max", workerMsMax, "ms"));
            gauges.Add(new NumberValue("map_precompress_published", published, "calls"));
            gauges.Add(new NumberValue("map_precompress_save_adoptions", saveAdoptions, "calls"));
            gauges.Add(new NumberValue("map_precompress_bulk_runs", bulkRuns, "calls"));
            gauges.Add(new NumberValue("map_precompress_primed_hits", MapCompressionCache.PrimedHits, "calls"));
            gauges.Add(new NumberValue("map_precompress_stale", stale, "calls"));
            gauges.Add(new NumberValue("map_precompress_failures", Interlocked.Read(ref failures), "calls"));
            labels.Add(new TextValue("map_precompress_status", Status));
            labels.Add(new TextValue("map_precompress_enabled", Enabled ? "true" : "false"));
            labels.Add(new TextValue("map_precompress_policy", policyLabel));
        }

        // Counters are process-cumulative like the cache's own; only the maxima restart.
        internal static void Reset() { snapshotMsMax = workerMsMax = 0; }

        internal static void Uninstall()
        {
            try { Patches.UnpatchSelf(); } catch { Interlocked.Increment(ref failures); }
            Release("disabled");
            option = null;
        }

        private static void Release(string status)
        {
            Installed = false;
            Status = status;
            lock (Gate) { pendingInput = pendingEncoded = null; pendingWorld = null; }
            policy?.Reset();
            exploredField = null; othersField = null; textureSizeField = null; pinsField = null; authorField = null;
        }

        internal struct Pin
        {
            internal string? Name;
            internal float X, Y, Z;
            internal int Type;
            internal bool Checked;
            internal long Owner;
            internal string? Author;
        }

        internal sealed class Snapshot
        {
            internal int TextureSize;
            internal int Length;
            internal int[] Explored = Array.Empty<int>();
            internal int[] Others = Array.Empty<int>();
            internal Pin[] Pins = Array.Empty<Pin>();
            internal bool ReferencePositionPublic;
            internal ZNet? World;
            internal long Change;
            internal bool BulkBitsAllowed;
        }
    }
}
