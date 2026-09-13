# Development rules

### Scope

- Initial scope: diagnostics for the local Valheim client and dedicated server.
- Keep measurement and optimization modules separate. No behavior changes in diagnostics-only mode.
- Do not claim runtime compatibility, negligible overhead, or performance gains without measurements.
- Do not launch Valheim or its server, deploy plugins, or alter game configurations without explicit authorization.

### Repository hygiene

- Never commit game assemblies, decompiled game sources, worlds, secrets, or raw captures.
- Reference game dependencies locally; do not bundle them for distribution.
- Preserve upstream licenses and attribution when importing third-party code.
- Use Scout for code discovery when available; use raw search when unavailable or insufficient. Do not use Graphify unless requested.

### Verification

- Validate aggregation, bounded buffers and export behavior with meaningful offline tests when implemented.
- Record units and distinguish elapsed time, CPU time and asynchronous completion.
- Keep unsupported or unavailable measurements explicit.
- Check documentation links and rendered Markdown before publishing.
