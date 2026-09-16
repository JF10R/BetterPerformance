# Frontier implementation work

Requested 2026-09-15: cover the research findings, implement validated opportunities and add bottleneck measurements, including CPU/RAM and multithreading investigation. Code and reports remain English. Existing 0.3.3 edits and evidence must be preserved.

### Assumptions and release boundary

- Existing authorization covers disposable hidden tests and installation after validation. Never touch normal worlds/characters; stop if a normal Valheim process is running.
- Research alternatives are evaluated separately. A rejected prototype is documented as rejected, not described as implemented gameplay functionality.
- Do not move live Unity objects, native AI, physics or mutable world snapshots onto arbitrary worker threads. A worker experiment must own immutable inputs and include copying/queue/coordination costs.
- Optimizations remain independently configurable, with measured scope, compatibility guards and fallback. Diagnostics do not change gameplay.
- No GitHub posting or release is needed for this local development/build request. If publishing becomes necessary, use exact body files and verify rendered Markdown. No commit/push currently requested.

### Work and acceptance criteria

- [x] Preserve prior baseline, installed DLL identity, uncommitted work and research.
- [x] Package-copy elimination: verified replication callsite and parity; Unity helper timings and avoided payload volume. Optional Mono allocation counter proved invalid and is reported unavailable, not zero allocation.
- [x] Exact map reuse: complete serialized-input equality, bounded cache and changed-input fallback; two sessions of paired save timings and character readback.
- [x] Compression/threading research: bounded 0/1/2/4-worker prototype, 224 desktop batches, queue/copy/memory tradeoffs; no unvalidated live worker pool shipped.
- [x] Memory diagnostics: resident/private commit and Unity allocator views with explicit availability; no forced collection or heap walking.
- [x] Rendering diagnostics: capability-checked optional sparse timing; correct headless status verified. Rendered GPU samples still require normal gameplay.
- [x] Ordinary action outcomes: bounded confirmed/censored/ambiguous pickup/container endpoints; actual Unity accepted eight pickups and one chest opening per session.
- [x] Bottleneck/incident reporting: bounded offline neighboring intervals, finer discovery/sort/character stages, prep/service split and explicit nested elapsed semantics.
- [x] Remaining research directions: inspected contracts and documented blocking invariants and promotion gates in the feature matrix; speculative architecture is not labeled implemented.
- [x] Added targeted preparation-clock option after startup measurements showed poor progress; offline boundary/guard tests and actual Unity validation completed.
- [x] Integrate streams: 37 core and 33 Python tests, current-game client/server checks, two disposable runtime sessions; test processes stopped and protected state verified.
- [x] Results, feature matrix and Scout feedback updated; identical validated 0.4.0 DLL installed on client/server with backups and no active QA plugin.

### Decision rules

Prefer avoiding work/copies before parallelizing it. Measure total CPU and retained/allocated memory as well as caller latency. Reserve CPU capacity for the simultaneous server/client; logical processor count is not an appropriate worker count by itself. Do not equate one thread at 100% with a machine at 100%.

Alternatives requiring deeper invariants include asynchronous persistence, direct packed-bit DEFLATE, shared world snapshots and off-thread collision readiness. Their required tests include save consistency and mutation coverage, not only throughput. Record evidence and unresolved blockers before activating such a change.

Unresolved user questions: none required to start. Runtime/API feasibility and measured tradeoffs are engineering questions to resolve with evidence.

Completion evidence: `docs/frontier-runtime-0.4.0.md`, `docs/frontier-implementation-0.4.0.md`, `.qa/deployment-0.4.0.json`, `.qa/scout-rune-feedback-20260915-v040.md`. The user confirmed Wardogs was the concurrent CPU load. Future architecture gates remain explicitly listed; completing this execution does not claim every research hypothesis became safe production code.

### Follow-up: loading and data safety, 0.4.1

- [x] Measure real loading boundaries and distinguish native game-dt spawn messages from complete observed wall duration.
- [x] Add fixed-size passive timeline and inclusive world-generation/terrain stages; report incomplete coverage honestly.
- [x] Review persistence/network risks; harden custom-stream and package-construction compatibility guards with regression checks.
- [x] Complete 38 core tests, 37 Python tests, both native contract suites and one isolated Unity session; verify unchanged protected state.
- [x] Install the exact runtime-tested 0.4.1 DLL on both normal installations, preserving prior DLL/config backups and a verified local save copy; no active QA DLL.
- [x] Publish local English loading results and updated Scout/Rune feedback. No GitHub posting requested.

Evidence: `docs/loading-runtime-0.4.1.md`, `docs/data-safety-audit-0.4.1.md`,
`docs/scout-feedback-0.4.1.md`, `.qa/deployment-0.4.1.json`.
