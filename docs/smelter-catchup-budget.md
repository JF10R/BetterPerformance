# Smelter catch-up budget

Opt-in, default off: `[Stations] SmelterCatchupBudgetEnabled`. Requires a restart.
The counters below install whenever `Smelter` resolves, whether or not the budget does.

## Mechanism

`Smelter.UpdateSmelter` runs on a one-second `InvokeRepeating`. It reads the elapsed
world seconds through `GetDeltaTime`, adds them to the accumulator it read from
`GetAccumulator`, clamps the total to 3600, then drains it in a `while (accumulator >= 1f)`
loop that simulates one second per iteration. Each iteration reads and writes the fuel
and bake-timer ZDO variables, and a completed product calls `RemoveOneOre` and
`QueueProcessed`. A station whose owner has not simulated it for a while therefore
replays the whole absence inside one call.

The remainder already survives the call: `SetAccumulator` writes it to the station's
`s_accTime` ZDO variable and the next invocation one second later resumes from it. A
budget that stops the loop early is therefore a deferral, not an approximation.

A transpiler replaces exactly one instruction, the loop-head constant, with a call to a
static plugin method. That method counts the loop tests for this call and returns `1f`
while the budget lasts, then positive infinity, which makes the native compare false
whichever way it branches. No instruction is added or removed, so no branch target can
move. The smelter family is one class, so this one patch covers every smelter prefab.

## Invariants

- Simulation order is unchanged. Each iteration is a self-contained simulated second
  whose entire state lives in `s_fuel`, `s_bakeTimer`, `s_queued`, the ore-queue strings,
  `s_spawnOre`, `s_spawnAmount` and `s_accTime`, so the same writes happen in the same
  order across several calls instead of one.
- `s_startTime` is never touched. `GetDeltaTime` advances it once per call whether or not
  the loop runs.
- The trailing `SpawnProcessed` and `SetAccumulator` always run; neither is skipped.
- Non-owners are unaffected: the native `IsOwner` early return precedes everything.
- The configured budget cannot go below two, so the drain always outpaces the one second
  per second that accrues and the 3600 clamp never starts discarding simulated time.
- An ownership change mid-carry is safe. The carry is in the ZDO and the new owner
  resumes from it; no product is made twice because production is decided by
  `s_bakeTimer` crossing `m_secPerProduct`, which is also in the ZDO.
- Residual risk, unmitigated: if the station is destroyed while time is still carried,
  `OnDestroyed` drops the still-queued ore as raw ore rather than the product vanilla
  would already have made. The window is the length of the deferral, a few seconds at the
  default budget, against a vanilla window of zero.

## Configuration

| Key | Default | Range | Meaning |
| --- | --- | --- | --- |
| `[Stations] SmelterCatchupBudgetEnabled` | `false` | bool | Install the transpiler. |
| `[Stations] SmelterCatchupIterationsPerCall` | `8` | 2..3600 | Simulated seconds one call may replay. |

## Contract check

Install validates, from the original IL, that `UpdateSmelter` is a private parameterless
instance void; that it calls `GetDeltaTime`, `GetAccumulator`, `SetAccumulator`,
`RemoveOneOre`, `QueueProcessed` and `SpawnProcessed` exactly once each; that the
accumulator local comes from `GetAccumulator` and never escapes by reference; that it is
clamped to 3600 by one compare and one assignment before the loop; that exactly one
compare against 1 sits in the loop head and exactly one decrement by 1 sits inside the
loop that compare controls; that `SetAccumulator` follows the loop; that the owner gate
precedes it; and that the rewritten site carries no exception-block metadata. Any
mismatch keeps vanilla and reports `smelter_budget_status=unavailable_unexpected_shape`.

## Telemetry

`smelter_loop_iterations_sum` and `smelter_loop_iterations_max` are the simulated seconds
executed, the real cost driver. They are derived from the gate the transpiler installs,
so they read zero while the budget is not installed; every other counter here works on
both sides of an A/B. `smelter_accumulator_carried_max` is the seconds left in `s_accTime`
after a call. `smelter_budget_truncations` counts calls that ended with a whole second
still carried, which vanilla cannot produce. `smelter_spawn_calls` counts `Smelter.Spawn`,
the one `Instantiate` site, which separates products from stack flushes and resolves the
ambiguity flagged in the research note. `smelter_remove_ore_calls` sizes the O(queue)
string shuffle. `smelter_probe_failures` counts exceptions inside the observation itself.
Labels: `smelter_budget_status`, `smelter_budget_enabled`, `smelter_budget_iterations`,
`smelter_budget_telemetry_status`, `smelter_budget_scope`.

## Verification

Offline, `tests/BetterPerformance.GameTests/SmelterGameTests.cs` proves the game
signatures, the ZDO variable keys, the loop shape, that the transpiler changes exactly
one instruction and preserves every label and exception block, and that a mutated compare
constant or clamp is refused. No game method is invoked.

Unproven: every performance claim. A gain requires an A/B on one world with one station
set, the module off then on, comparing the `SmelterUpdate` timing probe against
`smelter_loop_iterations_max` and `smelter_budget_truncations`. Equally unproven is the
product-parity claim at runtime, which needs a disposable world with a known ore and fuel
load drained once with the module off and once on, comparing the items produced.

## What not to do

Do not defer the spawns while leaving the simulation loop intact: that queue would live
outside the ZDO, so an ownership change, a zone unload or a crash mid-drain would destroy
items vanilla had already created. Do not set the budget to one. Do not touch
`s_startTime`, and do not skip the trailing `SpawnProcessed`.
