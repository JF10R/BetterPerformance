using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BetterPerformance.Core;

internal static class MapBitWriterTests
{
    internal static void Run()
    {
        if (!BitConverter.IsLittleEndian)
        {
            using var stream = new MemoryStream();
            Check(!MapBitWriter.TryWrite(new BinaryWriter(stream), new BitArray(8), 8) && stream.Length == 0,
                "unsupported endianness must fall back without output");
            return;
        }
        // Covers every lookup value, both signed-word extremes and all bit positions.
        var allBytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        Parity(new BitArray(allBytes), allBytes.Length * 8);
        foreach (int count in new[] { 0, 1, 7, 8, 9, 31, 32, 33, 65535, 65536, 65537, MapBitWriter.MaximumBits })
        {
            var random = new Random(count + 1729);
            var source = new byte[(count + 7) / 8];
            random.NextBytes(source);
            var bits = new BitArray(source) { Length = count };
            Parity(bits, count);
            bits.SetAll(true);
            Parity(bits, count);
            bits.SetAll(false);
            Parity(bits, count); // Detect retained-buffer contamination after the true fixture.
        }
        Parity(new BitArray(new[] { true, false, true, true, false, false, true, true, true }), 5);
        using (var output = new MemoryStream())
        {
            var writer = new BinaryWriter(output);
            Check(!MapBitWriter.TryWrite(writer, null, 0), "null zero-count shared map uses native fallback");
            Check(!MapBitWriter.TryWrite(writer, new BitArray(2), 3), "short shared map falls back");
            Check(!MapBitWriter.TryWrite(writer, new BitArray(2), -1), "negative count falls back");
            Check(!MapBitWriter.TryWrite(writer, new BitArray(MapBitWriter.MaximumBits + 1), 1), "oversized source falls back");
            Check(!MapBitWriter.TryWritePacked(writer, null, 0), "null packed source falls back");
            Check(!MapBitWriter.TryWritePacked(writer, new int[1], 33), "short packed source falls back");
            Check(!MapBitWriter.TryWritePacked(writer, new int[1], -1), "negative packed count falls back");
            Check(!MapBitWriter.TryWritePacked(writer, new int[1], MapBitWriter.MaximumBits + 1), "oversized packed count falls back");
            Check(output.Length == 0, "all rejection paths must leave writer untouched");
        }
        bool nestedRejected = false;
        using (var stream = new CallbackStream(() =>
        {
            using var nested = new MemoryStream();
            nestedRejected = !MapBitWriter.TryWrite(new BinaryWriter(nested), new BitArray(32, true), 32);
            Check(nested.Length == 0, "reentrant attempt must not write");
        }))
        {
            var bits = new BitArray(65537, true);
            Check(MapBitWriter.TryWrite(new BinaryWriter(stream), bits, bits.Length), "outer write succeeds");
            Check(nestedRejected && stream.ToArray().All(value => value == 1), "nested request cannot corrupt active workspace");
        }
        try
        {
            MapBitWriter.TryWrite(new BinaryWriter(new CallbackStream(() => throw new IOException("fixture"))), new BitArray(32), 32);
            throw new Exception("writer failure swallowed");
        }
        catch (IOException) { }
        Parity(new BitArray(33, true), 33); // Exception must release the per-thread guard.
        Parallel.For(0, 8, index => Parity(new BitArray(70000, index % 2 == 0), 69999));
    }

    private static void Parity(BitArray bits, int count)
    {
        using var native = new MemoryStream();
        using var optimized = new MemoryStream();
        using var expected = new BinaryWriter(native);
        using var actual = new BinaryWriter(optimized);
        expected.Write(0x10203040);
        actual.Write(0x10203040);
        for (int index = 0; index < count; index++) expected.Write(bits[index]);
        Check(MapBitWriter.TryWrite(actual, bits, count), "supported fixture accepted");
        expected.Write("pin suffix / é / 雪");
        actual.Write("pin suffix / é / 雪");
        Check(native.ToArray().SequenceEqual(optimized.ToArray()), "exact payload equality including surrounding bytes");
        var words = new int[(bits.Length + 31) / 32];
        bits.CopyTo(words, 0);
        var original = (int[])words.Clone();
        using var packed = new MemoryStream();
        using var packedWriter = new BinaryWriter(packed);
        packedWriter.Write(0x10203040);
        Check(MapBitWriter.TryWritePacked(packedWriter, words, count), "packed snapshot accepted");
        packedWriter.Write("pin suffix / é / 雪");
        Check(native.ToArray().SequenceEqual(packed.ToArray()), "packed snapshot matches native bytes including suffix");
        Check(words.SequenceEqual(original), "packed snapshot remains immutable");
    }

    private sealed class CallbackStream : MemoryStream
    {
        private Action? callback;
        internal CallbackStream(Action callback) { this.callback = callback; }
        public override void Write(byte[] buffer, int offset, int count)
        {
            var action = callback;
            callback = null;
            action?.Invoke();
            base.Write(buffer, offset, count);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("MapBitWriter: " + message);
    }
}
