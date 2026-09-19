# GUI group sound and mined-drop placement

Two independent opt-in modules, both default off, added in 0.4.7. Each reproduces a
decision the vanilla code already makes; neither adds a new behaviour of its own.
Mechanism and evidence: drop-placement and GUI-sound research.

## GUI group-sound deduplication

### Mechanism

`InventoryGui.SetActiveGroup(int index, bool playSound)` creates `m_setActiveGroupEffects`
whenever `playSound` is true, including when `index` is already `m_activeGroup`. The Craft
and Upgrade tabs and a recipe click all call `SetActiveGroup(m_uiGroups[3])` with the
default `playSound = true`, and index 3 is already active in every one of those cases, so
the group sound stacks on the button's own `ButtonSfx` click sound.

A Harmony prefix on the private int overload clamps the requested index the same way the
native code does and, when the result equals `m_activeGroup`, sets `playSound = false`.
Nothing else in the method changes: the group is still assigned, every `UIGroupHandler` is
still updated, and the touch selection is still cleared.

### Invariants

- A real group change keeps its sound. Only the already-active case is suppressed.
- The button click sound is untouched: `ButtonSfx` is never patched.
- The `m_inventoryGroupCycling` path wraps the index instead of clamping it, so the prefix
  returns immediately and that path is exactly vanilla.
- The prefix returns void, so it can never skip the original method, and it only ever
  writes the `playSound` argument.
- `SetActiveGroup(UIGroupHandler, bool)` is not patched; it forwards to the int overload.
- An exception inside the prefix is caught and counted, leaving `playSound` as the caller
  passed it.

### Configuration

| Key | Default |
| --- | --- |
| `[Gui] DeduplicateGroupSoundEnabled` | `false` |

Requires a restart to install. Counters export whether or not the module installed.

### Telemetry

Gauges `gui_group_sound_suppressed`, `gui_group_sound_kept`, `gui_sound_probe_failures`
(all in calls, drained per interval). Labels `gui_sound_dedup_status`
(`disabled` | `installed` | `unavailable` | `patch_failed`) and `gui_sound_dedup_enabled`.

### Verification

The game-contract harness reads `InventoryGui` from assembly metadata, because the type
cannot be loaded outside Unity. It checks the exact private signature of the int overload,
the four fields the prefix and the native sound path use, that the forwarding overload
calls the int one, that the three click paths still call a `SetActiveGroup` overload, that
the int overload still clamps and still reaches `EffectList.Create`, and that the plugin
prefix is a void method with exactly one by-reference bool.

## Mined-drop placement at the hit point

### Mechanism

`MineRock5.DamageArea` computes one position and uses it for the drops, the hit effect, the
destroy effect, the damage text and the origin of the 10 m `AddNoise` player search:

```
vector = (m_hitEffectAreaCenter && hitArea.m_collider != null)
    ? hitArea.m_collider.bounds.center
    : hit.m_point;
```

`m_hitEffectAreaCenter` is a public bool prefab field that defaults to true, so a buried
chunk drops its ore underground and a top chunk drops it in the air. `ItemDrop.SlowUpdate`
only lifts an item that is more than 0.5 m below ground, and only 1 to 10 s later.

A Harmony postfix on `MineRock5.Awake`, the lifecycle method that registers `RPC_Damage`,
sets that field to false on instances whose prefab name is in the configured list. The
older `MineRock` already spawns at the hit point, so this selects between two positions the
native code supports rather than computing a new one.

### Invariants

- The module writes one public bool field and nothing else. RPC format, ownership, drop
  tables, damage, resistances and tool-tier checks are untouched.
- Owner side only in effect: the owner runs `DamageArea` and writes the initial ZDO
  position, so a remote client needs no plugin.
- Instances whose prefab is not listed are never touched, and the original field value is
  remembered per instance and restored on a runtime toggle or on uninstall.
- The structural-collapse path is unchanged: `CheckSupport` builds its own `HitData` whose
  `m_point` is already the chunk centre, so its drops land where they did before.
- Tracking is bounded at 4096 live instances, with destroyed instances pruned. An instance
  past the cap keeps vanilla placement and is counted in `mining_hitpoint_instances_capped`.
- `mining_hitpoint_instances_applied` counts field overrides on this process, not mining
  events: `DamageArea` runs on the rock's owner, so a rock another client owns is unaffected
  by this process's override however healthy the counter reads.

### Configuration

| Key | Default |
| --- | --- |
| `[Mining] DropAtHitPointEnabled` | `false` |
| `[Mining] DropAtHitPointPrefabs` | `rock4_copper_frac` |

Requires a restart to install. The console command `bp_mining on | off | status` flips the
override on every tracked live instance in the running process, so one session can A/B the
result; it does not write configuration and does not reach another process.

### Telemetry

Gauges `mining_hitpoint_instances_applied`, `mining_hitpoint_instances_restored`,
`mining_hitpoint_instances_seen`, `mining_hitpoint_probe_failures`. Labels
`mining_hitpoint_status`, `mining_hitpoint_enabled` and `mining_hitpoint_prefabs`.
`instances_seen` counts every `MineRock5` observed, not only the listed prefabs, so the
ratio shows how much of the world the list covers. Label `mining_hitpoint_seen_prefabs`
lists up to eight observed `MineRock5` prefab names with the vanilla `m_hitEffectAreaCenter`
each carried at `Awake`, formatted `name=true;name=false`, which separates a configured name
that never matched from a prefab that already drops at the hit point.

### Verification

The harness reads `MineRock5` from assembly metadata. It checks that
`m_hitEffectAreaCenter` is a public instance bool, that `HitData.m_point` is the alternative
position, that `Awake` still registers `RPC_Damage`, that `RPC_Damage(long, HitData, int)`
keeps its private instance shape, that `DamageArea` still reads the field, the hit point,
the collider bounds centre and the drop list, that `CheckSupport` still calls `DamageArea`
with its own `m_point`, and that the module only ever writes `m_hitEffectAreaCenter`.

## What stays unproven

- The audible result of the GUI change. No capture metric covers audio; the counters prove
  that the suppression happened, not that the session sounds right.
- The runtime look of the hit and destroy effects at the pickaxe impact point instead of
  the chunk centre. That needs a disposable-world A/B with `bp_mining` in one session, on a
  fractured copper vein, watching where the effects and the drops land for a buried chunk
  and a top chunk.
- Whether the change is visible to a second player. The owner writes the spawn position, so
  it should be, but that has not been observed.
- Neither module is measured for cost. Both do a few field reads per event and allocate
  nothing on their hot paths.
