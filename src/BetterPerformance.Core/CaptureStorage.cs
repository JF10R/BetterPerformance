using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace BetterPerformance.Core
{
    public static class CaptureStorage
    {
        // Names this plugin writes: UTC start stamp, role, 32-hex id. Only these are ever purged.
        private static readonly Regex CaptureName = new Regex(@"^\d{8}T\d{9}Z-[a-z_]+-[0-9a-f]{32}\.(jsonl|log)$",
            RegexOptions.CultureInvariant);

        // Reserve room for the next file's full allowance.
        // One recording process must own its capture directory.
        public static bool HasCapacity(string directory, long fileBytes, long directoryBytes)
        {
            if (fileBytes <= 0 || directoryBytes <= 0 || fileBytes > directoryBytes) return false;
            long remaining = directoryBytes - fileBytes;
            if (!Directory.Exists(directory)) return true;
            foreach (string path in Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                long size = new FileInfo(path).Length;
                if (size > remaining) return false;
                remaining -= size;
            }
            return true;
        }

        // Deletes this plugin's oldest captures (by start stamp) until HasCapacity holds. Other
        // files are counted but never touched; a file that cannot be deleted is skipped.
        public static bool MakeRoom(string directory, long fileBytes, long directoryBytes, out int deleted, out long freedBytes)
        {
            deleted = 0;
            freedBytes = 0;
            if (HasCapacity(directory, fileBytes, directoryBytes)) return true;
            if (fileBytes <= 0 || directoryBytes <= 0 || fileBytes > directoryBytes) return false;
            foreach (string path in Oldest(directory, "*.jsonl"))
            {
                if (TryDelete(path, out long size)) { deleted++; freedBytes += size; }
                if (HasCapacity(directory, fileBytes, directoryBytes)) return true;
            }
            return false;
        }

        // Deletes this plugin's oldest files (captures and relayed logs) until the directory holds at
        // most targetBytes. Returns the size left.
        public static long TrimTo(string directory, long targetBytes, out int deleted, out long freedBytes)
        {
            deleted = 0;
            freedBytes = 0;
            if (!Directory.Exists(directory)) return 0;
            long total = 0;
            foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                total += new FileInfo(path).Length;
            foreach (string path in Oldest(directory, "*"))
            {
                if (total <= targetBytes) break;
                if (TryDelete(path, out long size)) { deleted++; freedBytes += size; total -= size; }
            }
            return total;
        }

        private static List<string> Oldest(string directory, string pattern)
        {
            var files = new List<string>();
            if (!Directory.Exists(directory)) return files;
            foreach (string path in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
                if (CaptureName.IsMatch(Path.GetFileName(path))) files.Add(path);
            // The name starts with the UTC stamp, so ordinal order is chronological.
            files.Sort((a, b) => string.CompareOrdinal(Path.GetFileName(a), Path.GetFileName(b)));
            return files;
        }

        private static bool TryDelete(string path, out long size)
        {
            size = 0;
            try
            {
                size = new FileInfo(path).Length;
                File.Delete(path);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }
}
