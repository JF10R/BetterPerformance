# Verification lessons

- Assign PowerShell `foreach` statement output to a variable before piping it; a bare closing loop brace cannot be piped directly.
- GitHub may render highlighted fenced code as `<div class="highlight"><pre>` without any `<code>` element. Accept rendered `pre` or `code` tags when checking fenced-code preservation.

- Read the actual project filename before invoking a local QA build; do not infer it from the output assembly name.
- The installed HarmonyX/MonoMod instruction reader requires the game's .NET Framework-compatible runtime for local IL verification; use the net472 game test project instead of a modern .NET console when `GetOriginalInstructions` rejects a method with a valid body.
- Validate cross-process clock origins before timing replicated objects. This Unity Mono runtime's Stopwatch values were offset between processes; use and validate a shared native counter for same-machine timestamps.
- QA cleanup must target exact spawned network IDs: dedicated servers can unload GameObjects while retaining their ZDOs, and native destruction requires ownership. Verify appearance and replicated disappearance in a pilot before accepting performance windows.

- GitHub's Markdown renderer adds HTML attributes and wrappers, including `class="notranslate"` on inline code. Match all tags with optional attributes (for example, `<table\b[^>]*>` and `<code\b[^>]*>`) instead of requiring bare tags; otherwise valid rendering can fail verification.
- Dispose Mono.Cecil assemblies/resolvers before rebuilding DLLs in the same PowerShell process, or finish inspection in a separate process. Deferred reads otherwise keep the output DLL locked on Windows.
- Do not infer a stalled live writer from Windows directory-entry file size. Open and read the file with sharing enabled; cached size metadata can remain stale until the writer closes it. Verify JSON records and completion/drop counters before diagnosing export failure.
- A background runner can publish a new run directory before its client log exists. Check `Test-Path` before reading role-specific logs; distinguish startup-in-progress from a failed launch.
- Validate relative documentation links inside the package as well as the repository. A new README link requires its target in the packaging whitelist.
