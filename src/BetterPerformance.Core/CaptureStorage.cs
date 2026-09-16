using System;
using System.IO;

namespace BetterPerformance.Core
{
    public static class CaptureStorage
    {
        // Reserve room for the next file's full allowance; never delete old captures.
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
    }
}
