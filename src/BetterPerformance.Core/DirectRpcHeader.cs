using System;
using System.IO;

namespace BetterPerformance.Core
{
    // Observe only the identifier native ReadInt32 will consume. No cursor changes,
    // stream reads, payload copies, or retained references to the package buffer.
    public static class DirectRpcHeader
    {
        public static bool TryRead(BinaryReader? reader, out int methodHash)
        {
            methodHash = 0;
            if (reader == null || reader.GetType() != typeof(BinaryReader)) return false;
            try
            {
                if (!(reader.BaseStream is MemoryStream stream) || stream.GetType() != typeof(MemoryStream) ||
                    !stream.TryGetBuffer(out ArraySegment<byte> buffer) || buffer.Array == null) return false;
                long position = stream.Position;
                if (position < 0 || position > buffer.Count - sizeof(int)) return false;
                int offset = buffer.Offset + (int)position;
                byte[] bytes = buffer.Array;
                methodHash = bytes[offset] | bytes[offset + 1] << 8 |
                    bytes[offset + 2] << 16 | bytes[offset + 3] << 24;
                return true;
            }
            catch (ObjectDisposedException) { return false; }
        }
    }
}
