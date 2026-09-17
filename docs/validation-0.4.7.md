# Validation: 0.4.7

2026-09-16 (local). Two opt-in modules (GUI group-sound deduplication, mined-drop placement at the hit point) and one fix (minimap texture cache no longer refuses the plugin's own timing probe). Built against Valheim 1.0.12 / Unity 6000.0.75f1. Mechanism and evidence: [gui-sound-and-drop-placement.md](gui-sound-and-drop-placement.md), [drop-placement research](drop-placement-and-gui-sound-research-2026-09-16.md), [session report 2026-09-16](session-report-2026-09-16.md).

### Offline gates

| Gate | Result |
| --- | ---: |
| Release build, `-warnaserror` | exit 0, 0 warnings |
| Core/offline suite | 44/44 |
| Python report suite | 64 tests OK |
| Game-contract harness, client | exit 0 (496 main, 497 gameplay-probe, 29 minimap cache, 30 GUI sound, 27 mined-drop checks) |
| Game-contract harness, dedicated server | exit 0, same counts |

The two new suites read `InventoryGui` and `MineRock5` through Mono.Cecil metadata because a standalone CLR cannot load those types. A negative control on the minimap accepted-owner check (expected set length forced to 3) failed the harness with its own message; reverted.

### Isolated runtime session

Headless client plus dedicated server on the disposable `bp_test_e2854dba07` world with the temporary character, every 0.4.5 switch on plus `[Gui] DeduplicateGroupSoundEnabled` (client) and `[Mining] DropAtHitPointEnabled` (both), minimap cache in verified mode. Run root `.qa/runs/20260917T004819Z-a6b28d`; 193 real save files and the preferences registry verified unchanged.

| Observation | Client | Server |
| --- | ---: | ---: |
| Probe failures / dropped records | 0 / 0 | 0 / 0 |
| `gui_sound_dedup_status` | installed | disabled (not configured) |
| `mining_hitpoint_status` / prefabs | installed / `rock4_copper_frac` | installed / same |
| `minimap_cache_status` | installed | disabled_at_startup |
| Module probe failures | 0 | 0 |

The headless workload opens no GUI, mines nothing and never generates the world map, so every new counter stayed at zero and `minimap_cache_result` is `none`. The run proves that both modules install on the current game, that the plugin starts with them, and that nothing else regressed. It does not prove their effect.

### What stays unproven, and how the next real session proves it

- **Sound.** `gui_group_sound_suppressed` should count each Craft/Upgrade tab and recipe click, `gui_group_sound_kept` each real group change. The audible result is the player's call. Note that the module also affects four inventory-group call sites and the two gamepad plus/minus sites when they target the already-active group; that is the same rule applied per call.
- **Drops.** Near a copper vein, `mining_hitpoint_instances_seen` and `_applied` must be above zero. A/B in one session on a disposable world: `bp_mining off`, hit a buried chunk and a top chunk, then `bp_mining on` and repeat; watch where the hit and destroy effects, the damage text and the ore land. If the effects at the impact point look wrong, the module can stay off, or the prefab list can be narrowed.
- **Minimap cache.** The 09-16 session reported `foreign_generate_patch` at join. The fix is verified by contract only; the first real join should now report `minimap_cache_result` `stored` after native generation and `served` on the following join with the same seed, mods and versions, with `WorldMapGenerate` recording the serve time instead of about 6.8 s.
