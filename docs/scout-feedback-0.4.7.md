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

### Not tested

CLI indexing, the scout-gate hook (not installed on this repo), cross-repo `references` with both indexes passed as an array, artifact views, `note_finding` / `recall_investigation`, and any timing comparison against a previous build.
