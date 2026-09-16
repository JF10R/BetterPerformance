using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BetterPerformance.Core;

internal static class MinimapCacheStoreTests
{
    private const string KeyA = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string KeyB = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    public static void Run()
    {
        KeysAndNames();
        RoundTrip();
        ExactComparison();
        CorruptFiles();
        DirectoryBehaviour();
        CleanupPlan();
    }

    private static MinimapCacheEntry Entry(string key, byte seed, bool verified = false, int mismatches = 0) =>
        new MinimapCacheEntry(key, 4, 4, "4/4/RGB24/R8G8B8_SRGB/1", "4/4/RGBA32/R8G8B8A8_SRGB/1", "4/4/RHalf/R16_SFloat/1",
            Fill(48, seed), Fill(64, (byte)(seed + 1)), Fill(32, (byte)(seed + 2)), verified, mismatches);

    private static byte[] Fill(int length, byte seed)
    {
        var buffer = new byte[length];
        for (int i = 0; i < length; i++) buffer[i] = (byte)(seed + i);
        return buffer;
    }

    private static void KeysAndNames()
    {
        Check(MinimapCacheStore.IsKey(KeyA), "A 64 character lowercase hexadecimal key is valid.");
        foreach (string bad in new[] { "", "xyz", KeyA.ToUpperInvariant(), KeyA.Substring(1), KeyA + "0" })
            Check(!MinimapCacheStore.IsKey(bad), "Malformed key accepted: " + bad);
        Check(MinimapCacheStore.FileName(KeyA) == KeyA + ".bin", "File name is the key plus our extension.");
        bool rejected = false;
        try { MinimapCacheStore.FileName("nope"); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "A non-key must never become a file name.");
    }

    private static void RoundTrip()
    {
        var entry = Entry(KeyA, 7, true, 0);
        byte[] file = MinimapCacheStore.Serialize(entry);
        Check(MinimapCacheStore.TryDeserialize(file, out var restored, out string failure) && restored != null,
            "Round trip failed: " + failure);
        Check(restored!.Key == entry.Key && restored.Width == 4 && restored.Height == 4, "Header round trips.");
        Check(restored.MapLayout == entry.MapLayout && restored.MaskLayout == entry.MaskLayout &&
            restored.HeightLayout == entry.HeightLayout, "Texture layout description round trips.");
        Check(restored.Verified && restored.Mismatches == 0, "Verification state round trips.");
        Check(MinimapCacheStore.SameContent(entry, restored), "Buffers round trip byte for byte.");
        Check(restored.PayloadBytes == 48 + 64 + 32, "Payload size is the three buffers.");
        Check(file.Length < entry.PayloadBytes * 4, "Serialized entry is compressed, not expanded without bound.");
    }

    private static void ExactComparison()
    {
        var left = Entry(KeyA, 7);
        var right = Entry(KeyA, 7);
        Check(MinimapCacheStore.SameContent(left, right), "Identical entries compare equal.");
        right.Heights[31] ^= 0x01;
        Check(!MinimapCacheStore.SameContent(left, right), "A single flipped bit in the last buffer must not compare equal.");
        right.Heights[31] ^= 0x01;
        right.Map[0] ^= 0x80;
        Check(!MinimapCacheStore.SameContent(left, right), "A single flipped bit in the first buffer must not compare equal.");
        var different = new MinimapCacheEntry(KeyA, 4, 4, "other", left.MaskLayout, left.HeightLayout,
            left.Map, left.Mask, left.Heights, false, 0);
        Check(!MinimapCacheStore.SameContent(left, different), "A different texture layout is never the same content.");
        Check(!left.SameLayout(4, 4, left.MapLayout, left.MaskLayout, left.HeightLayout, 47, 64, 32),
            "A buffer length that does not match the live texture is rejected.");
        Check(left.SameLayout(4, 4, left.MapLayout, left.MaskLayout, left.HeightLayout, 48, 64, 32),
            "The matching live layout is accepted.");
        Check(!left.Promote(true, 1).Verified, "A key that ever mismatched can never be promoted.");
        Check(left.Promote(true, 0).Verified, "A byte-identical repeat promotes the entry.");
    }

    private static void CorruptFiles()
    {
        var entry = Entry(KeyA, 3, true);
        byte[] file = MinimapCacheStore.Serialize(entry);
        Check(!MinimapCacheStore.TryDeserialize(null, out _, out string empty) && empty == "truncated", "Null input is rejected.");
        var foreign = (byte[])file.Clone();
        foreign[0] ^= 0xFF;
        Check(!MinimapCacheStore.TryDeserialize(foreign, out _, out string magic) && magic == "not_a_cache_file",
            "A file that is not ours is rejected by magic, got " + magic);
        var truncated = file.Take(file.Length / 2).ToArray();
        Check(!MinimapCacheStore.TryDeserialize(truncated, out _, out _), "A truncated file is rejected.");
        bool anyRejected = false;
        // Skip the plain magic and the gzip header: mtime/OS bytes do not change the payload.
        for (int offset = 20; offset < file.Length; offset += 7)
        {
            var damaged = (byte[])file.Clone();
            damaged[offset] ^= 0x5A;
            if (!MinimapCacheStore.TryDeserialize(damaged, out _, out _)) anyRejected = true;
            else Check(false, "A damaged payload byte was accepted at offset " + offset);
        }
        Check(anyRejected, "The corruption sweep must actually have run.");
    }

