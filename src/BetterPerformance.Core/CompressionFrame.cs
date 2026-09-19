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

        public static bool IsFramed(byte[] data) =>
            data != null && data.Length >= HeaderLength &&
            data[0] == Magic0 && data[1] == Magic1 && data[2] == Magic2 && data[3] == Magic3;

        // False for anything that is not a frame this encoder could have produced:
        // wrong magic, a length outside the bound, a truncated or corrupt payload, or a
        // payload whose inflated length disagrees with the header. Never throws.
        public static bool TryDecode(byte[] data, out byte[] raw)
        {
            raw = Array.Empty<byte>();
            if (!IsFramed(data)) return false;
            int length = data[4] | (data[5] << 8) | (data[6] << 16) | (data[7] << 24);
            if (length < 0 || length > MaxRawLength) return false;
            try
            {
                var output = length == 0 ? Array.Empty<byte>() : new byte[length];
                using (var buffer = new MemoryStream(data, HeaderLength, data.Length - HeaderLength, writable: false))
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
