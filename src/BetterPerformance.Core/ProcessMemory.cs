using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BetterPerformance.Core
{
    public static class ProcessMemory
    {
        // Unity's Windows Mono Process.WorkingSet64 can silently return zero.
        public static bool TryRead(out long bytes, out string source)
        {
            bytes = 0;
            source = "unavailable";
            try
            {
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    var counters = new MemoryCounters { Size = (uint)Marshal.SizeOf(typeof(MemoryCounters)) };
                    if (!GetProcessMemoryInfo(GetCurrentProcess(), out counters, counters.Size)) return false;
                    bytes = checked((long)counters.WorkingSet.ToUInt64());
                    if (bytes > 0) { source = "windows_psapi"; return true; }
                }
                else
                {
                    using (var process = Process.GetCurrentProcess()) bytes = process.WorkingSet64;
                    if (bytes > 0) { source = "system_diagnostics"; return true; }
                }
            }
            catch { /* Unsupported runtime or native API: omit the gauge. */ }
            bytes = 0;
            return false;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryCounters
        {
            public uint Size, PageFaultCount;
            public UIntPtr PeakWorkingSet, WorkingSet, QuotaPeakPagedPool, QuotaPagedPool;
            public UIntPtr QuotaPeakNonPagedPool, QuotaNonPagedPool, PagefileUsage, PeakPagefileUsage;
        }
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessMemoryInfo(IntPtr process, out MemoryCounters counters, uint size);
    }
}
