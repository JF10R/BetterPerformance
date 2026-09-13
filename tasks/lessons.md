# Verification lessons

- GitHub's Markdown renderer adds HTML attributes and wrappers. Match tags with optional attributes (for example, `<table\b[^>]*>`) instead of requiring the exact string `<table>`; otherwise valid rendering can fail verification.
