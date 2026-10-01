# Steam cloud write buffer

## Finding

`Splatform.Steam.SteamCloud.WriteFile` allocates `new byte[104857600]` as a scratch chunk
buffer on every cloud file write, whatever the payload. One character save performs three
cloud writes, so it allocates and zeroes about 300 MiB for a ~40 KB profile. Details and
the measured save window are in the research notes, kept outside the repository.

## What changes

A transpiler replaces the single `newarr byte` with a call to
`CloudWriteOptimization.AllocateChunkBuffer(chunkSize, data)`, which returns
`new byte[Math.Min(chunkSize, data.Length)]`. Every other instruction is preserved; one
`ldarg.2` is inserted to push the payload. `chunkSize` is the writer's own local, not a
hardcoded constant, so a changed native constant cannot shrink the buffer below one chunk.

Config: `[CharacterSave] CloudWriteBufferSizedToPayload`, default `false`, restart to install.

## Invariants

- The loop copies `Array.Copy(data, i * chunkSize, buffer, 0, n)` and writes exactly `n`
  bytes, where `n` is `chunkSize` or `data.Length % chunkSize`. Installation validates the
  chunk count, the loop bounds, the length select and both branches, and that the buffer is
  used only as the copy destination and the write argument. Those bound every `n` by
  `min(chunkSize, data.Length)`, so the bytes handed to Steam are identical.
- Any contract mismatch leaves the method unpatched and reports `unsupported_layout`.
- The dedicated server ships no `Splatform.Steam.dll`; install reports `type_unavailable`.
- Assumed, not proven here: `SteamRemoteStorage.FileWriteStreamWriteChunk` reads only
  `cubData` bytes of the pinned array, per its documented signature.

## Validating on a disposable cloud character

Never test on a real character. With diagnostics on and the option off, save a disposable
cloud character and read `CharacterSaveToDisk` plus `cloud_writes`, `cloud_write_bytes`,
`cloud_write_max_bytes` and `cloud_write_elapsed_sum/max`. Expect three writes per save at
roughly the profile size. Turn the option on, restart, repeat the same save, and compare the
same gauges; `cloud_write_scratch_bytes_avoided` should show about 100 MiB per write and
`cloud_write_optimization_status` should read `installed`. Then reload the character and
confirm the map, pins and stats are intact.

## What it does not change

Chunk bytes, chunk count, write order, the `.old` rotation, quota checks, backup rotation,
and the map serialization and `ZPackage` build that stay on the main thread. It is an
allocation fix only. `cloud_write_bytes` is payload, not Steam quota. No file names are
exported.

## Phase attribution of a character save

`[Diagnostics] CharacterSaveDiskPhases` defaults to `true` and requires restart. It breaks
`Game.SavePlayerProfile` (`CharacterSave`) and `PlayerProfile.SavePlayerToDisk`
(`CharacterSaveToDisk`) into the main-thread phases below, each attributed only while its
parent is on the stack of the same thread; `ZPackage.GenerateHash`/`GetArray`, `FileWriter`,
`Inventory.Save` and the save catalog also serve world saves and containers and are excluded
there. All timings are elapsed ms. *Hook* rows time one native call; *gap* rows time the span
between two hooked calls, whose native order the game-contract test pins.

| Timing | Kind | Native scope |
| --- | --- | --- |
| `CharacterSavePlayerData` | hook | `PlayerProfile.SavePlayerData`: `Player.Save` plus a `GetArray` copy |
| `CharacterSaveInventory`, `CharacterSaveSkills` | hook, nested in PlayerData | `Inventory.Save`, `Skills.Save` |
| `CharacterSaveMapData` | hook | `Minimap.SaveMapData`: `GetMapData` (`MapSerialization`) and its store |
| `CharacterSaveCloudChecks` | hook | `SaveSystem.PreSaveCloudChecksAndOperations` |
| `CharacterSaveBuild` | gap | cloud checks to hash: folder check, stats, world data (map bytes copied in), player data |
| `CharacterSaveHash` | hook | `ZPackage.GenerateHash`: a `GetArray` copy plus SHA-512 |
| `CharacterSaveGetArray` | gap | hash to `FileWriter`: the second full package copy, nothing else |
| `CharacterSaveOpen` | hook | `FileWriter` constructor: `File.Create` (local) or a `MemoryStream` (cloud) |
| `CharacterSaveBufferWrite` | gap | constructor to `Finish`: the inline `BinaryWriter.Write` calls only |
| `CharacterSaveFinish` | hook | `FileWriter.Finish`: flush, `Flush(flushToDisk: true)` (local) or chunked cloud upload, close |
| `CharacterSaveWrite` | hook span | constructor end through `Finish` = BufferWrite + Finish; kept for older captures |
| `CharacterSaveReplace` | hook | `FileHelpers.ReplaceOldFile`: `.old` rotation and rename |
| `CharacterSaveBackup` | hook | `ZNet.ConsiderAutoBackup` |
| `CharacterSaveCatalogReload` | hook, nested in Backup or CloudChecks | `SaveCollection.Reload` (characters only): lists the folder and stats every file, backups included; forced because the save invalidates the catalog twice first |
| `CharacterSaveBackupCopy` | hook, nested in Backup | `SaveSystem.Copy`, only when an auto-backup is due |
| `CharacterSaveToDiskUnaccounted` | remainder | `CharacterSaveToDisk` minus CloudChecks, Build, Hash, GetArray, Open, BufferWrite, Finish, Replace, Backup |
| `CharacterSaveUnaccounted` | remainder | `CharacterSave` minus PlayerData, MapData, ToDisk: mount, loading-screen push/pop, logout point, cloud-capacity check, achievement sync |

The non-nested rows of one level sum to their parent; a remainder is clamped at 0 and also
absorbs a failed-cloud local dump and any probe named in `character_save_probes_missing`
(`none` when all installed). Gauges, per capture interval: `character_saves` and
`character_save_trigger_{periodic,server_rpc,logout,sleep_wake,other}` (only saves that reached
the disk; the trigger comes from the call arguments, `Game.m_shuttingDown` and
`m_saveTimer > m_saveInterval` read before the save zeroes it),
`character_save_direct_disk_saves` (a disk save outside `SavePlayerProfile`), and interval
maxima `character_save_package_bytes`, `character_save_player_data_bytes`,
`character_save_map_data_bytes` (compressed) and `character_save_catalog_files`. Label
`character_save_file_source` is `local`/`cloud`/`none`. Map compression itself is timed by the
map compression cache gauges, not here.
