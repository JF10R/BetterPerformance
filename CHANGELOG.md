# Changelog

### 0.1.1

- Fix unavailable Windows Unity/Mono working-set readings with native PSAPI; omit invalid values.
- Add native Steam pending/unacknowledged bytes, rate, ping and queue-time estimates, separate from BetterNetworking-adjusted socket values.
- Support the dedicated Steam interface and known ServerSync buffering wrappers without altering their behavior.
- Add network-peer, RPC, save-update, sorted-object and distant-object timing probes.
- Add bounded scenario markers and identify loop gaps crossing phase boundaries or spanning GC collections.
- Extend reports with native counters, memory validity and marker interpretation.
- Add a three-block comparison report with run-level ranges and exploratory paired 95% uncertainty intervals.

Diagnostics only. No spawning, saving, replication, compression or bandwidth behavior changed. BetterNetworking remains a separate optional mod.

### 0.1.0

- Initial bounded client/server diagnostics, background JSONL export and offline reporting.
