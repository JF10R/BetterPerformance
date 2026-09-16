# Budget tradeoff observations

When the object creation budget is installed and a capture is active,
`Diagnostics.BudgetTradeoffEnabled` adds bounded interval diagnostics. It does
not change the scheduling decision, move objects, alter ownership, or scan an
extra creation queue. The default is enabled; installing the budget itself
still requires `ObjectLoading.Enabled = true` at startup.

### What the counters mean

- `budget_observed_yielded_batches` counts batches that hit the existing
  budget gate. Near and distant loop exits have separate counters; two exits
  can belong to one batch. Neither count is a number of delayed objects or
  saved milliseconds.
- `budget_observed_attempts` and `budget_observed_successes` distinguish
  attempted native creations from successful results. Batch elapsed sums and
  maxima include scanning, sorting, nested creation, and observational work.
- `budget_creation_elapsed_sum/max` measures individual native `CreateObject`
  calls during budget-active batches. These are inclusive elapsed durations,
  nested within the batch timings, not exclusive CPU. Null results and thrown
  exceptions are counted separately. Missing network identities are excluded
  and counted by `budget_candidate_key_unavailable`.
- `budget_creation_over_allowance` counts individual calls longer than the
  entire configured batch allowance. `budget_creation_excess_sum` totals their
  excess over that allowance. This is not time saved, and does not count a
  shorter call that merely exceeded the batch's remaining time.

### Observed wait after a yield

Immediately after the existing gate decides to yield, the probe reads the
native enumerator's next candidate in constant time. It tracks only that
candidate's network identity, not every remaining object. A successful later
native creation completes the observation.

`budget_post_yield_wait_sum/max` describes elapsed time from that candidate's
first observed yield to local creation. It does **not** establish that the
candidate was ready, that all intervening delay was caused by the budget, or
what would have happened with the budget disabled. It includes time between
updates and any other delays. Repeated yields for the same identity retain
the first timestamp. Observations can finish after the budget is switched off;
their timing can therefore span configurations.

The tracker holds at most 512 identities. Entries expire after 30 seconds
without another observation or 120 seconds overall. Capacity skips and
censored tracks are explicit. Scene changes and capture completion censor
pending tracks. No identity, prefab name, payload, stack trace, or per-call
event is exported.

The enumerator load must be provably compatible with the native method. If
that additional shape check fails, scheduling retains its previous gate and
the candidate probe is labeled unavailable; it does not guess a candidate.
An observation failure disables these diagnostics without replacing native
results or exceptions.

### Interpreting the tradeoff

Compare creation-batch peaks, successful progress, post-yield observations,
and pending/censored counts alongside normal loop timings. An increased yield
count alone proves neither improved smoothness nor worse loot responsiveness.
The soft budget cannot preempt a single creation or the candidate scan, and
the first successful creation still guarantees progress. Diagnostics also
have a bounded amount of work, not a guaranteed maximum wall-clock cost.

The new gauges are interval aggregates. Existing `object_budget_*_total`
gauges retain their cumulative semantics. Capture start resets observation
state; final export includes the remaining aggregates and censored tracks.
