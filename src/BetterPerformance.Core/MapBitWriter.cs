using System;
using System.Collections;
using System.IO;

namespace BetterPerformance.Core
{
    // Retains the native one-byte-per-boolean format. This is not bit-packed wire data.
    public static class MapBitWriter
    {
        public const int MaximumBits = 2048 * 2048;
        public const int ChunkBytes = 64 * 1024;
        private static readonly ulong[] ExpandedBytes = CreateLookup();
        [ThreadStatic] private static Workspace? workspace;

        private sealed class Workspace
        {
            internal readonly int[] Packed = new int[MaximumBits / 32];
            internal readonly ulong[] Expanded = new ulong[ChunkBytes / 8];
            internal readonly byte[] Output = new byte[ChunkBytes];
            internal bool Busy;
        }

        // False means no output was written. Once writing starts, exceptions propagate;
        // retrying vanilla after a partial write would corrupt the package.
        public static bool TryWrite(BinaryWriter writer, BitArray? bits, int count)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            if (bits == null || !BitConverter.IsLittleEndian || count < 0 || count > bits.Length || bits.Length > MaximumBits)
                return false;
            if (workspace != null && workspace.Busy) return false;
            if (count == 0) return true;
            var buffers = workspace ?? (workspace = new Workspace());
            buffers.Busy = true;
            try
            {
                bits.CopyTo(buffers.Packed, 0);
                for (int offset = 0; offset < count; offset += ChunkBytes)
                {
                    int length = Math.Min(ChunkBytes, count - offset);
                    int groups = (length + 7) / 8;
                    for (int group = 0; group < groups; group++)
                    {
                        int bit = offset + group * 8;
                        uint word = unchecked((uint)buffers.Packed[bit / 32]);
                        buffers.Expanded[group] = ExpandedBytes[(word >> (bit % 32)) & 255];
                    }
                    Buffer.BlockCopy(buffers.Expanded, 0, buffers.Output, 0, length);
                    writer.Write(buffers.Output, 0, length);
                }
                return true;
            }
            finally { buffers.Busy = false; }
        }

        private static ulong[] CreateLookup()
        {
            var lookup = new ulong[256];
            for (int value = 0; value < lookup.Length; value++)
                for (int bit = 0; bit < 8; bit++)
                    lookup[value] |= (ulong)((value >> bit) & 1) << (bit * 8);
            return lookup;
        }
    }
}
