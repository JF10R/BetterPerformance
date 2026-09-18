using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BetterPerformance.Core
{
    // One immutable biome-point generation result. The payload is the game's own
    // serialization, opaque here: this type never interprets a byte of it, so a change
    // to the native format can only be caught by the key, never by this store.
    public sealed class BiomeCacheEntry
    {
        public BiomeCacheEntry(string key, bool verified, int mismatches, byte[] payload)
        {
            if (!BiomeCacheStore.IsKey(key)) throw new ArgumentException("Cache keys are 64 hexadecimal characters.", nameof(key));
            Key = key;
            Verified = verified;
            Mismatches = mismatches < 0 ? 0 : mismatches;
            Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        }

        public string Key { get; }
        public bool Verified { get; }
        public int Mismatches { get; }
        public byte[] Payload { get; }

        // Promotion and mismatch both keep the payload: the bytes on disk are what a later
        // run compares against, so replacing them would destroy the only evidence available.
        public BiomeCacheEntry Promoted() => new BiomeCacheEntry(Key, true, Mismatches, Payload);

        public BiomeCacheEntry Mismatched() => new BiomeCacheEntry(Key, false, Mismatches + 1, Payload);
    }

    // Plugin-owned store. Only files named <64 hex>.bin inside the supplied directory are
    // ever read, written or deleted; no native game cache is touched. No static mutable
    // state, so a worker thread may call any of this while the main thread plays.
    public static class BiomeCacheStore
    {
        public const int FormatVersion = 1;
        public const string Extension = ".bin";
        private const int KeyBytes = 64;
        private const int ChecksumBytes = 32;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("BPBC");
        // magic, version, key, verified, mismatches, payload length, checksum.
        private static readonly int HeaderBytes = Magic.Length + 4 + KeyBytes + 1 + 4 + 8 + ChecksumBytes;

        public static bool IsKey(string key)
        {
            if (key == null || key.Length != KeyBytes) return false;
            foreach (char c in key)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            return true;
        }

        public static string FileName(string directory, string key)
        {
            if (string.IsNullOrEmpty(directory)) throw new ArgumentException("A directory is required.", nameof(directory));
            if (!IsKey(key)) throw new ArgumentException("Cache keys are 64 hexadecimal characters.", nameof(key));
            return Path.Combine(directory, key + Extension);
        }

        public static bool BytesEqual(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        public static byte[] Serialize(BiomeCacheEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            byte[] file = new byte[HeaderBytes + entry.Payload.Length];
            int offset = 0;
            Buffer.BlockCopy(Magic, 0, file, offset, Magic.Length);
            offset += Magic.Length;
            WriteInt32(file, ref offset, FormatVersion);
            Encoding.ASCII.GetBytes(entry.Key, 0, KeyBytes, file, offset);
            offset += KeyBytes;
            file[offset++] = entry.Verified ? (byte)1 : (byte)0;
            WriteInt32(file, ref offset, entry.Mismatches);
            WriteInt64(file, ref offset, entry.Payload.Length);
            byte[] checksum = Checksum(entry.Payload);
            Buffer.BlockCopy(checksum, 0, file, offset, ChecksumBytes);
            offset += ChecksumBytes;
            Buffer.BlockCopy(entry.Payload, 0, file, offset, entry.Payload.Length);
            return file;
        }

        // Never throws on bad bytes: a cache file is untrusted input, and a corrupt one must
        // cost a regeneration, not the session.
        public static bool TryDeserialize(byte[] bytes, out BiomeCacheEntry? entry, out string failure)
        {
            entry = null;
            if (bytes == null || bytes.Length < HeaderBytes) { failure = "truncated"; return false; }
            int offset = 0;
            for (int i = 0; i < Magic.Length; i++)
                if (bytes[i] != Magic[i]) { failure = "magic"; return false; }
            offset += Magic.Length;
            if (ReadInt32(bytes, ref offset) != FormatVersion) { failure = "version"; return false; }
            string key = Encoding.ASCII.GetString(bytes, offset, KeyBytes);
            offset += KeyBytes;
            if (!IsKey(key)) { failure = "key"; return false; }
            bool verified = bytes[offset++] != 0;
            int mismatches = ReadInt32(bytes, ref offset);
            long length = ReadInt64(bytes, ref offset);
            if (mismatches < 0 || length < 0 || length > int.MaxValue) { failure = "length"; return false; }
            if (bytes.Length - HeaderBytes != length) { failure = "length"; return false; }
            byte[] payload = new byte[(int)length];
            Buffer.BlockCopy(bytes, HeaderBytes, payload, 0, payload.Length);
            byte[] stored = new byte[ChecksumBytes];
            Buffer.BlockCopy(bytes, offset, stored, 0, ChecksumBytes);
            if (!BytesEqual(stored, Checksum(payload))) { failure = "checksum"; return false; }
            entry = new BiomeCacheEntry(key, verified, mismatches, payload);
            failure = "ok";
            return true;
        }

        public static bool TryLoad(string directory, string key, out BiomeCacheEntry? entry, out string failure)
        {
            entry = null;
            if (string.IsNullOrEmpty(directory) || !IsKey(key)) { failure = "invalid_request"; return false; }
            string path = FileName(directory, key);
            byte[] file;
            try
            {
                if (!File.Exists(path)) { failure = "missing"; return false; }
                file = File.ReadAllBytes(path);
            }
            catch (FileNotFoundException) { failure = "missing"; return false; }
            catch (DirectoryNotFoundException) { failure = "missing"; return false; }
            catch (IOException) { failure = "unreadable"; return false; }
            catch (UnauthorizedAccessException) { failure = "unreadable"; return false; }
            catch (OutOfMemoryException) { failure = "too_large"; return false; }
            if (!TryDeserialize(file, out entry, out failure)) return false;
            if (entry != null && string.Equals(entry.Key, key, StringComparison.Ordinal)) return true;
            entry = null;
            failure = "key_mismatch";
            return false;
        }

        public static bool TryStore(string directory, string key, BiomeCacheEntry entry, long maxEntryBytes,
            long maxDirectoryBytes, out string failure)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (string.IsNullOrEmpty(directory) || !IsKey(key)) { failure = "invalid_request"; return false; }
            if (!string.Equals(entry.Key, key, StringComparison.Ordinal)) { failure = "key_mismatch"; return false; }
            byte[] file;
            try { file = Serialize(entry); }
            catch (OutOfMemoryException) { failure = "serialize_failed"; return false; }
            if (file.LongLength > maxEntryBytes) { failure = "entry_too_large"; return false; }
            if (file.LongLength > maxDirectoryBytes) { failure = "directory_full"; return false; }
            string path = FileName(directory, key);
            string staging = path + ".writing";
            try
            {
                Directory.CreateDirectory(directory);
                // Write beside the target and replace, so an interrupted write never leaves a
                // half-written entry under a key a later session would trust.
                File.WriteAllBytes(staging, file);
                if (File.Exists(path)) File.Replace(staging, path, null);
                else File.Move(staging, path);
            }
            catch (IOException) { Discard(staging); failure = "write_failed"; return false; }
            catch (UnauthorizedAccessException) { Discard(staging); failure = "write_failed"; return false; }
            // Eviction runs after the write: the new entry is the one the caller needs, so it
            // is never a cleanup candidate even when it alone fills most of the allowance.
            foreach (string doomed in PlanCleanup(List(directory), maxDirectoryBytes, key))
            {
                try { File.Delete(FileName(directory, doomed)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            failure = "ok";
            return true;
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

        // Oldest-first, and only over our own files. The key just written is never deleted.
        public static List<string> PlanCleanup(IList<CachedFile> files, long allowanceBytes, string keepKey)
        {
            var doomed = new List<string>();
            if (files == null) return doomed;
            var others = new List<CachedFile>();
            long total = 0;
            foreach (var file in files)
            {
                if (!IsKey(file.Key) || file.Bytes < 0) continue;
                total += file.Bytes;
                if (string.Equals(file.Key, keepKey, StringComparison.Ordinal)) continue;
                others.Add(file);
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

        private static void Discard(string staging)
        {
            try { if (File.Exists(staging)) File.Delete(staging); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static byte[] Checksum(byte[] payload)
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(payload);
        }

        private static void WriteInt32(byte[] buffer, ref int offset, int value)
        {
            for (int i = 0; i < 4; i++) buffer[offset + i] = (byte)(value >> (8 * i));
            offset += 4;
        }

        private static void WriteInt64(byte[] buffer, ref int offset, long value)
        {
            for (int i = 0; i < 8; i++) buffer[offset + i] = (byte)(value >> (8 * i));
            offset += 8;
        }

        private static int ReadInt32(byte[] buffer, ref int offset)
        {
            int value = 0;
            for (int i = 0; i < 4; i++) value |= buffer[offset + i] << (8 * i);
            offset += 4;
            return value;
        }

        private static long ReadInt64(byte[] buffer, ref int offset)
        {
            long value = 0;
            for (int i = 0; i < 8; i++) value |= (long)buffer[offset + i] << (8 * i);
            offset += 8;
            return value;
        }
    }
}
