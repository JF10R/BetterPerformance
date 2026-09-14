# Changelog

### 0.3.0 — untested additions

- Add opt-in quota expansion under the existing soft creation-time budget.
- Add opt-in nearby-loot priority within vanilla object-type tiers; retain vanilla order every fourth eligible pass.
- Bound prefab classification and scratch storage; expose option activity and fallbacks in captures.
- Prepare regression coverage without executing tests or launching game/server sessions, as requested. No new performance claim or package release.

### 0.2.0

- Add an opt-in soft time budget shared by near/distant scene object creation.
- Preserve vanilla readiness, priority, count limits and invalid-prefab behavior; guarantee progress past failed prefabs.
- Add a main-thread runtime toggle and capture counters for controlled comparisons.
- Add budget boundary/progress regression tests and document latency tradeoffs.
- Validate one continuous eight-window client/server comparison: lower client creation-batch peaks, modest loop changes, variable loot latency; retain default-off status.

### 0.1.1

- Fix unavailable Windows Unity/Mono working-set readings with native PSAPI; omit invalid values.
- Add native Steam pending/unacknowledged bytes, rate, ping and queue-time estimates, separate from BetterNetworking-adjusted socket values.
- Support the dedicated Steam interface and known ServerSync buffering wrappers without altering their behavior.
- Add network-peer, RPC, save-update, sorted-object and distant-object timing probes.
- Add bounded scenario markers and identify loop gaps crossing phase boundaries or spanning GC collections.
- Extend reports with native counters, memory validity and marker interpretation.
- Add a two- or three-block comparison report with run-level ranges and exploratory paired 95% uncertainty intervals.

Diagnostics only. No spawning, saving, replication, compression or bandwidth behavior changed. BetterNetworking remains a separate optional mod.

### 0.1.0

- Initial bounded client/server diagnostics, background JSONL export and offline reporting.
