# Validation: 0.4.8

2026-09-17 (local). Three opt-in optimizations (smelter catch-up budget, dungeon spawn slicing, speculative map pre-compression) and count-only diagnostics ahead of three more (clutter, build mode, ownership release). Built against Valheim 1.0.12 / Unity 6000.0.75f1. Mechanisms: [smelter-catchup-budget.md](smelter-catchup-budget.md), [dungeon-spawn-slicing.md](dungeon-spawn-slicing.md), [map-precompression.md](map-precompression.md); research documents dated 2026-09-17 for all six items.

### Offline gates

| Gate | Result |
| --- | ---: |
| Release build, `-warnaserror` | exit 0, 0 warnings |
| Core/offline suite | 45/45 |
| Python report suite | 64 tests OK |
| Game-contract harness, client | exit 0 (496 main, 547 gameplay-probe, 324 gameplay-counter, 292 ownership, 202 smelter, 59 dungeon, 32 pre-compression checks) |
| Game-contract harness, dedicated server | exit 0, same counts |

Run twice: once on the wired tree, once after the one fix below. The smelter transpiler is verified against the shipped IL, where the 3600 s clamp is two instructions, not one as the research said; the contract requires the pair.

### Isolated runtime session

Headless client plus dedicated server on the disposable `bp_test_e2854dba07` world with the temporary character, every earlier switch on plus `[Stations] SmelterCatchupBudgetEnabled`, `[Dungeons] SliceRoomSpawnEnabled` and `[CharacterSave] SpeculativeMapCompressionEnabled`. Run root `.qa/runs/20260917T013856Z-67387e`; 193 real save files and the preferences registry verified unchanged.

| Observation | Client | Server |
| --- | ---: | ---: |
| Probe failures / dropped records | 0 / 0 | 0 / 0 |
| `smelter_budget_status` / iterations | installed / 8 | installed / 8 |
| `dungeon_slicing_status` / budget | installed / 4 ms | installed / 4 ms |
| `map_precompress_status` / policy | installed / quiet 2 s, interval 30 s, cap 2 per min | installed |
| Speculative runs / published / worker max | 2 / 2 / 182 ms | 1 / 1 / 210 ms (before the fix) |
| `ownership_caller_split_status` | installed | installed |
| Server release cycles / released / claimed / reclaimed | headless | 46 / 219 / 454 / 0 |
| Clutter patches / ground queries | 124 / 54,633 | headless |

**One fix from this run.** The pre-compression pump ran on the dedicated server, which has a `Minimap` but never saves a character profile: one wasted 210 ms compression and 8 MB. The pump now returns on `ZNet.IsDedicated()`; the gates and the isolated session were rerun on the final DLL (run root `.qa/runs/20260917T014402Z-3cc8a5`: client 2 runs, server 0, no failures, saves and preferences unchanged). The first run also showed the counters are process-cumulative, which the module doc now states.

The headless workload has no smelter and enters no dungeon, so those two modules are proven to install and to leave every other probe untouched, not to act. The clutter counters gave a first real number: about 440 ground raycasts per generated patch in the spawn biome, against the 1,000 to 2,000 the research assumed. The ownership split shows a single-client world produces zero same-cycle reclaims, as expected.

### What the next real session should show

- **Smelter.** `smelter_loop_iterations_max` capped at 8 with `smelter_budget_truncations` above zero on return to base, `SmelterUpdate` peaks well under the 20 to 34 ms of 09-16, and identical bar output over the same time (the accumulator carry).
- **Dungeons.** `dungeon_spawn_sliced` above zero in crypts, `dungeon_slice_ms_max` near the 4 ms budget, `DungeonSpawn` peaks gone from the slow-operation list, no `dungeon_spawn_vanilla_player_inside` unless the player was already inside. A/B in one session with `bp_dungeon off` and `on`.
- **Character save.** `map_precompress_primed_hits` against `map_precompress_stale` decides whether the 30 s policy fits real exploration; `MapSerialization` should drop from 60 to 87 ms toward the disk-only cost on primed saves.
- **Clutter, build mode, ownership.** The new counters attribute the 20 to 60 ms clutter frames (patches per frame versus rebuild-all frames), the 42 ms build-mode spikes (`BuildMenuOpen`, `PieceRemove`, `PieceCopy`) and the server's release-then-reclaim share; each decides whether its optimization is built.
