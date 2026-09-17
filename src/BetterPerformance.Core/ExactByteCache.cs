using System;
using System.IO;

namespace BetterPerformance.Core
{
    // One exact input/output pair, never exposed to callers. No hash-only hits.
    public sealed class ExactByteCache
    {
        private readonly int maximumBytes;
        private byte[]? input, output;
        public int RetainedBytes => (input?.Length ?? 0) + (output?.Length ?? 0);
        public ExactByteCache(int maximumBytes)
        {
            if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            this.maximumBytes = maximumBytes;
        }
        public void Clear() { input = output = null; }
        public bool TryWrite(ArraySegment<byte> candidate, BinaryWriter writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            if (input == null || output == null || candidate.Array == null || candidate.Count != input.Length) return false;
            for (int i = 0; i < input.Length; i++) if (input[i] != candidate.Array[candidate.Offset + i]) return false;
            writer.Write(output, 0, output.Length);
            return true;
        }
        // Publication without a copy. The caller transfers ownership of both arrays and must
        // never mutate them again; equality and the size bound are unchanged from Store.
        public bool Adopt(byte[] source, byte[] encoded)
        {
            if (source == null || encoded == null || (long)source.Length + encoded.Length > maximumBytes)
            { Clear(); return false; }
            Clear();
            input = source; output = encoded;
            return true;
        }
        public bool Store(ArraySegment<byte> source, ArraySegment<byte> encoded)
        {
            if (source.Array == null || encoded.Array == null || (long)source.Count + encoded.Count > maximumBytes)
            { Clear(); return false; }
            // Clear the prior entry before allocating so retained cache storage cannot grow with history.
            Clear();
            var copy = new byte[source.Count];
            var encodedCopy = new byte[encoded.Count];
            Buffer.BlockCopy(source.Array, source.Offset, copy, 0, copy.Length);
            Buffer.BlockCopy(encoded.Array, encoded.Offset, encodedCopy, 0, encodedCopy.Length);
            input = copy; output = encodedCopy;
            return true;
        }
    }
}
