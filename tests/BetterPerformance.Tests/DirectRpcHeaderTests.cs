using System;
using System.IO;
using System.Text;
using BetterPerformance.Core;

internal static class DirectRpcHeaderTests
{
    internal static void Run()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        writer.Write((byte)17);
        writer.Write(unchecked((int)0xFEDCBA98));
        writer.Write(1234);
        stream.Position = 1;
        Check(DirectRpcHeader.TryRead(reader, out int hash) && hash == unchecked((int)0xFEDCBA98),
            "Read the signed little-endian method hash at the current position.");
        Check(stream.Position == 1 && reader.ReadInt32() == hash && reader.ReadInt32() == 1234,
            "Observation must leave the native reader position and payload unchanged.");
        for (int remaining = 0; remaining < 4; remaining++)
        {
            stream.Position = stream.Length - remaining;
            Check(!DirectRpcHeader.TryRead(reader, out _) && stream.Position == stream.Length - remaining,
                "Truncated identifiers must be skipped without moving the cursor.");
        }
        stream.Position = stream.Length + 1;
        Check(!DirectRpcHeader.TryRead(reader, out _), "A cursor beyond length must be rejected.");
        Check(!DirectRpcHeader.TryRead(null, out _), "A missing reader is unsupported.");

        byte[] buffer = { 99, 99, 0x78, 0x56, 0x34, 0x12, 99 };
        using var sliced = new MemoryStream(buffer, 2, 4, false, true);
        using var slicedReader = new BinaryReader(sliced);
        Check(DirectRpcHeader.TryRead(slicedReader, out hash) && hash == 0x12345678 && sliced.Position == 0,
            "A publicly visible slice must honor its buffer offset.");
        using var hidden = new MemoryStream(buffer, false);
        using var hiddenReader = new BinaryReader(hidden);
        Check(!DirectRpcHeader.TryRead(hiddenReader, out _), "A non-exposable buffer must not be copied.");
        using var customReader = new CustomReader(stream);
        using var customStream = new CustomStream();
        using var customStreamReader = new BinaryReader(customStream);
        Check(!DirectRpcHeader.TryRead(customReader, out _) && !DirectRpcHeader.TryRead(customStreamReader, out _),
            "Custom reader or stream semantics must not be guessed.");

        stream.Position = 1;
        for (int i = 0; i < 10000; i++) DirectRpcHeader.TryRead(reader, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) DirectRpcHeader.TryRead(reader, out _);
        Check(GC.GetAllocatedBytesForCurrentThread() == allocated,
            "Header observation must not allocate after warmup.");
        stream.Dispose();
        Check(!DirectRpcHeader.TryRead(reader, out _), "Disposed streams are unavailable.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Direct RPC header: " + message);
    }

    private sealed class CustomReader : BinaryReader
    {
        internal CustomReader(Stream stream) : base(stream, Encoding.UTF8, true) { }
    }

    private sealed class CustomStream : MemoryStream { }
}
