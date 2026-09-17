# Build-mode placement cost — where `PlacementUpdate` spends 12–42 ms

Question: where does `Player.UpdatePlacement` spend 12–42 ms in single frames near a 700–860 piece
base, and how could an opt-in module cut it without changing snap results or placement validity.

Evidence base: 2026-09-16 client capture (165 fps) plus a read of the game methods named below.
Game code is cited by method and field name only.

## 1. Verified call tree of one build-mode frame

**Update phase — billed to `PlacementUpdate`.** `Player.UpdatePlacement(bool, float)` runs from the
player update path. Per frame, in build mode:

| Step | Scales with |
| --- | --- |
| `Player.UpdateWearNTearHover` → one `Physics.Raycast`, 50 m, `m_removeRayMask` (7 layers) | collider density along the ray, not piece count |
| `hitInfo.collider.GetComponentInParent<Piece>()` then `GetComponent<WearNTear>()` | hovered prefab depth |
| `WearNTear.Highlight` → `GetSupportColorValue` (ZDO float read), two `MaterialMan.SetValue`, `CancelInvoke("ResetHighlight")`, `Invoke(…, 0.2f)` | one hovered piece |
| `GetHoveringPiece`, `GetComponent<Feast>`, `GetComponent<ItemDrop>` | one piece |
| `GetComponent<IPieceMarker>` on ghost and on hovered piece | two objects |
| `ZInput` polls, scroll and rotation handling, `m_ghostRippleDistance` loop of `Material.SetFloat` | ghost material count |

Nothing in that list iterates the base. **`Piece.GetAllPiecesInRadius` and `Piece.s_allPieces` are not
reached from `UpdatePlacement`.** Neither is any snap-point enumeration.

**Rare branches, also billed to `PlacementUpdate`:** `UpdateBuildGuiInput` → `Hud.TogglePieceSelection`
→ `BuildUi.OpenBuildMenu`; `Player.TryPlacePiece`; `Player.RemovePiece`; `Player.CopyPiece`.

**LateUpdate phase — billed to `PlacementGhostUpdate`, a different probe.**
`Player.UpdatePlacementGhost(bool)` is called from `Player.LateUpdate` and from `Player.TryPlacePiece`.
`UpdatePlacement` never calls it. Everything that scales with base size lives here:

| Step | Cost shape |
| --- | --- |
| `Player.PieceRayTest` | one `Physics.Raycast`, 50 m |
| `StationExtension.FindClosestStationInRange`, `OtherExtensionInRange` | only for extension pieces |
| `m_blockRadius` check | one `Physics.OverlapSphere` + `GetComponentInParent<Piece>` per hit |
| `m_mustConnectTo` check | one `Physics.OverlapSphere` + `GetComponentInParent<ZNetView>` per hit |
| `Piece.GetSnapPoints(point, 10f, …)` inside `FindClosestSnapPoints` | `Physics.OverlapSphereNonAlloc` at 10 m, then `GetComponentInParent<Piece>` and a snap-point enumeration **per nearby piece** |
| `FindClosestSnappoint` | ghost snap points × nearby snap points, distance test each |
| `Location.IsInsideNoBuildLocation`, `PrivateArea.CheckAccess` | location/ward lists |
| `CheckPlacementGhostVSPlayers` | allocates a `List<Character>` every call, 30 m range, `ComputePenetration` per ghost collider × character |
| `TestGhostClipping` (only when `m_noClipping`) | allocating `Physics.OverlapSphere` at 10 m, then `ComputePenetration` for **every ghost collider × every hit collider** |

## 2. Which step is the 20–40 ms one

The measured split refutes the obvious hypothesis. The snap and ward work is the part that scales with
700–860 pieces, and it is all inside `PlacementGhostUpdate`, whose **max is 5.1 ms**. `PlacementUpdate`
peaks at 41.7 ms with a mean of 0.008 ms. So the spike is not `FindClosestSnapPoints`, not a
`PrivateArea`/`Location` scan, not a station-range check, and not an `s_allPieces` walk.

Within `UpdatePlacement` the per-frame body is bounded and small. That leaves the rare branches, and
the shape of the data (52 three-second windows containing one slow frame, against 120,002 build-mode
frames) is event-shaped, not load-shaped.

Ranked hypotheses:

1. **`BuildUi.OpenBuildMenu`, reached through `UpdateBuildGuiInput` → `Hud.TogglePieceSelection`.**
   It runs `SelectPieceList` → `UpdatePieceButtons`, which destroys the special button, calls
   `GetAvailablePiecesWithTag`, then per available piece either pops a pooled button or `Instantiate`s
   one and calls `Setup`, then `UpdateSearch` and `ConfigureButtonNavigation`, then activates the whole
   canvas. Unbounded in piece-table size, once per menu open. Opening is billed to `UpdatePlacement`;
   closing is not, because `UpdateBuildGuiInput` returns early while the selection is visible.
2. **`Player.RemovePiece` and `Player.CopyPiece`** — neither is timed today. Removal destroys a piece
   and spawns a destroy effect, and neighbouring `WearNTear` support caches are invalidated.
3. **`Player.TryPlacePiece`** — already timed as `PiecePlace`, max 8.8 ms over 284 calls. It is nested
   inside `PlacementUpdate`, so a placement frame carries it, but it cannot reach 41.7 ms alone.
4. **First-touch `MaterialMan.SetValue`** as the crosshair sweeps onto pieces never highlighted before.
   A plausible contributor to a 10–15 ms tail, not to the peak.

Hypothesis 1 is the only one whose cost is both unbounded and reached on a discrete player action.

## 3. Candidate designs

Each is a bounded Harmony patch on one exact method, opt-in, in the shape of
`src/BetterPerformance/TerrainSaveCoalescing.cs` (IL verification at install, `Sample` counters,
`Uninstall`).

