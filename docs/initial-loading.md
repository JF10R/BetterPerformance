# Experimental initial loading acceleration

Added in 0.4.3. The module gives native local-zone preparation more service during
the initial join to a remote server. It is independent of diagnostics and the
object-creation budget. It does not apply to a dedicated server, a listen-server
host, ordinary gameplay, death respawns or teleports after the first spawn.

### One configuration switch

Edit `BepInEx/config/jf10r.BetterPerformance.cfg` in the **client** installation:

```ini
[InitialLoading]
Enabled = false
```

Close the client, change the value and restart it. `false` installs no acceleration
patch; `true` opts in on the next launch. The generic default is false. The user's
test installation is explicitly configured true on the client and false on the
dedicated server. Neither another player's client nor the server needs this option
for the local client's acceleration or measurements.

The module rereads the bound config value before every extra pass, so a value
changed through an existing in-process config manager can stop further extras.
There is no new disk watcher: manually editing the file requires a client restart.
Enabling after launching with false also requires a restart because no patch was
installed. Capture controls do not implicitly enable or disable the optimization.

### Scheduling and fallback

The mandatory native call always remains. During an eligible initial spawn, at
most three additional calls run, for at most four total and a soft 8 ms allowance.
A false result stops the burst; a later false cannot erase the first success.
Individual native operations are indivisible and can exceed that allowance.
Terrain, resource and object readiness and the native minimum wait remain intact.

Extra calls require the same game, network, zone system and reference position,
an initial pending client respawn, the main thread and enabled configuration.
Nested entry does not start another burst. Native exceptions still propagate
normally and the nesting guard is cleared in `finally`.

Native method signatures and the inspected client/server IL fingerprints are
checked at installation. Unknown layouts decline installation. Foreign patches
on the guarded scheduling methods cause native fallback; only this module's
wrapper and the known BetterPerformance observational timers are accepted.
This is deliberately conservative and can disable acceleration after a game or
mod update. An unchanged UI checkbox alone does not prove acceleration is active;
the recorded status is authoritative.

The fingerprints are token-independent since 0.4.10 (`IlFingerprint`): the opcode stream
and every operand are hashed, with each member, type and string token replaced by the
resolved member's full name. The raw-byte SHA-256 pins this replaced broke on the 1.0.14
and 1.0.15 updates in turn while `CreateLocalZones` and `PokeLocalZone` stayed 179 and 97
bytes with unchanged logic; the new fingerprint is identical on the 1.0.15 client and the
1.0.14 dedicated server, which turns the earlier "token churn" argument into a measurement.
A change to an opcode, constant, branch target or referenced member still disables the
module, as before.

### Measurements

Existing captures include:

- `initial_loading_status` and `initial_loading_enabled`, plus observed config
  changes at the normal polling cadence.
- An initial-join sequence and first eligible observation UTC.
- Mandatory calls/successes and additional calls/successes. Additional successes
  represent zone registrations advanced by the extra service, not individual loot
  objects and not CPU time saved.
- Stops caused by lack of progress, the time allowance, pass limit or changed
  context, and native failure counts.
- Cumulative and maximum elapsed native-pass-window costs, plus cumulative and
  maximum additional native-call costs. The first eligibility check is outside
  this window; compatibility checks after eligibility are inside it.

The fixed counters survive capture rotation and retain pre-capture work. They
describe the latest observed initial-join episode; a new eligible game instance
starts a new sequence. The module holds only a weak reference to that instance.
Counters do not cause per-zone log lines. No work episode means unobserved or
inapplicable coverage, not zero native loading cost.

The offline report keeps the last snapshot per observed sequence rather than
adding cumulative samples together. Use it with the separate loading timeline,
effective distance settings, process CPU and loop-gap measurements. Inclusive
times overlap existing zone timings and cannot be added as independent costs.
Snapshots can miss episodes that began and ended between exports.

This observes actual work and latency. It cannot infer the exact counterfactual
loading duration of that same session with the option off. Comparable future
sessions and user feedback provide additional evidence without requiring two
identical gameplay passes.

### Test feedback

When reporting a problem, note whether it happened during the first connection,
whether the loading screen hung or only stuttered, the distance preset, and
whether the character behaved normally after release. Restarting with the option
false restores the native scheduling path. Existing world/character save formats
and persistence code are not modified by this module.

The preceding QA prototype improved two headless comparisons by 4.2–5.5 seconds;
this is not a guarantee for a graphical client or another world. See the
prototype results,
native loading reference and
0.4.3 validation.
