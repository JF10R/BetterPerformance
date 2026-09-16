using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using HarmonyLib;
using UnityEngine;

namespace BetterPerformance
{
    // Plugin-owned exact cache of the three minimap textures produced by
    // Minimap.GenerateWorldMap. An entry is only ever applied after a previous join
    // produced byte-identical raw texture data under the same key, so a cache hit can
    // only reproduce output the installed game already generated on this machine.
    // Never reads or writes the native cacheMinimap* files.
    internal static class MinimapTextureCache
    {
        private const int ClosureLimit = 4096;
        private const string ShadowMode = "shadow";
        private const string VerifiedMode = "verified";

        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".MinimapTextureCache");
        private static readonly MethodInfo? Generate = AccessTools.DeclaredMethod(typeof(Minimap), "GenerateWorldMap", Type.EmptyTypes);

        private static ConfigEntry<bool>? enabled;
        private static ConfigEntry<string>? mode;
        private static ConfigEntry<int>? maxEntryMiB;
        private static ConfigEntry<int>? maxDirectoryMiB;
        private static ManualLogSource? log;
        private static FieldInfo? mapField, maskField, heightField, textureSizeField, pixelSizeField;
        private static string directory = string.Empty;
        private static int ownerThread;
        private static bool busy, pendingStore, brokenAtRuntime;
        private static string? pendingKey;
        private static long nativeStarted;
        private static long attempts, verifiedHits, shadowMatches, shadowMismatches, loadFailures, storeFailures, keyFailures;
        private static double nativeMs, loadMs, compareMs, storeMs, keyMs;
        private static long entryBytes;

