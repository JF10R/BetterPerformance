# Synthetic map serialization benchmark

The experiment found a large opportunity in converting map bits into Valheim's byte-per-boolean save representation. It did **not** measure Valheim FPS, multiplayer latency, or a deployed save optimization.

### Compatibility finding

The installed Valheim 1.0.12 map fields are `BitArray`, not `bool[]`. A direct `Buffer.BlockCopy` of those fields is invalid. The standalone baseline therefore uses `BitArray` indexer reads followed by `BinaryWriter.Write(bool)`. No timed bool-array baseline was used.

Independently implemented candidates first copy the bit representation, then emit the same zero/one byte representation. The packed lookup version expands each input byte through a generated 256-entry table. It requires little-endian storage and checks that requirement. These are prototypes, not copied mod implementations.

### Method

Ran on 2026-09-15, AMD Ryzen 7 5800X3D, 8 cores/16 logical processors. The executable targets `net472`, running 64-bit desktop .NET Framework, **not Unity Mono**. It references no Valheim assemblies and touches no world or character data.

Four deterministic fixtures each contain two 2048x2048 bit arrays: sparse, dense, random and a spatial pattern with 250 synthetic pins. Before timing, all four variants passed exact uncompressed-payload equality and GZip decompression parity on every fixture. Uncompressed sizes were 8,388,617 bytes, or 8,402,507 for the pin fixture. GZip Fastest compressed sizes were respectively 142,531; 1,657,169; 1,795,118; and 78,993 bytes, identical in length across variants.

Each fixture/variant has 12 paired measurements after two warmup rounds. Order is counterbalanced across four positions, with fixture order rotated between blocks. Each variant occupies each position three times. No forced GC occurs inside timed runs. Allocations are measured in separate trials using `GC.GetAllocatedBytesForCurrentThread`, three repeats per combination.

### Results

Final prototype medians in milliseconds. “Combined” includes the measured serialization phase and GZip Fastest compression; it is not the complete game save lifecycle.

| Fixture | Bit loop serialize | Copy to bool + bulk | Packed lookup, fresh | Packed lookup, reused | Bit loop combined | Fresh lookup combined | Reused lookup combined |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Sparse | 81.196 | 14.070 | 4.040 | 7.637 | 117.548 | 39.273 | 41.618 |
| Dense | 98.568 | 14.160 | 4.332 | 7.245 | 201.181 | 107.321 | 110.908 |
| Random | 103.217 | 14.026 | 4.321 | 7.617 | 217.394 | 118.127 | 121.789 |
| Pin pattern | 80.740 | 14.167 | 4.107 | 7.275 | 115.810 | 37.693 | 40.394 |

Ranges show variation hidden by medians:

| Fixture | Bit loop combined range ms | Fresh lookup combined range ms | Reused lookup combined range ms |
| --- | ---: | ---: | ---: |
| Sparse | 106.479–160.082 | 35.593–50.139 | 40.048–51.091 |
| Dense | 190.157–241.571 | 102.673–124.082 | 104.988–125.792 |
| Random | 208.939–257.195 | 113.732–133.966 | 116.699–143.314 |
| Pin pattern | 105.598–139.240 | 35.664–48.651 | 38.403–47.268 |

Fresh lookup's paired mean combined differences versus the loop baseline were −84.827 ms for sparse, −97.261 ms for dense, −102.004 ms for random and −78.388 ms for the pin pattern. Exploratory 95% paired Student-t intervals were respectively [−95.732, −73.922], [−105.908, −88.614], [−109.620, −94.387] and [−83.889, −72.888] ms. These intervals assume sufficiently independent, approximately normal block differences; one host/process session cannot establish that assumption or predict Unity performance.

Serialization allocated about 32 MiB per baseline call because its fresh `MemoryStream` grows repeatedly, versus 12 MiB for copying to bools, 12.5 MiB for fresh packed lookup, and zero measured steady-state serialization allocations for the reused path. The pin fixture adds about 0.014 MiB to preallocated candidates. Reused buffers retain about 12.5 MiB per fixture; allocation avoidance is not free memory. Combined allocations still include GZip/output arrays: approximately 0.638, 5.586, 5.718 and 0.327 MiB for the reused path across the four fixtures.

The reused path was slower than fresh lookup here despite avoiding serialization allocation. An initial prototype redundantly cleared a reused stream; removing that work improved it, but did not reverse that ranking. Both result sets are retained locally. Compression remains the dominant measured phase after expansion is optimized: final fresh-lookup GZip medians range from 33.449 to 113.504 ms.

### Limits and next decision

- The input bit arrays are immutable synthetic fixtures. Safely capturing live state, snapshot consistency, thread ownership and concurrent mutation are not tested.
- The pin suffix is constructed before timing and appended identically; actual pin traversal/string serialization is excluded. The outer version-8 map envelope is also excluded.
- GZip Fastest is representative compression work, not proof of the game runtime's exact compressor implementation or compressed-byte identity. Decompressed payload parity was verified.
- BinaryWriter/BitArray/GC/JIT performance differs between desktop .NET Framework and Unity Mono. There is no measured game FPS gain, saved frame time, or complete character-save improvement yet.
- Save durability, temporary-file replacement, backups, cloud integration and shutdown behavior are untouched and untested by this prototype.

The result supports a narrow next experiment: preserve the native map format, compression and save lifecycle while replacing only bit-to-byte expansion, then verify actual game output and runtime behavior in isolation. It does not justify moving the whole save method onto a worker thread.

### Local reproduction

The source, raw synthetic results, hashes and commands are retained under `.qa/frontier-map-benchmark/` in the local workspace. This ignored experiment directory is not a shipped mod or a public benchmark-source distribution. `README.md` there specifies the full protocol; `results-final/summary.md` includes all phase medians, ranges, allocation trials and paired uncertainty estimates.
