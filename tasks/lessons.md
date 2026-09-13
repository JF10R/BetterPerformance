# Verification lessons

- GitHub's Markdown renderer adds HTML attributes and wrappers. Match tags with optional attributes (for example, `<table\b[^>]*>`) instead of requiring the exact string `<table>`; otherwise valid rendering can fail verification.
- Dispose Mono.Cecil assemblies/resolvers before rebuilding DLLs in the same PowerShell process, or finish inspection in a separate process. Deferred reads otherwise keep the output DLL locked on Windows.