        internal static bool Installed { get; private set; }
        internal static string Status { get; private set; } = "disabled_at_startup";
        internal static string Result { get; private set; } = "none";
        internal static bool Enabled => Installed && !brokenAtRuntime && enabled != null && enabled.Value && Status == "installed";
        private static string Mode => mode != null && mode.Value == VerifiedMode ? VerifiedMode : ShadowMode;

        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            log = logger;
            enabled = config.Bind("MinimapCache", "Enabled", false,
                "Experimental plugin-owned cache of the generated minimap textures, for remote joins where the native cache can never be read back. Requires restart. Off by default.");
            mode = config.Bind("MinimapCache", "Mode", ShadowMode, new ConfigDescription(
                "shadow: always generate natively, then compare and store. verified: additionally skip native generation when a previous join already produced byte-identical textures under the same key.",
                new AcceptableValueList<string>(ShadowMode, VerifiedMode)));
            maxEntryMiB = config.Bind("MinimapCache", "MaxEntryMiB", 64, new ConfigDescription(
                "Reject a cache entry larger than this, rather than storing it.", new AcceptableValueRange<int>(1, 256)));
            maxDirectoryMiB = config.Bind("MinimapCache", "MaxDirectoryMiB", 256, new ConfigDescription(
                "Total allowance for the plugin minimap cache directory. Oldest plugin entries are removed first; no other file is touched.",
                new AcceptableValueRange<int>(1, 4096)));
            if (!enabled.Value) return;
            try
            {
                ownerThread = Thread.CurrentThread.ManagedThreadId;
                ValidateContracts();
                directory = Path.Combine(Paths.BepInExRootPath, "BetterPerformance", "minimap-cache");
                Patches.Patch(Generate,
                    prefix: new HarmonyMethod(typeof(MinimapTextureCache), nameof(Prefix)),
                    postfix: new HarmonyMethod(typeof(MinimapTextureCache), nameof(Postfix)));
                Installed = true;
                Status = "installed";
                logger.LogInfo("Minimap texture cache installed in " + Mode + " mode.");
            }
            catch (Exception error)
            {
                Patches.UnpatchSelf();
                Installed = false;
                Status = "unsupported_native_layout";
                logger.LogWarning("Minimap texture cache unavailable; native generation retained: " + error.GetType().Name + " " + error.Message);
            }
        }

        // The cache may only replace native generation if the three textures are the whole
        // in-memory result. Reject any layout where that is not provable from the IL.
        private static void ValidateContracts()
        {
            if (Generate == null || Generate.IsStatic || Generate.ReturnType != typeof(void) || Generate.GetParameters().Length != 0)
                throw new InvalidOperationException("Unsupported GenerateWorldMap signature.");
            if ((Harmony.GetPatchInfo(Generate)?.Owners.Count ?? 0) != 0)
                throw new InvalidOperationException("GenerateWorldMap already patched by another mod.");
            mapField = AccessTools.DeclaredField(typeof(Minimap), "m_mapTexture");
            maskField = AccessTools.DeclaredField(typeof(Minimap), "m_forestMaskTexture");
            heightField = AccessTools.DeclaredField(typeof(Minimap), "m_heightTexture");
            textureSizeField = AccessTools.DeclaredField(typeof(Minimap), "m_textureSize");
            pixelSizeField = AccessTools.DeclaredField(typeof(Minimap), "m_pixelSize");
            if (mapField?.FieldType != typeof(Texture2D) || maskField?.FieldType != typeof(Texture2D) ||
                heightField?.FieldType != typeof(Texture2D) || textureSizeField?.FieldType != typeof(int) ||
                pixelSizeField?.FieldType != typeof(float))
                throw new InvalidOperationException("Unsupported minimap texture fields.");

            var body = ReadBody(Generate);
            if (body == null || body.Count == 0) throw new InvalidOperationException("GenerateWorldMap body is not decodable.");
            ValidateGenerateShape(body);
        }

        // Extracted so the offline verifier can feed it both the shipped IL and mutations of it.
        internal static void ValidateGenerateShape(List<ILStep> code)
        {
            var map = AccessTools.DeclaredField(typeof(Minimap), "m_mapTexture");
            var mask = AccessTools.DeclaredField(typeof(Minimap), "m_forestMaskTexture");
            var heights = AccessTools.DeclaredField(typeof(Minimap), "m_heightTexture");
            // The shipped body writes exactly one field, Color::r on a local array element.
            // Object fields, statics, field addresses and allocations would all let the
            // method carry state we do not cache, so none of them may appear.
            foreach (var step in code)
            {
                bool structField = step.Code == OpCodes.Stfld &&
                    (step.Operand as FieldInfo)?.DeclaringType?.IsValueType == true;
                if ((step.Code == OpCodes.Stfld && !structField) || step.Code == OpCodes.Stsfld ||
                    step.Code == OpCodes.Newobj || step.Code == OpCodes.Ldflda || step.Code == OpCodes.Ldsflda)
                    throw new InvalidOperationException("GenerateWorldMap writes state beyond the three textures (" +
                        step.Code + " " + (step.Operand?.ToString() ?? "?") + ").");
            }
            var called = code.Select(i => i.Operand as MethodBase).Where(m => m != null).Select(m => m!).ToList();
            var allowed = new HashSet<Type>
            {
                typeof(Minimap), typeof(WorldGenerator), typeof(ZNet), typeof(ZLog), typeof(FileHelpers),
                typeof(Texture2D), typeof(Color), typeof(Color32), typeof(Stopwatch), typeof(string),
                // Pure formatting of the native log line; no observable state.
                typeof(int), typeof(long), typeof(float), typeof(double), typeof(bool)
            };
            foreach (var method in called)
                if (method.DeclaringType == null || !allowed.Contains(method.DeclaringType))
                    throw new InvalidOperationException("GenerateWorldMap calls unexpected " +
                        (method.DeclaringType?.FullName ?? "<null>") + "::" + method.Name + ".");
            int Count(MethodInfo? target) => target == null ? -1 : called.Count(m => m == (MethodBase)target);
            var setPixels32 = AccessTools.DeclaredMethod(typeof(Texture2D), "SetPixels32", new[] { typeof(Color32[]) });
            var setPixels = AccessTools.DeclaredMethod(typeof(Texture2D), "SetPixels", new[] { typeof(Color[]) });
            var apply = AccessTools.DeclaredMethod(typeof(Texture2D), "Apply", Type.EmptyTypes);
            if (Count(setPixels32) != 2 || Count(setPixels) != 1 || Count(apply) != 3)
                throw new InvalidOperationException("GenerateWorldMap no longer writes exactly the three textures.");
            if (!called.Any(m => m.DeclaringType == typeof(WorldGenerator) && m.Name == "GetBiome") ||
                !called.Any(m => m.DeclaringType == typeof(WorldGenerator) && m.Name == "GetBiomeHeight"))
                throw new InvalidOperationException("GenerateWorldMap no longer samples the world generator.");
            foreach (var field in new[] { map, mask, heights })
                if (field == null || !code.Any(i => i.Code == OpCodes.Ldfld && Equals(i.Operand, field)))
                    throw new InvalidOperationException("GenerateWorldMap no longer reads a minimap texture field.");
        }

        // Own IL reader: Harmony's instruction decoder needs a runtime that can load every
        // referenced Unity type, which the offline verifier cannot, and it is far heavier
        // than this over the hundreds of methods the key hashes.
        internal struct ILStep
        {
            public ILStep(OpCode code, object? operand) { Code = code; Operand = operand; }
            public OpCode Code;
            public object? Operand;
        }

        private static readonly Dictionary<short, OpCode> KnownOpcodes = BuildOpcodes();

        private static Dictionary<short, OpCode> BuildOpcodes()
        {
            var map = new Dictionary<short, OpCode>();
            foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (field.FieldType == typeof(OpCode))
                {
                    var code = (OpCode)field.GetValue(null)!;
                    map[code.Value] = code;
                }
            return map;
        }

        // A body the runtime refuses to describe is unknown code; callers fail closed.
        internal static bool TryIL(MethodBase method, out byte[]? il)
        {
            try { il = method.GetMethodBody()?.GetILAsByteArray(); return true; }
            catch (Exception) { il = null; return false; }
        }

        internal static List<ILStep>? ReadBody(MethodBase method)
        {
            if (!TryIL(method, out byte[]? il) || il == null) return null;
            Type[]? typeArguments = null, methodArguments = null;
            try
            {
                typeArguments = method.DeclaringType != null && method.DeclaringType.IsGenericType
                    ? method.DeclaringType.GetGenericArguments() : null;
                methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
            }
            catch (Exception) { return null; }
            var steps = new List<ILStep>(il.Length / 4 + 4);
            var module = method.Module;
            int index = 0;
            while (index < il.Length)
            {
                byte first = il[index++];
                short key;
                if (first == 0xFE)
                {
                    if (index >= il.Length) return null;
                    key = unchecked((short)(0xFE00 | il[index++]));
                }
                else key = first;
                if (!KnownOpcodes.TryGetValue(key, out var code)) return null;
                object? operand = null;
                int size;
                switch (code.OperandType)
                {
                    case OperandType.InlineNone: size = 0; break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar: size = 1; break;
                    case OperandType.InlineVar: size = 2; break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR: size = 8; break;
                    case OperandType.InlineSwitch:
                        if (index + 4 > il.Length) return null;
                        size = 4 + 4 * BitConverter.ToInt32(il, index);
                        break;
                    default: size = 4; break;
                }
                if (index + size > il.Length) return null;
                if (size == 4 && code.OperandType != OperandType.InlineSwitch)
                {
                    int token = BitConverter.ToInt32(il, index);
                    try
                    {
                        if (code.OperandType == OperandType.InlineMethod)
                            operand = module.ResolveMethod(token, typeArguments, methodArguments);
                        else if (code.OperandType == OperandType.InlineField)
                            operand = module.ResolveField(token, typeArguments, methodArguments);
                        else if (code.OperandType == OperandType.InlineTok)
                            operand = module.ResolveMember(token, typeArguments, methodArguments);
                    }
                    catch (Exception)
                    {
                        // An unresolvable call target means the generation code is not fully
                        // known; never key a cache on code we cannot read.
                        if (code.OperandType == OperandType.InlineMethod) return null;
                    }
                }
                index += size;
                steps.Add(new ILStep(code, operand));
            }
            return steps;
        }

        private static bool Compatible() => Generate != null &&
            (Harmony.GetPatchInfo(Generate)?.Owners.All(owner => owner == Patches.Id) ?? true);

        // ---------------------------------------------------------------- key

        private static readonly Assembly[] GameAssemblies = { typeof(Minimap).Assembly, typeof(Utils).Assembly };

        private static bool IsGameMethod(MethodBase method) =>
            method.DeclaringType != null && GameAssemblies.Contains(method.DeclaringType.Assembly);

        private static List<MethodBase>? Closure()
        {
            var roots = new List<MethodBase>();
            if (Generate != null) roots.Add(Generate);
            foreach (string name in new[] { "Pregenerate", "VersionSetup", "Initialize" })
            {
                var method = AccessTools.DeclaredMethod(typeof(WorldGenerator), name);
                if (method != null) roots.Add(method);
            }
            foreach (var constructor in typeof(WorldGenerator).GetConstructors(AccessTools.all)) roots.Add(constructor);
            var seen = new HashSet<MethodBase>(roots.Where(IsGameMethod));
            var queue = new Queue<MethodBase>(seen);
            var found = new List<MethodBase>();
            while (queue.Count != 0)
            {
                var method = queue.Dequeue();
                found.Add(method);
                if (found.Count > ClosureLimit) return null;
                if (!TryIL(method, out byte[]? il)) return null;
                if (il == null) continue;
                var code = ReadBody(method);
                if (code == null) return null;
                foreach (var step in code)
                    if (step.Operand is MethodBase callee && IsGameMethod(callee) && seen.Add(callee))
                        queue.Enqueue(callee);
            }
            found.Sort((left, right) => string.CompareOrdinal(Signature(left), Signature(right)));
            return found;
        }

        private static string Signature(MethodBase method) =>
            (method.DeclaringType?.FullName ?? "<null>") + "::" + method;

        private static string? ComputeKey(Minimap minimap, Texture2D map, Texture2D mask, Texture2D heights)
        {
            var closure = Closure();
            if (closure == null) { Status = "il_closure_unreadable"; return null; }
            var world = ZNet.World;
            if (world == null) { Status = "no_world"; return null; }
            var material = new StringBuilder();
            material.Append("BetterPerformance/MinimapCache/").Append(MinimapCacheStore.FormatVersion).Append('\n');
            material.Append("plugin=").Append(Plugin.PluginVersion).Append('\n');
            material.Append("game=").Append(global::Version.GetVersionString()).Append('\n');
            material.Append("seed=").Append(world.m_seed.ToString(CultureInfo.InvariantCulture)).Append('\n');
            material.Append("worldGenVersion=").Append(world.m_worldGenVersion.ToString(CultureInfo.InvariantCulture)).Append('\n');
            material.Append("textureSize=").Append(Convert.ToInt32(textureSizeField!.GetValue(minimap)).ToString(CultureInfo.InvariantCulture)).Append('\n');
            material.Append("pixelSize=").Append(Convert.ToSingle(pixelSizeField!.GetValue(minimap)).ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            material.Append("map=").Append(Layout(map)).Append('\n');
            material.Append("mask=").Append(Layout(mask)).Append('\n');
            material.Append("height=").Append(Layout(heights)).Append('\n');
            material.Append("methods=").Append(closure.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            material.Append("il=").Append(ClosureHash(closure)).Append('\n');
            material.Append("mods=").Append(Plugins()).Append('\n');
            return Hash(Encoding.UTF8.GetBytes(material.ToString()));
        }

        internal static string Layout(Texture2D texture) => string.Join("/", new[]
        {
            texture.width.ToString(CultureInfo.InvariantCulture),
            texture.height.ToString(CultureInfo.InvariantCulture),
            texture.format.ToString(),
            texture.graphicsFormat.ToString(),
            texture.mipmapCount.ToString(CultureInfo.InvariantCulture)
        });

        private static string ClosureHash(List<MethodBase> closure)
        {
            using (var buffer = new MemoryStream())
            {
                foreach (var method in closure)
                {
                    byte[] name = Encoding.UTF8.GetBytes(Signature(method) + "\n");
                    buffer.Write(name, 0, name.Length);
                    TryIL(method, out byte[]? il);
                    byte[] length = BitConverter.GetBytes(il?.Length ?? -1);
                    buffer.Write(length, 0, length.Length);
                    if (il != null && il.Length != 0) buffer.Write(il, 0, il.Length);
                    // Harmony returns the original IL of a patched method, so patch
                    // ownership has to enter the key separately.
                    var owners = Harmony.GetPatchInfo(method)?.Owners;
                    byte[] patched = Encoding.UTF8.GetBytes(owners == null || owners.Count == 0
                        ? "|\n" : "|" + string.Join(",", owners.OrderBy(o => o, StringComparer.Ordinal).ToArray()) + "\n");
                    buffer.Write(patched, 0, patched.Length);
                }
                return Hash(buffer.ToArray());
            }
        }

        private static string Plugins()
        {
            var names = new List<string>();
            try
            {
                foreach (var info in Chainloader.PluginInfos)
                    names.Add(info.Key + "@" + (info.Value?.Metadata?.Version?.ToString() ?? "?"));
            }
            catch (Exception) { return "unavailable"; }
            names.Sort(StringComparer.Ordinal);
            return Hash(Encoding.UTF8.GetBytes(string.Join("\n", names.ToArray())));
        }

        private static string Hash(byte[] material)
        {
            using (var sha = SHA256.Create())
            {
                var text = new StringBuilder(64);
                foreach (byte value in sha.ComputeHash(material)) text.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }

        // ---------------------------------------------------------------- patches

        private static bool Prefix(Minimap __instance)
        {
            pendingStore = false;
            pendingKey = null;
            if (!Enabled || busy || Thread.CurrentThread.ManagedThreadId != ownerThread) return true;
            busy = true;
            try
            {
                if (!Compatible()) { Status = "foreign_generate_patch"; return true; }
                if (!TryTextures(__instance, out var map, out var mask, out var heights)) { Status = "textures_unavailable"; return true; }
                attempts++;
                long started = Stopwatch.GetTimestamp();
                string? key = ComputeKey(__instance, map!, mask!, heights!);
                keyMs += Since(started);
                if (key == null) { keyFailures++; Result = "key_failed"; Report(); return true; }
                pendingKey = key;
                pendingStore = true;
                if (Mode == VerifiedMode && TryServe(key, map!, mask!, heights!))
                {
                    pendingStore = false;
                    return false;
                }
                nativeStarted = Stopwatch.GetTimestamp();
                return true;
            }
            catch (Exception error)
            {
                brokenAtRuntime = true;
                Status = "runtime_error_" + error.GetType().Name;
                log?.LogWarning("Minimap texture cache disabled after " + error.GetType().Name + "; native generation retained.");
                pendingStore = false;
                return true;
            }
            finally { busy = false; }
        }

        private static bool TryServe(string key, Texture2D map, Texture2D mask, Texture2D heights)
        {
            long started = Stopwatch.GetTimestamp();
            bool loaded = MinimapCacheStore.TryLoad(directory, key, out var entry, out string failure);
            if (!loaded || entry == null)
            {
                loadMs += Since(started);
                if (failure != "missing") { loadFailures++; Result = "load_failed"; Report(failure); }
                else { Result = "miss"; }
                return false;
            }
            if (!entry.Verified || entry.Mismatches != 0 ||
                !entry.SameLayout(map.width, map.height, Layout(map), Layout(mask), Layout(heights),
                    entry.Map.Length, entry.Mask.Length, entry.Heights.Length))
            {
                loadMs += Since(started);
                Result = entry.Verified ? "miss" : "miss_unverified";
                return false;
            }
            try
            {
                // A partial apply is recoverable: returning true makes native generation
                // rewrite all three textures in full.
                map.LoadRawTextureData(entry.Map); map.Apply();
                mask.LoadRawTextureData(entry.Mask); mask.Apply();
                heights.LoadRawTextureData(entry.Heights); heights.Apply();
            }
            catch (Exception error)
            {
                loadMs += Since(started);
                loadFailures++;
                Result = "load_failed";
                Report(error.GetType().Name);
                return false;
            }
            loadMs += Since(started);
            entryBytes = entry.PayloadBytes;
            verifiedHits++;
            Result = "verified_hit";
            Report();
            return true;
        }

        private static void Postfix(Minimap __instance)
        {
            if (!pendingStore) return;
            pendingStore = false;
            nativeMs += Since(nativeStarted);
            string? key = pendingKey;
            pendingKey = null;
            if (key == null || busy) return;
            busy = true;
            try
            {
                if (!TryTextures(__instance, out var map, out var mask, out var heights)) return;
                var produced = new MinimapCacheEntry(key, map!.width, map.height,
                    Layout(map!), Layout(mask!), Layout(heights!),
                    map!.GetRawTextureData(), mask!.GetRawTextureData(), heights!.GetRawTextureData(), false, 0);
                entryBytes = produced.PayloadBytes;
                int mismatches = 0;
                bool verified = false;
                long started = Stopwatch.GetTimestamp();
                if (MinimapCacheStore.TryLoad(directory, key, out var previous, out _) && previous != null)
                {
                    if (MinimapCacheStore.SameContent(previous, produced))
                    {
                        mismatches = previous.Mismatches;
                        verified = mismatches == 0;
                        shadowMatches++;
                        Result = "shadow_match";
                    }
                    else
                    {
                        mismatches = previous.Mismatches + 1;
                        shadowMismatches++;
                        Result = "shadow_mismatch";
                    }
                }
                else Result = "miss";
                compareMs += Since(started);
                started = Stopwatch.GetTimestamp();
                bool stored = MinimapCacheStore.TryStore(directory, produced.Promote(verified, mismatches),
                    (long)(maxEntryMiB?.Value ?? 64) * 1024 * 1024, (long)(maxDirectoryMiB?.Value ?? 256) * 1024 * 1024,
                    out long bytes, out string failure);
                storeMs += Since(started);
                entryBytes = bytes;
                if (!stored) { storeFailures++; Result = "store_failed"; Report(failure); return; }
                Report(mismatches == 0 ? "verified=" + (verified ? "true" : "false") : "mismatches=" + mismatches);
            }
            catch (Exception error)
            {
                brokenAtRuntime = true;
                Status = "runtime_error_" + error.GetType().Name;
                storeFailures++;
                Result = "store_failed";
                log?.LogWarning("Minimap texture cache store failed (" + error.GetType().Name + "); native output is unaffected.");
            }
            finally { busy = false; }
        }

        private static bool TryTextures(Minimap minimap, out Texture2D? map, out Texture2D? mask, out Texture2D? heights)
        {
            map = mapField?.GetValue(minimap) as Texture2D;
            mask = maskField?.GetValue(minimap) as Texture2D;
            heights = heightField?.GetValue(minimap) as Texture2D;
            if (map == null || mask == null || heights == null) return false;
            if (!map.isReadable || !mask.isReadable || !heights.isReadable) return false;
            return map.width == mask.width && map.width == heights.width &&
                map.height == mask.height && map.height == heights.height && map.width > 0 && map.height > 0;
        }

        private static double Since(long started) => (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;

        private static void Report(string detail = "")
        {
            log?.LogInfo("Minimap texture cache: result=" + Result + "; mode=" + Mode + "; status=" + Status +
                (detail.Length == 0 ? "" : "; " + detail) +
                "; native_ms=" + nativeMs.ToString("F1", CultureInfo.InvariantCulture) +
                "; load_ms=" + loadMs.ToString("F1", CultureInfo.InvariantCulture) +
                "; compare_ms=" + compareMs.ToString("F1", CultureInfo.InvariantCulture) +
                "; store_ms=" + storeMs.ToString("F1", CultureInfo.InvariantCulture) +
                "; key_ms=" + keyMs.ToString("F1", CultureInfo.InvariantCulture) +
                "; entry_bytes=" + entryBytes.ToString(CultureInfo.InvariantCulture));
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            labels.Add(new TextValue("minimap_cache_status", Status));
            labels.Add(new TextValue("minimap_cache_enabled", Enabled ? "true" : "false"));
            labels.Add(new TextValue("minimap_cache_mode", Mode));
            labels.Add(new TextValue("minimap_cache_result", Result));
            if (attempts == 0) return;
            labels.Add(new TextValue("minimap_cache_semantics", "cumulative_since_start; entry_bytes_is_last_entry; native_ms_excludes_verified_hits; inclusive_elapsed_not_CPU"));
            gauges.Add(new NumberValue("minimap_cache_attempts", attempts, "calls"));
            gauges.Add(new NumberValue("minimap_cache_verified_hits", verifiedHits, "calls"));
            gauges.Add(new NumberValue("minimap_cache_shadow_matches", shadowMatches, "calls"));
            gauges.Add(new NumberValue("minimap_cache_shadow_mismatches", shadowMismatches, "calls"));
            gauges.Add(new NumberValue("minimap_cache_load_failures", loadFailures, "calls"));
            gauges.Add(new NumberValue("minimap_cache_store_failures", storeFailures, "calls"));
            gauges.Add(new NumberValue("minimap_cache_key_failures", keyFailures, "calls"));
            gauges.Add(new NumberValue("minimap_cache_native_ms", nativeMs, "ms"));
            gauges.Add(new NumberValue("minimap_cache_load_ms", loadMs, "ms"));
            gauges.Add(new NumberValue("minimap_cache_compare_ms", compareMs, "ms"));
            gauges.Add(new NumberValue("minimap_cache_store_ms", storeMs, "ms"));
            gauges.Add(new NumberValue("minimap_cache_key_ms", keyMs, "ms"));
            gauges.Add(new NumberValue("minimap_cache_entry_bytes", entryBytes, "bytes"));
        }

        internal static void Uninstall()
        {
            Patches.UnpatchSelf();
            Installed = false;
            pendingStore = false;
            pendingKey = null;
        }
    }
}
