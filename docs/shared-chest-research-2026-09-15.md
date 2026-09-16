# Shared chest: two players in one container at the same time

Research snapshot: 2026-09-15. Research only. No game launched, no code changed, nothing committed. Decompiled Valheim 1.0.12 (exact installed build) and ValheimPlus 0.10.1.2 were read here; no game source was copied into this repository. VERIFIED = read in the file and method named on the line. REPORTED = someone else's claim, not reproduced here.

## Verdict

**Adopt MultiUserChest's design; do not invent one.** The feature is solved prior art, MIT-licensed, 702 K downloads, and 1,435 mods depend on it. Its central insight is not "remove the in-use check" — it is **keep exactly one owner and route every item operation to that owner as a request/response RPC**. The second opener is granted a GUI but is *not* given ownership, so the whole-inventory write stays single-writer.

Recommendation: **do not reimplement inside ValheimPlus.** Ship a ValheimPlus compatibility shim and recommend MultiUserChest, or vendor it under MIT attribution only if a hard dependency on Jötunn is unacceptable. Reimplementing is ~3-5 weeks of work whose failure mode is silent item duplication in players' saves. Effort/risk detail in the last section.

## Mechanism: how vanilla forbids it

**The in-use flag is a client-local bool, mirrored into the ZDO for visuals only.** `Container.cs` holds `private bool m_inUse;`. `SetInUse` mutates it only on the owner — `if (m_nview.IsOwner() && m_inUse != inUse)` — and `UpdateUseVisual` writes `m_nview.GetZDO().Set(ZDOVars.s_inUse, m_inUse ? 1 : 0)` on the owner, while non-owners only *read* that int to animate the lid. (VERIFIED: `Container.cs` `SetInUse`, `UpdateUseVisual`.)

**Who sets and clears it.** `InventoryGui.cs` sets it on show — `if ((bool)m_currentContainer && m_currentContainer.IsOwner()) { m_currentContainer.SetInUse(inUse: true); }` (line ~700) — and clears it in two places on hide, `m_currentContainer.SetInUse(inUse: false); m_currentContainer = null;` (lines ~1071 and ~1093). (VERIFIED: `InventoryGui.cs`.)

**The refusal path.** `Container.Interact` always routes: `m_nview.InvokeRPC("RPC_RequestOpen", playerID)`. The owner's handler refuses with `if ((IsInUse() || ((bool)m_wagon && m_wagon.InUse())) && uid != ZNet.GetUID()) { m_nview.InvokeRPC(uid, "RPC_OpenResponse", false); }`, and the requester surfaces `Player.m_localPlayer.Message(MessageHud.MessageType.Center, "$msg_inuse")`. The same three-branch shape is repeated verbatim in `RPC_RequestStack` and `RPC_RequestTakeAll`. (VERIFIED: `Container.cs`.)

**Grant transfers ownership.** The success branch is `ZDOMan.instance.ForceSendZDO(uid, ...); m_nview.GetZDO().SetOwner(uid); m_nview.InvokeRPC(uid, "RPC_OpenResponse", true);`. The opener *becomes* the owner. (VERIFIED: `Container.cs` `RPC_RequestOpen`.)

**Stuck flag on disconnect/crash.** `m_inUse` is not persisted — it is a field on a `MonoBehaviour`, and the ZDO copy is only a visual mirror. When the holder disconnects, its `Container` instance dies with the client; the next client to instantiate the prefab starts with `m_inUse = false`. The stale ZDO int only leaves a chest rendered open until the new owner's `CheckForChanges` → `UpdateUseVisual` rewrites it (`InvokeRepeating("CheckForChanges", 0f, 1f)`). So vanilla has no permanent lock-out, at the cost of no lock at all across a reconnect. (VERIFIED: `Container.cs` `Awake`, `CheckForChanges`, `UpdateUseVisual`.)

