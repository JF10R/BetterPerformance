# Self-contained implementation prompt: ValheimPlus shared-chest compatibility

Read `docs/shared-chest-research-2026-09-15.md` before starting. It carries the decompiled evidence behind every claim here.

Scope decision made before you start, do not relitigate it: **you are not writing a chest consensus protocol.** MultiUserChest (repo `MSchmoecker/No-Chest-Block`, MIT, 702 K downloads, 1,435 dependents) already implements it correctly and has burned four distinct duplication bugs out of it across six releases. Your job is to make ValheimPlus correct and useful *alongside* that mod, and to add the one vanilla-side change that is safe on its own.

Impact estimate: no frame-time win. This is a correctness and quality-of-life change. The measurable defect today is that ValheimPlus's Auto Stack silently miscounts and can write through a container it does not own when a shared-chest mod is present.

---

You are implementing changes in a fork of ValheimPlus (https://github.com/Grantapher/ValheimPlus), a BepInEx 5 / Harmony mod for Valheim 1.0.12, C# targeting .NET Framework 4.7.2. Branch from the `0.10.1.2` tag. ValheimPlus is AGPL-3.0; any MIT code you vendor keeps its MIT header and copyright line, and the combined work ships AGPL.

## Background facts you must not re-derive

- `Container.m_inUse` is a plain client-side `bool`. Only the owner mutates it (`SetInUse` guards on `m_nview.IsOwner()`), and `UpdateUseVisual` mirrors it into `ZDOVars.s_inUse` for lid animation only.
- `Container.Load()` returns early on **`if (m_inUse) return false;`**. A player with the GUI open never re-reads the ZDO.
- `Container.Save()` writes the **whole** inventory as one `byte[]` into `ZDOVars.s_items`. There is no per-slot delta.
- `Container.RPC_RequestOpen`'s grant branch does `ForceSendZDO` then `SetOwner(uid)` then `RPC_OpenResponse true`. Granting an open **transfers ownership**.
- Therefore two simultaneous owners with suppressed `Load()` and whole-blob `Save()` duplicate or destroy items. The race timeline is in the research doc; reproduce it in a test, do not re-reason about it.

## Files

- `ValheimPlus/GameClasses/AutoStackSweep.cs`: `Pending`/`Granted`/`CutOff` dictionaries, the grant/refusal handler around line 111 that reads `chest.m_nview.GetZDO().GetInt(ZDOVars.s_inUse) == 1`, `StackInto(Container, Player)` with its `IsOwned(chest)` guard and `chest.Load()` call, `Describe(chest)`.
- `ValheimPlus/GameClasses/Inventory_NearbyChests_Cache.cs`, `Player_HaveRequirementItems_Transpiler.cs`, `Player_HaveRequirements_Transpiler.cs`: the read paths that let crafting pull from nearby chests.
- `ValheimPlus/GameClasses/Container_Awake_Patch.cs`: chest row/column resizing. Orthogonal, but its `m_width`/`m_height` writes must stay ahead of any GUI patch you add.
- `ValheimPlus/Configurations/Sections/`: add config keys here, following the existing section pattern.
- `tests/MapPinSync/` (`MapPinSync.csproj`, `Program.cs`, `Stubs.cs`, net8.0 console): copy this pattern for your test project. Do not modify it.

## PR 1: stop Auto Stack from writing through a chest it does not own

This is correct on its own, with or without any shared-chest mod, and is the only part that touches live item movement.

1. `StackInto` already early-returns on `!IsOwned(chest)`, but it then calls `chest.Load()` and `Inventory.StackAll`, which mutate and trigger `Save()`. Audit every path into `StackInto` and assert ownership immediately before the `StackAll` call, not only at entry — ownership can move between the two (`ReleaseNearbyZDOS` runs every 2 s per peer, ~20 real `SetOwner` transitions/s at a shared base). On loss, abort the stack for that chest and count it as missed.
2. The refusal classifier reads the visual mirror: `chest.m_nview.GetZDO().GetInt(ZDOVars.s_inUse) == 1`. Under a shared-chest mod, opens are granted while that int is 1, so "refused" and "in use" stop correlating. Keep the counter but rename the log line so it says what it measured (the ZDO flag), not what it inferred (why the refusal happened).
3. Do not call `chest.Load()` on a container whose `m_inUse` is true — it is a no-op that hides a stale read behind an apparently successful call. Check and log instead.
4. Unit tests in a new `tests/SharedChest/` console project: drive a stubbed container through (a) ownership lost between entry and `StackAll`, (b) grant received after `CutOff`, (c) refusal with the ZDO flag set and clear. Assert no write happens in (a).

## PR 2: detect MultiUserChest and stand down cleanly

1. Soft-detect the plugin by GUID at `Awake` (BepInEx `Chainloader.PluginInfos`). Never add a hard assembly reference and never take a Jötunn dependency.
2. When present, honour its opt-out contract: it reads a `MUC_Ignore` ZDO bool on a container's `ZNetView` to exclude that container. If ValheimPlus ever needs a container excluded, set that flag rather than patching around it.
3. When present, disable ValheimPlus's own Auto Stack grant-tracking state machine's assumption that a refusal means "in use", and route Auto Stack deposits through the same request path the shared-chest mod uses if one is exposed; if none is exposed, restrict Auto Stack to containers ValheimPlus owns and say so in the log at startup once.
4. Add a config key `sharedChestCompatibility` (default `true`) under the Inventory section, documented in `valheim_plus.cfg`.
5. Write the compatibility matrix into `COMPATIBILITY.md`: compatible with MultiUserChest and with Quick Stack Store Sort Trash Restock v1.3.10+, SmartContainers v1.6.4, QuickDeposit v1.0.1, DepositAnywhere v1.2.0, Valheim Simple Auto Sort v1.0.7; incompatible with QuickStore, QuickStack, SimpleSort (these break MultiUserChest, not ValheimPlus).

## PR 3, optional and only if PR 1 and PR 2 land clean: native shared chest

Do not start this unless the maintainers explicitly ask for a Jötunn-free implementation. If they do, implement MultiUserChest's design, not a new one.

**The protocol.** Five routed RPC pairs registered on the container's `ZNetView`, each carrying a `ZPackage`:

| Request | Payload | Response |
| --- | --- | --- |
| `VP_ChestAdd` | `requestId, toPos, serialized item, allowSwitch` | `requestId, success, inventoryPos, amount, switchItem` |
| `VP_ChestRemove` | `requestId, fromPos, toPos, amount, switchItem` | `requestId, success, responseItem, amount` |
| `VP_ChestMove` | `requestId, fromPos, toPos, itemHash, amount` | `requestId, success` |
| `VP_ChestConsume` | `requestId, pos, itemHash` | `requestId, success, item` |
| `VP_ChestDrop` | `requestId, pos, amount` | `requestId, success, item` |

**The invariants, in priority order:**

1. **One writer.** Patch `RPC_RequestOpen` so that when the container is in use by someone else it replies `RPC_OpenResponse true` and **returns before `SetOwner`**. The second opener gets a GUI over the replicated view and owns nothing. Apply the identical change to `RPC_RequestStack`.
2. **No item exists twice.** The requester removes the item from its own inventory **at request time**, before the RPC goes out, and the item lives only inside the in-flight request until a response commits or rolls it back.
3. **No item vanishes.** Every failure path returns the item: into the originating slot if free, else any free slot, else dropped on the ground at the player. A response that cannot be applied drops rather than discards.
4. **Per-slot arbitration, not whole-inventory hashes.** A refcounted blocked-slot map per inventory. A second request against a blocked slot fails immediately and locally. Do not send an expected-state hash over the whole inventory; it would fail every concurrent request.
5. **Unowned containers are inert.** `!m_nview.HasOwner()` short-circuits every request to a local failure. A distant container has no one to arbitrate.
6. **Ownership hand-off answers in flight.** A request arriving at a receiver that does not own the target must send an explicit failure response that restores the item. Never drop it silently.
7. **Local fast path.** When the requester is the owner, apply in-process and hand the response straight to the local handler. No network round trip for the common single-player case.

**GUI.** The non-owner's `m_inUse` stays false so its `Load()` still runs on each `DataRevision` bump; the grid follows replication. Show a pending-state preview for in-flight requests, otherwise players double-click and generate duplicate requests.

## Do not

- Do not delete the in-use check from `RPC_RequestOpen` without implementing the full request protocol. That is the exact change that duplicates items, and it looks like it works in a single-player test.
- Do not let two clients call `Container.Save()` against the same ZDO. The write is a whole-inventory blob; the later write wins totally.
- Do not transfer ownership on the second open.
- Do not copy MultiUserChest source without keeping its MIT header and copyright line, and do not send any ValheimPlus code upstream to it (AGPL cannot flow into MIT).
- Do not add a Jötunn dependency to ValheimPlus.
- Do not touch `Container_Awake_Patch`'s sizing logic or the `tests/MapPinSync/` project.
- Do not ship PR 3 on a same-day test. Duplication bugs surface in other people's saves days later.

## Definition of done per PR

Builds against the game assemblies. Unit tests pass. A two-client test on a **disposable** world, with the invariant checked by counting items before and after, never by eyeballing the grid:

**Invariant:** total count per item type across player A, player B, the chest, and the ground is unchanged, and every item traces to exactly one origin.

1. Concurrent take of the same 40-stack by both players within one frame: one winner, one clean refusal.
2. Simultaneous deposit into the same empty slot.
3. Drag-reorder by both at once across crossing slots; both GUIs agree within one second.
4. A splits a stack while B takes the same stack.
5. Take-all by A while B deposits.
6. B's client killed between request and response; A's chest consistent, B's items intact after reconnect.
7. Ownership hand-off mid-session: walk the owner out of the active area with both GUIs open.
8. Auto Stack sweep fired by A while B has the chest open (this is the PR 1 regression case).
9. Crafting station pulls requirements while B holds the chest open.
10. Mod added to and removed from a populated save; chests load intact both ways.

PR description carries the exact base commit, which of the ten cases were run, and the item counts before and after for each.
