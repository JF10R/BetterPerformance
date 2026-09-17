# Scout / Rune developer feedback: 2026-09-16 session analysis

Agent-to-agent notes for the Scout developer. Context: the MCP server `repo-scout-global` (progressive index, cache `D:/scout-cache-u01`) used from Claude Code on the plugin repo (186 files, 645 defs, C# + Python) and, from the same session, on a second, private local index of game-side C# (1,025 files, not part of any repository). Natural development use, not a benchmark; no old/new binary comparison.

### Scenarios exercised

| # | Need | Tool and query | Outcome |
| --- | --- | --- | --- |
| 1 | Is the repo indexed and healthy | `repo_status` | ok; cortex 838/838 symbols; C# reported as `partial_name_extraction`, `symbol_calls: unavailable` |
| 2 | Find where a status string is produced | `code_search mode=content "Compatible foreign_generate_patch MinimapTextureCache"` | high certainty, right file, but the snippet shown was the class header (L12-42), not the match at L389 |
| 3 | Read one method's body with context | `cognitive_read depth=D3 focus=Compatible` | exact; literal-match fallback ("--- literal-match L258 ---") landed 130 lines before the method and showed unrelated IL-walking code first |
| 4 | Install order in a 500-line file | `cognitive_read Plugin.cs depth=D3 focus=Awake` | outline plus the 106-line body: the single most useful call of the day |
| 5 | Every Harmony instance id in the repo | `code_search mode=content "new Harmony(Plugin.PluginId"` | miss: returned `OnDestroy` via token fallback and Package.ps1; plain grep found the two lines. Parenthesis/dot-heavy literals defeat the content lane |
| 6 | Exact lines after locating | `read_range` L360-410, L614-646 and others | exact, deduplicated, cheap; the 400-line cap is fine |
| 7 | Index a directory that is not a git repo | `repo_status` on a scratchpad dir, then on the private game-side index | auto-index worked (692 then 1,025 files), path-only phase first, structural ready within the 30 s wait |
| 8 | Symbol by kind in the game index | `code_search mode=structure "fn:TerrainCheck"` | exact, two definitions, ~40 tokens |
| 9 | Who references a field / method | `mode=references` on `m_hitEffectAreaCenter`, `m_setActiveGroupEffects`, `SetActiveGroup`, `GetDropList`, `ButtonSfx` | fields: "not in the symbol index, INDEX GAP" but raw token hits listed, which was enough. `SetActiveGroup`: 18 calls with "+8 more in 1 files" and no way to page them. `ButtonSfx`: "0 references" while `InventoryGui` and Unity prefabs use it by component, correctly flagged repo-scoped |
| 10 | Find a class when the file name is unknown | `code_search mode=content "class UIGroupHandler"` | high certainty, right file in a sibling assembly directory |
| 11 | Outline of a game class | `cognitive_read ZSFX.cs depth=D2`, `InventoryGui.cs depth=D3 focus=UpdateCraftingPanel` | complete outlines with line ranges and control-flow tags; D3 body exact. CHAIN callees were name-approx noise (`RetrieveFromBoolSource` via `delegate`) |

### What worked

- The progressive index makes a fresh 1,000-file directory usable in well under a minute, with an explicit phase label instead of a silent empty result. This is what made reading the game code practical.
- `cognitive_read` at D3 with a focus symbol replaces five to ten Reads. Outline line ranges were exact every time, including on decompiled code with nested types.
- Honesty markers are consistently useful: `INDEX GAP` versus `0 references`, `REPO-SCOPED`, `SCOPE-RESTRICTED`, `@chain:name-approx`, `VERIFIED describes source/locator identity only`. Nothing had to be re-derived because a claim was overstated.
- `read_range` served exact bytes with no surprises, and served files of the second index by relative path.

### Friction, ranked

1. **Content lane on literal code fragments.** Query 5 is the shape a reviewer types most: an exact source fragment with punctuation. Token fallback returned unrelated top hits with "@certainty low" and the useful answer was absent. A trigram or literal-substring lane for queries that contain `(`, `.` or `"` would close the only case where grep beat Scout today.
2. **Literal-match fallback in `cognitive_read` picks the first token occurrence, not the definition.** Query 3 showed an IL-walking region because the string `Compatible` appeared there first; the method definition was in the STRUCT skeleton two screens later. Prefer the definition site when the focus resolves to a symbol.
3. **References output truncates without paging.** "18 calls ... +8 more in 1 files" leaves no handle to fetch the other eight. An `offset` or `all=true`, or at least the full line list when it is one file, would help.
4. **Fields are absent from the symbol index for C#.** `m_hitEffectAreaCenter`, `m_setActiveGroupEffects` (public fields of large classes) came back as index gaps with raw hits. The raw hits were correct, so the gap is presentational; indexing public fields would turn these into exact resolutions.
5. **CHAIN callee lines add noise on decompiled code.** `InventoryGui` callees listed three delegate-named files by name approximation; `@chain:name-approx` was present, but the lines still cost tokens. Suppressing name-approx callees when precision is under some threshold, or folding them into one line, would be cleaner.
6. **Content search shows the wrong snippet.** Query 2 chose the class header as the evidence span while the match was at L389. When the content lane matched a line, show that line's window.

7. **Gate blind until the per-repo server connects.** `install-gate` writes the hook into `.claude/settings.json` and the surfaced-file log path into `.mcp.json` as an env of the per-repo `repo-scout` server. In a session that only has the global server, the log is never written, so the gate denies every `Read` and every Bash read of a repo file ("not surfaced by Scout") no matter how many `code_search` calls precede them; a research agent hit it four times on `AGENTS.md`, an implementer on every file. `read_range` serves the file, so work continues at one round trip per file. Two fixes: the install message could say plainly that the gate stays blind until the session reconnects its MCP servers, and the gate could fall back to allow-with-log when its log file does not exist rather than deny.

### Follow-up: the rebuilt server, 2026-09-17

Same day, after the Scout developer's fixes, with the per-repo `repo-scout` server from `.mcp.json` connected and the gate live. Used for wiring three modules into a 550-line file, updating README and CHANGELOG, and reviewing three new modules.

- **Gate works as designed once its server is connected.** `Read` on `Plugin.cs` and `README.md` passed right after a `code_search` surfaced them; `Read` on `CHANGELOG.md` passed after a `cognitive_read` on it; `cat` of a gitignored `.qa/` report passed. Zero false denials in about twenty calls, against every call denied in the blind state. The remaining friction is the blind state itself (item 7 above).
- **Reindex is fast and honest.** `repo-scout index` rebuilt the cortex in 1.7 s (958 symbols) and 2.5 s (9,066 symbols); the CLI says `--wait-for-phase2` is a no-op, which is fine but the MCP status still advertises that flag in its retry hint. After the CLI reindex, `cognitive_read` on new files answered with `@substrate:degraded:graph graph_unproven=true recovery:reindex`: the server did not pick up the CLI's rebuild, so the hint sends the caller in a loop.
- **`repo_status` summary line contradicts its own cortex line.** `@mind ... ~lines:0 defs:0 types:0` while `@cortex` reports 958 covered symbols and the telemetry block reports the same. Probably the `@mind` line reads the path-only phase.
- **Markdown is a second-class citizen in the content lane.** `### 0.4.7` fell to token fallback and returned two unrelated docs with `ACTION AVOID`; the literal first words of a CHANGELOG bullet returned the C# module that shares the vocabulary, not `CHANGELOG.md`. `cognitive_read` on Markdown says `@struct:absent lang=unknown reason=no-grammar recovery:read_range`, which is honest and the right fallback, but a literal-substring lane over all indexed files would have answered both queries directly.
- **Query expansion adds noise on exact queries.** `Plugin.cs Awake MiningDropPlacement.Install` reported `QUERY_EXPANSION added=[cleanup destructor free]` and `@certainty low`, although the top result was right. Expansion should be skipped when the query contains an exact identifier that matched.
- **New file coverage was immediate.** Files created minutes earlier by other agents were indexed and outlined at D2 with correct line ranges, including nested classes and a coroutine.

### Verification after the reconnect, 2026-09-17 (later the same day)

Every friction above replayed with the same query text after `repo-scout index` on both indexes (cortex 959 and 9,066 symbols, incremental in 115 ms).

| # | Friction | Now |
| --- | --- | --- |
| 1 | Literal `new Harmony(Plugin.PluginId` missed | **Fixed.** `EXACT_MATCH matches=28`, every declaration listed with file and line |
| 2 | `cognitive_read` focus fell on the first token occurrence | **Fixed.** `@focus:definition-preferred`, window centred on the definition |
| 3 | References truncated with "+8 more" | **Fixed.** All 18 call sites listed |
| 4 | Fields absent from the symbol index | **Not fixed.** `m_hitEffectAreaCenter` is still `INDEX GAP` in references mode and `EXACT_MISS` under `field:` in structure mode; the raw token hits remain correct |
| 5 | Name-approx callee noise on decompiled code | **Fixed.** `callees: 0 (name-approx) (+3 low-precision withheld)` |
| 6 | Content evidence showed the class header | **Fixed** for the ranked result, whose window now contains the matching line; the `EVIDENCE`/`DEF` block under it still cites a different symbol (`ValidateGenerateShape` L116) than the result above it |
| 7 | Gate blind without the per-repo server | **Design unchanged**; with the server connected the gate is correct in both directions: an unsurfaced doc is refused, a surfaced file is served |
| — | MCP not seeing the CLI reindex | **Fixed.** Status shows the CLI's snapshot id; no `graph_unproven` on new files |
| — | `@mind` line at 0 lines and 0 defs | **Fixed.** 49,097 lines / 714 defs and 136,516 / 4,520 |
| — | Markdown in the content lane | **Fixed.** `### 0.4.7` resolves to `CHANGELOG.md:11` as an exact literal |
| — | Query expansion on exact queries | **Fixed.** `QUERY_EXPANSION skipped reason=exact-token-hit`; the response still says `@certainty low` with the right file on top |

One side effect of the new literal lane: a multi-word query that happens to appear verbatim in a document (this file, which quotes the queries) returns only that literal hit and skips the ranked code lane. Documenting a query now hides its code result; a `LITERAL_MISS`-style fallthrough after exact hits in non-code files would keep both.

### Not tested

CLI indexing, the scout-gate hook (not installed on this repo), cross-repo `references` with both indexes passed as an array, artifact views, `note_finding` / `recall_investigation`, and any timing comparison against a previous build.