**Persistence is a whole-inventory blob written by the owner.** `OnContainerChanged` fires on any `Inventory.Changed()` and does `if (!m_loading && IsOwner()) { Save(); }`; `Save` serializes the entire inventory into one `ZPackage` and stores it as a byte array: `m_inventory.Save(zPackage); ... m_nview.GetZDO().Set(ZDOVars.s_items, array);`. There is no per-slot delta. (VERIFIED: `Container.cs` `OnContainerChanged`, `Save`. Correction to the brief: `s_items` is a `byte[]`, not a string, in 1.0.12.)

**The reader is suppressed while the GUI is open.** `Load()` opens with two early returns: `if (m_nview.GetZDO().DataRevision == m_lastRevision) return false;` and **`if (m_inUse) return false;`**. That second line is the load-bearing one for everything below: a player with the chest open never re-reads the ZDO. (VERIFIED: `Container.cs` `Load`.)

## Why naive removal duplicates items

Delete the in-use branch from `RPC_RequestOpen` and both openers become owner in turn, each holding a stale `m_inventory` that `Load()` refuses to refresh, and each `Save()` rewriting the whole blob.

Chest holds one stack of 40 Wood at slot (0,0). A opens, then B opens.

| T | Player A (first owner) | Player B | ZDO `s_items` |
| --- | --- | --- | --- |
| 0 | `RPC_RequestOpen` granted, `SetOwner(A)`, `m_inUse = true` | — | 40 Wood |
| 1 | GUI shows 40 Wood; `Load()` now returns early on `m_inUse` | `RPC_RequestOpen` granted, `SetOwner(B)`, `ForceSendZDO` | 40 Wood |
| 2 | still owner in its own mind; `IsOwner()` is now false | GUI shows 40 Wood, `m_inUse = true`, `Load()` suppressed | 40 Wood |
| 3 | — | takes 40 Wood → `Changed()` → `Save()` writes empty | **empty** |
| 4 | takes 40 Wood from its stale view; `Changed()` fires | B's inventory: +40 Wood | empty |
| 5 | A regains ownership on any `ClaimOwnership`/`ReleaseNearbyZDOS` pass, `Save()` writes **its** stale blob | — | **40 Wood** |
| 6 | A's inventory: +40 Wood | B's inventory: +40 Wood | 40 Wood in chest |

80 Wood exist where 40 did, and the chest still shows 40. The mirror case loses items: reverse steps 3 and 5 and B's deposit is overwritten by A's blob. Both failures come from one cause — **two stale whole-blob writers**, not from the absence of a lock.

Ownership churn makes step 5 routine rather than rare: `ReleaseNearbyZDOS` runs every 2 s per peer and reassigns boundary objects, measured at ~20 real `SetOwner` transitions per second at a shared base (`docs/ownership-latency-research-2026-09-15.md`, VERIFIED there against `ZDOMan.cs`).

## Correct design

One rule: **the ZDO owner is the only writer, and it never yields ownership while a second GUI is open.**

1. **Open without transfer.** Patch `RPC_RequestOpen` so that when the container is already in use by someone else, it replies `true` *without* `SetOwner`. The second opener gets a GUI over a replicated, read-only view.
2. **Every mutation is a request.** Take, put, move-within-chest, split, stack, drop, and consume become routed RPCs carrying `(requestId, fromPos, toPos, itemHash, amount)` addressed to the container ZDO. The owner applies them against its authoritative `Inventory` and replies with a response package.
3. **Requesters apply only responses.** The requester removes from its own inventory at request time (so the item is never in two inventories), blocks the affected slot, and commits or rolls back on the response. A failed or lost response must return the item, never silently drop it.
4. **Slot blocking, not state hashes.** A per-inventory blocked-slot map is enough; it prevents a second request against a slot with one in flight. An `expected state hash` over the whole inventory would fail every concurrent request, which is worse UX than per-slot arbitration.
5. **Non-owner GUI refreshes from replication.** The non-owner's `m_inUse` is false, so its `Load()` still runs on each `DataRevision` bump and the grid updates.
6. **Ownership hand-off.** If ownership moves while requests are in flight, the new owner must answer them. A request whose target `ZNetView` is not owned by the receiver must return an explicit failure response that restores the item, never be dropped.

## Prior art: MultiUserChest

