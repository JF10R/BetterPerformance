# Configuration history and diagnostic interpretation — 0.3.3

### Graphics state and changes

The collector reads an explicit allowlist from the running game's `GraphicsSettingsManager`, not arbitrary preferences or mod configuration files. No setting is modified. `graphics.player_raw.*` records stored player choices; `graphics.active.*` records applied settings, which presets or background mode can override. Both include simulation distance, LOD, target vertical 3D resolution, upscaling algorithm, FPS limit, vegetation, shadows, lights and graphics toggles. Runtime output dimensions, fullscreen mode, target FPS, VSync, LOD bias and focus are recorded separately.

`simulation.synced_*` describes negotiated simulation distances. These values must not be interpreted as the local graphics-menu selection. Setting values are native numeric IDs; do not assign localized labels without the matching game-version mapping. The server reports graphics as not applicable and still samples synchronized simulation distances.

The game raises `GraphicsSettingsChanged` after applying its active settings. During an active capture, the plugin observes this event on the main thread and stores field changes without file I/O. Regular collector polls provide fallback detection, including runtime values changed without that event. Consecutive A → B → A events before export retain both transitions. Poll timestamps indicate detection time; an intermediate change entirely between polls without a game event can be missed.

Snapshots appear in `config.*` labels; `configurationChanges` retains name, previous/current values, monotonic elapsed seconds since capture start and observation source. Correlate with that capture's start UTC. A window containing transitions is mixed, not a clean sample of its final configuration. Unavailable fields retain their last-known value; consult `graphics.status`. Captures from before 0.3.3 have no local graphics history and cannot reconstruct menu changes retrospectively.

Memory is bounded to 96 keys and 128 field transitions per export. Further transitions increment `configuration_changes_dropped_total`, while the latest state remains available. Writer drops can independently remove an entire record. Each capture segment starts a new baseline; events during the existing gap between segments are not retained, and the final export carries last-observed state plus pending transitions. No complete event-history guarantee is made across such gaps.

`GraphicsObservation` includes the read/change-tracking cost, including event callbacks. Poll-time observations are nested within collector timings and must not be added to them. There is no per-frame graphics reflection or preference scanning. Added overhead and volume still require runtime measurement; the approximately 32 MB/hour combined observed with 0.3.2 is not a measurement of this new build.

### Loot scope

`loot_queue_*` is a sampled wait from observing a nearby network object pending scene creation to its later local creation. It is not a counter of mob kills, drops, pickups, chest opens or inventory transfers. Objects created between scans, already-visible pickups and locally created drops that bypass the observed path can all be absent. One completed queue observation does not mean only one item was looted. The current build does not yet measure complete pickup/chest action-to-confirmation latency.

### Save and RPC interpretation

The installed Valheim 1.0.12 code sends character-save requests to connected clients. Each client handles the request synchronously through `Game.SavePlayerProfile`, which includes minimap serialization and character-file saving. World saving also prepares snapshots synchronously before starting its worker, and can join an existing worker or wait when explicitly requested. Thus asynchronous world writing does not prevent a visible freeze across players. A worker's elapsed duration is not itself a measured main-thread pause.

The finer save/RPC probes are inclusive elapsed timings. Map serialization includes compression; character-file saving includes serialization, hashing and file/backup operations, not pure disk time. Incoming object updates run under RPC handling; outgoing synchronization has separate list-building and send/serialize work. Network messages are handled inline, so an RPC spike can include a save, a burst of objects, another handler, or scheduling. These probes identify subpaths but do not measure exclusive CPU time or prove a cause retrospectively.

No save scheduling, file format, RPC ordering, inventory behavior or graphics settings are changed by these diagnostics. Moving live Unity state reads to a background thread would not be a safe save optimization. Any future off-thread work needs a consistent snapshot and preserved write/backup ordering.
