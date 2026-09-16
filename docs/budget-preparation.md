# Creation allowance after near preparation

`ObjectLoading.BudgetAfterNearPreparation` is experimental and defaults to `false`.
It requires the existing object budget to be installed and enabled. The option
is sampled once when the outer creation batch starts.

### Behavior

The original budget starts at entry to `ZNetScene.CreateObjects`. Native near
preparation scans and sorts pending candidates before the creation loop reaches
its first gate. Preparation can consume the entire allowance before any object
is attempted. The existing progress floor then permits work until one creation
succeeds, even if the allowance has already expired.

With this option enabled, the first verified near creation gate starts the shared
creation allowance. For example, 100 ms of preparation followed by a 4 ms
allowance can occupy at least 104 ms of a frame. This trades a potentially longer
batch for more opportunity to instantiate objects. It does not remove scan/sort
work or guarantee better frame times, faster loading, or faster loot pickup.

The option changes only the clock supplied to the existing budget decision:

- Native order, readiness checks, creation results, invalid-prefab handling,
  enumeration disposal and count limits are preserved.
- The successful-creation floor and attempt/success counters are not reset.
- Distant and nested creation share the same allowance; subsequent near gates
  cannot renew it. An empty near creation loop still establishes the boundary.
- A prior gate, including an empty distant gate, or any prior creation attempt
  prevents a retroactive clock refund.
- A nested batch entered before the boundary also prevents rebasing: its near
  gate cannot prove the outer near preparation has completed. An already applied
  offset remains shared when nested work begins.
- If the near enumerator cannot be verified, the whole-batch clock is retained.
  No additional transpiler edits are required.

### Measurements

While budget diagnostics and a capture are active, fixed counters report each
completed batch. These are elapsed wall intervals, not CPU time:

| Fields | Meaning |
| --- | --- |
| `budget_preparation_observed_batches` | Batches with a valid first near gate |
| `budget_preparation_unavailable_batches` | Missing gate or invalid interval |
| `budget_allowance_rebased_batches` | Observed batches actually using the new clock |
| `budget_preparation_elapsed_sum`, `budget_preparation_elapsed_max` | Batch entry through first near gate, inclusive |
| `budget_service_elapsed_sum`, `budget_service_elapsed_max` | First near gate through batch completion |
| `budget_service_over_allowance` | Service intervals exceeding the configured allowance |

Preparation includes any outer setup, scanning, sorting, optional priority work,
and the first native `MoveNext`; it is not an isolated sort measurement. Service
includes readiness checks, creation and any subsequent distant work. A skipped
rebase still reports these same intervals, making both policies observable.

Existing `budget_batch_elapsed_*` and `budget_batch_over_allowance` retain their
whole-batch meaning. Do not add creation timers to service or batch timers: those
intervals overlap. New counters drain with normal samples and reset with capture
lifecycle. Storage is constant; one extra timestamp is read per active batch
that reaches the near gate, with no queue traversal or per-object allocation.

### Validation and uncertainty

`BudgetPreparationGameTests.Run(game, plugin)` checks actual installed native IL
plus deterministic clock boundaries, the default-off setting, snapshot policy,
fallback, unsuccessful attempts, prior distant gates, nested sharing, empty near
loops, telemetry sums/maxima and reset. These offline checks establish scheduling
semantics, not Unity startup or multiplayer performance.

Compare complete startup runs using instantiated-object progress, successful
creations per batch, prep/service/whole-batch elapsed time, and frame-time tails.
Two starts alone cannot establish a causal improvement: world generation, cache
warmth, network delivery and the evolving candidate population can differ. Keep
this option opt-in until repeated comparable captures establish its tradeoff.
