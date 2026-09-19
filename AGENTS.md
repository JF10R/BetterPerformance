# Development rules

### Scope

- Initial scope: diagnostics for the local Valheim client and dedicated server.
- Keep measurement and optimization modules separate. No behavior changes in diagnostics-only mode.
- Do not claim runtime compatibility, negligible overhead, or performance gains without measurements.
- Do not launch Valheim or its server, deploy plugins, or alter game configurations without explicit authorization.

### Repository hygiene

- Never commit game assemblies, decompiled game sources, worlds, secrets, or raw captures.
- `docs/` holds project documentation only: guides, one page per optimization module, one per telemetry family. Session reports, research notes, reviews, validation records and Scout feedback go to `notes/` (excluded from git, `.git/info/exclude`); the CHANGELOG carries what the public needs from them.
- Reference game dependencies locally; do not bundle them for distribution.
- Preserve upstream licenses and attribution when importing third-party code.
- Use Scout for code discovery when available; use raw search when unavailable or insufficient. Do not use Graphify unless requested.

### Lessons that cost us (2026-09-16 to 09-18; each one happened here)

- Check the DEPLOYED config, not the code default, before calling a risk "unexposed". Two reviews graded a defect harmless because the key defaults to `false`; it was `true` on both installations both times.
- Archive the previous game build (assembly + decompilation) before decompiling a new one. Two updates in two days each overwrote the only copy of the previous IL, and a contract break had to be argued from body size instead of diffed. `docs/game-update-guide.md` step 3.
- Never pin a raw SHA-256 of IL bytes: metadata tokens renumber on any change elsewhere in the assembly. Use `IlFingerprint.Compute`, which held across 1.0.14 and 1.0.15 where four raw hashes broke.
- A harness must run every module and report all failures; one that throws on the first hides the rest. It hid a second broken contract for an unknown number of runs.
- A field the native code assigns on one peer is not available on another: `DungeonGenerator.m_originalPosition` is set only by the peer that spawns the location and is not in the ZDO. Follow the assignment sites before reading a field on the client.
- A behaviour patch must not depend on a "best-effort" telemetry patch for correctness. The smelter budget's only counter reset lived in its telemetry set; a telemetry failure would have frozen every smelter.
- A game update can supersede a module outright (1.0.15 removed the per-texel terrain save this plugin coalesced). The module's own `Verify` must refuse the new shape, and the contract test must assert both shapes strictly rather than widen one.
- An agent's report is a lead, not evidence: re-derive any claim before acting on it (signature, call sites, config value). Every wrong claim caught this week was caught that way.
- The user judges modules on absolute numbers from ordinary play, never on A/B or baseline sessions. Do not propose one.
- A deferral is only equivalent when the deferred work would have happened anyway. The smelter budget carried idle catch-up seconds that vanilla discards (`continue` on an empty station), so an empty smelter banked its whole absence and smelted later ore at 8x. Before bounding a native loop, list what the loop throws away and prove the bound throws the same.
- A gauge that reads zero for two sessions must be proven reachable. `loot_visibility_arrival_missing` and `destroyed_rock` were structurally zero (t2 sat on a path only network ZDOs take; the rock hook never counted) and the validation doc called them "unproven" instead of wrong. An isolated run must exercise every counter once, or the module must self-test the path.
- Parallel implementation agents must build in private directories or they collide on `obj/` (CS0579 duplicate attributes, NETSDK1005): `dotnet build <proj> -c Release -warnaserror -p:ValheimDir=<install> -p:BaseIntermediateOutputPath=<private>/obj/ -p:OutputPath=<private>/bin/ -p:DefaultItemExcludes=**/obj/**`; for the net10 test project use `-p:ArtifactsPath=<private>` instead. Hand agents that command verbatim and never let them run the harness or the isolated-world scripts concurrently.
- The dedicated-server build is a different assembly: `ZSteamSocket.GetConnectionQuality` calls the client Steam API there, so a "works on the client" native figure can be all zeros on the server. Check the archived server decompile for every native call a server-side gauge relies on.

### Verification

- Validate aggregation, bounded buffers and export behavior with meaningful offline tests when implemented.
- Record units and distinguish elapsed time, CPU time and asynchronous completion.
- Keep unsupported or unavailable measurements explicit.
- Check documentation links and rendered Markdown before publishing.
- After a Valheim update, follow [docs/game-update-guide.md](docs/game-update-guide.md): run `scripts/check-game-update.ps1`, fix broken contracts before widening any check, validate on a disposable world, then deploy with backups.
