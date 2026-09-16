using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace BetterPerformance.Core
{
    // One immutable minimap generation result. Buffers are raw texture storage bytes,
    // never interpreted here; the layout description must match the live textures exactly
    // before a caller may apply them.
    public sealed class MinimapCacheEntry
    {
        public MinimapCacheEntry(string key, int width, int height, string mapLayout, string maskLayout, string heightLayout,
            byte[] map, byte[] mask, byte[] heights, bool verified, int mismatches)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key required.", nameof(key));
            Key = key;
            Width = width;
            Height = height;
            MapLayout = mapLayout ?? throw new ArgumentNullException(nameof(mapLayout));
            MaskLayout = maskLayout ?? throw new ArgumentNullException(nameof(maskLayout));
            HeightLayout = heightLayout ?? throw new ArgumentNullException(nameof(heightLayout));
            Map = map ?? throw new ArgumentNullException(nameof(map));
            Mask = mask ?? throw new ArgumentNullException(nameof(mask));
            Heights = heights ?? throw new ArgumentNullException(nameof(heights));
            Verified = verified;
            Mismatches = mismatches < 0 ? 0 : mismatches;
        }

        public string Key { get; }
        public int Width { get; }
        public int Height { get; }
        public string MapLayout { get; }
        public string MaskLayout { get; }
        public string HeightLayout { get; }
        public byte[] Map { get; }
        public byte[] Mask { get; }
        public byte[] Heights { get; }
        public bool Verified { get; }
        public int Mismatches { get; }
        public long PayloadBytes => (long)Map.Length + Mask.Length + Heights.Length;

        // A mismatch is permanent for the key: generation was not reproducible under it,
        // so the entry can never be promoted afterwards.
        public MinimapCacheEntry Promote(bool verified, int mismatches) =>
            new MinimapCacheEntry(Key, Width, Height, MapLayout, MaskLayout, HeightLayout, Map, Mask, Heights,
                verified && mismatches == 0, mismatches);

        public bool SameLayout(MinimapCacheEntry other) =>
            other != null && Width == other.Width && Height == other.Height &&
            string.Equals(MapLayout, other.MapLayout, StringComparison.Ordinal) &&
            string.Equals(MaskLayout, other.MaskLayout, StringComparison.Ordinal) &&
            string.Equals(HeightLayout, other.HeightLayout, StringComparison.Ordinal) &&
            Map.Length == other.Map.Length && Mask.Length == other.Mask.Length && Heights.Length == other.Heights.Length;

        public bool SameLayout(int width, int height, string mapLayout, string maskLayout, string heightLayout,
            int mapBytes, int maskBytes, int heightBytes) =>
            Width == width && Height == height &&
            string.Equals(MapLayout, mapLayout, StringComparison.Ordinal) &&
            string.Equals(MaskLayout, maskLayout, StringComparison.Ordinal) &&
            string.Equals(HeightLayout, heightLayout, StringComparison.Ordinal) &&
            Map.Length == mapBytes && Mask.Length == maskBytes && Heights.Length == heightBytes;
    }

    public struct CachedFile
    {
        public CachedFile(string key, long bytes, DateTime lastWriteUtc) { Key = key; Bytes = bytes; LastWriteUtc = lastWriteUtc; }
        public string Key { get; }
        public long Bytes { get; }
        public DateTime LastWriteUtc { get; }
    }

    // Plugin-owned store. Only files named <64 lowercase hex>.bin inside the supplied
    // directory are ever read, written or deleted; no native game cache is touched.
    public static class MinimapCacheStore
    {
        public const int FormatVersion = 1;
        public const string Extension = ".bin";
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("BPMINIMAP");

        public static bool IsKey(string key)
        {
            if (key == null || key.Length != 64) return false;
            foreach (char c in key)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        public static string FileName(string key)
        {
            if (!IsKey(key)) throw new ArgumentException("Cache keys are 64 lowercase hexadecimal characters.", nameof(key));
            return key + Extension;
        }

        public static bool BytesEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        public static bool SameContent(MinimapCacheEntry left, MinimapCacheEntry right) =>
            left != null && right != null && left.SameLayout(right) &&
            BytesEqual(left.Map, right.Map) && BytesEqual(left.Mask, right.Mask) && BytesEqual(left.Heights, right.Heights);

        public static byte[] Serialize(MinimapCacheEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            using (var file = new MemoryStream())
            {
                file.Write(Magic, 0, Magic.Length);
                using (var compressed = new GZipStream(file, CompressionLevel.Fastest, true))
                using (var writer = new BinaryWriter(compressed, Encoding.UTF8, true))
                {
                    writer.Write(FormatVersion);
                    writer.Write(entry.Key);
                    writer.Write(entry.Width);
                    writer.Write(entry.Height);
                    writer.Write(entry.MapLayout);
                    writer.Write(entry.MaskLayout);
                    writer.Write(entry.HeightLayout);
                    writer.Write(entry.Verified);
                    writer.Write(entry.Mismatches);
                    writer.Write(Checksum(entry));
                    writer.Write(entry.Map.Length);
                    writer.Write(entry.Map);
                    writer.Write(entry.Mask.Length);
                    writer.Write(entry.Mask);
                    writer.Write(entry.Heights.Length);
                    writer.Write(entry.Heights);
                }
                return file.ToArray();
            }
        }

        public static bool TryDeserialize(byte[]? file, out MinimapCacheEntry? entry, out string failure)
        {
            entry = null;
            if (file == null || file.Length <= Magic.Length) { failure = "truncated"; return false; }
            for (int i = 0; i < Magic.Length; i++)
                if (file[i] != Magic[i]) { failure = "not_a_cache_file"; return false; }
            try
            {
                byte[] payload;
                // Decompress the whole member first: a partial read would leave the gzip
                // trailer unchecked, so a damaged tail could pass unnoticed.
                using (var source = new MemoryStream(file, Magic.Length, file.Length - Magic.Length, false))
                using (var compressed = new GZipStream(source, CompressionMode.Decompress))
                using (var plain = new MemoryStream())
                {
                    compressed.CopyTo(plain);
                    payload = plain.ToArray();
                }
                using (var body = new MemoryStream(payload, false))
                using (var reader = new BinaryReader(body, Encoding.UTF8))
                {
                    if (reader.ReadInt32() != FormatVersion) { failure = "format_version"; return false; }
                    string key = reader.ReadString();
                    if (!IsKey(key)) { failure = "malformed_key"; return false; }
                    int width = reader.ReadInt32(), height = reader.ReadInt32();
                    string mapLayout = reader.ReadString(), maskLayout = reader.ReadString(), heightLayout = reader.ReadString();
                    bool verified = reader.ReadBoolean();
                    int mismatches = reader.ReadInt32();
                    byte[] checksum = reader.ReadBytes(32);
                    if (checksum.Length != 32) { failure = "truncated"; return false; }
                    byte[] map = ReadBuffer(reader), mask = ReadBuffer(reader), heights = ReadBuffer(reader);
                    var candidate = new MinimapCacheEntry(key, width, height, mapLayout, maskLayout, heightLayout,
                        map, mask, heights, verified, mismatches);
                    if (!BytesEqual(checksum, Checksum(candidate))) { failure = "checksum_mismatch"; return false; }
                    entry = candidate;
                    failure = "ok";
                    return true;
                }
            }
            catch (EndOfStreamException) { failure = "truncated"; return false; }
            catch (InvalidDataException) { failure = "corrupt"; return false; }
            catch (IOException) { failure = "corrupt"; return false; }
            catch (ArgumentException) { failure = "corrupt"; return false; }
            catch (OutOfMemoryException) { failure = "too_large"; return false; }
        }

        private static byte[] ReadBuffer(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > 512 * 1024 * 1024) throw new InvalidDataException("Implausible buffer length.");
            byte[] buffer = reader.ReadBytes(length);
            if (buffer.Length != length) throw new EndOfStreamException("Buffer truncated.");
            return buffer;
        }

        private static byte[] Checksum(MinimapCacheEntry entry)
        {
            using (var sha = SHA256.Create())
            {
                void Absorb(byte[] buffer)
                {
                    byte[] length = BitConverter.GetBytes(buffer.Length);
                    sha.TransformBlock(length, 0, length.Length, null, 0);
                    if (buffer.Length != 0) sha.TransformBlock(buffer, 0, buffer.Length, null, 0);
                }
                byte[] header = Encoding.UTF8.GetBytes(string.Join(" ", new[]
                {
                    entry.Key,
                    entry.Width.ToString(CultureInfo.InvariantCulture),
                    entry.Height.ToString(CultureInfo.InvariantCulture),
                    entry.MapLayout, entry.MaskLayout, entry.HeightLayout
                }));
                Absorb(header);
                Absorb(entry.Map);
                Absorb(entry.Mask);
                Absorb(entry.Heights);
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return sha.Hash;
            }
        }

        // Oldest-first, and only over our own files. The key being written is never deleted.
        public static List<string> PlanCleanup(IList<CachedFile> files, long allowanceBytes, long incomingBytes, string keepKey)
        {
            var doomed = new List<string>();
            if (files == null) return doomed;
            var others = new List<CachedFile>();
            long total = incomingBytes;
            foreach (var file in files)
            {
                if (!IsKey(file.Key) || file.Bytes < 0) continue;
                if (string.Equals(file.Key, keepKey, StringComparison.Ordinal)) continue;
                others.Add(file);
                total += file.Bytes;
            }
            others.Sort((left, right) =>
            {
                int byAge = left.LastWriteUtc.CompareTo(right.LastWriteUtc);
                return byAge != 0 ? byAge : string.CompareOrdinal(left.Key, right.Key);
            });
            foreach (var file in others)
            {
                if (total <= allowanceBytes) break;
                doomed.Add(file.Key);
                total -= file.Bytes;
            }
            return doomed;
        }

        public static List<CachedFile> List(string directory)
        {
            var files = new List<CachedFile>();
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return files;
            foreach (string path in Directory.GetFiles(directory, "*" + Extension))
            {
                string key = Path.GetFileNameWithoutExtension(path);
                if (!IsKey(key)) continue;
                var info = new FileInfo(path);
                if (info.Exists) files.Add(new CachedFile(key, info.Length, info.LastWriteTimeUtc));
            }
            return files;
        }

        public static bool TryLoad(string directory, string key, out MinimapCacheEntry? entry, out string failure)
        {
            entry = null;
            if (string.IsNullOrEmpty(directory) || !IsKey(key)) { failure = "invalid_request"; return false; }
            string path = Path.Combine(directory, FileName(key));
            byte[] file;
            try
            {
                if (!File.Exists(path)) { failure = "missing"; return false; }
                file = File.ReadAllBytes(path);
            }
            catch (IOException) { failure = "unreadable"; return false; }
            catch (UnauthorizedAccessException) { failure = "unreadable"; return false; }
            catch (OutOfMemoryException) { failure = "too_large"; return false; }
            if (!TryDeserialize(file, out entry, out failure)) return false;
            if (entry != null && string.Equals(entry.Key, key, StringComparison.Ordinal)) return true;
            entry = null;
            failure = "key_mismatch";
            return false;
        }

        public static bool TryStore(string directory, MinimapCacheEntry entry, long maxEntryBytes, long allowanceBytes,
            out long storedBytes, out string failure)
        {
            storedBytes = 0;
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (string.IsNullOrEmpty(directory) || !IsKey(entry.Key)) { failure = "invalid_request"; return false; }
            byte[] file;
            try { file = Serialize(entry); }
            catch (OutOfMemoryException) { failure = "serialize_failed"; return false; }
            storedBytes = file.Length;
            if (file.Length > maxEntryBytes || file.Length > allowanceBytes) { failure = "entry_too_large"; return false; }
            try
            {
                Directory.CreateDirectory(directory);
                foreach (string doomed in PlanCleanup(List(directory), allowanceBytes, file.Length, entry.Key))
                {
                    try { File.Delete(Path.Combine(directory, FileName(doomed))); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                // Write beside the target and replace, so a failed write never leaves a
                // half-written entry under a key that would later be trusted.
                string path = Path.Combine(directory, FileName(entry.Key));
                string staging = path + ".writing";
                File.WriteAllBytes(staging, file);
                if (File.Exists(path)) File.Delete(path);
                File.Move(staging, path);
                failure = "ok";
                return true;
            }
            catch (IOException) { failure = "write_failed"; return false; }
            catch (UnauthorizedAccessException) { failure = "write_failed"; return false; }
        }
    }
}
