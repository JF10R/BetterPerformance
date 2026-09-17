using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;
using UnityEngine;

namespace BetterPerformance
{
    // DungeonGenerator.Spawn places every loaded room in one frame on the client, which is
    // the whole cost of a crypt appearing. This module skips that loop and replays the same
    // PlaceRoom calls, in the same order, across frames under a shared per-frame allowance.
    internal static class DungeonSpawnSlicing
    {
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".DungeonSpawnSlicing");
        private static readonly Dictionary<DungeonGenerator, Slice> Pending = new Dictionary<DungeonGenerator, Slice>();
        private static readonly List<Slice> Queue = new List<Slice>();
        private static ConfigEntry<bool>? option;
        private static ConfigEntry<float>? budgetMs;
        private static Func<DungeonGenerator, DungeonDB.RoomData, Vector3, Quaternion, RoomConnection, ZoneSystem.SpawnMode, Room>? placeRoom;
        private static Action<DungeonGenerator>? release;
        private static FieldInfo? loadedRooms, roomDataField, positionField, rotationField;
        private static SliceHost? host;
        private static long sliced, vanillaInside, vanillaServer, roomsPlaced, slices, aborted, releaseHeld, failures;
        private static double sliceMsMax;
        private static long spanFramesMax;

        internal static bool Installed { get; private set; }
        internal static bool Enabled { get; private set; }
        internal static string Status { get; private set; } = "disabled";
        internal static int PendingCount => Pending.Count;

        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            option = config.Bind("Dungeons", "SliceRoomSpawnEnabled", false,
                "Experimental. Spread the client-side dungeon room spawn across frames instead of placing every room in one frame. Client only; the placed rooms, their order and the network state are unchanged. Requires restart to install.");
            budgetMs = config.Bind("Dungeons", "RoomSpawnBudgetMilliseconds", 4f, new ConfigDescription(
                "Soft per-frame allowance shared by every dungeon being placed. At least one room is placed per frame, so a single room longer than the allowance still runs whole.",
                new AcceptableValueRange<float>(1f, 50f)));
            if (!option.Value) { Status = "disabled"; return; }
            MethodInfo spawn, releaseMethod, destroy;
            try { ValidateContracts(out spawn, out releaseMethod, out destroy); }
            catch (Exception exception)
            {
                Release("unavailable");
                logger.LogWarning("Dungeon spawn slicing unavailable; native single-frame spawn retained: " +
                    exception.GetType().Name + ": " + exception.Message);
                return;
            }
            try
            {
                Patches.Patch(spawn, prefix: new HarmonyMethod(typeof(DungeonSpawnSlicing), nameof(BeforeSpawn)));
                Patches.Patch(releaseMethod, prefix: new HarmonyMethod(typeof(DungeonSpawnSlicing), nameof(BeforeRelease)));
                Patches.Patch(destroy, prefix: new HarmonyMethod(typeof(DungeonSpawnSlicing), nameof(BeforeDestroy)));
                Installed = Enabled = true;
                Status = "installed";
                logger.LogWarning("Experimental dungeon spawn slicing installed at " + budgetMs.Value + " ms per frame.");
            }
            catch (Exception exception)
            {
                try { Patches.UnpatchSelf(); } catch { Interlocked.Increment(ref failures); }
                Release("patch_failed");
                logger.LogWarning("Dungeon spawn slicing could not patch the generator: " + exception.GetType().Name);
            }
        }

        // Every member the coroutine replays is resolved here, so an unsupported layout
        // leaves the native single-frame spawn in place instead of half-patching it.
        private static void ValidateContracts(out MethodInfo spawn, out MethodInfo releaseMethod, out MethodInfo destroy)
        {
            spawn = AccessTools.DeclaredMethod(typeof(DungeonGenerator), "Spawn", Type.EmptyTypes)
                ?? throw new InvalidOperationException("DungeonGenerator.Spawn() is missing.");
            if (spawn.IsStatic || spawn.ReturnType != typeof(void))
                throw new InvalidOperationException("Unsupported DungeonGenerator.Spawn signature.");
            releaseMethod = AccessTools.DeclaredMethod(typeof(DungeonGenerator), "ReleaseHeldReferences", Type.EmptyTypes)
                ?? throw new InvalidOperationException("DungeonGenerator.ReleaseHeldReferences() is missing.");
            destroy = AccessTools.DeclaredMethod(typeof(DungeonGenerator), "OnDestroy", Type.EmptyTypes)
                ?? throw new InvalidOperationException("DungeonGenerator.OnDestroy() is missing.");
            MethodInfo place = AccessTools.DeclaredMethod(typeof(DungeonGenerator), "PlaceRoom",
                new[] { typeof(DungeonDB.RoomData), typeof(Vector3), typeof(Quaternion), typeof(RoomConnection), typeof(ZoneSystem.SpawnMode) })
                ?? throw new InvalidOperationException("DungeonGenerator.PlaceRoom(RoomData, Vector3, Quaternion, RoomConnection, SpawnMode) is missing.");
            if (place.IsStatic || place.ReturnType != typeof(Room))
                throw new InvalidOperationException("Unsupported DungeonGenerator.PlaceRoom signature.");
            loadedRooms = AccessTools.DeclaredField(typeof(DungeonGenerator), "m_loadedRooms")
                ?? throw new InvalidOperationException("DungeonGenerator.m_loadedRooms is missing.");
            Type element = loadedRooms.FieldType.GetElementType()
                ?? throw new InvalidOperationException("DungeonGenerator.m_loadedRooms is not an array.");
            roomDataField = AccessTools.DeclaredField(element, "m_roomData");
            positionField = AccessTools.DeclaredField(element, "m_position");
            rotationField = AccessTools.DeclaredField(element, "m_rotation");
            if (roomDataField?.FieldType != typeof(DungeonDB.RoomData) || positionField?.FieldType != typeof(Vector3) ||
                rotationField?.FieldType != typeof(Quaternion))
                throw new InvalidOperationException("Unsupported room placement layout.");
            if (AccessTools.DeclaredMethod(typeof(SnapToGround), "SnappAll", Type.EmptyTypes) is not MethodInfo snap ||
                !snap.IsStatic || snap.ReturnType != typeof(void))
                throw new InvalidOperationException("SnapToGround.SnappAll() is missing.");
            if (AccessTools.DeclaredField(typeof(DungeonGenerator), "m_zoneSize")?.FieldType != typeof(Vector3) ||
                AccessTools.DeclaredField(typeof(DungeonGenerator), "m_originalPosition")?.FieldType != typeof(Vector3))
                throw new InvalidOperationException("Unsupported DungeonGenerator bounds fields.");
            placeRoom = (Func<DungeonGenerator, DungeonDB.RoomData, Vector3, Quaternion, RoomConnection, ZoneSystem.SpawnMode, Room>)
                Delegate.CreateDelegate(typeof(Func<DungeonGenerator, DungeonDB.RoomData, Vector3, Quaternion, RoomConnection, ZoneSystem.SpawnMode, Room>), place);
            release = (Action<DungeonGenerator>)Delegate.CreateDelegate(typeof(Action<DungeonGenerator>), releaseMethod);
        }

        // Returning false skips the native single-frame loop; every other exit runs it whole.
        private static bool BeforeSpawn(DungeonGenerator __instance)
        {
            if (!Enabled || !Installed || __instance == null) return true;
            try
            {
                ZNet network = ZNet.instance;
                if (network == null || network.IsServer()) { Interlocked.Increment(ref vanillaServer); return true; }
                if (PlayerInsideBounds(__instance)) { Interlocked.Increment(ref vanillaInside); return true; }
                if (Pending.ContainsKey(__instance)) return false; // A queued slice already owns these rooms.
                if (loadedRooms!.GetValue(__instance) is not Array rooms || rooms.Length == 0) return true;
                var slice = new Slice(__instance, rooms);
                Pending.Add(__instance, slice);
                Queue.Add(slice);
                EnsureHost();
                Interlocked.Increment(ref sliced);
                return false;
            }
            catch (Exception)
            {
                Interlocked.Increment(ref failures);
                Pending.Remove(__instance);
                Queue.RemoveAll(entry => ReferenceEquals(entry.Generator, __instance));
                return true;
            }
        }

        // Generate() assigns m_zoneCenter and never runs on a client, so the bounds are
        // rebuilt here with the same zone formula the native code uses.
        private static bool PlayerInsideBounds(DungeonGenerator generator)
        {
            Player player = Player.m_localPlayer;
            if (player == null) return false;
            Vector3 origin = generator.transform.position;
            Vector3 center = origin;
            if (ZoneSystem.instance != null)
            {
                center = ZoneSystem.GetZonePos(ZoneSystem.GetZone(origin));
                center.y = origin.y - generator.m_originalPosition.y;
            }
            return new Bounds(center, generator.m_zoneSize).Contains(player.transform.position);
        }

        // OnRoomLoaded calls this immediately after Spawn returns. Releasing the room prefab
        // references and the loading-in-zone registration early would break later slices.
        private static bool BeforeRelease(DungeonGenerator __instance)
        {
            if (__instance == null || !Pending.ContainsKey(__instance)) return true;
            Interlocked.Increment(ref releaseHeld);
            return false;
        }

        // The escape: a generator destroyed mid-slice drops its entry first, so the native
        // OnDestroy release is never deferred and nothing is held after the object is gone.
        private static void BeforeDestroy(DungeonGenerator __instance)
        {
            if (__instance == null || !Pending.Remove(__instance)) return;
            Queue.RemoveAll(entry => ReferenceEquals(entry.Generator, __instance));
            Interlocked.Increment(ref aborted);
        }

        private static void EnsureHost()
        {
            if (host != null) return;
            var carrier = new GameObject("BetterPerformance.DungeonSpawnSlicing") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(carrier);
            host = carrier.AddComponent<SliceHost>();
            host.StartCoroutine(Drive());
        }

        // One shared allowance per frame across every queued dungeon, so two crypts
        // resolving on the same frame share the budget instead of doubling it.
        private static IEnumerator Drive()
        {
            var clock = new Stopwatch();
            while (host != null)
            {
                if (Queue.Count == 0) { yield return null; continue; }
                clock.Restart();
                int placed = 0;
                float allowance = budgetMs?.Value ?? 4f;
                try
                {
                    while (Queue.Count > 0 && (placed == 0 || clock.Elapsed.TotalMilliseconds < allowance))
                    {
                        Slice slice = Queue[0];
                        if (slice.Generator == null) { Drop(slice); Interlocked.Increment(ref aborted); continue; }
                        if (slice.Index >= slice.Rooms.Length) { Finish(slice); continue; }
                        Place(slice);
                        placed++;
                    }
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref failures);
                    if (Queue.Count > 0) Abandon(Queue[0]);
                }
                if (placed > 0)
                {
                    Interlocked.Increment(ref slices);
                    Interlocked.Add(ref roomsPlaced, placed);
                    double elapsed = clock.Elapsed.TotalMilliseconds;
                    if (elapsed > sliceMsMax) sliceMsMax = elapsed;
                }
                yield return null;
            }
        }

        private static void Place(Slice slice)
        {
            object entry = slice.Rooms.GetValue(slice.Index++)!;
            placeRoom!(slice.Generator, (DungeonDB.RoomData)roomDataField!.GetValue(entry),
                (Vector3)positionField!.GetValue(entry), (Quaternion)rotationField!.GetValue(entry),
                null!, ZoneSystem.SpawnMode.Client);
        }

        // Exactly the native tail of Spawn, then the release OnRoomLoaded was denied.
        private static void Finish(Slice slice)
        {
            DungeonGenerator generator = slice.Generator;
            SnapToGround.SnappAll();
            loadedRooms!.SetValue(generator, null);
            Drop(slice);
            long span = Time.frameCount - slice.StartFrame;
            if (span > spanFramesMax) spanFramesMax = span;
            release!(generator);
        }

        // A failure inside the loop stops owning the generator: drop the entry, then let the
        // native release run so the loading-in-zone registration is not leaked.
        private static void Abandon(Slice slice)
        {
            Drop(slice);
            if (slice.Generator == null) return;
            try { release!(slice.Generator); } catch { Interlocked.Increment(ref failures); }
        }

        private static void Drop(Slice slice)
        {
            Queue.Remove(slice);
            if (slice.Generator != null) Pending.Remove(slice.Generator);
        }

        // Main-thread runtime toggle. In-flight slices always finish; only new dungeons
        // return to the native single-frame spawn.
        internal static bool SetEnabled(bool enabled)
        {
            Enabled = Installed && enabled;
            return Enabled;
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            gauges.Add(new NumberValue("dungeon_spawn_sliced", Interlocked.Exchange(ref sliced, 0), "calls"));
            gauges.Add(new NumberValue("dungeon_spawn_vanilla_player_inside", Interlocked.Exchange(ref vanillaInside, 0), "calls"));
            gauges.Add(new NumberValue("dungeon_spawn_vanilla_server_or_full", Interlocked.Exchange(ref vanillaServer, 0), "calls"));
            gauges.Add(new NumberValue("dungeon_rooms_placed", Interlocked.Exchange(ref roomsPlaced, 0), "rooms"));
            gauges.Add(new NumberValue("dungeon_slices", Interlocked.Exchange(ref slices, 0), "frames"));
            gauges.Add(new NumberValue("dungeon_slice_ms_max", Math.Round(Interlocked.Exchange(ref sliceMsMax, 0d), 3), "ms"));
            gauges.Add(new NumberValue("dungeon_spawn_span_frames_max", Interlocked.Exchange(ref spanFramesMax, 0), "frames"));
            gauges.Add(new NumberValue("dungeon_spawn_aborted_destroyed", Interlocked.Exchange(ref aborted, 0), "calls"));
            gauges.Add(new NumberValue("dungeon_release_held", Interlocked.Exchange(ref releaseHeld, 0), "calls"));
            gauges.Add(new NumberValue("dungeon_probe_failures", Interlocked.Exchange(ref failures, 0), "calls"));
            labels.Add(new TextValue("dungeon_slicing_status", Status));
            labels.Add(new TextValue("dungeon_slicing_enabled", Enabled ? "true" : "false"));
            labels.Add(new TextValue("dungeon_slicing_budget_ms", (budgetMs?.Value ?? 0f).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        internal static void Reset()
        {
            Interlocked.Exchange(ref sliced, 0);
            Interlocked.Exchange(ref vanillaInside, 0);
            Interlocked.Exchange(ref vanillaServer, 0);
            Interlocked.Exchange(ref roomsPlaced, 0);
            Interlocked.Exchange(ref slices, 0);
            Interlocked.Exchange(ref sliceMsMax, 0d);
            Interlocked.Exchange(ref spanFramesMax, 0);
            Interlocked.Exchange(ref aborted, 0);
            Interlocked.Exchange(ref releaseHeld, 0);
            Interlocked.Exchange(ref failures, 0);
        }

        internal static void Uninstall()
        {
            foreach (var slice in Queue.ToArray()) Abandon(slice);
            Queue.Clear();
            Pending.Clear();
            // Unity comparisons and Destroy are unavailable outside the engine, so the whole
            // teardown is guarded rather than the Destroy call alone.
            try
            {
                if (host != null) { var carrier = host.gameObject; host = null; UnityEngine.Object.Destroy(carrier); }
            }
            catch { host = null; }
            Reset();
            try { Patches.UnpatchSelf(); } catch { }
            Release("disabled");
            option = null;
            budgetMs = null;
        }

        private static void Release(string status)
        {
            Installed = Enabled = false;
            Status = status;
            placeRoom = null;
            release = null;
            loadedRooms = roomDataField = positionField = rotationField = null;
        }

        private sealed class Slice
        {
            internal readonly DungeonGenerator Generator;
            internal readonly Array Rooms;
            internal readonly int StartFrame;
            internal int Index;

            internal Slice(DungeonGenerator generator, Array rooms)
            {
                Generator = generator;
                Rooms = rooms;
                StartFrame = Time.frameCount;
            }
        }

        // Nothing but a coroutine carrier; it owns no state and never touches the game.
        private sealed class SliceHost : MonoBehaviour
        {
            private void OnDestroy() { if (ReferenceEquals(host, this)) host = null; }
        }
    }
}
