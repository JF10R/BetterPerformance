using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using BetterPerformance.Core;

namespace BetterPerformance
{
    internal static class ThreadCpuTelemetry
    {
        private static readonly ThreadCpuWindow Window = new ThreadCpuWindow(Stopwatch.Frequency);
        private static int mainThreadId;
        private static bool nativeUnavailable;

        // Called on the Unity main thread at capture start; no native sample outside normal polling.
        internal static void Reset()
        {
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            nativeUnavailable = false;
            Window.Reset();
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            string status;
            if (mainThreadId == 0) status = "not_initialized";
            else if (Thread.CurrentThread.ManagedThreadId != mainThreadId) status = "wrong_thread";
            else if (Environment.OSVersion.Platform != PlatformID.Win32NT) status = "unsupported_platform";
            else if (nativeUnavailable) status = "native_api_unavailable";
            else
            {
                try
                {
                    FileTime created, exited, kernel, user;
                    // This pseudo handle always refers to the calling thread. Do not cache or close it.
                    if (!GetThreadTimes(GetCurrentThread(), out created, out exited, out kernel, out user))
                    {
                        Window.Reset();
                        status = "read_failed";
                    }
                    else
                    {
                        double cpuMs, wallMs;
                        ThreadCpuWindowStatus observed = Window.Observe(Stopwatch.GetTimestamp(),
                            kernel.Value, user.Value, out cpuMs, out wallMs);
                        status = observed == ThreadCpuWindowStatus.Available ? "available" :
                            observed == ThreadCpuWindowStatus.Warmup ? "warmup" : "invalid_delta";
                        if (observed == ThreadCpuWindowStatus.Available)
                        {
                            gauges.Add(new NumberValue("main_thread_cpu_delta", cpuMs, "ms"));
                            gauges.Add(new NumberValue("main_thread_cpu_window", wallMs, "ms"));
                            // One thread, not normalized by machine cores. Short windows can show accounting noise.
                            gauges.Add(new NumberValue("main_thread_cpu_percent", cpuMs / wallMs * 100.0, "%"));
                        }
                    }
                }
                catch
                {
                    // Unsupported native binding must not repeatedly throw or stop a capture.
                    Window.Reset();
                    nativeUnavailable = true;
                    status = "native_api_unavailable";
                }
            }
            labels.Add(new TextValue("main_thread_cpu_status", status));
            labels.Add(new TextValue("main_thread_cpu_scope", "windows_thread_kernel_plus_user; wall_residual_is_not_scheduler_wait"));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime
        {
            public uint Low, High;
            public ulong Value => ((ulong)High << 32) | Low;
        }

        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetThreadTimes(IntPtr thread, out FileTime created, out FileTime exited,
            out FileTime kernel, out FileTime user);
    }
}
