using System;
using System.Collections.Generic;
using System.Linq;
using BetterPerformance.Core;

internal static class CloudWriteBufferTests
{
    internal static void Run()
    {
        Check(CloudChunkBuffer.NativeChunkSize == 104857600, "native chunk size matches the decompiled constant");
        Check(CloudChunkBuffer.BufferLength(CloudChunkBuffer.NativeChunkSize, 40658) == 40658, "a 40 KB profile allocates 40 KB");
        Check(CloudChunkBuffer.BufferLength(CloudChunkBuffer.NativeChunkSize, 300000000) == CloudChunkBuffer.NativeChunkSize,
            "a payload above one chunk still allocates a whole chunk");
        Check(CloudChunkBuffer.BufferLength(CloudChunkBuffer.NativeChunkSize, CloudChunkBuffer.NativeChunkSize) == CloudChunkBuffer.NativeChunkSize,
            "an exact multiple of the chunk size allocates a whole chunk");
        // Degenerate inputs must keep the native allocation size so the caller cannot diverge.
        Check(CloudChunkBuffer.BufferLength(0, 40) == 0 && CloudChunkBuffer.BufferLength(-5, 40) == -5 &&
            CloudChunkBuffer.BufferLength(64, 0) == 64, "degenerate inputs keep the native allocation size");

        // Byte-level equivalence over the native loop, with small chunk sizes so that
        // multi-chunk payloads, exact multiples and remainders are all exercised.
        foreach (int chunkSize in new[] { 1, 2, 3, 7, 16, 64 })
            for (int length = 1; length <= 200; length++)
            {
                var payload = new byte[length];
                new Random(length * 131 + chunkSize).NextBytes(payload);
                int sized = CloudChunkBuffer.BufferLength(chunkSize, length);
                Check(sized <= chunkSize && sized <= Math.Max(length, chunkSize), $"sized buffer never grows chunk={chunkSize} length={length}");
                var native = Emit(chunkSize, payload, chunkSize);
                var patched = Emit(chunkSize, payload, sized);
                Check(native.Count == patched.Count, $"chunk count unchanged chunk={chunkSize} length={length}");
                for (int i = 0; i < native.Count; i++)
                    Check(native[i].SequenceEqual(patched[i]), $"chunk {i} bytes identical chunk={chunkSize} length={length}");
                Check(native.SelectMany(c => c).SequenceEqual(payload), $"native loop reconstructs the payload chunk={chunkSize} length={length}");
            }
    }

    // Faithful transcription of the verified Splatform.Steam.SteamCloud.WriteFile chunk
    // loop. Only the first `length` bytes of the scratch buffer reach Steam.
    private static List<byte[]> Emit(int chunkSize, byte[] data, int bufferLength)
    {
        var buffer = new byte[bufferLength];
        int count = CloudChunkBuffer.ChunkCount(chunkSize, data.Length);
        var chunks = new List<byte[]>();
        for (int i = 0; i < count; i++)
        {
            int length = CloudChunkBuffer.ChunkLength(chunkSize, data.Length, i);
            Array.Copy(data, i * chunkSize, buffer, 0, length);
            var chunk = new byte[length];
            Array.Copy(buffer, 0, chunk, 0, length);
            chunks.Add(chunk);
        }
        return chunks;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("CloudWriteBuffer: " + message);
    }
}
