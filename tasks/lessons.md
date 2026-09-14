# Verification lessons

- GitHub's Markdown renderer adds HTML attributes and wrappers, including `class="notranslate"` on inline code. Match all tags with optional attributes (for example, `<table\b[^>]*>` and `<code\b[^>]*>`) instead of requiring bare tags; otherwise valid rendering can fail verification.
- Dispose Mono.Cecil assemblies/resolvers before rebuilding DLLs in the same PowerShell process, or finish inspection in a separate process. Deferred reads otherwise keep the output DLL locked on Windows.
- Do not infer a stalled live writer from Windows directory-entry file size. Open and read the file with sharing enabled; cached size metadata can remain stale until the writer closes it. Verify JSON records and completion/drop counters before diagnosing export failure.
