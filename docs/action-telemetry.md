# Local pickup and container outcomes

These observations separate user-action completion from general frame, RPC and replication timings. They are passive: the module never requests ownership, sends RPCs, picks up objects, changes inventory or opens a GUI. Original arguments, return values and exceptions are preserved.

## What is actually confirmed

For a local player's pickup, `ItemDrop.Pickup(Humanoid)` records an optional request-entry timestamp. A nested `ItemDrop.RequestOwn` call marks that request as involving the ownership path; entering that method does not prove an RPC was sent. Later, `Humanoid.Pickup(GameObject,bool,bool)` records the direct attempt, retaining the exact source `ItemData` reference and the local player's bound `Inventory` reference. The finalizer observing true and no exception from the matching `Inventory.AddItem(ItemData)` inside that scope confirms native acceptance at that probe. It does not confirm later drop disposal, complete pickup success or peer visibility. Observers use last-priority finalizers; another mod can still affect later behavior.

The return value of `Humanoid.Pickup` does not confirm acceptance: the inspected native implementation can return true in its no-ZDO branch even if `AddItem` returned false. The observer therefore ignores that outer return value. A false result from the matching `AddItem` is labeled rejected acceptance; it does not prove zero partial-stack changes or explain why acceptance failed. An attempt ending without a matching AddItem outcome is censored, even when the outer method returned true.

The fixed `action_acceptance_semantics` and `action_ownership_semantics` labels carry these limitations in captures. Partial transfer quantities are not measured by this version.

For a local player's non-hold `Container.Interact`, the observer starts a container attempt. It confirms opening only after `InventoryGui.Show(Container,int)` returns normally and its `m_currentContainer` field matches that same container object. `RPC_OpenResponse(false)` is an explicit rejection. Other early exits, such as access checks, remain unconfirmed and eventually censored. Opening inventory without a container is ignored.

## Delay definitions

- Request wait: observed `ItemDrop.Pickup` or `Container.Interact` entry to a matching confirmed outcome.
- Direct pickup wait: `Humanoid.Pickup` entry to the matching inventory acceptance, without inventing an earlier ownership delay when no request was observed.
- Ownership-path wait: the **full request-entry to accepted-inventory delay**, restricted to the subset with a proven nested `RequestOwn` call. It does not start at `RequestOwn` and does not end at ownership acquisition. It includes all intervening client scheduling and processing; it is not a network RTT measurement.

Automatic versus manual pickup is exported as `unspecified`. No classification is inferred from names or timing. Duplicate requests for the same object cannot be paired to individual responses without changing native RPCs. Confirmed outcomes still count, but ambiguous request timelines are excluded from request/ownership latency sums. Duplicate direct attempts are likewise excluded from direct latency sums. `ambiguous_confirmed` reports this limitation.

## Bounds, coverage and privacy

One tracker holds at most 128 pending object references across pickup and container actions, with a 30-second timeout. Entries are retired on completion, rejection, capture end, local-player replacement, or timeout. Timeouts are checked on sampling and on a new attempt for the same object. The observer performs one `GetComponent<ItemDrop>` lookup at a direct pickup call; it never scans the scene or inventories.

Capture start binds observations to the current main thread. Each sample and eligible hook refreshes the local player/inventory binding, so disappearance or replacement censors old pending references even when no further actions occur. Other-thread callbacks are skipped and counted. The dedicated server normally has no local player and reports that coverage explicitly. No player IDs, item names, item contents, object IDs, container identities or coordinates are exported. Bounded references exist only in memory while pending.

Capacity skips, unmatched outcomes, duplicates, timeouts, censored entries and observer exceptions remain visible. They are not counted as lost loot or failed networking. Exception callbacks may count the same game exception at multiple nested observation boundaries; they are not unique exception events. A confirmed local inventory/GUI outcome does not establish when another peer observed it or when rendering became visible.

## Export and integration

`Install(logger)` installs independent observation hooks. The writable `Enabled` gate is separate from capture state. Root capture integration calls `StartCapture()`, `Sample(gauges, labels)` for each interval, and `Finish(gauges, labels)` for the final record. `Reset()` drops previous state when beginning a separate capture/session. `Uninstall()` disables and removes the hooks.

`action_pickup_*` and `action_container_*` values are interval aggregates, except `pending`, which is the current stock of pending entries. Sum counts and wait sums across intervals; combine maxima using max. Divide each wait sum only by its corresponding `*_completed` count, never by all confirmations. Do not average interval means or interpret summed maxima as total delay. Request, direct and ownership waits overlap and must not be added together.

Entirely inactive categories with zero pending entries are omitted to reduce idle/server log volume. The explicit `action_zero_category_semantics` label marks this as zero **observed** interval activity, not complete coverage or zero latency. Without that marker, absent gauges remain unavailable. Categories with any activity, censored outcomes or pending entries still export their complete aggregate set.

`Finish` first censors outstanding entries and exports the final aggregates, so capture-end losses are visible. Interval records may contain confirmations of attempts started in earlier intervals. Timeout and capture-end censoring mean completed-only means underrepresent long/unobserved outcomes; always read them alongside coverage counts.

Core tests cover request/direct attribution, duplicate ambiguity, shared capacity, explicit rejection, inclusive timeout, late unmatched outcomes and capture reset/end. Static game-reference tests verify exact native signatures, the actual inventory acceptance call, GUI confirmation field and non-mutating observer signatures. Hook installation and real outcomes still require an isolated Unity runtime test; no latency improvement is claimed from adding measurements.