**D1 — nearby-snap-point cache on `Piece.GetSnapPoints(Vector3, float, List<Transform>, List<Piece>)`.**
Prefix returns a cached `(points, pieces)` pair when the query point is within a small epsilon of the
cached one; otherwise runs the original and stores the result. Invalidate on `Piece.Awake`,
`Piece.OnDestroy`, `Player.PlacePiece`, `Player.RemovePiece`, and on ghost prefab change.
Invariants: `FindClosestSnapPoints` must receive the identical `Transform` set in the identical order,
so the cache stores the `Transform` references, not positions. Breaks if a cached piece moves — exclude
any piece whose collider has an `attachedRigidbody` or that is parented to a vehicle. Also breaks if a
piece is destroyed by another player between frames, which is why the network destroy path must
invalidate too. Expected gain: at most 2–4 ms on the worst `PlacementGhostUpdate` frame. Low priority.

**D2 — skip the redundant `WearNTear.Highlight` re-issue.** Prefix on `WearNTear.Highlight` that
returns early when the same instance was highlighted in the previous frame and its support colour value
is unchanged. The 0.2 s `ResetHighlight` timer must still be re-armed, so the prefix has to keep the
`CancelInvoke`/`Invoke` pair and skip only the two `MaterialMan.SetValue` calls. Invariant: same colour
on screen. Risk: a support change that lands between frames shows a stale colour for one frame.
Expected gain: small and steady, not the peak.

**D3 — bound `Player.TestGhostClipping`.** Reuse the ghost collider array cached at
`SetupPlacementGhost` time instead of `GetComponentsInChildren<Collider>` per call, and replace the
allocating `Physics.OverlapSphere` with a `NonAlloc` variant into a static buffer. Invariant: identical
penetration verdict, so the buffer must be large enough and overflow must fall back to the original
call. Only affects pieces with `m_noClipping`.

**D4 — no design for the build-menu open yet.** It is UI work, not placement work, and any fix (button
pooling across opens, deferring `Setup` of off-screen buttons) changes the build UI rather than
placement. Out of scope for a placement module, and it should not be attempted before step 4 confirms
it is the cost.

Per-frame slicing is rejected for all of the above: the placement verdict must be exact on the frame
the player clicks, and `TryPlacePiece` recomputes it anyway.

None of these touch `Player.PlacePiece` or the RPC that instantiates the piece.

## 4. Count-only telemetry to add first

The capture cannot currently attribute the spike. Add, before any optimization:

- `BuildMenuOpen` timing on `BuildUi.OpenBuildMenu`, and counter `build_menu_opens`. Nested inside
  `PlacementUpdate`; correlating its max against the `PlacementUpdate` max settles hypothesis 1 alone.
- `piece_buttons_rebuilt` — pieces iterated per `BuildUi.UpdatePieceButtons` call.
- `PieceRemove` timing on `Player.RemovePiece` and `PieceCopy` on `Player.CopyPiece`, with
  `pieces_copied`. These are the two untimed branches of `UpdatePlacement`.
- `snap_pieces_scanned` and `snap_points_enumerated` — colliders returned and snap points appended per
  `Piece.GetSnapPoints(Vector3, …)` call. Confirms the ghost path really is bounded at this base size.
- `ghost_physics_queries` — overlap and raycast calls per `UpdatePlacementGhost`.
- `ghost_clipping_pairs` — collider pairs tested per `TestGhostClipping`.
- A 10 ms stall bucket for `PlacementUpdate` alongside the existing 50 ms one, so the 52 windows become
  a count rather than a max.

All are `Interlocked` counters in the `GameplayTelemetry.Sample` idiom
(`src/BetterPerformance/GameplayTelemetry.cs:397`), diagnostics-only, no behaviour change.

## 5. Verdict

**Feasible to investigate, not yet feasible to fix.** The step that scales with base size is already
measured and cheap (5.1 ms max). The 41.7 ms sits in `UpdatePlacement`'s event branches, most likely
the build-menu rebuild, which is UI work rather than placement work. Shipping D1–D3 today would buy
roughly 2–5 ms on peak ghost frames and would not move the 41.7 ms figure at all.

Recommendation: ship the step-4 counters as a diagnostics-only change, re-capture one build session,
then decide. Risk of the counters: negligible. Risk of D1 if shipped blind: a stale snap set after a
remote piece change, which would move where a piece snaps — exactly the invariant the brief forbids
breaking.

## Verified by reading vs assumed

Verified: the call sites of `UpdatePlacementGhost` (`Player.LateUpdate` and `Player.TryPlacePiece`, not
`UpdatePlacement`); the full body of `UpdatePlacement`, `UpdatePlacementGhost`, `FindClosestSnapPoints`,
`FindClosestSnappoint`, `TestGhostClipping`, `CheckPlacementGhostVSPlayers`, `PieceRayTest`,
`UpdateWearNTearHover`, `WearNTear.Highlight`, `GetSupportColorValue`, `Piece.GetSnapPoints`,
`Piece.GetAllPiecesInRadius`, `SetupPlacementGhost`, `TryPlacePiece`, `PlacePiece`,
`Hud.TogglePieceSelection`, `BuildUi.OpenBuildMenu`, `BuildUi.UpdatePieceButtons`; the probe mapping in
`docs/gameplay-telemetry.md:20`.

Assumed, not read: the body of `Player.GetHoveringPiece` (taken to return the field set by
`UpdateWearNTearHover`); the internals of `MaterialMan.SetValue`; the cost of `BuildUiPieceButton.Setup`
and of the canvas activation in `OpenBuildMenu`; that the 52 slow windows coincide with build-menu
opens, which is the hypothesis step 4 exists to test.
