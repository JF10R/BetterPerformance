# Experimental loot scheduling — 0.3.0

### Validation status

Initially implemented with validation deferred, then tested after renewed user authorization. Offline regression checks and a sixteen-window isolated runtime comparison now pass. Budget plus quota reduced mean observed loot availability from 268.57 to 158.73 ms in that headless workload; the incremental priority effect was mixed across blocks. See [0.3.0 results, safety scope and uncertainty](loot-results-2026-09-14.md). The [0.2.0 measurements](object-budget.md) remain historical budget-only results.

### Configuration

Both additions require the existing object-creation budget and are independently disabled by default:

```ini
[ObjectLoading]
Enabled = false
BudgetMilliseconds = 4
AdaptiveCreationQuota = false
ExpandedCreationQuota = 64
PrioritizeNearbyLoot = false
LootPriorityRadius = 20
```

Enable the parent module and the desired option explicitly, then restart that process to use the configuration. `bp_budget off` disables the budget and both additions together; `bp_budget on` resumes the options configured for that process. Nothing is synchronized to another client or server. No existing installation or save configuration was changed during development.

### Adaptive count allowance

When enabled inside an active timed batch, raise the supplied vanilla near/distant count allowance to at least `ExpandedCreationQuota` (10–256, default 64). Preserve any larger vanilla loading/backlog allowance. Both paths still share the existing created-object counter and elapsed-time budget.

This permits more inexpensive objects to finish when the vanilla count allowance would otherwise stop a batch before its time budget is exhausted. It does not predict object cost or identify cheap prefabs in advance. Expensive creation still consumes the budget, and the next loop continuation can yield. The quota value is not a hard maximum: native backlog adjustments and distant-loop boundary semantics remain unchanged.

The existing minimum-progress rule still permits one successful creation, even after invalid prefabs or preparation exhaust the soft budget. Individual object creation, scanning and sorting can exceed the target. A larger count allowance can increase work per update; benefit and latency require measurement.

### Nearby loot priority

After vanilla sorts its near-object candidates, perform a stable priority partition **inside each contiguous `ZDO.ObjectType` tier**. Keep terrain ahead of solid objects, then the prioritized tier, then the default tier. Within a tier, favor candidates whose prefab has a root `ItemDrop` component and whose vanilla squared distance is within `LootPriorityRadius` (1–64 metres, default 20).

Keep the relative vanilla order of the selected loot and of the remaining candidates. Do not reorder the distant list. Do not bypass active-area readiness, per-zone terrain/collision readiness, ownership, invalid-prefab handling or native creation/destruction.

Every fourth eligible pass retains vanilla ordering entirely; the other three may prioritize nearby loot. This gives ordinary scheduling regular opportunities instead of letting the new priority consume every pass. It is **not a finite waiting-time guarantee** under continuous arrivals, persistent unreadiness or existing vanilla starvation. Loot priority does not move default-tier loot ahead of higher-tier objects.

Prefab component inspection happens before partitioning, outside the sort comparator, with at most 2,048 cached classifications per scene. Missing prefabs are not negatively cached. If an uncached nearby prefab is encountered after the cache fills, the entire pass keeps vanilla order. Lists larger than 16,384 candidates also retain vanilla order. Classification caches reset on scene replacement; weak scene references and cleared scratch lists avoid retaining the old world's objects.

If classification fails, the candidate list retains its original order and the priority option disables itself for the current process, with a warning and diagnostic status. The existing time budget and quota option remain independent of that failure. Priority preparation counts against the same elapsed-time budget, so its own overhead can delay creation.

### Diagnostics and remaining checks

Passive `loot_queue_*` observations measure sampled network-object wait before local scene creation, not pickup latency or chest/inventory activity. See [diagnostic scope and configuration history](configuration-history.md) before interpreting small sample counts.

Captures expose configured quota/radius, actual option enablement under the parent switch, quota-expansion call counts, priority/vanilla-order pass counts, selected-candidate counts and bounded-fallback counts. A selected candidate can still fail readiness or remain pending; these counters are **not** successful loot spawns or latency measurements.

Executed regression coverage includes disabled/active quota behavior, larger vanilla allowances, exhausted time budgets, tier boundaries, stable partitioning, failure rollback, scratch cleanup, the three-to-one ordering cadence, and the new Harmony hook's placement after vanilla sorting.

Validation covers only the installed mod combination and documented workloads. Broader runtime compatibility, total overhead, terrain-streaming collision races, rendered gameplay and remote-client behavior remain unvalidated. No distributable package replaces the previously tested 0.2.0 package as part of this change.
