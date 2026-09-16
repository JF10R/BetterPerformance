# Character save research — 2026-09-15

Read-only research. No game process was started, no save was modified, no mod was installed.

## Verdict

The character-save stall is **not disk I/O and not payload size**. The session character is on Steam
Cloud, and Valheim's Steam cloud writer allocates a **100 MiB chunk buffer per cloud file write**,
regardless of payload. One character save performs **three** cloud writes (new file, `.old`
rotation, `.new` rename), so it allocates and zeroes about 300 MiB of scratch per save, plus a
fourth when an auto-backup is due. The profile itself is about 40 KB.

`SteamCloud.WriteFile`, `Splatform.Steam.dll`: `int num = 104857600; ... byte[] array = new byte[num];`

Recommended order: (1) confirm by moving the test character to local storage, no code; (2) a
~10-line patch that sizes that buffer to the payload; (3) treat the async worker as a later,
larger, riskier candidate that this finding largely removes the need for.

## Synchronous sequence

`Game.SavePlayerProfile` (`Game.cs:424`) then `PlayerProfile.SavePlayerToDisk` (`PlayerProfile.cs:227`).

| # | Step | Evidence | Cost driver |
|---|---|---|---|
| 1 | `SavePlayerData`, `Minimap.SaveMapData`, `SaveLogoutPoint` | `Game.cs:451-456` | map serialize + gzip, measured 17–65 ms |
| 2 | `FileHelpers.Mount(ReadWrite)` twice, nested | `Game.cs:441`, `PlayerProfile.cs:233` | `SteamRemoteStorage.BeginFileWriteBatch`, cheap |
| 3 | `PreSaveCloudChecksAndOperations` | `SaveSystem.cs:660` | quota/`CheckMove`, cheap when no move |
| 4 | ZPackage build: 10×205 stats, six dictionaries, map bytes | `PlayerProfile.cs:253-380` | main-thread only, mutates `m_lastSaveLoad` |
| 5 | `GenerateHash()` then `GetArray()` | `PlayerProfile.cs:382-383` | SHA512 + **two** full array copies; negligible at 40 KB |
| 6 | `FileWriter.Finish()` | `assembly_utils` `FileWriter` | cloud: `CloudFileWriteInChunks` → **100 MiB alloc**. Local: `m_file.Flush(flushToDisk: true)`, a real fsync |
| 7 | `FileHelpers.ReplaceOldFile` | `assembly_utils` `FileHelpers` | cloud: two `MoveFile` = `ReadFile`+`WriteFile`+`DeleteFile` each → **two more 100 MiB allocs**. Local: two `File.Move` |
| 8 | `SaveSystem.InvalidateCache(Character)` ×2 | `PlayerProfile.cs:385,398` | forces `SaveCollection.Reload` on next read |
| 9 | `ZNet.ConsiderAutoBackup` | `PlayerProfile.cs:402` | `Reload` enumerates every cloud file + one `GetFileTimestamp` each; when due, a full `CopyFile` = **fourth 100 MiB alloc** |

Steam Cloud is unconditionally *supported* on PC: `CloudStorageSupported => PlatformManager.DistributionPlatform.SaveDataProvider != null`, and `Splatform.Steam.dll` returns `SteamCloud`. So step 2 runs on every save even for local characters.

## Verified machine state

| Fact | Value | Source |
|---|---|---|
| Session character | the real character, Steam Cloud | `D:/Steam/userdata/<account>/892970/remote/characters/<name>.fch` |
| Profile size | 40,658 bytes | same file, written 2026-09-15 20:36 local |
| Auto-backup in window | 37,568 bytes at 19:52 | `<name>_backup_auto-20260915-195258.fch` |
| `AutoBackups` preference | 4 (default 2) | `HKCU:\Software\IronGate\Valheim`, `AutoBackups_h2630279319` |
| Cloud character files | 47 (68 total remote) | `remote/` file count |
| Uncompressed map payload | 8,388,617 bytes | `docs/frontier-runtime-2026-09-15.md` |

The session ran 19:23–20:39 local, so the 20:36 write and the 19:52 backup are inside it. The
40 KB profile makes bandwidth, SHA512 and the double `GetArray` copy irrelevant as explanations
for 130–202 ms.

## Prior art

**AsyncSave** (MidnightsFX, 0.5.0, ~1k downloads). Keeps the ZPackage build on the main thread
because it walks dictionaries gameplay mutates every frame, and hands only the finished `byte[]`
to one ordered worker that does SHA512, `FileWriter`, `ReplaceOldFile`, cache invalidation and
`ConsiderAutoBackup`. Its own comment records the baseline it fixed: *"measured at 219 ms on the
main thread before this change"*, close to our 130–202 ms. Gates it keeps: a single worker under
one `Gate` lock shared with its `SaveWorldThread` patch, because `SaveSystem.s_saveCollections`
and `FileHelpers.m_depotReferenceCounter` are unsynchronized statics; a 5-stage state machine
(`Idle/Compressing/ReadyToBuild/Writing/Done`); duplicate saves **dropped**, not queued; a 10 s
bounded `Drain` on `Game.Shutdown` and `OnApplicationQuit`; `ForceSync` after shutdown, reset in
`Game.Awake`; vanilla path for menu character creation; `SavingFinished` re-raised on the main
thread. Its repository has zero open or closed issues. Thunderstore states incompatibility with
SmoothSave (same patch targets) and self-disable when `ZNet.SaveWorld` fails to patch.

