using System;
using System.IO;
using BetterPerformance.Core;

internal static class ExactByteCacheTests
{
    public static void Run()
    {
        var cache = new ExactByteCache(100);
        byte[] input = { 9, 1, 2, 3, 9 }, output = { 9, 4, 5, 9 };
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        Check(!cache.TryWrite(new ArraySegment<byte>(input, 1, 3), writer), "empty cache must miss");
        Check(cache.Store(new ArraySegment<byte>(input, 1, 3), new ArraySegment<byte>(output, 1, 2)), "bounded store");
        output[1] = 0;
        Check(cache.TryWrite(new ArraySegment<byte>(input, 1, 3), writer), "exact segment hits");
        Check(stream.ToArray()[0] == 4 && stream.Length == 2, "output owns copy and has no invented length prefix");
        input[2] = 7;
        Check(!cache.TryWrite(new ArraySegment<byte>(input, 1, 3), writer) && stream.Length == 2, "one-bit change misses without output");
        Check(!cache.TryWrite(new ArraySegment<byte>(input, 1, 2), writer), "different length misses");
        Check(cache.RetainedBytes == 5, "retained storage measured");
        cache.Store(new ArraySegment<byte>(new byte[] { 1, 2, 3 }), new ArraySegment<byte>(new byte[] { 8 }));
        Check(!cache.TryWrite(new ArraySegment<byte>(new byte[] { 1, 2, 4 }), writer), "tail difference matters");
        using var broken = new BinaryWriter(new BrokenStream());
        bool threw = false;
        try { cache.TryWrite(new ArraySegment<byte>(new byte[] { 1, 2, 3 }), broken); }
        catch (IOException) { threw = true; }
        Check(threw, "output failure propagates; cannot retry partial write");
        Check(!cache.Store(new ArraySegment<byte>(new byte[100]), new ArraySegment<byte>(new byte[1])) && cache.RetainedBytes == 0, "oversize clears cache");
        cache.Store(new ArraySegment<byte>(Array.Empty<byte>()), new ArraySegment<byte>(new byte[] { 4 }));
        Check(cache.TryWrite(new ArraySegment<byte>(Array.Empty<byte>()), writer), "empty input is valid");
        cache.Clear(); Check(cache.RetainedBytes == 0, "world/end clear releases references");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class BrokenStream : MemoryStream
    { public override void Write(byte[] buffer, int offset, int count) => throw new IOException("injected output failure"); }
}
