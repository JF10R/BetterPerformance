using System;
using System.Diagnostics;
using System.Runtime.Serialization;

namespace BetterPerformance.Core
{
    public enum Metric
    {
        LoopInterval, NetworkUpdate, ReplicationUpdate, SceneUpdate, ZoneUpdate,
        ObjectCreate, ObjectRemove, SaveWorldCall, SaveWorker, SavePrepare,
        CollectorPoll, CollectorSnapshot, TimingRecorder,
        NetworkPeers, SaveUpdate, RpcUpdate, ObjectCreateSorted, DistantObjectCreate,
        LoopWithGcCollection, LoopAcrossPhaseBoundary,
        CharacterSave, MapSerialization, CharacterSaveToDisk, SaveClone,
        RpcDispatch, IncomingZdoData, SyncListBuild, SendZdos, GraphicsObservation,
        AiBatch, PathQuery, PathfindingUpdate, SpawnListUpdate, SpawnAttempt,
        SectorDiscovery, ClientReplicationSort, ServerReplicationSort, CharacterFixedBatch,
        JoinPeerInfo, WorldInitialize, WorldPregenerate, WorldFindLakes, WorldPlaceRivers,
        WorldPlaceStreams, LocalZoneDemand, TerrainSyncWait, TerrainBuildWorker,
        MapTextureCacheLoad, WorldMapGenerate, PlayerMapLoad, SpawnResourceCheck,
        // Base-simulation probes. Appended at the end: histogram order is positional.
        WearBatch, WearSupportUpdate, HeightmapLateBatch, HeightmapRegenerate,
        HeightmapApplyModifiers, HeightmapCollisionRebuild, HeightmapRenderRebuild,
        TerrainCompApply, PlantUpdate, SmelterUpdate, FireplaceUpdate, CookingStationUpdate,
        BeehiveUpdate, SapCollectorUpdate, FermenterUpdate, WindmillUpdate,
        LocationSpawn, VegetationPlace, ZoneSpawn, ZonePlaceLocations,
        DungeonGenerate, DungeonSpawn,
        // Gameplay-loop probes. Appended at the end: histogram order is positional.
        InventoryGuiUpdate, InventoryGridUpdate, ContainerGridUpdate, InventoryGuiShow,
        ContainerInteract, ContainerChanged, ContainerCheckForChanges, InventoryAddItem,
        InventoryMoveItem, PlacementGhostUpdate, PlacementUpdate, PiecePlace, BuildGuiUpdate,
        MinimapUpdate, MinimapExploreUpdate, MinimapLargeMapUpdate, MinimapSetMapMode,
        ShipFixedUpdate, VagonUpdate, VagonAttach, VagonDetach,
        TreeDamage, TreeSpawnLog, TreeLogDamage, TreeLogDestroy,
        MineRockDamage, MineRockDamageArea, DestructibleDamage, DestructibleDestroy,
        WearDamage, CharacterDamage, CharacterApplyDamage, AttackStart,
        PieceDropResources, DropTableDrop, SmelterSpawn,
        CraftingStationBatch, SfxBatch, InstanceRendererBatch, SmokeBatch,
        FloatingBatch, ShipBatch, ZSyncTransformBatch, ZSyncAnimationBatch,
        ItemDropSlowUpdate, ItemAutoStack, PickableInteract,
        PlayerUpdate, PlayerFixedUpdate, HudUpdate, ClutterLateUpdate, WaterStaticUpdate
    }

    [DataContract]
    public sealed class TimingSummary
    {
        [DataMember(Name = "name")] public string Name { get; set; } = "";
        [DataMember(Name = "count")] public long Count { get; set; }
        [DataMember(Name = "sumMs")] public double SumMs { get; set; }
        [DataMember(Name = "maxMs")] public double MaxMs { get; set; }
        [DataMember(Name = "p50UpperBoundMs")] public double P50UpperBoundMs { get; set; }
        [DataMember(Name = "p95UpperBoundMs")] public double P95UpperBoundMs { get; set; }
        [DataMember(Name = "p99UpperBoundMs")] public double P99UpperBoundMs { get; set; }
        [DataMember(Name = "stallsOver50Ms")] public long StallsOver50Ms { get; set; }
        [DataMember(Name = "failedCalls")] public long FailedCalls { get; set; }
    }