**Not transferable as-is to 1.0.12.** The inspected source targets an older layout: singular
`profile.m_playerStats.m_stats`, `PlayerProfile.GetCharacterFolderPath`, a `FileWriter` without
`CloudStorageFileGrouping`, and no `PreSaveCloudChecksAndOperations`. 1.0.12 has 10 `PlayerStats`
blocks of 205 entries, moved the path helper to `SaveSystem`, and added the pre-save cloud step.
This compounds the `BitArray` mismatch already recorded in the save-optimization research.

**SmoothSave** addresses world-save ZDO copying, not the client character path. **OdinSaves**
(ComfyMods) saves the profile more often and is documented as having *removed* async profile
saving to work with Unity threading. **Iron Gate** shipped chunked world saves in 1.0 and
resilience against mid-write failures; no async character save appears in the patch notes.

## Recommended work, in order

**A. Confirm the cause, zero code.** Use the in-game "Manage saves" menu to move the *disposable*
test character to local storage, then repeat the measurement. Do not toggle Steam Cloud off for
the account: that risks a sync conflict on the real character.

**B. Size the Steam chunk buffer to the payload.** A Harmony prefix or transpiler on
`SteamCloud.WriteFile` (internal type in `Splatform.Steam.dll`, reach it with
`AccessTools.TypeByName`) replacing `new byte[104857600]` with `new byte[Math.Min(104857600, data.Length)]`,
or passing `data` itself when it fits one chunk. Output bytes are identical by construction: the
loop only copies a prefix of `data` into the scratch array and writes `num3` bytes of it. Guard
with the same contract validation `MapCompressionCache` already uses, and fall back to vanilla if
the constant or the loop shape does not match. This is a pure allocation fix with no threading,
no state machine and no ordering risk.

**C. Only then consider the ordered worker.** Everything up to the `byte[]` stays on the main
thread, as AsyncSave does and as our map work already does.

Invariants any async design must hold, all of them load-bearing:
never lose the last good `.fch`; never interleave two character saves, and never run beside
`ZNet.SaveWorldThread` (`SaveSystem` collections and the mount refcount are unsynchronized, and
`m_depotReferenceCounter++` is not `Interlocked`); `SteamCloud.MountDepot` refuses a second
concurrent mount and Steam permits one write batch at a time, so the worker and the main thread's
own `Mount`/`Unmount` pair must be mutually exclusive; `PlayerProfile.m_fileSource` may be
rewritten to `Local` by the quota path and the worker must use the value captured at enqueue;
backup rotation must stay ordered after the replace; `SavingStarted`/`SavingFinished` must be
raised on the main thread; the logout, shutdown and quit paths must join with a bounded timeout;
character creation from the menu must stay fully synchronous.

Byte-parity verification, on a disposable character only: build the package on the main thread,
write it through both the native and the patched path into two temp files, compare SHA-256, then
load the character back through the normal profile loader and compare the decompressed map, the
pin set and the stat blocks. This is the same shape as the existing map-cache fixture.

## Expected gain

Removing the three 100 MiB allocations should remove most of the 130–202 ms `CharacterSaveToDisk`
window. What stays on the main thread either way: map serialization 17–65 ms unless the cache
hits, the ZPackage build, six Steam IPC calls, and the `SaveCollection.Reload` that
`ConsiderAutoBackup` forces over 47 cloud files. The async worker would additionally hide those,
at the cost of the whole invariant list above; it does not reduce the allocation itself.

## Risks and what not to do

Do not disable Steam Cloud on the user's account to chase this. Do not reorder or skip the
`.old` rotation: it is the only copy that survives a failed write. Do not run the backup copy
concurrently with the replace. Do not port AsyncSave's `MapSnapshot` (`Buffer.BlockCopy` over
`BitArray`) or its package builder; both target a different Valheim. Windows Defender and the
local SSD are **not** implicated here, because with a cloud file source `FileWriter` never opens
a local `FileStream` and the `Flush(flushToDisk: true)` fsync never runs; they would become
relevant only after arm A moves the character local. The unrelated 1.66 s machine-wide freeze in
the same session is a separate item already recorded in the session report.

## Sources

- Decompiled Valheim 1.0.12: `Game.cs`, `PlayerProfile.cs`, `SaveSystem.cs`, `SaveCollection.cs`, `ZNet.cs`, `ZPackage.cs`, `Minimap.cs`; `assembly_utils.dll` (`FileWriter`, `FileHelpers`); `Splatform.Steam.dll` (`SteamCloud`).
- [AsyncSave repository](https://github.com/MidnightsFX/Valheim_AsyncSave) at `07b90b229bc0d933bbad68511e4fb81280c09e55` — `ProfileSave.cs`, `SaveIo.cs`, `SavePlayerToDiskPatch.cs`, `SavePlayerProfilePatch.cs`, `ShutdownPatches.cs`.
- [AsyncSave on Thunderstore](https://thunderstore.io/c/valheim/p/MidnightMods/AsyncSave/)
- [OdinSaves on Thunderstore](https://thunderstore.io/c/valheim/p/ComfyMods/OdinSaves/)
- [SmoothSave](https://github.com/blaxxun-boop/SmoothSave)
- [Valheim 1.0 save-system notes](https://low.ms/blog/valheim-1-0-patch-notes-server-owners)
- [Steam FileWriteStreamWriteChunk](https://partner.steamgames.com/doc/api/ISteamRemoteStorage#FileWriteStreamWriteChunk)
