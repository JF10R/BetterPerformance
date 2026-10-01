using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterPerformance.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace BetterPerformance
{
    // Machine resources beside the game's own figures: this process's video memory against the OS
    // budget, system RAM and commit, whole-machine CPU, uptime and power. Read-only; nothing is changed.
    // docs/system-resource-telemetry.md.
    internal static class SystemTelemetry
    {
        private static readonly string[] ProxyModules = { "dxgi.dll", "d3d11.dll", "d3d12.dll", "dinput8.dll", "version.dll", "opengl32.dll" };
        private static readonly SystemCpuWindow Cpu = new SystemCpuWindow();
        private static ManualLogSource? log;
        private static ProcessorMhzReader? mhz;
        private static DxgiVideoMemory? video;
        private static bool enabled, videoEnabled, headless;
        private static bool memoryUnavailable, cpuUnavailable, powerUnavailable, mhzUnavailable, uptimeUnavailable;
        private static int worldId;
        private static long worldStarted;

        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            log = logger;
            enabled = config.Bind("Diagnostics", "SystemResourcesEnabled", true,
                "Read system RAM and commit, whole-machine CPU, uptime, power and hardware labels at the capture poll cadence. Read-only. Requires restart.").Value;
            videoEnabled = config.Bind("Diagnostics", "VideoMemoryQueryEnabled", true,
                "Also read this process's video memory usage and OS budget through DXGI (Windows, graphics clients only). Read-only. Requires restart.").Value;
        }

        // Capture start: static hardware labels, then fresh delta baselines.
        internal static void StartLabels(List<TextValue> labels, List<NumberValue> gauges)
        {
            labels.Add(new TextValue("system_status", enabled ? "enabled" : "disabled"));
            if (!enabled) return;
            labels.Add(new TextValue("system_scope",
                "read_only; machine_wide_figures_include_other_processes; video_memory_is_this_process_on_one_adapter; " +
                "cpu_mhz_as_reported_by_power_manager; process_uptime_is_unity_realtime_since_startup; world_session_time_from_first_capture_start_in_this_world"));
            labels.Add(new TextValue("gpu_temperature_clock_status", "unavailable_requires_vendor_sdk"));
            try
            {
                headless = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
                labels.Add(new TextValue("gpu_name", SystemInfo.graphicsDeviceName));
                labels.Add(new TextValue("gpu_vendor", SystemInfo.graphicsDeviceVendor));
                labels.Add(new TextValue("gpu_device_type", SystemInfo.graphicsDeviceType.ToString()));
                labels.Add(new TextValue("gpu_api_version", SystemInfo.graphicsDeviceVersion));
                gauges.Add(new NumberValue("gpu_memory_size_reported", SystemInfo.graphicsMemorySize, "mib"));
                labels.Add(new TextValue("cpu_model", SystemInfo.processorType));
                gauges.Add(new NumberValue("cpu_nominal_frequency", SystemInfo.processorFrequency, "mhz"));
                if (!headless)
                {
                    Resolution mode = Screen.currentResolution;
                    gauges.Add(new NumberValue("display_width", mode.width, "pixels"));
                    gauges.Add(new NumberValue("display_height", mode.height, "pixels"));
                    gauges.Add(new NumberValue("display_refresh_rate", mode.refreshRateRatio.value, "hz"));
                }
                labels.Add(new TextValue("system_hardware_status", "available"));
            }
            catch (Exception exception) { labels.Add(new TextValue("system_hardware_status", "unavailable:" + exception.GetType().Name)); }
            if (SystemResources.IsWindows)
            {
                try { labels.Add(new TextValue("graphics_proxy_modules", SystemResources.ForeignModules(ProxyModules))); }
                catch { labels.Add(new TextValue("graphics_proxy_modules", "unavailable")); }
                try
                {
                    if (SystemResources.TryReadMemory(out SystemMemorySnapshot memory))
                        gauges.Add(new NumberValue("system_physical_total", memory.PhysicalTotal, "bytes"));
                }
                catch { }
            }
            OpenVideo();
            if (video != null && video.Status == "available")
            {
                labels.Add(new TextValue("video_memory_adapter", video.AdapterName));
                labels.Add(new TextValue("video_memory_adapter_match", video.AdapterMatch));
                labels.Add(new TextValue("video_memory_dxgi_source", "system_directory"));
                labels.Add(new TextValue("gpu_driver_version", video.DriverVersion));
                gauges.Add(new NumberValue("video_memory_dedicated_total", video.DedicatedVideoMemory, "bytes"));
                gauges.Add(new NumberValue("video_memory_adapters_seen", video.AdaptersSeen, "adapters"));
            }
            else labels.Add(new TextValue("gpu_driver_version", "unavailable"));
            Cpu.Reset();
            memoryUnavailable = cpuUnavailable = powerUnavailable = mhzUnavailable = uptimeUnavailable = false;
            // Prime the CPU window so the first interval already carries a delta.
            try { if (SystemResources.IsWindows && SystemResources.TryReadCpuTimes(out ulong idle, out ulong kernel, out ulong user)) Cpu.Observe(idle, kernel, user, out _); }
            catch { cpuUnavailable = true; }
            int id = ZNet.instance != null ? ZNet.instance.GetInstanceID() : 0;
            if (id != worldId || worldStarted == 0) { worldId = id; worldStarted = Stopwatch.GetTimestamp(); }
        }

        // Once per process, on the main thread after the graphics device exists.
        private static void OpenVideo()
        {
            if (video != null) return;
            if (!videoEnabled) video = DxgiVideoMemory.Unavailable("disabled");
            else if (!SystemResources.IsWindows) video = DxgiVideoMemory.Unavailable("unsupported_platform");
            else if (headless) video = DxgiVideoMemory.Unavailable("headless");
            else
            {
                try { video = DxgiVideoMemory.Open((uint)SystemInfo.graphicsDeviceVendorID, (uint)SystemInfo.graphicsDeviceID); }
                catch (Exception exception) { video = DxgiVideoMemory.Unavailable("native_api_unavailable:" + exception.GetType().Name); }
                log?.LogInfo("Video memory query: " + video.Status + "; adapter match " + video.AdapterMatch);
            }
        }

        internal static void Sample(List<NumberValue> gauges, List<TextValue> labels)
        {
            if (!enabled) { labels.Add(new TextValue("system_status", "disabled")); return; }
            long started = Stopwatch.GetTimestamp();
            bool windows = SystemResources.IsWindows;
            labels.Add(new TextValue("system_memory_status", windows ? Memory(gauges) : "unsupported_platform"));
            labels.Add(new TextValue("system_cpu_status", windows ? SystemCpu(gauges) : "unsupported_platform"));
            labels.Add(new TextValue("cpu_mhz_status", windows ? ProcessorClocks(gauges) : "unsupported_platform"));
            labels.Add(new TextValue("power_status", windows ? Power(gauges, labels) : "unsupported_platform"));
            labels.Add(new TextValue("video_memory_status", VideoMemory(gauges)));
            Uptime(gauges, windows);
            gauges.Add(new NumberValue("system_probe_cost", (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency, "ms"));
        }

        private static string Memory(List<NumberValue> gauges)
        {
            if (memoryUnavailable) return "native_api_unavailable";
            try
            {
                if (!SystemResources.TryReadMemory(out SystemMemorySnapshot memory)) return "read_failed";
                gauges.Add(new NumberValue("system_physical_available", memory.PhysicalAvailable, "bytes"));
                gauges.Add(new NumberValue("system_commit_total", memory.CommitTotal, "bytes"));
                gauges.Add(new NumberValue("system_commit_limit", memory.CommitLimit, "bytes"));
                return "available";
            }
            catch { memoryUnavailable = true; return "native_api_unavailable"; }
        }

        private static string SystemCpu(List<NumberValue> gauges)
        {
            if (cpuUnavailable) return "native_api_unavailable";
            try
            {
                if (!SystemResources.TryReadCpuTimes(out ulong idle, out ulong kernel, out ulong user)) { Cpu.Reset(); return "read_failed"; }
                SystemCpuWindowStatus status = Cpu.Observe(idle, kernel, user, out double busy);
                if (status == SystemCpuWindowStatus.Warmup) return "warmup";
                if (status == SystemCpuWindowStatus.InvalidDelta) return "invalid_delta";
                gauges.Add(new NumberValue("system_cpu_busy_percent", busy, "percent"));
                return "available";
            }
            catch { Cpu.Reset(); cpuUnavailable = true; return "native_api_unavailable"; }
        }

        private static string ProcessorClocks(List<NumberValue> gauges)
        {
            if (mhzUnavailable) return "native_api_unavailable";
            try
            {
                mhz ??= new ProcessorMhzReader(Environment.ProcessorCount);
                if (!mhz.TryRead(out ProcessorMhzSummary clocks)) return "read_failed";
                gauges.Add(new NumberValue("cpu_mhz_current_avg", clocks.CurrentAverage, "mhz"));
                gauges.Add(new NumberValue("cpu_mhz_current_min", clocks.CurrentMin, "mhz"));
                gauges.Add(new NumberValue("cpu_mhz_max", clocks.MaxMhz, "mhz"));
                gauges.Add(new NumberValue("cpu_mhz_limit_min", clocks.LimitMin, "mhz"));
                return "available";
            }
            catch { mhzUnavailable = true; return "native_api_unavailable"; }
        }

        private static string Power(List<NumberValue> gauges, List<TextValue> labels)
        {
            if (powerUnavailable) return "native_api_unavailable";
            try
            {
                if (!SystemResources.TryReadPower(out PowerSnapshot power)) return "read_failed";
                labels.Add(new TextValue("power_ac_line", SystemResources.AcLineLabel(power.AcLine)));
                labels.Add(new TextValue("battery_state", SystemResources.BatteryLabel(power)));
                labels.Add(new TextValue("battery_saver", SystemResources.BatterySaverLabel(power.StatusFlag)));
                if (SystemResources.HasBatteryPercent(power)) gauges.Add(new NumberValue("battery_percent", power.BatteryPercent, "percent"));
                return "available";
            }
            catch { powerUnavailable = true; return "native_api_unavailable"; }
        }

        private static string VideoMemory(List<NumberValue> gauges)
        {
            if (video == null) return "not_opened";
            if (video.Status != "available") return video.Status;
            try
            {
                if (!video.TryQuery(out VideoMemoryInfo local, out VideoMemoryInfo nonLocal)) return "query_failed";
                gauges.Add(new NumberValue("video_memory_local_usage", local.CurrentUsage, "bytes"));
                gauges.Add(new NumberValue("video_memory_local_budget", local.Budget, "bytes"));
                gauges.Add(new NumberValue("video_memory_nonlocal_usage", nonLocal.CurrentUsage, "bytes"));
                gauges.Add(new NumberValue("video_memory_nonlocal_budget", nonLocal.Budget, "bytes"));
                return "available";
            }
            catch (Exception exception)
            {
                // A failing native call must not repeat every interval.
                video.Dispose();
                video = DxgiVideoMemory.Unavailable("native_api_unavailable:" + exception.GetType().Name);
                return video.Status;
            }
        }

        private static void Uptime(List<NumberValue> gauges, bool windows)
        {
            try { gauges.Add(new NumberValue("process_uptime", Time.realtimeSinceStartupAsDouble, "seconds")); }
            catch { }
            if (worldStarted != 0)
                gauges.Add(new NumberValue("world_session_time", (Stopwatch.GetTimestamp() - worldStarted) / (double)Stopwatch.Frequency, "seconds"));
            if (!windows || uptimeUnavailable) return;
            try { gauges.Add(new NumberValue("system_uptime", SystemResources.UptimeMilliseconds() / 1000.0, "seconds")); }
            catch { uptimeUnavailable = true; }
        }

        internal static void Uninstall()
        {
            video?.Dispose();
            video = null;
            mhz?.Dispose();
            mhz = null;
        }
    }
}
