using System;
using System.IO;
using System.Linq;
using BetterPerformance.Core;

internal static class BiomeCacheStoreTests
{
    public static void Run()
    {
        KeysAreExactlySixtyFourHexCharacters();
        RoundTripsAMultiMegabytePayload();
        EveryCorruptionIsRejectedByName();
        PromotionAndMismatchAreImmutableCopies();
        BytesEqualComparesContentNotReference();
        StoreReplacesAtomicallyAndLoadsBack();
        OversizedEntriesAreRefusedBeforeWriting();
        CleanupEvictsTheOldestAndNeverTheNewEntry();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string Key(char hex) => new string(hex, 64);

    private static byte[] Payload(int bytes, int seed)
    {
        var random = new Random(seed);
        byte[] payload = new byte[bytes];
        random.NextBytes(payload);
        return payload;
    }

    private static string TempDirectory() =>
        Path.Combine(Path.GetTempPath(), "BetterPerformance-biome-" + Guid.NewGuid().ToString("N"));

    private static void KeysAreExactlySixtyFourHexCharacters()
    {
        Check(BiomeCacheStore.IsKey(Key('a')) && BiomeCacheStore.IsKey(Key('F')) && BiomeCacheStore.IsKey(Key('0')),
            "Both hexadecimal cases are keys.");
        Check(!BiomeCacheStore.IsKey(null!) && !BiomeCacheStore.IsKey("") && !BiomeCacheStore.IsKey(new string('a', 63)) &&
              !BiomeCacheStore.IsKey(new string('a', 65)), "Only a 64 character key is a key.");
        Check(!BiomeCacheStore.IsKey(new string('g', 64)) && !BiomeCacheStore.IsKey(new string('a', 63) + "z"),
            "A non-hexadecimal character is not a key.");
        // A key becomes a file name, so a separator or a traversal must never pass.
        foreach (string hostile in new[] { new string('a', 63) + "/", new string('a', 63) + "\\", "../" + new string('a', 61) })
            Check(!BiomeCacheStore.IsKey(hostile), "A path character is not a key.");
        Check(BiomeCacheStore.FileName("D:/cache", Key('a')) == Path.Combine("D:/cache", Key('a') + ".bin"),
            "An entry is <directory>/<key>.bin.");
    }

    private static void RoundTripsAMultiMegabytePayload()
    {
        // Four megabytes rather than the ~21 a real world produces: the same code path,
        // without a slow suite.
        byte[] payload = Payload(4 * 1024 * 1024, 7);
        var entry = new BiomeCacheEntry(Key('b'), true, 2, payload);
        byte[] file = BiomeCacheStore.Serialize(entry);
        Check(file.Length == payload.Length + 117, "The header is a fixed 117 bytes before the payload.");
        Check(BiomeCacheStore.TryDeserialize(file, out var restored, out string failure), "A serialized entry deserializes: " + failure);
        Check(restored != null && restored.Key == entry.Key && restored.Verified && restored.Mismatches == 2,
            "The header fields survive the round trip.");
        Check(BiomeCacheStore.BytesEqual(restored!.Payload, payload), "The payload survives the round trip byte for byte.");
    }

    private static void EveryCorruptionIsRejectedByName()
    {
        var entry = new BiomeCacheEntry(Key('c'), false, 0, Payload(4096, 11));
        byte[] original = BiomeCacheStore.Serialize(entry);

        void Rejects(Func<byte[], byte[]> damage, string expected, string message)
        {
            byte[] copy = damage((byte[])original.Clone());
            Check(!BiomeCacheStore.TryDeserialize(copy, out var broken, out string failure) && broken == null,
                message + " must be rejected.");
            Check(failure == expected, message + " reports \"" + expected + "\", not \"" + failure + "\".");
        }

        Rejects(bytes => { bytes[0] ^= 0xFF; return bytes; }, "magic", "A foreign file");
        Rejects(bytes => { bytes[4] = 99; return bytes; }, "version", "A future format version");
        Rejects(bytes => { bytes[8] = (byte)'z'; return bytes; }, "key", "A malformed embedded key");
        Rejects(bytes => { bytes[bytes.Length - 1] ^= 0xFF; return bytes; }, "checksum", "A flipped payload byte");
        Rejects(bytes => bytes.Take(bytes.Length - 1).ToArray(), "length", "A payload cut short");
        Rejects(bytes => bytes.Take(50).ToArray(), "truncated", "A file shorter than the header");
        Check(!BiomeCacheStore.TryDeserialize(Array.Empty<byte>(), out _, out string empty) && empty == "truncated",
            "An empty file is truncated, never an exception.");
        Check(!BiomeCacheStore.TryDeserialize(null!, out _, out string missing) && missing == "truncated",
            "No bytes at all is truncated, never an exception.");
    }

    private static void PromotionAndMismatchAreImmutableCopies()
    {
        byte[] payload = Payload(64, 13);
        var entry = new BiomeCacheEntry(Key('d'), false, 1, payload);
        var promoted = entry.Promoted();
        var mismatched = entry.Mismatched();
        Check(!entry.Verified && entry.Mismatches == 1, "The original entry is unchanged by either copy.");
        Check(promoted.Verified && promoted.Mismatches == 1 && promoted.Key == entry.Key,
            "Promotion only sets the verified flag.");
        Check(!mismatched.Verified && mismatched.Mismatches == 2 && mismatched.Key == entry.Key,
            "A mismatch clears the flag and counts itself.");
        Check(BiomeCacheStore.BytesEqual(promoted.Payload, payload) && BiomeCacheStore.BytesEqual(mismatched.Payload, payload),
            "Neither copy may replace the payload a later run compares against.");
        Check(!ReferenceEquals(promoted, entry) && !ReferenceEquals(mismatched, entry), "Each copy is a new entry.");
    }

    private static void BytesEqualComparesContentNotReference()
    {
        byte[] left = { 1, 2, 3 };
        Check(BiomeCacheStore.BytesEqual(left, new byte[] { 1, 2, 3 }), "Equal content is equal.");
        Check(BiomeCacheStore.BytesEqual(left, left) && BiomeCacheStore.BytesEqual(null!, null!), "Identity and absence are equal.");
        Check(!BiomeCacheStore.BytesEqual(left, new byte[] { 1, 2 }), "A different length is never equal.");
        Check(!BiomeCacheStore.BytesEqual(left, new byte[] { 1, 2, 4 }), "One differing byte is never equal.");
        Check(!BiomeCacheStore.BytesEqual(left, null!), "A missing array is never equal to a present one.");
    }

    private static void StoreReplacesAtomicallyAndLoadsBack()
    {
        string directory = TempDirectory();
        try
        {
            string key = Key('e');
            var first = new BiomeCacheEntry(key, false, 0, Payload(8192, 17));
            Check(BiomeCacheStore.TryStore(directory, key, first, 1 << 20, 1 << 24, out string failure), "A first store succeeds: " + failure);
            Check(File.Exists(BiomeCacheStore.FileName(directory, key)), "The entry exists under its own name.");
            Check(Directory.GetFiles(directory, "*.writing").Length == 0, "No staging file may outlive a successful store.");

            // Replacing an existing entry is the promotion path, so it must not need a delete first.
            var promoted = first.Promoted();
            Check(BiomeCacheStore.TryStore(directory, key, promoted, 1 << 20, 1 << 24, out failure), "A replacing store succeeds: " + failure);
            Check(Directory.GetFiles(directory).Length == 1, "Replacement leaves exactly one file.");
            Check(BiomeCacheStore.TryLoad(directory, key, out var loaded, out failure), "The stored entry loads: " + failure);
            Check(loaded != null && loaded.Verified && BiomeCacheStore.BytesEqual(loaded.Payload, first.Payload),
                "The replacement is what comes back.");

            Check(!BiomeCacheStore.TryLoad(directory, Key('f'), out var absent, out failure) && absent == null && failure == "missing",
                "An absent key is missing, never an exception.");
            Check(!BiomeCacheStore.TryLoad(directory, "not a key", out _, out failure) && failure == "invalid_request",
                "A malformed key is refused before touching the disk.");
            File.WriteAllBytes(BiomeCacheStore.FileName(directory, Key('f')), new byte[] { 1, 2, 3 });
            Check(!BiomeCacheStore.TryLoad(directory, Key('f'), out _, out failure) && failure == "truncated",
                "A damaged file costs a regeneration, not an exception.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static void OversizedEntriesAreRefusedBeforeWriting()
    {
        string directory = TempDirectory();
        try
        {
            string key = Key('a');
            var entry = new BiomeCacheEntry(key, false, 0, Payload(4096, 19));
            Check(!BiomeCacheStore.TryStore(directory, key, entry, 1024, 1 << 24, out string failure) && failure == "entry_too_large",
                "An entry over the per-entry limit is refused.");
            Check(!BiomeCacheStore.TryStore(directory, key, entry, 1 << 20, 1024, out failure) && failure == "directory_full",
                "An entry that alone cannot fit the directory is refused.");
            Check(!Directory.Exists(directory) || Directory.GetFiles(directory).Length == 0,
                "A refused store must not leave a file behind.");
            Check(!BiomeCacheStore.TryStore(directory, Key('b'), entry, 1 << 20, 1 << 24, out failure) && failure == "key_mismatch",
                "An entry may only be stored under its own key.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static void CleanupEvictsTheOldestAndNeverTheNewEntry()
    {
        string directory = TempDirectory();
        try
        {
            var written = new[] { Key('1'), Key('2'), Key('3') };
            long entryBytes = 0;
            for (int i = 0; i < written.Length; i++)
            {
                var entry = new BiomeCacheEntry(written[i], false, 0, Payload(1024, 23 + i));
                Check(BiomeCacheStore.TryStore(directory, written[i], entry, 1 << 20, 1 << 24, out string stored), "Fixture store: " + stored);
                string path = BiomeCacheStore.FileName(directory, written[i]);
                entryBytes = new FileInfo(path).Length;
                File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1 + i, 0, 0, 0, DateTimeKind.Utc));
            }

            string newest = Key('4');
            var incoming = new BiomeCacheEntry(newest, false, 0, Payload(1024, 29));
            // Room for three entries only: the new one stays, the oldest pays for it.
            Check(BiomeCacheStore.TryStore(directory, newest, incoming, 1 << 20, entryBytes * 3, out string failure),
                "A store that needs eviction still succeeds: " + failure);
            var remaining = Directory.GetFiles(directory, "*.bin").Select(path => Path.GetFileNameWithoutExtension(path)!).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            Check(remaining.SequenceEqual(new[] { Key('2'), Key('3'), Key('4') }),
                "Eviction takes the oldest entries and never the one just written.");
            Check(BiomeCacheStore.TryLoad(directory, newest, out var loaded, out failure) && loaded != null,
                "The new entry survives its own cleanup: " + failure);

            // An allowance that fits exactly one entry leaves only the incoming one.
            string last = Key('5');
            var single = new BiomeCacheEntry(last, false, 0, Payload(1024, 31));
            Check(BiomeCacheStore.TryStore(directory, last, single, 1 << 20, entryBytes, out failure), "A single-entry allowance stores: " + failure);
            Check(Directory.GetFiles(directory, "*.bin").Length == 1 && File.Exists(BiomeCacheStore.FileName(directory, last)),
                "A tight allowance keeps exactly the entry just written.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
