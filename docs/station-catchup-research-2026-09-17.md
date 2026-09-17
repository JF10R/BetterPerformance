# Station catch-up: mechanism, cost and a bounded opt-in design

Research note, 2026-09-17. Read-only study of game method and field names (Valheim 1.0.12) plus this
repo's telemetry. No game launched. Claims are split into **verified by reading** and **assumed**.

## 1. Verified mechanism, per station type

All station types derive elapsed world time the same way: a `DateTime` built from a tick count stored
in the ZDO, differenced against `ZNet.instance.GetTime()`, with the current ticks written straight
back. Only the ZDO variable and what happens with the result differ.

| Type | Driver | ZDO time var | Work shape per call | Spawns in the tick? |
| --- | --- | --- | --- | --- |
| `Smelter` | `UpdateSmelter`, `InvokeRepeating` 1 s | `s_startTime` + `s_accTime` | **`while` loop, one iteration per simulated second** | yes, `Spawn` per product |
| `CookingStation` | `UpdateCooking` | `s_startTime` | one `dt` added per slot, fixed slot count | only on player interaction |
| `Fermenter` | `GetFermentationTime` on demand | `s_startTime` | O(1) subtraction | `DelayedTap`, player-triggered |
| `Beehive` | `UpdateBees` | `s_lastTime`, `s_product` | integer division `(int)(num / m_secPerUnit)` | `RPC_Extract`, player-triggered |
| `SapCollector` | `UpdateTick` | `s_lastTime`, `s_product` | integer division, same shape as beehive | `RPC_Extract`, player-triggered |
| `Fireplace` | `UpdateFireplace` | `s_lastTime` | one subtraction on `s_fuel` | never |
| `Plant` | `SUpdate`, throttled to 10 s | `s_plantTime` | O(1) compare | one prefab at maturity |
| `Windmill` | `Update` | none | cover check plus audio, no time integration | never |

`Smelter` is the only type that iterates. Everything else collapses an arbitrary absence into one
arithmetic step, which is why the telemetry shows all of them under 3.1 ms while the smelter reaches
33.7 ms.

**Ownership.** `UpdateSmelter` runs the visual work (`UpdateRoof`, `UpdateSmoke`, `UpdateState`) for
everyone, then returns early unless `m_nview.IsOwner()`. The whole catch-up is owner-only. The same
`IsOwner` guard appears in `UpdateBees`, `UpdateTick`, `UpdateFireplace` and the owner branch of
`UpdateCooking`. Verified by reading.

**Smelter loop, exactly.** `GetDeltaTime()` reads `s_startTime`, returns the elapsed seconds and
rewrites `s_startTime` to now. That delta is added to `s_accTime`, clamped to `3600f`. Then
`while (accumulator >= 1f)` decrements one second at a time. Each iteration: read `s_fuel`, subtract
`1 / (m_secPerProduct / m_fuelPerProduct)` scaled by the windmill power output, write `s_fuel`, read
`s_bakeTimer`, write it. When `bakeTimer >= m_secPerProduct`, it resets the timer, calls
`RemoveOneOre()` and `QueueProcessed(ore)`. After the loop, `SpawnProcessed()` flushes if the ore
queue is empty or fuel hit zero, then `SetAccumulator(accumulator)` persists the remainder.

**What an item costs.** With `m_spawnStack` false, `QueueProcessed` calls `Spawn` immediately: one
`m_produceEffects.Create`, one `Instantiate` of the output prefab, one `ItemDrop.OnCreateNew`. With
`m_spawnStack` true the products accumulate in `s_spawnOre` and `s_spawnAmount` and only one
`Instantiate` runs per full stack. `RemoveOneOre` is the second cost: it shifts the whole `item0..itemN`
string array down by one, so it is O(queue) ZDO string reads and writes per item, quadratic across a
drain of a full queue.

## 2. Why one call processes 15 items, and what bounds it

Two independent bounds, both verified:

- **Iterations** are bounded only by the `3600f` accumulator clamp. A station idle for a day still
  runs at most 3600 iterations, but 3600 iterations is already 14,400 ZDO reads and writes with no
  items produced at all.
- **Items** are bounded by the ore queue (`m_maxOre`) and by fuel: one product costs
  `m_fuelPerProduct`, so items per call cannot exceed `floor(fuel / m_fuelPerProduct)` where
  `m_maxFuel > 0`, and cannot exceed the queued ore count.

Assumed, not verified: the observed `smelter_catchup_items_max` of 15 most likely comes from a
fuel-free station (`m_maxFuel == 0`, the charcoal-kiln shape) where the ore queue alone sets the
ceiling. Prefab field values are asset data, not source, so this is not readable from code.

**Caution on the counter.** `AfterSmelter` computes `consumed` as the drop in
`queueSize + processedQueueSize` across one call. `processedQueueSize` is `s_spawnAmount`. On a
stacking station a single `SpawnProcessed` flush drops `s_spawnAmount` by the whole stack, so a
recorded 15 can mean fifteen ore consumed **or** one `Instantiate` flushing a stack of fifteen. The
counter as written cannot separate those, and the 2 ms per item inferred from 33.7 ms / 15 rests on
the first reading. Treat it as unproven until a dedicated spawn-per-call counter exists.

