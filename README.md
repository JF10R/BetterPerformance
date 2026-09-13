# BetterPerformance

Performance diagnostics and, later, evidence-based optimizations for Valheim clients and dedicated servers.

**Status: project foundation. No plugin, installer, or performance improvement is implemented yet.**

The initial focus is measuring a client and dedicated server running on the same computer. The goal is to distinguish simulation stalls, object-loading delays, save pauses, and network backlogs before changing game behavior.

### Initial scope

- Measure the client and dedicated server independently and correlate their captures.
- Compare frame and update times, object creation/removal, zone loading, saves, and networking.
- Keep collection bounded and measure the collector's own overhead.
- Export local diagnostic summaries suitable for repeatable before/after comparisons.
- Preserve gameplay, world persistence, and network behavior during the diagnostics phase.

The first two-process capture cannot establish what a remote client is doing. Measurements from additional clients can be added when needed.

### Relationship to other mods

BetterPerformance is an independent project. It is intended to work alongside ValheimPlus, without requiring it.

The diagnostics phase is intended to coexist with BetterNetworking. A possible later networking module may reuse and improve BetterNetworking's implementation; if that happens, overlapping networking patches must not run simultaneously. No BetterNetworking code is included in this initial repository, and no runtime compatibility is certified yet.

### Documentation

- [Measurement scope and interpretation](docs/measurements.md)
- [Development rules](AGENTS.md)
- [License](LICENSE)

### Development

There is currently no build or installation step. Runtime instrumentation, packaging, and automated checks will be added with the first implementation.

Do not commit Valheim assemblies, decompiled game sources, save files, or raw diagnostic captures. Keep game references and test artifacts local. Creating this repository does not install anything or start the game or server.

### Credits and license

Original project files are available under the [MIT License](LICENSE).

The networking investigation draws on [CW-Jesse's BetterNetworking](https://github.com/CW-Jesse/valheim-betternetworking) and the [manchyy fork](https://github.com/manchyy/valheim-betternetworking). Any future code reuse must retain the applicable copyright and license notices, including those of redistributed dependencies.
