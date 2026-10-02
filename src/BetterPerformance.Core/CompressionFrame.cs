using System;
using System.IO;
using System.IO.Compression;

namespace BetterPerformance.Core
{
    // A self-describing Deflate frame for one network payload.
    //
    // The frame exists so a receiver can tell a compressed payload from a plain one
    // without a handshake or a protocol version: a payload that does not start with the
    // magic is passed through untouched. Encode never returns a frame that is not
    // strictly smaller than the input, so framing can only ever reduce bytes on the wire,
    // and TryDecode answers false instead of throwing on anything it cannot verify.
    public static class CompressionFrame
    {
        // Steam's maximum message size; a header claiming more than this is refused.
        public const int MaxRawLength = 524288;
        // 'B','P','Z','1' followed by the int32 little-endian raw length.
        public const int HeaderLength = 8;
        private const byte Magic0 = (byte)'B', Magic1 = (byte)'P', Magic2 = (byte)'Z', Magic3 = (byte)'1';

        // Returns a frame when it is strictly smaller than raw, otherwise raw itself
        // (the same reference, so the caller can test for framing by reference).
        public static byte[] Encode(byte[] raw)
        {
            // A null input is handed straight back, as the contract says "unchanged".
            if (raw == null || raw.Length == 0 || raw.Length > MaxRawLength) return raw!;
            using (var buffer = new MemoryStream(raw.Length))
            {
                buffer.WriteByte(Magic0);
                buffer.WriteByte(Magic1);
                buffer.WriteByte(Magic2);
                buffer.WriteByte(Magic3);
                buffer.WriteByte((byte)raw.Length);
                buffer.WriteByte((byte)(raw.Length >> 8));
                buffer.WriteByte((byte)(raw.Length >> 16));
                buffer.WriteByte((byte)(raw.Length >> 24));
                using (var deflate = new DeflateStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
                    deflate.Write(raw, 0, raw.Length);
                if (buffer.Length >= raw.Length) return raw;
                return buffer.ToArray();
            }
        }

        public static bool IsFramed(byte[] data) => data != null && IsFramed(data, 0, data.Length);

        // The segment form reads a frame in place, e.g. inside a capacity-sized stream buffer;
        // only the count bytes from offset belong to the payload. A bad segment is not a frame.
        public static bool IsFramed(byte[] data, int offset, int count) =>
            data != null && offset >= 0 && count >= HeaderLength && offset <= data.Length - count &&
            data[offset] == Magic0 && data[offset + 1] == Magic1 && data[offset + 2] == Magic2 && data[offset + 3] == Magic3;

        public static bool TryDecode(byte[] data, out byte[] raw)
        {
            if (data != null) return TryDecode(data, 0, data.Length, out raw);
            raw = Array.Empty<byte>();
            return false;
        }

        // False for anything that is not a frame this encoder could have produced:
        // wrong magic, a length outside the bound, a truncated or corrupt payload, or a
        // payload whose inflated length disagrees with the header. Never throws.
        public static bool TryDecode(byte[] data, int offset, int count, out byte[] raw)
        {
            raw = Array.Empty<byte>();
            if (!IsFramed(data, offset, count)) return false;
            int length = data[offset + 4] | (data[offset + 5] << 8) | (data[offset + 6] << 16) | (data[offset + 7] << 24);
            if (length < 0 || length > MaxRawLength) return false;
            try
            {
                var output = length == 0 ? Array.Empty<byte>() : new byte[length];
                using (var buffer = new MemoryStream(data, offset + HeaderLength, count - HeaderLength, writable: false))
                using (var inflate = new DeflateStream(buffer, CompressionMode.Decompress))
                {
                    int read = 0;
                    while (read < length)
                    {
                        int step = inflate.Read(output, read, length - read);
                        if (step <= 0) break;
                        read += step;
                    }
                    if (read != length) return false;
                    // A payload that inflates past the declared length is not the payload we framed.
                    var probe = new byte[1];
                    if (inflate.Read(probe, 0, 1) > 0) return false;
                }
                raw = output;
                return true;
            }
            catch (Exception)
            {
                raw = Array.Empty<byte>();
                return false;
            }
        }
    }
}