    private static void DirectoryBehaviour()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bp-minimap-" + Guid.NewGuid().ToString("N"));
        try
        {
            Check(!MinimapCacheStore.TryLoad(directory, KeyA, out _, out string missing) && missing == "missing",
                "An absent entry reports a miss, not a failure.");
            var entry = Entry(KeyA, 11, true);
            Check(MinimapCacheStore.TryStore(directory, entry, 1024 * 1024, 1024 * 1024, out long bytes, out string stored) && stored == "ok",
                "Store failed: " + stored);
            Check(bytes > 0 && File.Exists(Path.Combine(directory, KeyA + ".bin")), "The entry is written under its key.");
            Check(!Directory.GetFiles(directory).Any(p => p.EndsWith(".writing", StringComparison.Ordinal)),
                "No staging file survives a successful write.");
            Check(MinimapCacheStore.TryLoad(directory, KeyA, out var loaded, out _) && loaded != null &&
                MinimapCacheStore.SameContent(entry, loaded!) && loaded!.Verified, "The stored entry loads back exactly.");
            Check(!MinimapCacheStore.TryLoad(directory, KeyB, out _, out string other) && other == "missing",
                "A different key never returns another key's entry.");

            // A file placed under the wrong key must never be served for that key.
            File.WriteAllBytes(Path.Combine(directory, KeyB + ".bin"), MinimapCacheStore.Serialize(entry));
            Check(!MinimapCacheStore.TryLoad(directory, KeyB, out _, out string mismatch) && mismatch == "key_mismatch",
                "A misfiled entry is rejected, got " + mismatch);

            File.WriteAllBytes(Path.Combine(directory, KeyB + ".bin"), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 });
            Check(!MinimapCacheStore.TryLoad(directory, KeyB, out _, out _), "A corrupt file on disk is rejected, not thrown.");

            Check(!MinimapCacheStore.TryStore(directory, entry, 8, 1024 * 1024, out _, out string tooBig) && tooBig == "entry_too_large",
                "An entry above the per-entry limit is refused.");
            Check(!MinimapCacheStore.TryStore(directory, entry, 1024 * 1024, 8, out _, out string noRoom) && noRoom == "entry_too_large",
                "An entry larger than the whole directory allowance is refused.");
            Check(MinimapCacheStore.TryLoad(directory, KeyA, out _, out _), "A refused store leaves the existing entry intact.");

            // Allowance cleanup removes our own oldest entries and nothing else.
            string foreign = Path.Combine(directory, "unrelated.txt");
            File.WriteAllText(foreign, new string('x', 4096));
            File.SetLastWriteTimeUtc(Path.Combine(directory, KeyB + ".bin"), new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var replacement = Entry(KeyA, 21);
            long allowance = MinimapCacheStore.Serialize(replacement).Length;
            Check(MinimapCacheStore.TryStore(directory, replacement, 1024 * 1024, allowance, out _, out string tight) && tight == "ok",
                "A tight allowance still stores the incoming entry: " + tight);
            Check(!File.Exists(Path.Combine(directory, KeyB + ".bin")), "The oldest plugin entry is removed first.");
            Check(File.Exists(foreign) && new FileInfo(foreign).Length == 4096, "Cleanup never touches a file we did not write.");
            Check(MinimapCacheStore.TryLoad(directory, KeyA, out var current, out _) && current != null &&
                MinimapCacheStore.SameContent(replacement, current!), "The incoming entry replaced the previous one under its key.");
            Check(MinimapCacheStore.List(directory).Count == 1, "Only our own keyed files are listed.");
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }

    private static void CleanupPlan()
    {
        var files = new List<CachedFile>
        {
            new CachedFile(KeyA, 100, new DateTime(2020, 1, 3, 0, 0, 0, DateTimeKind.Utc)),
            new CachedFile(KeyB, 100, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            new CachedFile("not a key", 1000, new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc))
        };
        Check(MinimapCacheStore.PlanCleanup(files, 500, 100, KeyA).Count == 0, "Nothing is deleted while the allowance holds.");
        var doomed = MinimapCacheStore.PlanCleanup(files, 150, 100, KeyA);
        Check(doomed.Count == 1 && doomed[0] == KeyB, "The oldest entry goes first and the incoming key is kept.");
        var all = MinimapCacheStore.PlanCleanup(files, 10, 100, "");
        Check(all.Count == 2 && all[0] == KeyB && all[1] == KeyA, "An impossible allowance still only plans our own files, oldest first.");
        Check(!MinimapCacheStore.PlanCleanup(files, 10, 100, "").Contains("not a key"), "A foreign file is never scheduled for deletion.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