    // Fixed histogram storage: no per-call allocations or growing sample history.
    public sealed class MetricBook
    {
        private readonly object gate = new object();
        private readonly Histogram[] histograms;
        private bool closed;
        private long invalidSamples;
        private int recorderCountdown = RecorderSampleEvery;
        public const int RecorderSampleEvery = 64;
        public long InvalidSamples { get { lock (gate) return invalidSamples; } }

        public MetricBook()
        {
            histograms = new Histogram[Enum.GetValues(typeof(Metric)).Length];
            for (int i = 0; i < histograms.Length; i++) histograms[i] = new Histogram(((Metric)i).ToString());
        }

        public void Record(Metric metric, double milliseconds, bool failed = false)
        {
            lock (gate)
            {
                if (closed) return;
                if ((int)metric < 0 || (int)metric >= histograms.Length ||
                    double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0)
                { invalidSamples++; return; }
                bool sampleRecorder = metric != Metric.TimingRecorder && --recorderCountdown == 0;
                long started = sampleRecorder ? Stopwatch.GetTimestamp() : 0;
                histograms[(int)metric].Add(milliseconds, failed);
                // Sample only aggregation inside the lock. This is NOT total recorder
                // cost, lock acquisition, or Harmony dispatch overhead; all game samples remain.
                if (sampleRecorder)
                {
                    recorderCountdown = RecorderSampleEvery;
                    histograms[(int)Metric.TimingRecorder].Add(
                        (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency, false);
                }
            }
        }

        public TimingSummary[] Drain() { lock (gate) return DrainLocked(); }
        public TimingSummary[] CloseAndDrain()
        {
            lock (gate) { closed = true; return DrainLocked(); }
        }

        private TimingSummary[] DrainLocked()
        {
            var result = new TimingSummary[histograms.Length];
            for (int i = 0; i < histograms.Length; i++) result[i] = histograms[i].Drain();
            return result;
        }

        private sealed class Histogram
        {
            private readonly string name;
            // 0.125, 0.25, ..., 65536 ms, followed by an overflow bucket.
            private readonly long[] buckets = new long[21];
            private long count, stalls, failed;
            private double sum, max;
            public Histogram(string name) { this.name = name; }
            public void Add(double value, bool wasFailed)
            {
                int index = 0;
                double bound = 0.125;
                while (index < buckets.Length - 1 && value > bound) { index++; bound *= 2; }
                buckets[index]++;
                count++;
                sum += value;
                max = Math.Max(max, value);
                if (value > 50) stalls++;
                if (wasFailed) failed++;
            }
            private double Quantile(double fraction)
            {
                if (count == 0) return 0;
                long target = (long)Math.Ceiling(count * fraction);
                long accumulated = 0;
                double bound = 0.125;
                for (int i = 0; i < buckets.Length; i++, bound *= 2)
                {
                    accumulated += buckets[i];
                    if (accumulated >= target) return i == buckets.Length - 1 ? max : Math.Min(bound, max);
                }
                return max;
            }
            public TimingSummary Drain()
            {
                var result = new TimingSummary
                {
                    Name = name, Count = count, SumMs = sum, MaxMs = max,
                    P50UpperBoundMs = Quantile(0.5), P95UpperBoundMs = Quantile(0.95),
                    P99UpperBoundMs = Quantile(0.99), StallsOver50Ms = stalls, FailedCalls = failed
                };
                Array.Clear(buckets, 0, buckets.Length);
                count = stalls = failed = 0;
                sum = max = 0;
                return result;
            }
        }
    }
}
