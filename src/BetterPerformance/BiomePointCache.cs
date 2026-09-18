using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;

namespace BetterPerformance
{
    // Valheim 1.0.14 disabled its biome-data cache because its key was the world NAME in a
    // local directory plus one enum, so two worlds sharing a name loaded each other's grid.
    // AltBiomeWorldData.GenerateBiomePoints is a pure function of (seed, world-gen version,
    // game binary): 4.19 M points, ~4.3-5.0 s on this machine, once per client join and once
    // per server world load. This module keeps a plugin-owned cache of that stage only, keyed
    // by seed, uid, versions, the fingerprint of every WorldGenerator method and the mod set,
    // serialized through the game's own Save/Load, and served only after a byte-identical
    // reproduction on a later load. GenerateSectors, which is not pure, stays native.
    internal static class BiomePointCache
    {
        private const string ShadowMode = "shadow", VerifiedMode = "verified";
        private const int SizeConstant = 2048;
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".BiomePointCache");
        private static ConfigEntry<bool>? enabled;
        private static ConfigEntry<string>? mode;
        private static ConfigEntry<int>? maxEntryMiB, maxDirectoryMiB;
        private static ManualLogSource logger = null!;
        private static AccessTools.FieldRef<AltBiomeWorldData, World>? dataWorld;
        private static string directory = "";
        private static string? pendingKey;
        private static bool pendingStore;
        private static long hits, misses, unverifiedMisses, stored, promoted, mismatches, keyFailures, loadFailures, storeFailures;
        private static double loadMsMax, storeMsMax, keyMs;
        private static volatile string result = "none";

        internal static bool Installed { get; private set; }
        internal static bool Enabled { get; private set; }
        internal static string Status { get; private set; } = "disabled";
        internal static string Mode => mode?.Value == VerifiedMode ? VerifiedMode : ShadowMode;

