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
    // Attributes observed cost to the prefab or RPC that caused it. Hooks read
    // identifiers only: a prefab hash, an RPC method hash, and the written length of a
    // package. No package contents, no player data, no game state is modified.
    internal static class AttributionTelemetry
    {
        private const int TopRows = 24;
        private const int NameCapacity = 512;
        private const int TargetPairCapacity = 256;
        private const int TargetSplitLimit = 32;
        private const string TargetSplitDefault = "RPC_Damage,RPC_ApplyOperation,RPC_RequestOwn,RPC_RequestOpen";
        private static readonly Harmony Patches = new Harmony(Plugin.PluginId + ".AttributionTelemetry");
        private static readonly KeyedAggregator PrefabCreate = new KeyedAggregator("prefab_create", 256, AttributionOrder.SumMs);
        private static readonly KeyedAggregator PrefabSendBytes = new KeyedAggregator("prefab_send_bytes", 256, AttributionOrder.Bytes);
        private static readonly KeyedAggregator RoutedRpc = new KeyedAggregator("routed_rpc", 256, AttributionOrder.SumMs);
        private static readonly KeyedAggregator RoutedRpcTarget = new KeyedAggregator("routed_rpc_target", 256, AttributionOrder.SumMs);
        private static readonly KeyedAggregator DirectRpc = new KeyedAggregator("direct_rpc", 256, AttributionOrder.SumMs);
        // Method metadata only: never retain a delegate, its target, a peer or a package.
        private static readonly Dictionary<int, MethodInfo?> DirectHandlers = new Dictionary<int, MethodInfo?>(NameCapacity);
        private static AccessTools.FieldRef<ZRpc, object>? directFunctions;
        private static AccessTools.FieldRef<ZPackage, BinaryReader>? packageReader;
        private static string directRpcStatus = "disabled";
        private static int directCaptureGeneration;
        private static long directRpcFailures, directHeaderUnavailable, directPingSkips, directNameCapacitySkips;
        private static long unresolvedDirectRpcKeys, handlerResolvedDirectRpcKeys;
        private static readonly object NameGate = new object();
        // Composite key -> packed (method hash, prefab hash). Value type entries only, so a
        // warm map records no managed allocation inside the hook.
        private static readonly object TargetGate = new object();
        private static readonly Dictionary<int, long> TargetPairs = new Dictionary<int, long>(TargetPairCapacity);
        private static int[] targetSplitHashes = new int[0];
        private static string targetSplitNames = "none";
        private static long targetNoneIds, targetMissingZdos, targetUnmappedKeys, targetKeyCollisions, targetPairSkips;
        private static AccessTools.FieldRef<DamageText, object>? worldTexts;
        private static long damageTextAdded, damageTextLiveMax;
        private static string damageTextStatus = "disabled";
        private static readonly Dictionary<int, string> RpcNames = new Dictionary<int, string>(NameCapacity);
        private static readonly Dictionary<int, string> PrefabNames = new Dictionary<int, string>(NameCapacity);
        private static bool capturing;
        private static int ownerThread;
        private static long probeFailures, otherThreadSkips, negativeByteDeltas;
        private static long unresolvedPrefabKeys, unresolvedRpcKeys, nameCapacitySkips, handlerResolvedRpcKeys;
        // The registry's value type is internal to the game, so it is read as an object.
        private static AccessTools.FieldRef<ZRoutedRpc, object>? routedFunctions;
        private static int registerCandidates, registerHooks, registerHookFailures, registerGenericSkips;
        private static string registerFailure = "none";

        internal static bool Enabled { get; set; }
        internal static bool Installed { get; private set; }
        internal static string Status { get; private set; } = "disabled";

        internal struct TimedScope
        {
            internal bool Active;
            internal int Key;
            internal long Started;
            // Second key for the allow-listed target split; the same elapsed time is
            // recorded once per group, never added twice to one group.
            internal bool TargetActive;
            internal int TargetKey;
        }

        internal struct BytesScope
        {
            internal bool Active;
            internal int Key;
            internal int Before;
            internal long Started;
            internal ZPackage? Package;
        }

        internal struct DirectScope
        {
            internal bool Active;
            internal bool KeyKnown;
            internal int Key;
            internal long Started;
            internal int Generation;
            internal CaptureSession? Session;
        }

        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            ConfigureTargetSplit(config);
            try
            {
                Patch(typeof(ZNetScene), "CreateObject", new[] { typeof(ZDO) }, typeof(GameObject),
                    nameof(CreateBefore), nameof(CreateAfter));
                Patch(typeof(ZDO), "Serialize", new[] { typeof(ZPackage) }, typeof(void),
                    nameof(SerializeBefore), nameof(SerializeAfter));
                Patch(typeof(ZRoutedRpc), "HandleRoutedRPC", new[] { typeof(ZRoutedRpc.RoutedRPCData) }, typeof(void),
                    nameof(RoutedBefore), nameof(RoutedAfter));
                PatchRegistrations(typeof(ZRoutedRpc), logger);
                PatchRegistrations(typeof(ZRpc), logger);
                InstallDirectRpc(logger);
                InstallDamageTextGauge(logger);
                Installed = true;
                Status = registerHookFailures == 0 ? "installed" : "installed_partial_names";
            }
            catch (Exception exception)
            {
                Installed = Enabled = false;
                Status = "unavailable";
                Patches.UnpatchSelf();
                logger.LogWarning("Attribution telemetry unavailable: " + exception.GetType().Name);
            }
        }

        private static void Patch(Type type, string name, Type[] parameters, Type result, string prefix, string finalizer)
        {
            var method = AccessTools.DeclaredMethod(type, name, parameters);
            if (method == null || method.ReturnType != result || method.IsStatic)
                throw new InvalidOperationException("Unsupported attribution signature: " + type.Name + "." + name);
            Patches.Patch(method,
                prefix: new HarmonyMethod(typeof(AttributionTelemetry), prefix),
                finalizer: new HarmonyMethod(typeof(AttributionTelemetry), finalizer) { priority = Priority.Last });
        }

        private static void InstallDirectRpc(ManualLogSource logger)
        {
            directRpcStatus = "unavailable";
            try
            {
                directFunctions = AccessTools.FieldRefAccess<ZRpc, object>("m_functions");
                packageReader = AccessTools.FieldRefAccess<ZPackage, BinaryReader>("m_reader");
                Patch(typeof(ZRpc), "HandlePackage", new[] { typeof(ZPackage) }, typeof(void),
                    nameof(DirectBefore), nameof(DirectAfter));
                directRpcStatus = "installed";
            }
            catch (Exception exception)
            {
                directFunctions = null;
                packageReader = null;
                directRpcStatus = "unavailable:" + exception.GetType().Name;
                logger.LogWarning("Direct RPC attribution unavailable: " + exception.GetType().Name);
            }
        }

        // Ten of the twelve Register overloads are open generic definitions. Harmony
        // refuses those ("The given generic instantiation was invalid") and a refused
        // attempt still leaves patch state that blocks removal, so they are skipped.
        // ResolveRoutedHandler recovers those names from the registry instead.
        private static void PatchRegistrations(Type type, ManualLogSource logger)
        {
            foreach (MethodInfo method in AccessTools.GetDeclaredMethods(type))
            {
                if (method.Name != "Register" || method.ReturnType != typeof(void) || method.IsStatic) continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 2 || parameters[0].ParameterType != typeof(string)) continue;
                registerCandidates++;
                if (method.IsGenericMethodDefinition || method.ContainsGenericParameters)
                {
                    registerGenericSkips++;
                    continue;
                }
                try
                {
                    Patches.Patch(method, postfix: new HarmonyMethod(typeof(AttributionTelemetry), nameof(RegisterAfter)));
                    registerHooks++;
                }
                catch (Exception exception)
                {
                    registerHookFailures++;
                    registerFailure = exception.GetType().Name;
                    logger.LogWarning("RPC name hook unavailable for " + type.Name + ".Register: " + registerFailure);
                }
            }
        }

        // Names are resolved to the same stable hash the game registers them under. A
        // configured name that no handler registers simply never matches a call.
        private static void ConfigureTargetSplit(ConfigFile config)
        {
            string configured = config.Bind("Diagnostics", "AttributionTargetSplitRpcs", TargetSplitDefault,
                "Routed RPC names whose attribution is additionally split by the target ZDO's prefab, as a comma separated list; empty disables the split. Each listed name costs one ZDO dictionary lookup per handled call.").Value ?? "";
            var hashes = new List<int>(TargetSplitLimit);
            var names = new List<string>(TargetSplitLimit);
            foreach (string entry in configured.Split(','))
            {
                string name = entry.Trim();
                if (name.Length == 0 || hashes.Count >= TargetSplitLimit) continue;
                int hash = name.GetStableHashCode();
                if (hashes.Contains(hash)) continue;
                hashes.Add(hash);
                names.Add(name);
                lock (NameGate)
                {
                    if (!RpcNames.ContainsKey(hash) && RpcNames.Count < NameCapacity) RpcNames[hash] = name;
                }
            }
            targetSplitHashes = hashes.ToArray();
            targetSplitNames = names.Count == 0 ? "none" : string.Join("|", names.ToArray());
        }

        // A pressure gauge for the unpooled damage numbers, not an attribution group:
        // it counts calls and reads the world-text list length, nothing else. Its
        // absence must not cost the attribution groups, so failure is reported here.
        private static void InstallDamageTextGauge(ManualLogSource logger)
        {
            try
            {
                MethodInfo? add = AccessTools.DeclaredMethod(typeof(DamageText), "AddInworldText");
                if (add == null || add.IsStatic || add.ReturnType != typeof(void))
                {
                    damageTextStatus = "unavailable_signature";
                    return;
                }
                worldTexts = AccessTools.FieldRefAccess<DamageText, object>("m_worldTexts");
                Patches.Patch(add, prefix: new HarmonyMethod(typeof(AttributionTelemetry), nameof(DamageTextAdded)));
                damageTextStatus = "installed";
            }
            catch (Exception exception)
            {
                worldTexts = null;
                damageTextStatus = "unavailable:" + exception.GetType().Name;
                logger.LogWarning("Damage text pressure gauge unavailable: " + exception.GetType().Name);
            }
        }

        // Takes no arguments, so the text, position and type of a damage number are
        // never read. A void prefix cannot skip the original call.
        private static void DamageTextAdded()
        {
            try { if (Observe()) Interlocked.Increment(ref damageTextAdded); }
            catch { Interlocked.Increment(ref probeFailures); }
        }

        private static bool Observe()
        {
            if (!Enabled || !capturing) return false;
            if (Thread.CurrentThread.ManagedThreadId != ownerThread)
            {
                Interlocked.Increment(ref otherThreadSkips);
                return false;
            }
            return Volatile.Read(ref TimingHooks.Current) != null;
        }

        private static void CreateBefore(ZDO __0, out TimedScope __state)
        {
            __state = default;
            try
            {
                if (!Observe() || ReferenceEquals(__0, null)) return;
                __state = new TimedScope { Active = true, Key = __0.GetPrefab(), Started = Stopwatch.GetTimestamp() };
            }
            catch { Interlocked.Increment(ref probeFailures); __state = default; }
        }

        // Void finalizers keep the native exception. Elapsed time is inclusive of
        // everything the observed call does, including other plugins' patches.
        private static void CreateAfter(TimedScope __state, Exception? __exception)
        {
            if (!__state.Active) return;
            try
            {
                PrefabCreate.Record(__state.Key,
                    (Stopwatch.GetTimestamp() - __state.Started) * 1000.0 / Stopwatch.Frequency);
            }
            catch { Interlocked.Increment(ref probeFailures); }
        }

        private static void SerializeBefore(ZDO __instance, ZPackage __0, out BytesScope __state)
        {
            __state = default;
            try
            {
                if (!Observe() || ReferenceEquals(__0, null) || ReferenceEquals(__instance, null)) return;
                __state = new BytesScope
                {
                    Active = true,
                    Key = __instance.GetPrefab(),
                    Before = __0.Size(),
                    Package = __0,
                    Started = Stopwatch.GetTimestamp()
                };
            }
            catch { Interlocked.Increment(ref probeFailures); __state = default; }
        }

        // Size() is the written length of the package and is unaffected by the read
        // cursor; the position is never set. A failed call contributes time, not bytes.
        private static void SerializeAfter(BytesScope __state, Exception? __exception)
        {
            if (!__state.Active) return;
            try
            {
                double ms = (Stopwatch.GetTimestamp() - __state.Started) * 1000.0 / Stopwatch.Frequency;
                long written = 0;
                if (__exception == null && __state.Package != null)
                {
                    written = __state.Package.Size() - (long)__state.Before;
                    if (written < 0) { Interlocked.Increment(ref negativeByteDeltas); written = 0; }
                }
                PrefabSendBytes.Record(__state.Key, ms, written);
            }
            catch { Interlocked.Increment(ref probeFailures); }
        }

        private static void RoutedBefore(ZRoutedRpc.RoutedRPCData __0, out TimedScope __state)
        {
            __state = default;
            try
            {
                if (!Observe() || ReferenceEquals(__0, null)) return;
                int method = __0.m_methodHash;
                bool split = false;
                int composite = 0;
                // The target lookup happens before the clock starts, so the split costs
                // the measured RPC nothing it did not already do.
                if (IsTargetSplit(method))
                {
                    if (__0.m_targetZDO.IsNone()) Interlocked.Increment(ref targetNoneIds);
                    else
                    {
                        ZDOMan manager = ZDOMan.instance;
                        ZDO? target = manager == null ? null : manager.GetZDO(__0.m_targetZDO);
                        if (target == null) Interlocked.Increment(ref targetMissingZdos);
                        composite = Remember(method, target == null ? 0 : target.GetPrefab());
                        split = true;
                    }
                }
                __state = new TimedScope
                {
                    Active = true,
                    Key = method,
                    TargetActive = split,
                    TargetKey = composite,
                    Started = Stopwatch.GetTimestamp()
                };
            }
            catch { Interlocked.Increment(ref probeFailures); __state = default; }
        }

        private static void RoutedAfter(TimedScope __state, Exception? __exception)
        {
            if (!__state.Active) return;
            try
            {
                double ms = (Stopwatch.GetTimestamp() - __state.Started) * 1000.0 / Stopwatch.Frequency;
                RoutedRpc.Record(__state.Key, ms);
                if (__state.TargetActive) RoutedRpcTarget.Record(__state.TargetKey, ms);
            }
            catch { Interlocked.Increment(ref probeFailures); }
        }

        private static void DirectBefore(ZRpc __instance, ZPackage __0, out DirectScope __state)
        {
            __state = default;
            try
            {
                if (directRpcStatus != "installed" || !Observe() || ReferenceEquals(__0, null) || packageReader == null) return;
                bool known = DirectRpcHeader.TryRead(packageReader(__0), out int hash);
                if (known && hash == 0) { Interlocked.Increment(ref directPingSkips); return; }
                if (known) RememberDirectHandler(__instance, hash);
                else Interlocked.Increment(ref directHeaderUnavailable);
                __state = new DirectScope
                {
                    Active = true,
                    KeyKnown = known,
                    Key = hash,
                    Generation = directCaptureGeneration,
                    Session = Volatile.Read(ref TimingHooks.Current),
                    Started = Stopwatch.GetTimestamp()
                };
            }
            catch { Interlocked.Increment(ref probeFailures); __state = default; }
        }

        // Each invocation owns its state, including nested dispatches. A void finalizer
        // records failures without replacing the native exception or its catch path.
        private static void DirectAfter(DirectScope __state, Exception? __exception)
        {
            if (!__state.Active || !Enabled || !capturing || __state.Generation != directCaptureGeneration ||
                !ReferenceEquals(__state.Session, Volatile.Read(ref TimingHooks.Current))) return;
            try
            {
                double ms = (Stopwatch.GetTimestamp() - __state.Started) * 1000.0 / Stopwatch.Frequency;
                if (__exception != null) Interlocked.Increment(ref directRpcFailures);
                if (__state.KeyKnown) DirectRpc.Record(__state.Key, ms);
            }
            catch { Interlocked.Increment(ref probeFailures); }
        }

        // A first-seen key resolves metadata before its clock starts. Generic Register
        // overloads cannot be patched, but their holders still expose the real callback.
        private static void RememberDirectHandler(ZRpc dispatcher, int hash)
        {
            lock (NameGate)
            {
                if (DirectHandlers.ContainsKey(hash)) return;
                if (DirectHandlers.Count >= NameCapacity) { directNameCapacitySkips++; return; }
                if (directFunctions == null || !(directFunctions(dispatcher) is IDictionary functions)) return;
                object? registered = functions[hash];
                // An unknown RPC may become registered later; do not cache its absence.
                if (registered == null) return;
                FieldInfo? action = AccessTools.Field(registered.GetType(), "m_action");
                DirectHandlers[hash] = (action?.GetValue(registered) as Delegate)?.Method;
            }
        }

        private static bool IsTargetSplit(int methodHash)
        {
            int[] allowed = targetSplitHashes;
            for (int i = 0; i < allowed.Length; i++)
                if (allowed[i] == methodHash) return true;
            return false;
        }

        // Mixing is lossy, so the pair is kept to name the key at export. Past the cap the
        // key is still recorded and simply exports unnamed; a mix collision keeps the
        // first pair and is counted rather than silently renaming the row.
        private static int Remember(int methodHash, int prefabHash)
        {
            int composite = CompositeKey.Mix(methodHash, prefabHash);
            long packed = CompositeKey.Pack(methodHash, prefabHash);
            lock (TargetGate)
            {
                if (TargetPairs.TryGetValue(composite, out long existing))
                {
                    if (existing != packed) targetKeyCollisions++;
                }
                else if (TargetPairs.Count < TargetPairCapacity) TargetPairs[composite] = packed;
                else targetPairSkips++;
            }
            return composite;
        }

        // Registration names only; RPC payloads are never read or stored. Names are
        // collected whenever the hook is installed, because registration happens
        // before the first capture starts.
        private static void RegisterAfter(string __0)
        {
            try
            {
                if (string.IsNullOrEmpty(__0)) return;
                int hash = __0.GetStableHashCode();
                lock (NameGate)
                {
                    if (RpcNames.ContainsKey(hash)) return;
                    if (RpcNames.Count >= NameCapacity) { nameCapacitySkips++; return; }
                    RpcNames[hash] = __0;
                }
            }
            catch { Interlocked.Increment(ref probeFailures); }
        }

        internal static void StartCapture()
        {
            Reset();
            capturing = true;
            ownerThread = Thread.CurrentThread.ManagedThreadId;
        }

        // Names are resolved here, on the main thread, never inside a hook.
        internal static AttributionSummary[] Drain()
        {
            var rows = new List<AttributionSummary>(TopRows * 5 + 5);
            try
            {
                rows.AddRange(PrefabCreate.Drain(TopRows, ResolvePrefab));
                rows.AddRange(PrefabSendBytes.Drain(TopRows, ResolvePrefab));
                rows.AddRange(RoutedRpc.Drain(TopRows, ResolveRpc));
                rows.AddRange(RoutedRpcTarget.Drain(TopRows, ResolveTargetKey));
                rows.AddRange(DirectRpc.Drain(TopRows, ResolveDirectRpc));
            }
            catch { Interlocked.Increment(ref probeFailures); }
            return rows.ToArray();
        }

        // A destroyed or unknown target keys as "<rpc>(none)" with the arrow separator:
        // the RPC was handled, the prefab behind it is simply no longer resolvable.
        private static string ResolveTargetKey(int composite)
        {
            long packed;
            bool known;
            lock (TargetGate) { known = TargetPairs.TryGetValue(composite, out packed); }
            if (!known)
            {
                Interlocked.Increment(ref targetUnmappedKeys);
                return "composite:" + composite.ToString(CultureInfo.InvariantCulture);
            }
            int prefab = CompositeKey.Target(packed);
            return CompositeKey.Name(ResolveRpc(CompositeKey.Owner(packed)),
                prefab == 0 ? CompositeKey.NoTarget : ResolvePrefab(prefab));
        }

        private static string ResolvePrefab(int hash)
        {
            lock (NameGate)
            {
                if (PrefabNames.TryGetValue(hash, out string cached)) return cached;
            }
            try
            {
                if (Thread.CurrentThread.ManagedThreadId == ownerThread)
                {
                    ZNetScene scene = ZNetScene.instance;
                    if (scene != null)
                    {
                        GameObject prefab = scene.GetPrefab(hash);
                        if (prefab != null && !string.IsNullOrEmpty(prefab.name))
                        {
                            lock (NameGate)
                            {
                                if (PrefabNames.Count < NameCapacity) PrefabNames[hash] = prefab.name;
                                else nameCapacitySkips++;
                            }
                            return prefab.name;
                        }
                    }
                }
            }
            catch { Interlocked.Increment(ref probeFailures); }
            Interlocked.Increment(ref unresolvedPrefabKeys);
            return "prefab:" + hash.ToString(CultureInfo.InvariantCulture);
        }

        private static string ResolveRpc(int hash)
        {
            lock (NameGate)
            {
                if (RpcNames.TryGetValue(hash, out string name)) return name;
            }
            string? handler = ResolveRoutedHandler(hash);
            if (handler != null)
            {
                Interlocked.Increment(ref handlerResolvedRpcKeys);
                lock (NameGate)
                {
                    if (RpcNames.Count < NameCapacity) RpcNames[hash] = handler;
                    else nameCapacitySkips++;
                }
                return handler;
            }
            Interlocked.Increment(ref unresolvedRpcKeys);
            return "hash:" + hash.ToString(CultureInfo.InvariantCulture);
        }

        private static string ResolveDirectRpc(int hash)
        {
            lock (NameGate)
            {
                if (RpcNames.TryGetValue(hash, out string registeredName)) return registeredName;
                if (DirectHandlers.TryGetValue(hash, out MethodInfo? handler) && handler != null)
                {
                    handlerResolvedDirectRpcKeys++;
                    return handler.DeclaringType == null ? handler.Name : handler.DeclaringType.Name + "." + handler.Name;
                }
            }
            Interlocked.Increment(ref unresolvedDirectRpcKeys);
            return "hash:" + hash.ToString(CultureInfo.InvariantCulture);
        }

        // Harmony cannot patch the six generic Register overloads ("The given generic
        // instantiation was invalid"), so most registered names are never observed.
        // The registry keyed by the same hash still holds each handler delegate: its
        // method name is read here, at export, and is an identifier, not a payload.
        private static string? ResolveRoutedHandler(int hash)
        {
            try
            {
                if (Thread.CurrentThread.ManagedThreadId != ownerThread) return null;
                ZRoutedRpc dispatcher = ZRoutedRpc.instance;
                if (dispatcher == null) return null;
                routedFunctions ??= AccessTools.FieldRefAccess<ZRoutedRpc, object>("m_functions");
                if (!(routedFunctions(dispatcher) is IDictionary functions)) return null;
                object? registered = functions[hash];
                if (registered == null) return null;
                FieldInfo? action = AccessTools.Field(registered.GetType(), "m_action");
                MethodInfo? handler = (action?.GetValue(registered) as Delegate)?.Method;
                if (handler == null || string.IsNullOrEmpty(handler.Name)) return null;
                return handler.DeclaringType == null ? handler.Name : handler.DeclaringType.Name + "." + handler.Name;
            }
            catch { Interlocked.Increment(ref probeFailures); return null; }
        }

        // Counters are cumulative since StartCapture, so Sample and Drain may be
        // called in either order within one export.
        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            labels.Add(new TextValue("attribution_telemetry_status", Status));
            labels.Add(new TextValue("attribution_rpc_name_resolution",
                registerHooks == 0 ? "registry_handler_names_only"
                    : registerGenericSkips == 0 && registerHookFailures == 0 ? "all_register_overloads"
                    : "non_generic_register_overloads_plus_registry"));
            labels.Add(new TextValue("attribution_register_hook_failure", registerFailure));
            labels.Add(new TextValue("attribution_scope",
                "main_thread_only_while_capturing; inclusive_elapsed_includes_other_patches; top_rows_plus_other"));
            labels.Add(new TextValue("attribution_bytes_semantics",
                "serialized_payload_length_delta; not_wire_bytes_after_compression_or_headers"));
            labels.Add(new TextValue("attribution_key_semantics",
                "prefab_and_rpc_identifiers_only; no_payloads; unknown_exports_as_hash_fallback"));
            labels.Add(new TextValue("attribution_rpc_key_source",
                "registered_name_when_hooked; else_handler_method_name_from_registry; else_hash"));
            labels.Add(new TextValue("attribution_direct_rpc_status", directRpcStatus));
            labels.Add(new TextValue("attribution_direct_rpc_scope",
                "HandlePackage_inclusive_elapsed_ms; includes_deserialization_and_nested_routed_rpc; " +
                "do_not_sum_with_routed_rpc; four_byte_method_identifier_only; ping_excluded; " +
                "unavailable_headers_not_attributed; failed_calls_included; names_resolved_before_clock"));
            labels.Add(new TextValue("attribution_target_split_scope",
                "routed_rpc_target_splits_allow_listed_rpcs_by_target_zdo_prefab; one_zdo_lookup_before_the_clock_starts; " +
                "duplicates_routed_rpc_time_it_does_not_extend_it; split_rpcs=" + targetSplitNames));
            labels.Add(new TextValue("attribution_damage_text_gauge", damageTextStatus));
            Add(gauges, "attribution_prefab_create_keys", PrefabCreate.TrackedKeys, "keys");
            Add(gauges, "attribution_prefab_create_dropped_records", PrefabCreate.DroppedRecords, "calls");
            Add(gauges, "attribution_prefab_create_invalid_samples", PrefabCreate.InvalidSamples, "calls");
            Add(gauges, "attribution_prefab_send_bytes_keys", PrefabSendBytes.TrackedKeys, "keys");
            Add(gauges, "attribution_prefab_send_bytes_dropped_records", PrefabSendBytes.DroppedRecords, "calls");
            Add(gauges, "attribution_prefab_send_bytes_invalid_samples", PrefabSendBytes.InvalidSamples, "calls");
            Add(gauges, "attribution_routed_rpc_keys", RoutedRpc.TrackedKeys, "keys");
            Add(gauges, "attribution_routed_rpc_dropped_records", RoutedRpc.DroppedRecords, "calls");
            Add(gauges, "attribution_routed_rpc_invalid_samples", RoutedRpc.InvalidSamples, "calls");
            Add(gauges, "attribution_routed_rpc_target_keys", RoutedRpcTarget.TrackedKeys, "keys");
            Add(gauges, "attribution_routed_rpc_target_dropped_records", RoutedRpcTarget.DroppedRecords, "calls");
            Add(gauges, "attribution_routed_rpc_target_invalid_samples", RoutedRpcTarget.InvalidSamples, "calls");
            Add(gauges, "attribution_direct_rpc_keys", DirectRpc.TrackedKeys, "keys");
            Add(gauges, "attribution_direct_rpc_dropped_records", DirectRpc.DroppedRecords, "calls");
            Add(gauges, "attribution_direct_rpc_invalid_samples", DirectRpc.InvalidSamples, "calls");
            Add(gauges, "attribution_direct_rpc_failures", Interlocked.Read(ref directRpcFailures), "calls");
            Add(gauges, "attribution_direct_rpc_header_unavailable", Interlocked.Read(ref directHeaderUnavailable), "calls");
            Add(gauges, "attribution_direct_rpc_ping_skips", Interlocked.Read(ref directPingSkips), "calls");
            Add(gauges, "attribution_direct_rpc_name_capacity_skips", Interlocked.Read(ref directNameCapacitySkips), "calls");
            Add(gauges, "attribution_direct_rpc_unresolved_keys", Interlocked.Read(ref unresolvedDirectRpcKeys), "rows");
            Add(gauges, "attribution_direct_rpc_handler_resolved_keys", Interlocked.Read(ref handlerResolvedDirectRpcKeys), "rows");
            Add(gauges, "attribution_target_split_rpcs", targetSplitHashes.Length, "rpcs");
            lock (TargetGate) Add(gauges, "attribution_target_split_pairs", TargetPairs.Count, "pairs");
            Add(gauges, "attribution_target_split_pair_capacity_skips", Interlocked.Read(ref targetPairSkips), "pairs");
            Add(gauges, "attribution_target_split_key_collisions", Interlocked.Read(ref targetKeyCollisions), "pairs");
            Add(gauges, "attribution_target_none_ids", Interlocked.Read(ref targetNoneIds), "calls");
            Add(gauges, "attribution_target_missing_zdos", Interlocked.Read(ref targetMissingZdos), "calls");
            Add(gauges, "attribution_target_unmapped_keys", Interlocked.Read(ref targetUnmappedKeys), "rows");
            SampleDamageText(gauges);
            lock (NameGate)
            {
                Add(gauges, "attribution_rpc_names_known", RpcNames.Count, "names");
                Add(gauges, "attribution_prefab_names_cached", PrefabNames.Count, "names");
                Add(gauges, "attribution_direct_rpc_handlers_cached", DirectHandlers.Count, "keys");
            }
            Add(gauges, "attribution_name_capacity_skips", Interlocked.Read(ref nameCapacitySkips), "names");
            Add(gauges, "attribution_unresolved_prefab_keys", Interlocked.Read(ref unresolvedPrefabKeys), "rows");
            Add(gauges, "attribution_unresolved_rpc_keys", Interlocked.Read(ref unresolvedRpcKeys), "rows");
            Add(gauges, "attribution_handler_resolved_rpc_keys", Interlocked.Read(ref handlerResolvedRpcKeys), "names");
            Add(gauges, "attribution_negative_byte_deltas", Interlocked.Read(ref negativeByteDeltas), "calls");
            Add(gauges, "attribution_probe_failures", Interlocked.Read(ref probeFailures), "calls");
            Add(gauges, "attribution_other_thread_skips", Interlocked.Read(ref otherThreadSkips), "calls");
            Add(gauges, "attribution_register_hook_candidates", registerCandidates, "methods");
            Add(gauges, "attribution_register_hooks_installed", registerHooks, "methods");
            Add(gauges, "attribution_register_hook_failures", registerHookFailures, "methods");
            Add(gauges, "attribution_register_generic_skips", registerGenericSkips, "methods");
        }

        // Reads only the world-text list length, on the polling thread, and keeps the
        // largest value seen. It never enumerates, mutates or destroys an entry.
        private static void SampleDamageText(List<NumberValue> gauges)
        {
            try
            {
                if (worldTexts != null && Thread.CurrentThread.ManagedThreadId == ownerThread)
                {
                    // Reference equality, not Unity's operator: the managed list is readable
                    // even on a destroyed component, and the operator is the costlier check.
                    DamageText texts = DamageText.instance;
                    if (!ReferenceEquals(texts, null) && worldTexts(texts) is ICollection live && live.Count > damageTextLiveMax)
                        damageTextLiveMax = live.Count;
                }
            }
            catch { Interlocked.Increment(ref probeFailures); }
            Add(gauges, "damage_text_added", Interlocked.Read(ref damageTextAdded), "calls");
            Add(gauges, "damage_text_live_max", damageTextLiveMax, "texts");
        }

        private static void Add(List<NumberValue> gauges, string name, double value, string unit) =>
            gauges.Add(new NumberValue(name, value, unit));

        // Registered RPC names survive a reset: they are established at startup and
        // re-collecting them would need a second registration pass that never happens.
        internal static void Reset()
        {
            capturing = false;
            unchecked { directCaptureGeneration++; }
            PrefabCreate.Reset();
            PrefabSendBytes.Reset();
            RoutedRpc.Reset();
            RoutedRpcTarget.Reset();
            DirectRpc.Reset();
            lock (NameGate) DirectHandlers.Clear();
            directRpcFailures = directHeaderUnavailable = directPingSkips = directNameCapacitySkips = 0;
            unresolvedDirectRpcKeys = handlerResolvedDirectRpcKeys = 0;
            probeFailures = otherThreadSkips = negativeByteDeltas = 0;
            unresolvedPrefabKeys = unresolvedRpcKeys = nameCapacitySkips = handlerResolvedRpcKeys = 0;
            targetNoneIds = targetMissingZdos = targetUnmappedKeys = 0;
            damageTextAdded = damageTextLiveMax = 0;
            lock (TargetGate) { targetKeyCollisions = targetPairSkips = 0; }
        }

        internal static void Uninstall()
        {
            Enabled = false;
            Reset();
            lock (NameGate)
            {
                RpcNames.Clear();
                PrefabNames.Clear();
            }
            lock (TargetGate) TargetPairs.Clear();
            targetSplitHashes = new int[0];
            targetSplitNames = "none";
            worldTexts = null;
            damageTextStatus = "disabled";
            registerCandidates = registerHooks = registerHookFailures = registerGenericSkips = 0;
            registerFailure = "none";
            routedFunctions = null;
            directFunctions = null;
            packageReader = null;
            directRpcStatus = "disabled";
            Installed = false;
            // Removal must not throw into the game's shutdown path; a refused unpatch
            // leaves inert hooks behind and is reported instead of propagating.
            try { Patches.UnpatchSelf(); Status = "disabled"; }
            catch (Exception exception) { Status = "unpatch_failed:" + exception.GetType().Name; }
        }
    }
}
