# Gameplay-loop observations

### What is measured

These diagnostics time existing native calls while a capture is active. They change no inventory, placement, map, vehicle, damage or loot behaviour. They are installed with `Capture.MethodTimings`; a signature that is missing, or a type that cannot be loaded, is reported as unavailable instead of guessed.

Rate classes: **event** runs on a player or network action, **batch** times one `MonoUpdaters` dispatch group, **frame** runs every frame on a small, bounded instance count.

| Timing | Native scope | Meaning | Rate |
| --- | --- | --- | --- |
| `InventoryGuiUpdate` | `InventoryGui.Update` | Whole inventory/crafting GUI frame, one instance | frame |
| `InventoryGridUpdate`, `ContainerGridUpdate` | `InventoryGui.UpdateInventory`, `UpdateContainer` | Rebuild of the player grid and of the open container grid | frame |
| `InventoryGuiShow` | `InventoryGui.Show(Container,int)` | Opening the inventory over a container | event |
| `ContainerInteract` | `Container.Interact` | One chest interaction, including the ownership request | event |
| `ContainerChanged` | `Container.OnContainerChanged` | Reacting to a remote inventory change | event |
| `ContainerCheckForChanges` | `Container.CheckForChanges` | 1 Hz poll per loaded container; carries the inventory deserialization when it changed | event |
| `InventoryAddItem` | `Inventory.AddItem(ItemDrop.ItemData)` | One item insertion | event |
| `InventoryMoveItem` | `Inventory.MoveItemToThis(Inventory,ItemDrop.ItemData)` | One drag/drop move between inventories | event |
| `PlacementUpdate`, `PlacementGhostUpdate` | `Player.UpdatePlacement`, `UpdatePlacementGhost` | Build-mode targeting and ghost refresh, local player only | frame |
| `PiecePlace` | `Player.TryPlacePiece(Piece)` | One placement attempt, including its ghost refresh | event |
| `PieceRemove`, `PieceCopy` | `Player.RemovePiece`, `Player.CopyPiece` | The two other event branches of `UpdatePlacement`; both nest inside it | event |
| `BuildMenuOpen` | `BuildUi.OpenBuildMenu` | Opening the build menu, including the piece-button rebuild it reaches through `SelectPieceList` and `UpdatePieceButtons` | event |
| `BuildGuiUpdate` | `Hud.UpdateBuild` | Build-piece HUD refresh | frame |
| `MinimapUpdate` | `Minimap.Update` | Whole minimap frame, one instance | frame |
| `MinimapExploreUpdate` | `Minimap.UpdateExplore(float,Player)` | Fog-of-war exploration writes | frame |
| `MinimapLargeMapUpdate` | `Minimap.UpdateMap(Player,float,bool)` | Large-map frame, including every pin pass | frame |
| `MinimapSetMapMode` | `Minimap.SetMapMode` | Switching between small, large and hidden map | event |
| `ShipFixedUpdate` | `Ship.CustomFixedUpdate(float)` | One ship's buoyancy/sail/rudder step | frame |
| `VagonUpdate` | `Vagon.Update` | One cart's frame | frame |
| `VagonAttach`, `VagonDetach` | `Vagon.AttachTo`, `Vagon.Detach` | Hooking and releasing a cart | event |
| `TreeDamage`, `TreeLogDamage` | `TreeBase.RPC_Damage`, `TreeLog.RPC_Damage` | Applying one hit to a standing tree or a felled log | event |
| `TreeSpawnLog` | `TreeBase.SpawnLog` | Felling a tree and instantiating its log | event |
| `TreeLogDestroy` | `TreeLog.Destroy(HitData,bool)` | Splitting a log into its drops | event |
| `MineRockDamage`, `MineRockDamageArea` | `MineRock5.RPC_Damage`, `MineRock5.DamageArea` | One ore/rock hit and the per-area work inside it | event |
| `DestructibleDamage`, `DestructibleDestroy` | `Destructible.RPC_Damage`, `Destructible.Destroy` | One destructible hit and its destruction | event |
| `WearDamage` | `WearNTear.RPC_Damage` | One hit on a built piece | event |
| `CharacterDamage`, `CharacterApplyDamage` | `Character.RPC_Damage`, `Character.ApplyDamage` | Inbound damage routing and the applied result | event |
| `AttackStart` | `Attack.Start` | Starting one attack, including ammo and stamina checks | event |
| `PieceDropResources` | `Piece.DropResources(HitData)` | Refunding a removed build piece | event |
| `DropTableDrop` | `DropOnDestroyed.OnDestroyed` | One loot spawn, including its drop-table roll | event |
| `SmelterSpawn` | `Smelter.Spawn(string,int)` | Producing one smelter output item | event |
| `CraftingStationBatch`, `SfxBatch`, `InstanceRendererBatch`, `SmokeBatch` | `MonoUpdatersExtra.CustomUpdate` filtered on the matching `MonoUpdaters.Update.*` label | Inclusive dispatch of that update group | batch |
| `FloatingBatch`, `ShipBatch`, `ZSyncTransformBatch`, `ZSyncAnimationBatch`, `CharacterFixedBatch` | `MonoUpdatersExtra.CustomFixedUpdate` filtered on the matching `MonoUpdaters.FixedUpdate.*` label | Inclusive dispatch of that fixed-update group | batch |
| `ItemDropSlowUpdate`, `ItemAutoStack` | `ItemDrop.SlowUpdate`, `ItemDrop.AutoStackItems` | 0.1 Hz poll per dropped item, and the stacking it triggers | event |
| `PickableInteract` | `Pickable.Interact` | Picking one berry, mushroom or similar | event |
| `PlayerUpdate`, `PlayerFixedUpdate` | `Player.Update`, `Player.FixedUpdate` | One `Player` component's frame; remote players are included | frame |
| `HudUpdate` | `Hud.Update` | Whole HUD frame, one instance | frame |
| `ClutterLateUpdate` | `ClutterSystem.LateUpdate` | Grass and clutter patch maintenance, one instance | frame |
| `ClutterGeneratePatches` | `ClutterSystem.GeneratePatches` | The per-frame ring sweep over the patch grid, including any patch it admits | frame |
| `ClutterGenerateVegPatch` | `ClutterSystem.GenerateVegPatch` | Building one clutter patch: the ground queries and the prefab instantiation | event |
| `WaterStaticUpdate` | `WaterVolume.StaticUpdate` | Static water-time step, once per frame | frame |

