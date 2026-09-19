# Smelter: catch-up telemetry (and the removed catch-up budget)

## Catch-up budget — removed in 0.4.11

`Smelter.UpdateSmelter` reads elapsed world seconds, adds them to the accumulator read from the
station's `accTime` ZDO variable, clamps the sum to 3600, then drains it one simulated second per
loop iteration; an iteration with no ore or fuel hits `continue` and that second is discarded, not
carried. A station the owner has not simulated for a while therefore replays the whole absence in
one call, and vanilla loses any idle time beyond what one call can drain.

The removed module (`SmelterCatchupBudget`) stopped that loop after a configurable number of
iterations (default 8) and let the native `SetAccumulator` persist the remainder in `accTime`
instead of discarding it. Its doc claimed totals were unchanged; that was false for an idle
absence, because vanilla discards idle iterations while the budget banked them. Measured in play
on 2026-09-18: `smelter_accumulator_carried_max` reached 1435 s with 1789 truncations across four
episodes (19:28-20:21, 20:51, 21:14, 21:36 local), and the user observed ore added after a return
smelting at roughly 8x real time for minutes, amplified by ValheimPlus chest auto-feed. Nothing in
the pipeline lost ore (653 `RemoveOneOre` calls matched 653 products), but the pacing was wrong.
Vanilla's own one-call replay is cheap regardless: `SmelterUpdate` peaked at 24.8 ms this session,
for up to 3600 loop iterations in the worst case per station.

## Telemetry — always on

Counting and one clamp-reproducing read per owner call; nothing is patched behaviourally and no
ZDO variable is written.

- `smelter_catchup_seconds_max`: the largest catch-up size a call was about to replay (carried
  `accTime` plus elapsed time, clamped to 3600 the same way vanilla clamps it). High values mean
  a station sat unsimulated for a while; this is the number the removed budget used to bank.
- `smelter_catchup_calls_over_60s`: owner calls whose catch-up size was a minute or more — a
  return-to-base replay, not routine drift.
- `smelter_accumulator_carried_max`: `accTime` read after the call. Vanilla always leaves this
  under one second; a value at or above one is the same truncation signature the old budget left,
  and now means something else changed the loop, since nothing here stops it early.
- `smelter_spawn_calls`, `smelter_remove_ore_calls`: count `Smelter.Spawn` and `RemoveOneOre`, the
  product and ore-consumption sites.
- `smelter_probe_failures`: exceptions inside the observation itself.
- Labels: `smelter_telemetry_status`, `smelter_telemetry_scope`.

Offline, `tests/BetterPerformance.GameTests/SmelterGameTests.cs` proves the game signatures this
module reads, the two ZDO variable keys, and its own hooks' shape; it also carries a two-sided
advisory NOTE (printed, not failing) if the loop's 3600 clamp or its idle `continue` path changes
shape, since the catch-up arithmetic above assumes both.