Repository is **`MSchmoecker/No-Chest-Block`** (the mod ships as MultiUserChest); the URL in the brief 404s. **License: MIT** (VERIFIED via GitHub API `license.spdx_id`). v0.6.2, "Updated for Valheim 1.0". 702.2 K downloads, 1,435 dependent mods. Requires BepInEx **and Jötunn**, on server and every client, same version or the connection is refused.

It implements exactly the design above, and its open patch is the confirmation: `ContainerRPC_RequestOpenPatch` replies `InvokeRPC(uid, nameof(Container.RPC_OpenResponse), true)` and **returns before `SetOwner`** when `IsContainerInUse(__instance, uid)`. The same shape is applied to `RPC_RequestStack`. (VERIFIED: `MultiUserChest/Patches/ContainerPatch.cs`.)

Covered, from source read here:

- **Five request types** with matching responses: `MUC_RequestItemAdd`, `MUC_RequestItemRemove`, `MUC_RequestItemMove` (reorder *within* the chest), `MUC_RequestItemConsume`, `MUC_RequestItemDrop` (`ContainerPatch.cs` constants; `ContainerRPCHandler.cs` handlers).
- **Split stacks**: `PossibleDragAmount` clamps a partial drag against the target slot's free stack space and returns 0 when the target cannot stack (`ContainerHandler.cs`).
- **Local fast path**: when the requester *is* the owner, the request is applied in-process and the response handed straight to `InventoryHandler`, skipping the network (`ContainerHandler.AddItemToChest`).
- **Slot blocking + optimistic preview**: `InventoryBlock` is a refcounted `Dictionary<Vector2i,int>` of blocked slots; `InventoryPreview` shows the pending result before the response lands (`InventoryBlock.cs`, changelog 0.5.0).
- **Item loss recovery**: a rejected removal re-adds via a temp inventory and drops on the ground if it will not fit (`InventoryHandler.RPC_RequestItemRemoveResponse`).
- **Unowned containers ignored**: `!container.m_nview.HasOwner()` short-circuits, and changelog 0.4.3 says distant containers "have no player assigned (owner) responsible for managing their inventory and will be ignored".
- **Carts and ships**: `m_wagon.InUse()` is carried through `IsContainerInUse`; changelog 0.5.9 "Fixed issues with ships and carts", 0.4.4 excludes multi-chest OdinShipPlus vessels.
- **Opt-out for other mods**: a `MUC_Ignore` ZDO bool (changelog 0.6.0).

Not covered / known rough edges (REPORTED, from the changelog and Thunderstore page):

- **Incompatible with QuickStore, QuickStack, SimpleSort.** Compatible quick-stack path is *Quick Stack Store Sort Trash Restock* (v1.3.10+), plus SmartContainers, QuickDeposit, DepositAnywhere, Valheim Simple Auto Sort.
- **Crafting stations pulling from nearby chests is not addressed** — no patch for it in the tree, and it is a *read* path, so it sees the replicated blob with normal latency rather than a request/response.
- **Vanilla take-all / stack-all are left on the vanilla grant path**, patched only to answer `true` while in use.
- History of real duplication bugs now fixed: 0.4.2 (repeated move of a non-stackable), 0.4.4 (AdventureBackpacks), 0.5.0 (stack size changed underneath a removal), 0.5.6 (a mod patching `AddItem` inconsistently across clients). Expect this class of bug in any reimplementation.

**License**: ValheimPlus is **AGPL-3.0** (`LICENSE.md`, "GNU AFFERO GENERAL PUBLIC LICENSE Version 3"). MIT is one-way compatible into AGPL-3.0: MultiUserChest code may be vendored into ValheimPlus provided the MIT notice and copyright are retained on the vendored files and the whole result ships AGPL. The reverse is forbidden — no ValheimPlus code may go upstream into MultiUserChest. Vendoring also drags in the Jötunn dependency or a port away from it.

## Interaction with what we already ship

