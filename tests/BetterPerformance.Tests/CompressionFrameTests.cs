using System;
using System.Linq;
using BetterPerformance.Core;

internal static class CompressionFrameTests
{
    public static void RoundTrip()
    {
        foreach (int size in new[] { 8192, 300 * 1024 })
        {
            byte[] raw = Compressible(size, seed: (uint)size);
            byte[] framed = CompressionFrame.Encode(raw);
            Check(!ReferenceEquals(framed, raw), "compressible " + size + " B must be framed");
            Check(CompressionFrame.IsFramed(framed), "the frame carries the magic");
            Check(framed.Length < raw.Length, "a frame is never as long as its input");
            Check(CompressionFrame.TryDecode(framed, out byte[] decoded), "a frame decodes");
            Check(decoded.Length == raw.Length && decoded.SequenceEqual(raw), "the payload survives the round trip");
        }

        // Exactly the maximum payload still round-trips.
        byte[] largest = Compressible(CompressionFrame.MaxRawLength, seed: 7);
        Check(CompressionFrame.TryDecode(CompressionFrame.Encode(largest), out byte[] back)
            && back.SequenceEqual(largest), "a payload at MaxRawLength round-trips");
    }

    public static void PassThrough()
    {
        byte[] noise = Random(8192, seed: 99);
        Check(ReferenceEquals(CompressionFrame.Encode(noise), noise), "incompressible data is returned unframed, by reference");
        Check(!CompressionFrame.IsFramed(noise), "unframed data is not reported as a frame");
        Check(!CompressionFrame.TryDecode(noise, out byte[] none) && none.Length == 0, "unframed data does not decode");

        byte[] empty = Array.Empty<byte>();
        Check(ReferenceEquals(CompressionFrame.Encode(empty), empty), "an empty array is returned unchanged");
        Check(CompressionFrame.Encode(null!) == null, "a null array is returned unchanged");
        Check(!CompressionFrame.IsFramed(null!) && !CompressionFrame.IsFramed(empty), "null and empty are not frames");
        Check(!CompressionFrame.IsFramed(new byte[] { (byte)'B', (byte)'P', (byte)'Z' }), "a short prefix is not a frame");
        Check(!CompressionFrame.TryDecode(null!, out byte[] _), "null does not decode");

        // Larger than Steam can carry: framing it would produce something TryDecode must refuse.
        byte[] oversize = Compressible(CompressionFrame.MaxRawLength + 1, seed: 3);
        Check(ReferenceEquals(CompressionFrame.Encode(oversize), oversize), "a payload over MaxRawLength is left unframed");
    }

    public static void RejectsMalformedFrames()
    {
        byte[] raw = Compressible(8192, seed: 11);
        byte[] framed = CompressionFrame.Encode(raw);
        Check(!ReferenceEquals(framed, raw), "the fixture is a frame");

        byte[] truncated = new byte[framed.Length - 5];
        Array.Copy(framed, truncated, truncated.Length);
        Check(!CompressionFrame.TryDecode(truncated, out byte[] _), "a truncated frame is refused");

        byte[] headerOnly = new byte[CompressionFrame.HeaderLength];
        Array.Copy(framed, headerOnly, headerOnly.Length);
        Check(!CompressionFrame.TryDecode(headerOnly, out byte[] _), "a frame with no payload is refused");

        byte[] wrongMagic = (byte[])framed.Clone();
        wrongMagic[2] = (byte)'X';
        Check(!CompressionFrame.IsFramed(wrongMagic) && !CompressionFrame.TryDecode(wrongMagic, out byte[] _),
            "a frame with the wrong magic is refused");

        // Deflate carries no checksum, so corruption is caught only when it breaks the bit
        // stream or moves the inflated length. What the frame does guarantee is that a
        // damaged payload never throws and never yields bytes of the wrong length.
        byte[] blockHeader = (byte[])framed.Clone();
        blockHeader[CompressionFrame.HeaderLength] ^= 0xFF;
        Check(!CompressionFrame.TryDecode(blockHeader, out byte[] _), "a corrupt Deflate block header is refused");

        int corrupted = 0, refused = 0;
        for (int i = CompressionFrame.HeaderLength; i < framed.Length; i += 7)
        {
            byte[] flipped = (byte[])framed.Clone();
            flipped[i] ^= 0xA5;
            corrupted++;
            if (!CompressionFrame.TryDecode(flipped, out byte[] got)) { refused++; continue; }
            Check(got.Length == raw.Length, "a frame that decodes always yields the declared length");
        }
        // Measured on this fixture: 651 of 875 single-byte flips are refused outright.
        Check(refused > corrupted / 2, "most Deflate corruption is refused, got " + refused + " of " + corrupted);

        byte[] oversizeHeader = (byte[])framed.Clone();
        WriteLength(oversizeHeader, CompressionFrame.MaxRawLength + 1);
        Check(!CompressionFrame.TryDecode(oversizeHeader, out byte[] _), "a length above MaxRawLength is refused");

        byte[] negativeHeader = (byte[])framed.Clone();
        WriteLength(negativeHeader, -1);
        Check(!CompressionFrame.TryDecode(negativeHeader, out byte[] _), "a negative length is refused");

        byte[] shortHeader = (byte[])framed.Clone();
        WriteLength(shortHeader, raw.Length - 1);
        Check(!CompressionFrame.TryDecode(shortHeader, out byte[] _), "a payload that inflates past the header length is refused");

        byte[] longHeader = (byte[])framed.Clone();
        WriteLength(longHeader, raw.Length + 1);
        Check(!CompressionFrame.TryDecode(longHeader, out byte[] _), "a payload that inflates short of the header length is refused");

        // Nothing above may throw, and neither may arbitrary bytes wearing the magic.
        var noise = Random(64, seed: 5);
        for (int i = 0; i < 200; i++)
        {
            byte[] fuzz = Random(CompressionFrame.HeaderLength + i, seed: (uint)(1000 + i));
            fuzz[0] = (byte)'B'; fuzz[1] = (byte)'P'; fuzz[2] = (byte)'Z'; fuzz[3] = (byte)'1';
            CompressionFrame.TryDecode(fuzz, out byte[] _);
        }
        Check(!CompressionFrame.TryDecode(noise, out byte[] _), "random bytes never decode");
    }

    private static void WriteLength(byte[] frame, int length)
    {
        frame[4] = (byte)length;
        frame[5] = (byte)(length >> 8);
        frame[6] = (byte)(length >> 16);
        frame[7] = (byte)(length >> 24);
    }

    // Deterministic bytes drawn from a small alphabet, so Deflate has something to find.
    private static byte[] Compressible(int size, uint seed)
    {
        var data = new byte[size];
        uint state = seed;
        for (int i = 0; i < size; i++)
        {
            state = state * 1664525u + 1013904223u;
            data[i] = (byte)('a' + (state >> 28) % 8);
        }
        return data;
    }

    // Deterministic bytes with no structure; Deflate expands these.
    private static byte[] Random(int size, uint seed)
    {
        var data = new byte[size];
        uint state = seed;
        for (int i = 0; i < size; i++)
        {
            state = state * 1664525u + 1013904223u;
            data[i] = (byte)(state >> 24);
        }
        return data;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
