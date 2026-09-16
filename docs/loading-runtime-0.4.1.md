# Loading diagnostics and safety validation: 0.4.1

Validated and installed on both local installations on 2026-09-15. Valheim 1.0.12,
Unity 6000.0.75f1, BetterNetworking 2.3.3 and the existing VPlus/plant mods were
present. This version adds observation and compatibility hardening; it does not
introduce another loading scheduler, generation cache or save worker.

### Measured client loading

One isolated, silent, headless reconnect used a copy of the established disposable
world and temporary character. Normal worlds and characters were not loaded.
The accepted scene-transition boundary was observed before capture initialization.

| Observation | Monotonic wall time from episode start |
| --- | ---: |
| Scene transition requested | 0.017 s |
| Game scene Awake entered | 2.641 s |
| Respawn requested / method entered | 13.200 / 13.204 s |
| Native spawn point accepted | 36.603 s |
| Player construction returned | 36.663 s |
| Respawn update completed | 36.667 s |
| Native loading overlay hidden | 37.617 s |

The first timestamp differs slightly from episode start because the first observer
invocation includes cold initialization. This is not click-to-play time. The HUD
endpoint verifies the native CanvasGroup state; headless execution does not verify
a rendered frame or responsive controls. Native `Spawned after` was 22.960 seconds
of accumulated game `dt`, clearly different from the complete observed wall span.
The previous 180/35-second markers therefore must not be called complete join times.

### Where work and waiting occurred

| Native operation | Observed inclusive elapsed cost | Interpretation |
| --- | ---: | --- |
| Client RPC_PeerInfo | 8,262.4 ms, one call | Connection handling and local initialization, not network transit |
| WorldGenerator.Initialize | 2,823.9 ms | Nested within peer-info handling |
| FindLakes | 2,002.9 ms | Nested generation stage |
| PlaceRivers | 105.1 ms | Nested generation stage |
| PlaceStreams | 715.9 ms across two calls | Nested generation stage |
| FindSpawnPoint | 50.2 ms across 1,148 calls | Includes 1,147 unsuccessful returns; not the 23.4-second wall wait |
| IsAreaReady inside FindSpawnPoint | 47.6 ms across 748 calls | 747 false results; nested within the preceding row |
| SpawnPlayer | 59.8 ms, one call | Includes native character construction and load callbacks |
| RequestTerrainSync, entire client capture | 0.372 ms across 106 calls | Polling/backoff optimization would have negligible benefit here |
| Terrain worker, entire client capture | 2,169.8 ms across 304 jobs | Overlapping background service; maximum job 31.9 ms |

Do not sum parents, children and workers. About 5.4 seconds of the peer-info scope
remain unattributed by the current child timers. Installed IL calls
`AltBiomeWorldData.VerifyBiomeData` after world initialization. That method loads
or generates cached biome points and generates sectors; it is a concrete next
measurement target, **not a proven explanation for all remaining time**. Mod hooks,
other native calls and scheduling may also contribute.

The current capture did not observe calls to map-texture loading/generation or
SetMapData. Enabled probes with zero calls mean no completed invocation inside
capture coverage, not zero cost before capture. Dedicated-server world generation
also preceded capture coverage in this test.

The next priorities are the 23.4-second spawn-readiness period and the peer-info
initialization gap. Existing object preparation/service and replication metrics
can be correlated with that period. Exact generation reuse deserves further
research, but global random/noise state and complete-output ownership must be
preserved. A seed-only cache or an arbitrary Task.Run would not meet that bar.

During loading, sampled scene occupancy progressed from 2 to 1,130, remained at
1,130 across another roughly six-second observation interval, then reached 3,614
and 5,738 around spawn completion. This supports investigating streaming/readiness
progress rather than treating character construction as a 36-second operation.
It does not identify the missing zone, prefab or object responsible for each wait.
The Game constructor sets the native minimum respawn delay to eight game-time
seconds by default; the active serialized/runtime value was not exported in this
capture, so eight seconds must not be subtracted as a verified exclusive wall phase.

This is one diagnostic session, not a controlled speedup comparison. The user
confirmed Wardogs caused contention in earlier runs; its exact load during this
run was not independently controlled. The fixture's old WardogsWasRunning field
is a preset, not fresh evidence. No new percentage loading improvement is claimed.

