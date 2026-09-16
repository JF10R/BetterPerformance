using System;
using System.IO;
using System.Linq;
using BetterPerformance.Core;

internal static class PackageCopyTests
{
    internal static void Run()
    {
        foreach (int length in new[] { 0, 1, 31, 256, 65537, 1024 * 1024 })
        {
            var payload = new byte[length];
            new Random(length + 97).NextBytes(payload);
            using var source = new MemoryStream();
            using var sourceWriter = new BinaryWriter(source);
            sourceWriter.Write(payload);
            source.Position = length / 2; // Full logical contents, not remaining bytes.
            Parity(source, sourceWriter);
        }
        using (var source = new MemoryStream(new byte[] { 9, 8, 1, 2, 3, 7 }, 2, 3, true, true))
            Parity(source, new BinaryWriter(source)); // Origin/offset and fixed capacity.
        using (var source = new MemoryStream())
        using (var writer = new BinaryWriter(source))
        {
            writer.Write(new byte[] { 1, 2, 3 });
            Check(PackageCopy.TryWrite(writer, source, writer, source, out _) == PackageCopyOutcome.AliasedBuffer,
                "self-copy falls back before length prefix");
            Check(source.ToArray().SequenceEqual(new byte[] { 1, 2, 3 }) && source.Position == 3, "self-copy remains unchanged");
        }
        var shared = new byte[32];
        using (var first = new MemoryStream(shared, 0, 8, true, true))
        using (var second = new MemoryStream(shared, 16, 8, true, true))
            Check(PackageCopy.TryWrite(new BinaryWriter(first), first, new BinaryWriter(second), second, out _) ==
                PackageCopyOutcome.AliasedBuffer && first.Position == 0, "different streams over same array fall back");
        using (var source = new MemoryStream(new byte[4]))
        using (var destination = new MemoryStream())
            Check(PackageCopy.TryWrite(new BinaryWriter(destination), destination, new BinaryWriter(source), source, out _) ==
                PackageCopyOutcome.HiddenBuffer && destination.Length == 0, "nonexposed source buffer falls back");
        using (var source = new MemoryStream())
        using (var destination = new MemoryStream())
        {
            Check(PackageCopy.TryWrite(new CustomWriter(destination), destination, new BinaryWriter(source), source, out _) ==
                PackageCopyOutcome.UnsupportedStream && destination.Length == 0, "custom virtual writer semantics preserved");
            var sourceWriter = new BinaryWriter(source);
            source.Close();
            Check(PackageCopy.TryWrite(new BinaryWriter(destination), destination, sourceWriter, source, out _) ==
                PackageCopyOutcome.ClosedStream && destination.Length == 0, "closed source falls back");
        }
        // Native writes the prefix before discovering fixed-capacity output is too small.
        // The optimized path must propagate the same error and preserve the same partial bytes.
        using (var source = new MemoryStream(new byte[] { 1, 2, 3, 4 }, 0, 4, true, true))
        using (var native = new MemoryStream(new byte[5], 0, 5, true, true))
        using (var fast = new MemoryStream(new byte[5], 0, 5, true, true))
        {
            var expectedError = Failure(() => Native(new BinaryWriter(native), new BinaryWriter(source), source));
            var actualError = Failure(() => PackageCopy.TryWrite(new BinaryWriter(fast), fast, new BinaryWriter(source), source, out _));
            Check(expectedError == typeof(NotSupportedException) && actualError == expectedError &&
                native.ToArray().SequenceEqual(fast.ToArray()) && native.Position == fast.Position,
                "partial write failure is not swallowed or retried");
        }
    }

    private static void Parity(MemoryStream source, BinaryWriter sourceWriter)
    {
        using var expected = new MemoryStream();
        using var actual = new MemoryStream();
        using var expectedWriter = new BinaryWriter(expected);
        using var actualWriter = new BinaryWriter(actual);
        expectedWriter.Write(new byte[17]); actualWriter.Write(new byte[17]);
        expected.Position = actual.Position = 3; // Overwrite position with existing suffix.
        long position = source.Position;
        Native(expectedWriter, sourceWriter, source);
        Check(PackageCopy.TryWrite(actualWriter, actual, sourceWriter, source, out int copied) == PackageCopyOutcome.Copied,
            "supported package accepted");
        Check(copied == source.Length && source.Position == position && expected.Position == actual.Position &&
            expected.ToArray().SequenceEqual(actual.ToArray()), "exact logical bytes, length prefix and positions");
    }

    private static void Native(BinaryWriter destination, BinaryWriter sourceWriter, MemoryStream source)
    {
        sourceWriter.Flush(); source.Flush();
        byte[] snapshot = source.ToArray();
        destination.Write(snapshot.Length); destination.Write(snapshot);
    }
    private static Type? Failure(Action action) { try { action(); return null; } catch (Exception exception) { return exception.GetType(); } }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("PackageCopy: " + message); }
    private sealed class CustomWriter : BinaryWriter { internal CustomWriter(Stream stream) : base(stream) { } }
}