### Nesting

These scopes overlap by construction. `ClutterLateUpdate` contains `ClutterGeneratePatches`, which contains at most one `ClutterGenerateVegPatch` in the normal path and up to 121 in a forced rebuild. `PlacementUpdate` contains `BuildMenuOpen`, `PieceRemove` and `PieceCopy`, which is the point of those three: their maxima are what a 41.7 ms `PlacementUpdate` frame has to be made of. `ShipBatch` contains `ShipFixedUpdate` calls, `PiecePlace` contains a `PlacementGhostUpdate`, `MinimapLargeMapUpdate` contains every pin pass, `MineRockDamage` contains `MineRockDamageArea`, `CharacterDamage` contains `CharacterApplyDamage`, and `ItemDropSlowUpdate` contains `ItemAutoStack`. Never add their sums or maxima to obtain exclusive CPU time. Failed native calls keep their exceptions and are counted in `failedCalls`.

### What is deliberately not hooked

`Player.PlacePiece` runs inside `TryPlacePiece`; `Minimap.UpdatePins` and `UpdateDynamicPins` run inside `UpdateMap`; `Smelter.SpawnProcessed` calls the hooked `Spawn(string,int)`; `DropTable.GetDropList` runs inside `DropOnDestroyed.OnDestroyed`. Hooking any of them would double count. `InventoryGui.OnSelectedItem` is a UI event wrapper, so the move itself is timed at `Inventory.MoveItemToThis` instead. `Character.CustomFixedUpdate` is already the content of `CharacterFixedBatch`.

Two methods the plan assumed do not exist in Valheim 1.0.12. `Vagon` has no `FixedUpdate` or `CustomFixedUpdate`, so `VagonUpdate` is registered instead. `TreeBase` has no `Destroy` or `RPC_Destroy`, so `TreeSpawnLog` covers the log-spawn path. Both absences are asserted by the verifier, so a future build that adds them fails the test rather than silently drifting.

### Cost and compatibility limits

Each probe adds a Harmony prefix/finalizer pair. Frame-rate probes are restricted to methods with a small instance count: one GUI, one HUD, one minimap, one clutter system, one static water step, a few vehicles, and the loaded `Player` components. The eight batch labels share two patches, one per `MonoUpdaters` dispatch method, selected by a dictionary on the profiler-scope string. This is a bounded design, not a claim of measured zero overhead.

The export record carries one summary per metric whether or not it was sampled, so this stream grows every interval record by roughly half again. A 30-minute capture at the default two-second interval stays well inside the 64 MiB default `Capture.MaxFileMiB`, but the headroom is smaller than before. The offline segment-export test needed its deliberately tiny file bound raised for the same reason. Other mods can replace or disable these paths; status labels and probe failures are part of interpretation.

### Verification scope

`GameplayProbesGameTests.Run` reads every signature from the shipped `assembly_valheim.dll` through Mono.Cecil, so the contract is proven for types that a standalone CLR cannot load as well as for those it can. It verifies the declaring type, the exact parameter types, the return type, the static/instance shape and the presence of a managed body; that exactly one overload matches; that each metric is exportable and was appended after the previously last member, since histogram order is positional; that each batch label occurs in exactly one method of the shipped assembly and is passed to the expected `MonoUpdatersExtra` entry; that the batch prefixes bind the profiler scope at argument index 2 and cannot skip the original; and that every deliberately skipped method still exists and is still unhooked.

Registration is asserted at runtime only where the types load in this process. `Player`, `Character`, `Humanoid`, `Ship`, `ZSyncAnimation`, `CharacterAnimEvent` and `VisEquipment` implement `IMonoUpdater`, which carries default interface methods, so 16 probes are reported as `STATIC ONLY` and must report `unavailable` with no registration offline. No game method is invoked by these tests, and separately authorized runtime validation must report its own results.
