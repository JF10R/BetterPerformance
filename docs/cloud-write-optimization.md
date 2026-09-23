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

## Phase attribution inside CharacterSaveToDisk

`[Diagnostics] CharacterSaveDiskPhases` defaults to `true` and requires restart. It breaks
`PlayerProfile.SavePlayerToDisk` into the phases below, attributed only while that call is
on the stack on its own thread; `ZPackage.GenerateHash`/`GetArray` and `FileWriter` also
serve the world-save path and are excluded there by the same scope flag.

| Timing | Native scope | Interpretation |
| --- | --- | --- |
| `CharacterSaveCloudChecks` | `SaveSystem.PreSaveCloudChecksAndOperations` | Cloud quota check and pending move/backup decision |
| `CharacterSaveHash` | `ZPackage.GenerateHash` | `GetArray` copy plus `SHA512.ComputeHash` over the built package |
| `CharacterSaveWrite` | `FileWriter` construction through `Finish` | Spans the inline `m_binary.Write` calls between them: local file flush or the cloud chunk upload |
| `CharacterSaveReplace` | `FileHelpers.ReplaceOldFile` | `.old` rotation and the atomic rename/move into place |
| `CharacterSaveBackup` | `ZNet.ConsiderAutoBackup` | Auto-backup interval check, and the copy itself when due |

These are sequential, not nested, so summing them approximates (never exceeds) the parent
`CharacterSaveToDisk` total; the gap is the ZPackage build (stat/world-data serialization)
and the second `GetArray` call, neither separately timed. Gauges: `character_save_package_bytes`
(interval max of the written package length) and label `character_save_file_source`
(`local`/`cloud`/`none` when no save completed that interval).
