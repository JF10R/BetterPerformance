# BetterPerformance

Performance diagnostics and, later, evidence-based optimizations for Valheim clients and dedicated servers.

**Status: experimental diagnostics plugin, version 0.1.1. Offline checks and isolated headless client/server tests run on Valheim 1.0.12. Rendered gameplay and total measurement overhead remain unvalidated. No performance optimizations are enabled or claimed.**

The initial focus is measuring a client and dedicated server running on the same computer. The goal is to distinguish simulation stalls, object-loading delays, save pauses, and network backlogs before changing game behavior.

### Implemented diagnostics

- Independent client/server captures with UTC timestamps and monotonic durations.
- Loop and method timing distributions, process CPU/memory, GC activity, reported socket queues, scene instance counts, zone readiness and effective simulation radius.
- Separate timing of save preparation, the save call and the save worker.
- Bounded aggregation and background JSONL export with dropped-record accounting and a per-capture file-size limit.
- Native Steam transport counters, valid resident-memory readings, scenario markers and GC/boundary-crossing loop identification.
- A Python report command for comparing captures without changing gameplay, persistence or networking settings.

The first two-process capture cannot establish what a remote client is doing. Measurements from additional clients can be added when needed.

### Relationship to other mods

BetterPerformance is an independent project. It is intended to work alongside ValheimPlus, without requiring it.

The diagnostics phase is intended to coexist with BetterNetworking. Queue measurements explicitly retain its adjusted socket results. A possible later networking module may reuse and improve BetterNetworking's implementation; if that happens, overlapping networking patches must not run simultaneously. No BetterNetworking code is included. One headless run with BetterNetworking 2.3.3 and ValheimPlus 0.10.1.1 completed; broader compatibility remains unvalidated.

### Documentation

- [Measurement scope and interpretation](docs/measurements.md)
- [Build, installation and capture guide](docs/capture-guide.md)
- [Validation results and remaining checks](docs/validation.md)
- [Repeated-test protocol and uncertainty](docs/repeated-tests.md)
- [Runtime results, including possible overhead](docs/runtime-results-2026-09-14.md)
- [Implemented changes](CHANGELOG.md)
- [Development rules](AGENTS.md)
- [License](LICENSE)

### Development

The plugin targets BepInEx 5 / .NET Framework 4.7.2. Build against your local game installation:

```powershell
dotnet run --project tests/BetterPerformance.Tests -c Release
python -m unittest discover -s tests -p 'test_*.py' -v
dotnet build src/BetterPerformance/BetterPerformance.csproj -c Release '-p:ValheimDir=D:/Steam/steamapps/common/Valheim'
```

The offline checks require .NET SDK 10 and Python 3.10+. CI runs the game-independent tests on Windows and Linux. The [capture guide](docs/capture-guide.md) covers configuration, packaging and interpretation.

Do not commit Valheim assemblies, decompiled game sources, save files, or raw diagnostic captures. Keep game references and test artifacts local. Building and packaging do not install anything or start the game or server.

### Credits and license

Original project files are available under the [MIT License](LICENSE).

The networking investigation draws on [CW-Jesse's BetterNetworking](https://github.com/CW-Jesse/valheim-betternetworking) and the [manchyy fork](https://github.com/manchyy/valheim-betternetworking). Any future code reuse must retain the applicable copyright and license notices, including those of redistributed dependencies.
