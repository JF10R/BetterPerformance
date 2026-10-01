using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace BetterPerformance.Core
{
    // Whole-machine figures: they include every other process on the host.
    public struct SystemMemorySnapshot
    {
        public double PhysicalTotal, PhysicalAvailable, CommitTotal, CommitLimit;
    }

    public struct PowerSnapshot
    {
        public byte AcLine, BatteryFlag, BatteryPercent, StatusFlag;
    }

    // Per-logical-processor clocks as the Windows power manager reports them.
    public struct ProcessorMhzSummary
    {
        public int Count;
        public double CurrentSum;
        public uint MaxMhz, LimitMin, CurrentMin;

        public double CurrentAverage => Count == 0 ? 0 : CurrentSum / Count;

        public void Add(uint maxMhz, uint currentMhz, uint limitMhz)
        {
            if (Count == 0) { LimitMin = limitMhz; CurrentMin = currentMhz; }
            else
            {
                if (limitMhz < LimitMin) LimitMin = limitMhz;
                if (currentMhz < CurrentMin) CurrentMin = currentMhz;
            }
            if (maxMhz > MaxMhz) MaxMhz = maxMhz;
            CurrentSum += currentMhz;
            Count++;
        }
    }

    // Read-only native readers. A false return is a failed read; an exception means the
    // native API itself is unavailable and the caller should stop asking.
    public static class SystemResources
    {
        public static bool IsWindows => Environment.OSVersion.Platform == PlatformID.Win32NT;

        public static double PagesToBytes(ulong pages, ulong pageSize) => (double)pages * pageSize;

        public static bool TryReadMemory(out SystemMemorySnapshot snapshot)
        {
            snapshot = default;
            var info = new PerformanceInformation { Size = (uint)Marshal.SizeOf(typeof(PerformanceInformation)) };
            if (!GetPerformanceInfo(out info, info.Size)) return false;
            ulong page = info.PageSize.ToUInt64();
            if (page == 0 || info.PhysicalTotal.ToUInt64() == 0) return false;
            snapshot.PhysicalTotal = PagesToBytes(info.PhysicalTotal.ToUInt64(), page);
            snapshot.PhysicalAvailable = PagesToBytes(info.PhysicalAvailable.ToUInt64(), page);
            snapshot.CommitTotal = PagesToBytes(info.CommitTotal.ToUInt64(), page);
            snapshot.CommitLimit = PagesToBytes(info.CommitLimit.ToUInt64(), page);
            return true;
        }

        public static bool TryReadCpuTimes(out ulong idle100ns, out ulong kernel100ns, out ulong user100ns)
        {
            idle100ns = kernel100ns = user100ns = 0;
            if (!GetSystemTimes(out ulong idle, out ulong kernel, out ulong user)) return false;
            idle100ns = idle; kernel100ns = kernel; user100ns = user;
            return true;
        }

        public static ulong UptimeMilliseconds() => GetTickCount64();

        public static bool TryReadPower(out PowerSnapshot power)
        {
            power = default;
            if (!GetSystemPowerStatus(out SystemPowerStatus status)) return false;
            power.AcLine = status.AcLineStatus;
            power.BatteryFlag = status.BatteryFlag;
            power.BatteryPercent = status.BatteryLifePercent;
            power.StatusFlag = status.SystemStatusFlag;
            return true;
        }

        // Constant strings only: these run every interval and must not format.
        public static string AcLineLabel(byte acLine) =>
            acLine == 0 ? "offline" : acLine == 1 ? "online" : "unknown";

        public static string BatteryLabel(PowerSnapshot power)
        {
            if (power.BatteryFlag == 255) return "unknown";
            if ((power.BatteryFlag & 128) != 0) return "no_battery";
            if ((power.BatteryFlag & 8) != 0) return "charging";
            if (power.AcLine == 0) return "discharging";
            return power.AcLine == 1 ? "on_ac_not_charging" : "unknown";
        }

        public static bool HasBatteryPercent(PowerSnapshot power) =>
            power.BatteryFlag != 255 && (power.BatteryFlag & 128) == 0 && power.BatteryPercent <= 100;

        public static string BatterySaverLabel(byte statusFlag) => (statusFlag & 1) != 0 ? "on" : "off";

        // Which of the named modules are loaded from outside the system directory (an injector
        // or proxy such as ReShade's dxgi.dll). Names only; a path is never returned.
        public static string ForeignModules(string[] names)
        {
            string system = Path.GetFullPath(Environment.SystemDirectory).TrimEnd('\\', '/');
            var found = new StringBuilder();
            var path = new StringBuilder(1024);
            foreach (string name in names)
            {
                IntPtr module = GetModuleHandleW(name);
                if (module == IntPtr.Zero) continue;
                path.Length = 0;
                if (GetModuleFileNameW(module, path, (uint)path.Capacity) == 0) continue;
                string directory = Path.GetDirectoryName(path.ToString()) ?? "";
                if (string.Equals(directory.TrimEnd('\\', '/'), system, StringComparison.OrdinalIgnoreCase)) continue;
                if (found.Length > 0) found.Append(',');
                found.Append(name);
            }
            return found.Length == 0 ? "none" : found.ToString();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PerformanceInformation
        {
            public uint Size;
            public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache;
            public UIntPtr KernelTotal, KernelPaged, KernelNonpaged, PageSize;
            public uint HandleCount, ProcessCount, ThreadCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemPowerStatus
        {
            public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public uint BatteryLifeTime, BatteryFullLifeTime;
        }

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetPerformanceInfo(out PerformanceInformation information, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
        [DllImport("kernel32.dll")] private static extern ulong GetTickCount64();
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetModuleFileNameW(IntPtr module, StringBuilder path, uint size);
    }

    // Reused unmanaged buffer: CallNtPowerInformation fills one 24-byte record per processor.
    public sealed class ProcessorMhzReader : IDisposable
    {
        private const int ProcessorInformation = 11, RecordBytes = 24;
        private readonly int count, bufferBytes;
        private IntPtr buffer;

        public ProcessorMhzReader(int processorCount)
        {
            count = Math.Max(1, Math.Min(processorCount, 1024));
            // Room for at least 64 records, in case the managed count is narrower than the kernel's.
            bufferBytes = Math.Max(count, 64) * RecordBytes;
            buffer = Marshal.AllocHGlobal(bufferBytes);
        }

        public bool TryRead(out ProcessorMhzSummary summary)
        {
            summary = default;
            if (buffer == IntPtr.Zero) return false;
            if (CallNtPowerInformation(ProcessorInformation, IntPtr.Zero, 0, buffer, (uint)bufferBytes) != 0) return false;
            for (int i = 0; i < count; i++)
            {
                int offset = i * RecordBytes;
                // Record: Number, MaxMhz, CurrentMhz, MhzLimit, MaxIdleState, CurrentIdleState.
                summary.Add((uint)Marshal.ReadInt32(buffer, offset + 4), (uint)Marshal.ReadInt32(buffer, offset + 8),
                    (uint)Marshal.ReadInt32(buffer, offset + 12));
            }
            return summary.MaxMhz > 0;
        }

        public void Dispose()
        {
            if (buffer == IntPtr.Zero) return;
            Marshal.FreeHGlobal(buffer);
            buffer = IntPtr.Zero;
        }

        [DllImport("powrprof.dll")]
        private static extern uint CallNtPowerInformation(int level, IntPtr input, uint inputSize, IntPtr output, uint outputSize);
    }
}
