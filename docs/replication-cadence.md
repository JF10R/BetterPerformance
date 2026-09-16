# Replication cadence

Two opt-in changes to the replication send path, both default off, plus always-on
observation of it. Nothing here is measured in a running game yet. Mechanism and evidence:
`docs/replication-research-2026-09-15.md`.

## Configuration

| key | default | effect |
| --- | --- | --- |
| `[Replication] CosmeticResendIntervalEnabled` | `false` | Apply a minimum resend interval to owned cosmetic prefabs. |
| `[Replication] CosmeticPrefabs` | the twelve `Fish*`, `Crow`, `Seagal` | Names it applies to. Names absent from the loaded scene are counted and ignored. |
| `[Replication] CosmeticMinimumIntervalSeconds` | `0.2` | Range 0.05-1.0. 0.2 is five sends per second against the vanilla twenty. |
| `[Replication] BirdVelocityEnabled` | `false` | Publish an estimated velocity for flying birds. |
| `[Diagnostics] ReplicationCadenceTelemetryEnabled` | `true` | Observation only; independent of the two switches above. |

Enabling either switch needs a restart; turning one off at runtime stops it immediately.

## A. Cosmetic resend interval

A postfix on `ZDOMan.CreateSyncList`, covering both the client and server branches, drops
an entry while the peer's own `m_syncTime` for it is younger than the interval. An entry is
kept, never deferred, when it is not a configured cosmetic prefab, is in the peer's
force-send set, is `ObjectType.Prioritized`, is owned by another process, or has never been
sent to this peer.

Deferring loses nothing. `ZDO.Serialize` always writes full current state and never a
delta, and a skipped entry stays queued: on the client it is still in `m_clientChangeQueue`,
which `SendZDOs` only clears for entries it actually wrote, and on the server `ShouldSend`
stays true while `DataRevision` is ahead. Wire format, revision accounting, ownership and
save contents are untouched. One known side effect: the server tops the list up with
distant objects only while it holds under ten entries, decided before this postfix runs,
so a pass can end below ten without the top-up.

## B. Bird velocity

`RandomFlyingBird` advances `transform.position` directly and carries no `Rigidbody`, so
`ZSyncTransform.GetVelocity` returns zero and the owner publishes a zero `s_velHash`. The
non-owner runs `position += velocity * timer` with that zero and only lerps, so a 10 m/s
bird trails and catches up in bursts.

A postfix on `ZSyncTransform.OwnerSync` publishes a difference quotient instead, for
instances with a `RandomFlyingBird` and no `Rigidbody`, in world metres per second, which is
what the vanilla consumer expects. It is written only when the value changes, so it adds no
revision on a frame the object was not already dirty. A position jump, a frame gap over
0.5 s or an implied speed over 40 m/s publishes zero instead of a wrong extrapolation, and
a bird still for 0.15 s publishes zero once.

`ZSyncTransform.SyncPosition` consumes `s_velHash` in vanilla, so the second player sees
the improvement without running this plugin and a bird owned by a plugin-less client is
unaffected. Landing and take-off logic is not touched.

## C. Telemetry

Exported as `replication_*` gauges with a `replication_scope` label, drained per interval.
Per-prefab resend-interval histograms (8 buckets, up to 32 prefabs tracked, top 6 exported
by name), an aggregate distance-at-send histogram, and forced and prioritized entry counts
all come from one postfix registered to run before any deferral, so they describe the
vanilla selection either way. Byte-budget truncations are counted without a transpiler, as
the shortfall between the selected list and the `m_zdosSent` delta across one `SendZDOs`
call; that field is assigned exactly once, in the loop whose only early exit is the budget.