        internal static void Install(ConfigFile config, ManualLogSource log)
        {
            logger = log;
            enabled = config.Bind("BiomeCache", "Enabled", false,
                "Experimental. Cache the biome point grid Valheim regenerates on every client join and server world load (1.0.14 disabled the native cache). Serves an entry only after a byte-identical reproduction; requires restart.");
            mode = config.Bind("BiomeCache", "Mode", ShadowMode,
                new ConfigDescription("shadow: generate natively, store and compare, never serve. verified: also serve entries that reproduced byte-for-byte on an earlier load.",
                    new AcceptableValueList<string>(ShadowMode, VerifiedMode)));
            maxEntryMiB = config.Bind("BiomeCache", "MaxEntryMiB", 32,
                new ConfigDescription("Largest entry kept; the 2048-point grid is about 21 MiB.", new AcceptableValueRange<int>(8, 256)));
            maxDirectoryMiB = config.Bind("BiomeCache", "MaxDirectoryMiB", 128,
                new ConfigDescription("Cache directory allowance; the oldest entries beyond it are deleted.", new AcceptableValueRange<int>(32, 2048)));
            if (!enabled.Value) { Status = "disabled"; return; }
            try
            {
                var (generate, _, _) = ValidateContracts();
                directory = Path.Combine(Paths.BepInExRootPath, "BetterPerformance", "biome-cache");
                Patches.Patch(generate,
                    prefix: new HarmonyMethod(typeof(BiomePointCache), nameof(BeforeGenerate)),
                    postfix: new HarmonyMethod(typeof(BiomePointCache), nameof(AfterGenerate)));
                Installed = Enabled = true;
                Status = "installed";
                logger.LogWarning("Experimental biome point cache installed in " + Mode + " mode; entries under " + directory);
            }
            catch (Exception exception)
            {
                Installed = Enabled = false;
                Status = "unavailable";
                try { Patches.UnpatchSelf(); } catch { storeFailures++; }
                logger.LogWarning("Biome point cache unavailable; native generation retained: " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static (MethodInfo Generate, MethodInfo Load, MethodInfo Save) ValidateContracts()
        {
            var generate = AccessTools.DeclaredMethod(typeof(AltBiomeWorldData), "GenerateBiomePoints", new[] { typeof(World) })
                ?? throw new InvalidOperationException("AltBiomeWorldData.GenerateBiomePoints(World) is missing.");
            if (!generate.IsStatic || generate.ReturnType != typeof(void))
                throw new InvalidOperationException("Unsupported GenerateBiomePoints signature.");
            var load = AccessTools.DeclaredMethod(typeof(AltBiomeWorldData), "Load", new[] { typeof(BinaryReader), typeof(global::Version.World) })
                ?? throw new InvalidOperationException("AltBiomeWorldData.Load(BinaryReader, Version.World) is missing.");
            if (!load.IsStatic || load.ReturnType != typeof(AltBiomeWorldData))
                throw new InvalidOperationException("Unsupported AltBiomeWorldData.Load signature.");
            var save = AccessTools.DeclaredMethod(typeof(AltBiomeWorldData), "Save", new[] { typeof(BinaryWriter) })
                ?? throw new InvalidOperationException("AltBiomeWorldData.Save(BinaryWriter) is missing.");
            if (save.IsStatic || save.ReturnType != typeof(void))
                throw new InvalidOperationException("Unsupported AltBiomeWorldData.Save signature.");
            var worldField = AccessTools.DeclaredField(typeof(AltBiomeWorldData), "m_world");
            if (worldField == null || worldField.IsStatic || worldField.FieldType != typeof(World))
                throw new InvalidOperationException("Unsupported AltBiomeWorldData.m_world field.");
            dataWorld = AccessTools.FieldRefAccess<AltBiomeWorldData, World>(worldField);
            if (AccessTools.DeclaredField(typeof(AltBiomeWorldData), "PointsGenerated")?.FieldType != typeof(bool) ||
                AccessTools.DeclaredField(typeof(AltBiomeWorldData), "Size")?.FieldType != typeof(int) ||
                AccessTools.DeclaredField(typeof(World), "m_biomeData")?.FieldType != typeof(AltBiomeWorldData) ||
                AccessTools.DeclaredField(typeof(World), "m_seed")?.FieldType != typeof(int) ||
                AccessTools.DeclaredField(typeof(World), "m_uid")?.FieldType != typeof(long) ||
                AccessTools.DeclaredField(typeof(World), "m_worldGenVersion")?.FieldType != typeof(int) ||
                Enum.GetUnderlyingType(typeof(Heightmap.BiomeIndex)) != typeof(byte))
                throw new InvalidOperationException("Unsupported biome data or world field contract.");
            // GetBiome carries defaulted trailing parameters (oceanLevel, waterAlwaysOcean); match by
            // name and leading (float, float) rather than an exact list that a default would break.
            bool biomeLookup = false;
            foreach (var method in typeof(WorldGenerator).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var parameters = method.GetParameters();
                if (method.Name == "GetBiome" && parameters.Length >= 2 && parameters[0].ParameterType == typeof(float) && parameters[1].ParameterType == typeof(float)) biomeLookup = true;
            }
            if (!biomeLookup || AccessTools.DeclaredMethod(typeof(global::Version), "GetVersionString", new[] { typeof(bool) }) == null)
                throw new InvalidOperationException("WorldGenerator.GetBiome or Version.GetVersionString is missing.");
            return (generate, load, save);
        }

        // Returns false only for a verified, byte-identical entry that loaded cleanly.
        private static bool BeforeGenerate(World __0)
        {
            pendingStore = false;
            pendingKey = null;
            if (!Enabled || __0 == null) return true;
            try
            {
                long started = Stopwatch.GetTimestamp();
                string? key = ComputeKey(__0);
                keyMs = Since(started);
                if (key == null) { keyFailures++; result = "key_failed"; return true; }
                pendingKey = key;
                pendingStore = true;
                if (Mode == VerifiedMode && TryServe(key, __0)) { pendingStore = false; return false; }
                return true;
            }
            catch (Exception exception)
            {
                Disable(exception);
                return true;
            }
        }

        // Native generation just ran: serialize its result through the game's own writer on
        // this thread, then compare and store on a worker so the join does not wait on disk.
        private static void AfterGenerate(World __0)
        {
            if (!pendingStore || pendingKey == null) return;
            string key = pendingKey;
            pendingStore = false;
            pendingKey = null;
            try
            {
                var data = __0?.m_biomeData;
                if (data == null || data.Size != SizeConstant || !data.PointsGenerated) { result = "not_stored_unexpected_data"; return; }
                long started = Stopwatch.GetTimestamp();
                byte[] payload;
                using (var stream = new MemoryStream(SizeConstant * SizeConstant * 5 + 16))
                using (var writer = new BinaryWriter(stream))
                {
                    data.Save(writer);
                    writer.Flush();
                    payload = stream.ToArray();
                }
                double serializeMs = Since(started);
                var worker = new Thread(() => CompareAndStore(key, payload, serializeMs))
                { IsBackground = true, Name = "BetterPerformance.BiomePointCache", Priority = System.Threading.ThreadPriority.BelowNormal };
                worker.Start();
            }
            catch (Exception exception) { Disable(exception); }
        }

        private static void CompareAndStore(string key, byte[] payload, double serializeMs)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                long entryLimit = (maxEntryMiB?.Value ?? 32) * 1024L * 1024L;
                long directoryLimit = (maxDirectoryMiB?.Value ?? 128) * 1024L * 1024L;
                bool loaded = BiomeCacheStore.TryLoad(directory, key, out var existing, out string failure);
                BiomeCacheEntry entry;
                string outcome;
                if (loaded && existing != null)
                {
                    if (BiomeCacheStore.BytesEqual(existing.Payload, payload))
                    {
                        // Same bytes on a later load: the reproduction that earns "verified".
                        if (existing.Verified) { result = "verified_match"; return; }
                        entry = existing.Promoted(); outcome = "promoted"; Interlocked.Increment(ref promoted);
                    }
                    else { entry = new BiomeCacheEntry(key, false, existing.Mismatches + 1, payload); outcome = "mismatch"; Interlocked.Increment(ref mismatches); }
                }
                else
                {
                    if (loaded == false && failure != "missing") { Interlocked.Increment(ref loadFailures); }
                    entry = new BiomeCacheEntry(key, false, 0, payload); outcome = "stored"; Interlocked.Increment(ref stored);
                }
                if (!BiomeCacheStore.TryStore(directory, key, entry, entryLimit, directoryLimit, out string storeFailure))
                { Interlocked.Increment(ref storeFailures); result = "store_failed_" + storeFailure; return; }
                result = outcome;
            }
            catch (Exception) { Interlocked.Increment(ref storeFailures); result = "store_failed"; }
            finally
            {
                double total = serializeMs + Since(started);
                if (total > storeMsMax) storeMsMax = total;
            }
        }

        private static bool TryServe(string key, World world)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                if (!BiomeCacheStore.TryLoad(directory, key, out var entry, out string failure) || entry == null)
                {
                    if (failure == "missing") { misses++; result = "miss"; }
                    else { loadFailures++; result = "load_failed_" + failure; }
                    return false;
                }
                if (!entry.Verified) { unverifiedMisses++; result = "miss_unverified"; return false; }
                AltBiomeWorldData data;
                using (var stream = new MemoryStream(entry.Payload, false))
                using (var reader = new BinaryReader(stream))
                    data = AltBiomeWorldData.Load(reader, world.m_worldVersion);
                if (data == null || data.Size != SizeConstant || !data.PointsGenerated) { loadFailures++; result = "load_failed_shape"; return false; }
                // Exactly what the native TryLoadCache did after a successful read.
                world.m_biomeData = data;
                dataWorld!(data) = world;
                hits++;
                result = "verified_hit";
                return true;
            }
            catch (Exception)
            {
                loadFailures++;
                result = "load_failed";
                return false;
            }
            finally
            {
                double elapsed = Since(started);
                if (elapsed > loadMsMax) loadMsMax = elapsed;
            }
        }

        // Everything the point grid depends on. Any unreadable component yields no key: a
        // placeholder would let two different generators share one entry.
        private static string? ComputeKey(World world)
        {
            var material = new StringBuilder(4096);
            material.Append("BetterPerformance/BiomeCache/").Append(BiomeCacheStore.FormatVersion).Append('\n');
            material.Append("plugin=").Append(Plugin.PluginVersion).Append('\n');
            material.Append("game=").Append(global::Version.GetVersionString()).Append('\n');
            material.Append("seed=").Append(world.m_seed.ToString(CultureInfo.InvariantCulture)).Append('\n');
            material.Append("seedName=").Append(world.m_seedName ?? "").Append('\n');
            material.Append("uid=").Append(world.m_uid.ToString(CultureInfo.InvariantCulture)).Append('\n');
            material.Append("worldGenVersion=").Append(world.m_worldGenVersion.ToString(CultureInfo.InvariantCulture)).Append('\n');
            material.Append("size=").Append(SizeConstant).Append('\n');
            // The whole generator plus the two point-loop methods, by token-independent
            // fingerprint, and any Harmony owner on them: a mod that reshapes biomes must
            // change the key even when it does not change the game binary.
            var methods = new List<MethodBase>();
            foreach (var method in typeof(WorldGenerator).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                methods.Add(method);
            foreach (var constructor in typeof(WorldGenerator).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                methods.Add(constructor);
            var generate = GenerateMethod();
            var mapToWorld = MapSpaceMethod();
            if (generate == null || mapToWorld == null) return null;
            methods.Add(generate); methods.Add(mapToWorld);
            methods.Sort((a, b) => string.CompareOrdinal(a.DeclaringType!.FullName + "::" + a, b.DeclaringType!.FullName + "::" + b));
            foreach (var method in methods)
            {
                if (method.IsAbstract || method.GetMethodBody() == null) continue;
                material.Append(method.DeclaringType!.FullName).Append("::").Append(method).Append('=')
                    .Append(IlFingerprint.Compute(method)).Append('|');
                var owners = Harmony.GetPatchInfo(method)?.Owners;
                if (owners != null && owners.Count > 0)
                {
                    var sorted = new List<string>(owners); sorted.Sort(StringComparer.Ordinal);
                    material.Append(string.Join(",", sorted.ToArray()));
                }
                material.Append('\n');
            }
            string? mods = Plugins();
            if (mods == null) return null;
            material.Append("mods=").Append(mods).Append('\n');
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(material.ToString()))).Replace("-", "").ToLowerInvariant();
        }

        // The two point-loop methods the key covers, looked up by the shapes the installed
        // build actually has: the map-to-world helper takes a float, not the loop's int.
        private static MethodInfo? GenerateMethod() =>
            AccessTools.DeclaredMethod(typeof(AltBiomeWorldData), "GenerateBiomePoints", new[] { typeof(World) });
        private static MethodInfo? MapSpaceMethod() =>
            AccessTools.DeclaredMethod(typeof(AltBiomeWorldData), "MapSpaceToWorldSpace", new[] { typeof(float) });

        // Every lookup Install and ComputeKey depend on, for the game-contract harness: a
        // wrong signature here would otherwise surface only as a runtime "unavailable" or a
        // permanent key_failed, which the metadata checks cannot see.
        internal static string? MissingContract()
        {
            try
            {
                ValidateContracts();
                if (GenerateMethod() == null) return "AltBiomeWorldData.GenerateBiomePoints(World)";
                if (MapSpaceMethod() == null) return "AltBiomeWorldData.MapSpaceToWorldSpace(float)";
                return null;
            }
            catch (InvalidOperationException exception) { return exception.Message; }
        }

        private static string? Plugins()
        {
            var names = new List<string>();
            try
            {
                foreach (var info in Chainloader.PluginInfos)
                    names.Add(info.Key + "@" + (info.Value?.Metadata?.Version?.ToString() ?? "?"));
            }
            catch (Exception) { return null; }
            names.Sort(StringComparer.Ordinal);
            return string.Join("\n", names.ToArray());
        }

        private static double Since(long started) => (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;

        // Any exception on the main-thread path disables the module rather than risking a
        // wrong grid; native generation is what runs from then on.
        private static void Disable(Exception exception)
        {
            Enabled = false;
            Status = "failed_" + exception.GetType().Name;
            pendingStore = false;
            logger.LogWarning("Biome point cache disabled after " + exception.GetType().Name + "; native generation retained.");
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            labels.Add(new TextValue("biome_cache_status", Status));
            labels.Add(new TextValue("biome_cache_mode", Mode));
            labels.Add(new TextValue("biome_cache_enabled", Enabled ? "true" : "false"));
            labels.Add(new TextValue("biome_cache_result", result));
            gauges.Add(new NumberValue("biome_cache_hits", Interlocked.Read(ref hits), "loads"));
            gauges.Add(new NumberValue("biome_cache_misses", Interlocked.Read(ref misses), "loads"));
            gauges.Add(new NumberValue("biome_cache_misses_unverified", Interlocked.Read(ref unverifiedMisses), "loads"));
            gauges.Add(new NumberValue("biome_cache_stored", Interlocked.Read(ref stored), "entries"));
            gauges.Add(new NumberValue("biome_cache_promoted", Interlocked.Read(ref promoted), "entries"));
            gauges.Add(new NumberValue("biome_cache_mismatches", Interlocked.Read(ref mismatches), "entries"));
            gauges.Add(new NumberValue("biome_cache_key_failures", Interlocked.Read(ref keyFailures), "loads"));
            gauges.Add(new NumberValue("biome_cache_load_failures", Interlocked.Read(ref loadFailures), "loads"));
            gauges.Add(new NumberValue("biome_cache_store_failures", Interlocked.Read(ref storeFailures), "stores"));
            gauges.Add(new NumberValue("biome_cache_key_ms", keyMs, "ms"));
            gauges.Add(new NumberValue("biome_cache_load_ms_max", loadMsMax, "ms"));
            gauges.Add(new NumberValue("biome_cache_store_ms_max", storeMsMax, "ms"));
        }

        internal static void Reset()
        {
            Interlocked.Exchange(ref hits, 0); Interlocked.Exchange(ref misses, 0); Interlocked.Exchange(ref unverifiedMisses, 0);
            Interlocked.Exchange(ref stored, 0); Interlocked.Exchange(ref promoted, 0); Interlocked.Exchange(ref mismatches, 0);
            Interlocked.Exchange(ref keyFailures, 0); Interlocked.Exchange(ref loadFailures, 0); Interlocked.Exchange(ref storeFailures, 0);
            loadMsMax = storeMsMax = keyMs = 0;
        }

        internal static void Uninstall()
        {
            Reset();
            try { Patches.UnpatchSelf(); } catch { }
            Installed = Enabled = false;
            Status = "disabled";
            pendingStore = false;
            pendingKey = null;
            dataWorld = null;
        }
    }
}
