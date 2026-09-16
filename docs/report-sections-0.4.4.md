# Report sections added in 0.4.4

`scripts/summarize_capture.py` renders five per-capture sections after the loading sections and before the cross-capture slow-window sections. Each section prints nothing when its fields are absent, so captures from earlier versions render unchanged.

### Base simulation

One row per observed stage among the 22 base-simulation timings (wear batch and support, heightmap batch/regenerate/modifiers/collision/render, terrain operations, station ticks, location, vegetation, zone and dungeon spawns): calls, sum, maximum and calls over 50 ms, summed across retained windows. Rows are inclusive elapsed times and must not be added together. A population line gives the min/max of the sampled list sizes. A note flags zero `WearSupportUpdate` calls, which can mean another mod disabled wear; absence is not proof.

### Attribution

Per-group tables for `prefab_create`, `prefab_send_bytes` and `routed_rpc`, aggregated per key across the whole capture (interval boundaries are lost). Top 15 keys by sum ms, or by bytes for the bytes group, plus the plugin's own `other` row and one `remaining N keys` row, so the printed rows conserve the group total. Bytes are serialized payload before batching and compression. `hash:<int>` and `prefab:<hash>` are unresolved names; routed-RPC keys can be handler names rather than registered names.

### Engine markers

Timing markers (`engine_<marker>_count/sum/max`) summed across windows; the frame count is the number of completed frames with a sample, and a maximum is the worst frame, not the worst call. A ring wrap is reported as yes/no only, because the engine does not report how many frames were lost. Counters print minimum, maximum and last sample; no percentile or rate is derived. Fixed-step accounting, GC mode and every unavailable or headless metric are listed so a missing number reads as unmeasured, not zero.

### Host and network path

Start-record labels (priority class, affinity mask, power scheme, online backend) and the set of transport-path values seen, with min/max of timer resolution, relayed/direct connection counts and peer socket types. The shared host counter prints the first and last `host_qpc_timestamp` with their record UTC, so a client capture and a server capture from the same machine can be aligned by hand. That alignment is unvalidated until a known-simultaneous event confirms it.

### Ownership and replication

Per-interval call counters (`zdo_set_owner_calls`, `zdo_request_rpcs`, `item_request_own_rpcs`, `container_open_requests`) summed across windows, and ZDO manager counters as observed ranges. Requests are not completed transfers and carry no latency; action telemetry holds the latency side.

### Limits

All values come from bounded interval aggregates, never per-call logs. Sums across windows include collector backoff gaps. Nothing here is a confidence interval, an FPS figure or a causal attribution; see [measurements](measurements.md).