## 3. Candidate designs

### A. Bound the loop, carry in the accumulator (recommended)

The game already carries the remainder: `s_accTime` survives the call and the next invocation one
second later resumes from it. Stopping the loop early and letting `SetAccumulator` persist what is
left is therefore not an approximation. Each iteration is a self-contained simulated second whose
entire state lives in `s_fuel`, `s_bakeTimer`, `s_queued`, `s_item*`, `s_spawnOre`, `s_spawnAmount`
and `s_accTime`. Executing them over several frames produces the same sequence of writes in the same
order, so totals, fuel use and the final `s_startTime` are identical.

Hook shape: a **transpiler** on `UpdateSmelter` that rewrites the loop condition from
`accumulator >= 1f` to `accumulator >= 1f && budget-- > 0`, with the budget read from a per-call
local seeded by a plugin method. A prefix cannot do this: the accumulator is a local, not a field.
A postfix is useless, the work is already done. A prefix that instead lowers `s_accTime` before the
call and restores the difference afterwards would work without IL rewriting, but it leaves a window
where a concurrent reader sees a falsified accumulator, so the transpiler is cleaner.

**Invariants to hold.** Budget strictly greater than 1 per call, otherwise the drain rate never
exceeds the 1 s per second that accrues and the 3600 s clamp starts discarding time that vanilla
would have simulated. Never touch `s_startTime`, it is advanced once per call by `GetDeltaTime`
whether or not the loop runs. Never skip the trailing `SpawnProcessed`, it is idempotent when
`s_spawnAmount` is zero. Non-owners are unaffected because the early `IsOwner` return precedes
everything.

### B. Slice only the spawn loop

Defer the `Spawn` calls, keeping the simulation loop intact. This is worse. It requires a plugin-side
queue that is not in the ZDO, so an ownership change, a zone unload or a crash mid-drain destroys
items that vanilla had already created. Reject.

### C. Do nothing but flatten `RemoveOneOre`

Orthogonal and cheap: patching `RemoveOneOre` to avoid the O(queue) string shuffle would cut the
quadratic term without changing any timing semantics. Smaller gain, much smaller risk. Worth
considering as a separate module.

**Coverage.** One transpiler on `Smelter.UpdateSmelter` covers every smelter-family prefab, because
they all share this one class. No other station type has this shape, so no other type needs the
mechanism. That is the honest scope: the fan-out is across prefabs, not across classes.

**Residual risks.** If the station is destroyed while time is still carried, `OnDestroyed` runs
`DropAllItems`, which drops the still-queued ore as raw ore rather than the product vanilla would
already have made. The window is the length of the deferral, a few seconds at a sane budget, against
a vanilla window of zero. An ownership change mid-carry is safe: the carry lives in `s_accTime` in
the ZDO and the new owner resumes from it, and no item is produced twice because production only
happens where `s_bakeTimer` crosses `m_secPerProduct`, which is itself in the ZDO.

## 4. Telemetry to add

Existing counters (`smelter_updates`, `smelter_catchup_items_sum`, `smelter_catchup_items_max`,
`smelter_spawns`) do not distinguish the two cost drivers. Add, all count-only:

- `smelter_loop_iterations_sum` and `_max`: simulated seconds executed per call, the real driver.
- `smelter_accumulator_carried_max`: seconds left in `s_accTime` after a call, proves the carry works
  and that the 3600 clamp is never reached.
- `smelter_budget_truncations`: calls where the budget stopped the loop early, the module's duty cycle.
- `smelter_spawn_calls` separated from `smelter_catchup_items_*`, which resolves the stack-flush
  ambiguity in section 2.
- `smelter_remove_ore_string_ops`: bounds the quadratic term and sizes design C.

## 5. Verdict per station type

| Type | Feasible | Expected gain | Risk |
| --- | --- | --- | --- |
| `Smelter` family | Yes, transpiler on `UpdateSmelter` | Removes the 20 to 34 ms spikes; converts one 33.7 ms call into N calls under the budget | Medium. IL rewrite on a loop condition, needs a shape contract and a clean fallback |
| `CookingStation` | Not needed | None, already O(slots) | N/A |
| `Fermenter`, `Beehive`, `SapCollector`, `Fireplace`, `Plant` | Not needed | None, already O(1) | N/A |
| `Windmill` | Not needed | None, no time integration | N/A |

No performance claim here is measured. The 2 ms per item figure is inferred from the session maximum
and depends on the counter reading flagged in section 2. An A/B with the module off and on, same
world and same station set, is required before any gain is stated.

Design A is implemented as the opt-in `[Stations] SmelterCatchupBudgetEnabled` module; see
[docs/smelter-catchup-budget.md](smelter-catchup-budget.md).
