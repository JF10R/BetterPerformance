# Attribution telemetry

Per-name cost attribution. Aggregate timings say a phase is slow; these rows say
which prefab or routed RPC carried the cost. Diagnostics only: no game state is
read for its value, written, or repositioned.

### Schema

Each `interval` and `capture_end` record carries an `attributions` array. A row is:

| field | meaning |
| --- | --- |
| `group` | `prefab_create`, `prefab_send_bytes`, `routed_rpc` or `routed_rpc_target` |
| `key` | prefab name, RPC identifier, a hash fallback, or `other` |
| `count` | observations in the interval |
| `sumMs` | total inclusive elapsed time, milliseconds |
| `maxMs` | slowest single observation, milliseconds |
| `bytes` | serialized payload bytes; always 0 outside `prefab_send_bytes` |

Older captures have no `attributions` key and the array is empty when the probe
is disabled. The report script ignores the array today.

### Semantics

- `prefab_create` times `ZNetScene.CreateObject(ZDO)` and keys it by the ZDO's
  prefab hash. `prefab_send_bytes` times `ZDO.Serialize(ZPackage)` and measures
  the package's written length before and after the call.
- `routed_rpc` times `ZRoutedRpc.HandleRoutedRPC` and keys it by the routed
  method hash.
- `routed_rpc_target` re-reports the same timings for the RPCs named in
  `[Diagnostics] AttributionTargetSplitRpcs` (default `RPC_Damage`,
  `RPC_ApplyOperation`, `RPC_RequestOwn`, `RPC_RequestOpen`), keyed by the RPC and
  the target ZDO's prefab, as `RPC_Damage→Greydwarf`. The target is resolved with
  one `ZDOMan.GetZDO` lookup before the clock starts; a destroyed target keys as
  `RPC_Damage→(none)`. This group duplicates `routed_rpc` time and must never be
  added to it.
- `damage_text_added` counts `DamageText.AddInworldText` calls and
  `damage_text_live_max` the largest world-text list length seen at a poll, both
  cumulative since the capture started. They size the pooling candidate in
  the research notes (kept outside the repository), F1; they change no behavior.
- Elapsed time is inclusive: it covers everything the observed call does,
  including work added by other plugins patching the same method.
- Bytes are the serialized payload the game wrote into the package. They are not
  wire bytes: headers, batching and compression happen later and are not counted.
- Each group emits its top 24 rows by time, or by bytes for `prefab_send_bytes`,
  plus one `other` row holding every remaining and every dropped observation. The
  emitted rows therefore still sum to the interval total.

### Key resolution

- Prefab hashes resolve through `ZNetScene.GetPrefab(int)` at export time on the
  main thread, never inside a hook, with a 512-entry cache. An unresolved hash
  exports as `prefab:<hash>`.
- RPC hashes resolve from names captured at `ZRoutedRpc.Register` and
  `ZRpc.Register`. Harmony cannot patch the ten generic overloads, so most names
  come instead from the registry `ZRoutedRpc.m_functions`, read at export: the
  key is then the handler's method name, such as `ZNet.RPC_PeerInfo`, not the
  registered name. An unresolved hash exports as `hash:<int>`.
- Keys are prefab and method identifiers. No RPC payload is read or stored.
- A `routed_rpc_target` key is a lossy mix of the two hashes, named from a
  256-entry pair map. Past that cap the row exports as `composite:<int>`; a mix
  collision keeps the first pair's name. Both are counted in `attribution_target_*`.

### Limits

- Observations are recorded only while a capture is active and only on the thread
  that started it. Calls on other threads, including background saves that also
  serialize ZDOs, are skipped and counted in `attribution_other_thread_skips`.
- Each group tracks at most 256 distinct keys per interval. Past that, new keys
  are dropped into `other` and counted in the group's `dropped_records` gauge.
- Non-finite, negative durations and negative byte counts are counted as invalid
  and never stored.
- Overhead has not been measured in a running game. Compilation and the offline
  harness establish signatures and installation, not runtime cost.