### Data safety and verification

Two conditional mod-compatibility gaps were fixed: custom map streams now retain
native writes; Harmony patches on local package construction/Clear force native
copying. Neither case was observed corrupting the installed mod set. See the
[detailed audit](data-safety-audit-0.4.1.md).

- Build: zero warnings/errors. Core suite 38/38; Python suite 37/37.
- Both installed game assemblies: 496 existing checks, AI contracts, 176 map-bit,
  215 package-copy, 230 compression-cache and 37 preparation-clock checks.
- Loading contracts: 53 checks per installation on a modern CLR. Standalone
  .NET Framework reports its interface-loading limitation explicitly.
- Actual Unity: all 47 registered probe capabilities enabled on client/server;
  loading observer installed and HUD completion observed on client, correctly
  not applicable on dedicated server. No recorded probe failures or writer drops.
- Native/cache map parity across explored/shared bits and pin mutations,
  temporary-character save/reload and package byte parity passed again.
- 168 protected save files unchanged; original preferences restored exactly after
  Unity changed four automatic session bookkeeping values. Test processes stopped.

No normal-path data corruption was found, but this is not proof against all mod
combinations, disk/cloud failures, concurrency bugs or long-term ownership effects.
Map serialization/cache touch saved bytes; package copying touches replicated
bytes. The object budget can delay local appearance/interactions under sustained
backlog, without deleting the underlying ZDO. The plugin does not replace native
save transactions or make saving immune to interruption.

### Log size and overhead

The two captures total 1.505 MB. Extrapolating average interval record sizes at the
configured three-second cadence gives **64.6 MB/hour combined**; using the largest
observed record for each role gives **70.0 MB/hour combined**. This is below the
requested 100 MB/hour in this workload, not a rolling byte-rate guarantee. Other
mods' verbose logs are excluded; file/directory bounds remain enforced.

Collector polls averaged 0.80 ms client and 0.73 ms server, with cold maxima about
15.8 ms. Excluding the first cold poll gives about 0.20 and 0.17 ms. This measures
collector elapsed time, not all Harmony/timestamp overhead or a causal FPS tax.
Loading hooks retain eight milestones and four aggregate operation counters;
they do not scan the scene or write per-call log lines.

### Deployment

Identical 0.4.1.0 DLL installed on client and dedicated server:
`01131C90FF7CDC3C5FF4B3F3695080E3CBDFBF8156B81A688462C7EAAD94D3BE` (SHA256).
Client loading timeline enabled; server keeps relevant native stage timing.
Existing optimization choices preserved; loot priority remains disabled. No QA
DLL is active in either normal installation.

Previous DLL/configuration backups and receipts are local under
`.qa/deployment-backups` and `.qa/deployment-0.4.1.json`. A separate hash-verified
copy of 82 local `.db`/`.fwl`/`.fch` files (646,462,022 bytes) was made before
deployment in `.qa/save-backups/20260915T122600Z-before-0.4.1`. This protects those
local files at that instant; it is not continuous or cloud backup.

At the user's request, the existing `G:/Valheim_Backup/run_backup_modded.v2.ps1`
was then run with retention disabled. It already includes `characters_local` and
`characters`; no script edit was needed. Archive
`G:/Valheim_Backup/2026/Archives/valheim_live_2026-09-15_082914.tar.zst` contains 68
manifested files, including three character-directory files (two `.fch` saves),
and is about 87 MiB. A complete extraction into an isolated verification directory
matched all 68 SHA256 manifest entries. Archive SHA256:
`80CECFE5A75B56964D2124FC4E42CE7AD4B8157E88CB9D4BDF06F82CAA4ACB76`.
Receipt: `.qa/backup-v041-gdrive-receipt.json`. Existing backups were retained.

Private runtime evidence: `.qa/runs/20260915T122110Z-5e4b37`,
`.qa/frontier-v041-analysis.json`, and `.qa/frontier-v041-{client,server}.md`.
The older fixture workload labels still say v040; the actual tested DLL identity
and deployment receipt establish the 0.4.1 build. No game assembly, save or raw
capture is included in public source.
