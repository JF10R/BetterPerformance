# Capture guide

BetterPerformance 0.3.2 provides diagnostics and a separate, opt-in [object-creation budget](object-budget.md), with [quota and loot-priority options](loot-latency.md). The [real-session guide](play-session.md) covers continuous capture, passive loot observations, slow-operation alerts and collection-cost safeguards. Compilation does not establish runtime compatibility or low overhead. It changes no networking settings and contains no BetterNetworking implementation.

### Build and package

Requirements: .NET SDK 10, a local Valheim installation with BepInEx 5, and Python 3.10+ for reporting. Python is not required inside the game.

From the repository root, run:

```powershell
dotnet run --project tests/BetterPerformance.Tests -c Release
python -m unittest discover -s tests -p 'test_*.py' -v
dotnet build src/BetterPerformance/BetterPerformance.csproj -c Release '-p:ValheimDir=D:/Steam/steamapps/common/Valheim'
```

Set `ValheimDir` to your own client or dedicated server directory. Alternatively, copy `GamePaths.local.props.example` to `GamePaths.local.props`, edit it, and omit the command-line property. `GameManagedDir` and `BepInExCoreDir` can also be supplied explicitly. Local paths and game references are excluded from Git.

To create a ZIP without installing anything:

```powershell
./scripts/Package.ps1 -ValheimDir 'D:/Steam/steamapps/common/Valheim'
```

The package contains one plugin DLL, documentation, report/comparison scripts, and the project license. It includes no game or BepInEx assemblies.

### Install when ready to test

With the game and server stopped, copy `BetterPerformance.dll` into each installation's `BepInEx/plugins/BetterPerformance/` directory. BetterNetworking and ValheimPlus can remain installed for this diagnostics phase; see validation for tested versions and limitations.

The first launch generates `BepInEx/config/jf10r.BetterPerformance.cfg`. Default behavior:

- Start one capture when each world session begins in that process.
- Capture for up to 300 seconds, aggregating once per second.
- Queue up to 16 records for the background writer; discard excess records and count them.
- Limit each capture to 64 MiB, including its writer completion record.
- Reserve each next file's allowance within a 512 MiB JSONL directory limit; preserve old captures.
- Stop on world-session exit, the duration limit, a writer failure, or the file-size limit.
- Leave the experimental object-creation budget disabled.

Each process writes to its own `BepInEx/BetterPerformance/captures/` directory. Filenames contain UTC start time, role and a random capture ID. Collection includes loading; separate loading from steady play when interpreting it.

In the game's local console, if available, use:

```text
bp_capture start
bp_capture status
bp_capture stop
bp_mark lag_loot
```

Commands affect only that process. They are not remote server commands. The dedicated server can use automatic capture without an interactive console. Restart to apply configuration changes; `Capture.MethodTimings=false` omits method probes for an overhead comparison. `Capture.Enabled=false` installs no diagnostic probes and starts no captures; `ObjectLoading.Enabled` controls the optimization independently.

File serialization and writes run on a background thread. Shutdown allows at most two seconds to flush; forced termination, a blocked filesystem, or a full disk may leave an incomplete tail. Check the completion record and the BepInEx log. A new capture is refused while the previous writer is still finishing, preventing accumulation of blocked writers.

### Compare the local client and server

Keep the active mods and world situation comparable. Record the graphics settings and the route or action you repeat. The plugin records the effective simulation radius; it does not record every graphics option.

Capture the client and server over overlapping periods, then run:

```powershell
python scripts/summarize_capture.py 'path/to/client.jsonl' 'path/to/server.jsonl' --output 'comparison.md'
```

The Markdown report provides timing summaries and the largest observed loop gaps across the supplied files. Its warnings expose dropped records and missing completion markers. All timing aggregates cover retained records only. Inspect the original JSONL for CPU/GC, zone-readiness and simulation-radius detail.

For overhead checks, compare a repeatable baseline without the plugin, a capture with method timings disabled, and a capture with timings enabled. The self-measured counters are useful but do not include all Harmony dispatch, callback or scheduling costs.

The optional `BetterPerformance.Plugin.Mark("loot_spawn")` API and local `bp_mark loot_spawn` command record bounded scenario markers from the Unity main thread. The API returns false when unavailable, called from another thread, given an invalid identifier or after 256 markers per segment. It does not send network messages. Normal diagnostics require no harness or manual markers.

`scripts/compare_runs.py` supports the two- or three-block comparison described in [the repeated-test protocol](repeated-tests.md). It consumes independent observer JSON, not ordinary capture JSONL. Its run-level uncertainty estimates must not be interpreted as thousands of independent frame-level experiments.

### Privacy and limitations

Captures remain local and are not uploaded automatically. Routine capture excludes player names/IDs, IP addresses, world names, RPC payloads and save contents. It records game/mod versions, process role, timestamps, settings and aggregate measurements. Review captures before sharing them.

This version does not measure GPU time, exact packet throughput, compression ratio, end-to-end action latency, pure disk I/O duration, or another computer's frame times. Steam rate estimates cover only its native transport. Network counters that reset game statistics are deliberately not called. See [the metric definitions](measurements.md) before drawing conclusions.