- **ValheimPlus AutoStack** reads the mirror directly: `chest.m_nview.GetZDO().GetInt(ZDOVars.s_inUse) == 1` decides whether a refusal counts as `skippedInUse` or `chestsMissed` (`AutoStackSweep.cs:111`), and `StackInto` calls `chest.Load()` after an `IsOwned` check. Under a shared-chest mod the grant becomes unconditional, so this classification goes dead and the sweep may write through a non-owner path. This is the highest-risk interaction and needs its own test.
- **Chest row/column resizing** (`Container_Awake_Patch`) is orthogonal; MultiUserChest advertises compatibility with size mods.
- **Crafting/machine access to nearby chests** (`Inventory_NearbyChests_Cache`, `Player_HaveRequirementItems_Transpiler`, `Smelter_*`, `Fermenter_*`, `Beehive_*`) are read paths plus owner-side writes. They do not open a GUI and do not set `m_inUse`, so they are unaffected by the open patch but *are* affected by a chest whose owner no longer changes on open.
- **Our owner-grant expedite** (`docs/ownership-expedite.md`) helps here rather than conflicting: a shared chest keeps one stable owner, which removes exactly the churn the expedite works around. The two changes point the same direction.

## Test plan (disposable world, two clients, dedicated server)

Invariant for every case: **total count per item type across player A, player B, the chest, and the ground is unchanged, and every item traces to one origin.** Verify by counting before and after, not by eyeballing the grid.

1. Concurrent take of the same stack: both click the same 40-Wood slot within one frame. Expect one winner, one clean refusal, 40 Wood total.
2. Simultaneous deposit into the same empty slot: expect one placed, one relocated or refused, none lost.
3. Drag-reorder by both at once, crossing slots: expect a consistent final grid on both GUIs after one second.
4. Split: A splits 40 into 20 while B takes the same stack.
5. Take-all by A while B deposits into the chest.
6. Disconnect mid-operation: kill B's client between request and response; A's chest must stay consistent and B's items must survive the reconnect.
7. Ownership hand-off mid-session: walk the owner out of the active area so `ReleaseNearbyZDOS` reassigns, with a GUI open on both sides.
8. AutoStack sweep fired by A while B has the chest open.
9. Crafting station pull while B holds the chest open.
10. Add and remove the mod from a populated save; chests must load intact both ways.

Automate what does not need the game: a console harness mirroring `tests/MapPinSync/` (`Program.cs` + `Stubs.cs`, net8.0) can round-trip request/response packages and drive a stubbed inventory through concurrent request sequences.

## Effort and risk

| | |
| --- | --- |
| Reimplement inside ValheimPlus | 3-5 weeks, high risk |
| Compatibility shim + recommend MultiUserChest | 2-4 days, low risk |
| Vendor MultiUserChest under MIT into ValheimPlus | 1-2 weeks, medium risk |

The failure mode of getting this wrong is silent duplication in other people's saves, discovered days later. MultiUserChest needed six point releases to find four distinct duplication bugs, all in paths a fresh implementation would also have to get right. Our comparative advantage is performance work, not inventory consensus.

**Recommendation: ship the shim.** Make ValheimPlus's AutoStack and nearby-chest paths correct in the presence of MultiUserChest, document the combination, and leave the consensus protocol to the mod that already has 1,435 dependents proving it. Vendor only if a hard requirement forbids Jötunn.

## Sources

Decompiled, read here: `Container.cs`, `Inventory.cs`, `InventoryGui.cs`, `ZNetView.cs` (Valheim 1.0.12); `AutoStackSweep.cs`, `Container_Awake_Patch.cs` (ValheimPlus 0.10.1.2). Repos: `D:/GitHub/ValheimPlus` (`LICENSE.md`), `D:/GitHub/ValheimPlus-share-all-pins` (`tests/MapPinSync/`).

- https://github.com/MSchmoecker/No-Chest-Block — source and MIT license
- https://thunderstore.io/c/valheim/p/MSchmoecker/MultiUserChest/ — description, compatibility list, download counts
- https://thunderstore.io/c/valheim/p/MSchmoecker/MultiUserChest/changelog/ — known issues and fixed duplication bugs
- https://www.nexusmods.com/valheim/mods/1766 — Nexus listing
- `docs/ownership-latency-research-2026-09-15.md`, `docs/ownership-expedite.md` — ownership churn measurements
