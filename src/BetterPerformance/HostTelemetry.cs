using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using BetterPerformance.Core;

namespace BetterPerformance
{
    // Read-only host facts. Nothing here changes process priority, affinity, the power
    // scheme or the system timer resolution; timeBeginPeriod is never called.
    internal static class HostTelemetry
    {
        private static bool timerUnavailable;
        private static bool counterUnavailable;

        private static bool Windows => Environment.OSVersion.Platform == PlatformID.Win32NT;

        internal static void StartLabels(List<TextValue> labels, List<NumberValue> gauges)
        {
            labels.Add(new TextValue("host_scope", "read_only; nothing_is_changed; timer_resolution_affects_sleep_pacing"));
            gauges.Add(new NumberValue("host_logical_processors", Environment.ProcessorCount, "count"));
            if (!Windows)
            {
                labels.Add(new TextValue("host_status", "unsupported_platform"));
                return;
            }
            labels.Add(new TextValue("host_status", "available"));
            labels.Add(new TextValue("os_version", Environment.OSVersion.VersionString));
            string priority = "unavailable", affinity = "unavailable";
            try
            {
                using (var process = Process.GetCurrentProcess())
                {
                    priority = process.PriorityClass.ToString();
                    ulong mask = (ulong)(long)process.ProcessorAffinity;
                    // Unity Mono reports 0 here instead of throwing; read the kernel mask directly in that case.
                    if (mask == 0 && GetProcessAffinityMask(GetCurrentProcess(), out UIntPtr processMask, out UIntPtr _))
                        mask = processMask.ToUInt64();
                    affinity = mask == 0 ? "unavailable" : "0x" + mask.ToString("x", CultureInfo.InvariantCulture);
                }
            }
            catch { }
            labels.Add(new TextValue("process_priority_class", priority));
            labels.Add(new TextValue("process_affinity_mask", affinity));
            labels.Add(new TextValue("power_scheme", PowerScheme()));
            Sample(gauges, labels);
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            if (!Windows)
            {
                labels.Add(new TextValue("host_timer_resolution_status", "unsupported_platform"));
                labels.Add(new TextValue("host_qpc_status", "unsupported_platform"));
                return;
            }
            labels.Add(new TextValue("host_timer_resolution_status", TimerResolution(gauges)));
            labels.Add(new TextValue("host_qpc_status", Counter(gauges)));
        }

        // Resolution is reported in 100ns units. The "minimum" value is the coarsest
        // period the kernel supports and the "maximum" the finest, matching ntdll.
        private static string TimerResolution(List<NumberValue> gauges)
        {
            if (timerUnavailable) return "native_api_unavailable";
            try
            {
                uint coarsest, finest, current;
                if (NtQueryTimerResolution(out coarsest, out finest, out current) != 0) return "read_failed";
                gauges.Add(new NumberValue("host_timer_resolution_current_ms", current / 10000.0, "ms"));
                gauges.Add(new NumberValue("host_timer_resolution_min_ms", finest / 10000.0, "ms"));
                gauges.Add(new NumberValue("host_timer_resolution_max_ms", coarsest / 10000.0, "ms"));
                return "available";
            }
            catch { timerUnavailable = true; return "native_api_unavailable"; }
        }

        // The raw kernel counter is the only clock shared by the client and the dedicated
        // server on this host; Stopwatch origins differ between the two Unity processes.
        private static string Counter(List<NumberValue> gauges)
        {
            if (counterUnavailable) return "native_api_unavailable";
            try
            {
                long timestamp, frequency;
                if (!QueryPerformanceCounter(out timestamp) || !QueryPerformanceFrequency(out frequency) || frequency <= 0)
                    return "read_failed";
                gauges.Add(new NumberValue("host_qpc_timestamp", timestamp, "counts"));
                gauges.Add(new NumberValue("host_qpc_frequency", frequency, "counts_per_second"));
                return "available";
            }
            catch { counterUnavailable = true; return "native_api_unavailable"; }
        }

        private static string PowerScheme()
        {
            IntPtr scheme = IntPtr.Zero, buffer = IntPtr.Zero;
            try
            {
                if (PowerGetActiveScheme(IntPtr.Zero, out scheme) != 0 || scheme == IntPtr.Zero) return "unavailable";
                uint size = 0;
                if (PowerReadFriendlyName(IntPtr.Zero, scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size) != 0 ||
                    size == 0 || size > 4096) return "unavailable";
                buffer = Marshal.AllocHGlobal((int)size);
                if (PowerReadFriendlyName(IntPtr.Zero, scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0) return "unavailable";
                return Marshal.PtrToStringUni(buffer) ?? "unavailable";
            }
            catch { return "unavailable"; }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                // PowerGetActiveScheme allocates the GUID with LocalAlloc.
                if (scheme != IntPtr.Zero) LocalFree(scheme);
            }
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQueryTimerResolution(out uint minimumResolution, out uint maximumResolution, out uint currentResolution);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessAffinityMask(IntPtr process, out UIntPtr processMask, out UIntPtr systemMask);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryPerformanceCounter(out long count);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryPerformanceFrequency(out long frequency);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
        [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);
        [DllImport("powrprof.dll")]
        private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, IntPtr schemeGuid,
            IntPtr subGroupOfPowerSettingsGuid, IntPtr powerSettingGuid, IntPtr buffer, ref uint bufferSize);
    }
}
