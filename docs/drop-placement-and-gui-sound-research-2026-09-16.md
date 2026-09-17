# Research: mined-drop placement and the doubled crafting-tab sound

Follow-up to the [2026-09-16 session report](session-report-2026-09-16.md). Both behaviours are vanilla Valheim 1.0.12; neither is caused by BetterPerformance, BetterNetworking or ValheimPlus. Evidence comes from reading the installed game methods named below; nothing here was measured at runtime yet.

### Doubled sound on the Craft / Upgrade tabs

Two effects play on one click:

1. The tab button carries the standard `ButtonSfx` component (`assembly_guiutils`), whose `onClick` listener instantiates the click prefab, throttled by a 2-frame timer shared by all buttons.
2. `InventoryGui.OnTabCraftPressed` and `OnTabUpgradePressed` call `SetActiveGroup(m_uiGroups[3])` with the default `playSound = true`. `SetActiveGroup(int, bool)` then creates `m_setActiveGroupEffects` at the player position even when the crafting group (index 3) was already the active group, which it always is when a tab is clicked.

The same stacking happens on `OnSelectedRecipe` (recipe click) and on the gamepad group cycle, where a real group change makes the second sound intentional. `OnSelectedItem` already passes `playSound: false` for inventory grids. ValheimPlus does not touch these methods; its only `InventoryGui` patches are `Show`, `DoCrafting`, `RepairOneItem`, `SetupRequirement` and `UpdateRepair`.

**Candidate fix, opt-in, client only.** A prefix on the private `InventoryGui.SetActiveGroup(int index, bool playSound)` that sets `playSound = false` when the clamped index equals `m_activeGroup`. Group changes keep their sound; a click on the already-active group loses the redundant one. Cost is a field read per call, no allocation. It must not run when `m_inventoryGroupCycling` is true, because that path wraps the index instead of clamping it. Verification: the game-contract harness checks the method's exact signature and the `m_activeGroup` field, and the 2-frame `ButtonSfx` throttle explains why the doubled sound is audible: the two prefabs are different objects, so the throttle never suppresses the second.

**Measurement first.** No capture metric covers audio. A bounded counter on `EffectList.Create` keyed by the calling method name would show `SetActiveGroup` creations per tab click, and a local-`ZSFX` creation count per frame would show the stacking directly. Either is count-only and fits the gameplay-counter module.

### Copper drops: underground, too high, slow, sometimes invisible

`MineRock5.DamageArea` (the fractured copper and tin veins, `rock4_copper_frac`) spawns each drop at `hitArea.m_collider.bounds.center` plus a 0.3 m random sphere when the prefab field `m_hitEffectAreaCenter` is true, which is its default and which the drops observed this session used. A buried chunk therefore drops the ore under the terrain and a top chunk drops it in the air. The owner's `ItemDrop.SlowUpdate` is scheduled by `InvokeRepeating` 1 to 2 s after creation and every 10 s after that; its `TerrainCheck` lifts the item to ground + 0.5 m only when it is more than 0.5 m under the ground. Items between 0 and 0.5 m under the surface are never lifted and stay invisible until auto-pickup reaches them.

The game already solves this elsewhere: `DropOnDestroyed.OnDestroyed` checks the ground height before spawning and raises the spawn point to ground + 0.1 m, and the older `MineRock.DamageArea` spawns at the hit point pulled 0.2 m toward the attacker. `TreeBase`, `TreeLog` and `LootSpawner` spawn at their own positions.

**Candidate fix A, opt-in, owner side.** A postfix on the static `ItemDrop.OnCreateNew(GameObject, bool)`, which every spawning path calls right after `Instantiate` on the owning process: read the ground height once and, when the item is below ground, move it to ground + 0.5 m and zero the rigidbody velocity, exactly what `TerrainCheck` does 1 to 10 s later. This is generic (all eight callers), costs one ground raycast per drop (585 drops in the whole 09-16 session), replicates naturally because the owner sets the initial ZDO position, and changes nothing for items that spawn above ground. It does not fix "too high".

**Candidate fix B, opt-in, per prefab.** Set `m_hitEffectAreaCenter = false` on `MineRock5` instances at `Awake` for a configured prefab list (default `rock4_copper_frac`, `MineRock_Tin`), so drops and hit effects use the hit point as `MineRock` does. Drops then appear where the pickaxe landed, never buried and never floating, for both players since the owner applies the hit point sent in the `HitData`. It also moves the hit and destroy effects to the hit point, which is a visible change to review on a disposable world before recommending it. The multi-collider damage path sends the same hit point for several areas, which is acceptable.

**What the second player sees.** Her hits on a host-owned vein travel client, server, host; the host spawns the drop and the ZDO returns through the server. Fix A and B shorten nothing on that path; they only make the spawn position correct at creation instead of after the owner's first slow update, which is the part she perceives as "the ore appears late".

### Not covered

No runtime measurement of either fix, no check of console or touch input paths, and no review of other `EffectList` stacking in the GUI. Both fixes are behaviour changes and belong in opt-in modules with their own keys, separate from diagnostics, per the repository rules.

The sound fix and candidate B shipped as opt-in modules in 0.4.7: [GUI group sound and mined-drop placement](gui-sound-and-drop-placement.md). Candidate A was not implemented.
