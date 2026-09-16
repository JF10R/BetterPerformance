# Scout / Rune developer feedback: loading investigation

### Follow-up: 0.4.3 production integration

CLI discovery continued to identify exact indexed definitions and mark changed
source coverage unknown. Final reindex covered 122 files, 458 cortex symbols and
27 families; approximate graph 41 file edges / 76 symbol edges, with the separate
symbol graph still empty. Incremental cortex reported 371 ms (431 ms wall).
`InitialLoadingOptimization` then resolved exactly to its new definition at line
18. Native contracts were verified separately against local client/server IL;
different metadata tokens produced different valid method-body fingerprints.
No MCP transport comparison was performed in this phase.

### Follow-up: 0.4.2 fast-loading investigation

The same CLI binary reindexed the changed source successfully: 438 symbols,
26 families, 42 approximate file edges; the separate symbol graph still had
zero edges. Incremental cortex processing reported 224 ms (276 ms wall).
An exact symbol search located `LoadingDetailsTelemetry.cs:37`; D2 provided
its methods and exact ranges were readable. The output explicitly limited
VERIFIED to source/locator identity and suppressed low-trust call chains.
This is useful evidence discipline. D2 remains an outline, not function bodies.

Installed Valheim IL remains outside the indexed repository, so its native
loading/cache contracts required the local IL inspection tool. That fallback
is a scope limitation, not a failed Scout search. MCP reconnection and a
controlled comparison against the previous Scout binary were not tested.

2026-09-15. Same private `bp-7828fd55` candidate as the preceding cohort, used via
CLI with repository `D:/GitHub/BetterPerformance` and cache
`D:/scout-bp-valheim-7828fd55`. Binary SHA256:
`FEFFB5330DC62FFCE8A9C0A981F0D97663A14BED41E6307E333626ECC74E9916`.
This is natural development feedback, not a controlled old/new binary benchmark
or validation of the MCP transport fix.

### Useful behavior

- Exact symbol queries for StartCapture and Metric found the correct definitions.
- Cognitive reads provided useful Plugin/TimingHooks outlines, followed by exact
  ranges for implementation. The 400-line range cap was explicit.
- Modified-file searches reported SOURCE_UNKNOWN/CHANGED instead of silently
  asserting complete fresh-source coverage. Stale graph chains were withheld.
- Reindexing succeeded: 110 files, 410 cortex symbols, 23 families; approximate
  graph 36 file edges and 68 symbol edges. Separate symbol graph had zero edges.
- After indexing, `loading_report` resolved exactly to Python line 511 and
  `LoadingTimeline` to C# line 10, without coherence warnings. Both were new code.

### Remaining friction and recommendations

1. Broad `loading respawn` content discovery produced low-confidence generic
   file candidates rather than useful native boundaries. The uncertainty was
   visible; ranking still requires significant narrowing by the caller.
2. Distinguish fresh exact source evidence from approximate graph relationships.
   A zero-edge separate symbol graph should not be conflated with the useful
   approximate file/symbol graph in summaries.
3. Keep changed-source absence distinct from verified absence. In the preceding
   cohort, a real changed Python definition was headed EXACT_MISS while also
   marked SOURCE_UNKNOWN; reindexing recovered it. This remains an actionable
   presentation issue, not evidence of universally broken Python indexing.
4. De-duplicate graph/freshness diagnostics and separate relevant stale code from
   unrelated changed documents. Earlier requests repeated diagnostics four to
   eight times; this cohort still listed multiple unrelated changed files for a
   narrow known-term query. Do not bury the actionable result.
5. Make outline-versus-implementation semantics clear for cognitive D1/D2 reads;
   the additional exact-range call was necessary to verify save/clock guards.

Installed proprietary game methods and ignored QA artifacts were outside the
index, so native IL inspection was an expected fallback. It should not count as a
Scout failure. The runtime tests, hardware contention and growing repository
prevent a reliable tool-throughput comparison. No stale relationship was observed
being presented as verified; no MCP fix or broad parser regression is claimed.

Evidence is retained locally in `.qa/scout-v041-reindex.txt` and the task's CLI
outputs, plus the preceding `.qa/scout-rune-feedback-20260915-v040.md`. No external
developer message or GitHub publication was sent; this report is ready to share.
