using System;
using System.Diagnostics;
using BetterPerformance.Core;

internal static class SystemResourceTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    internal static void CpuWindow()
    {
        var window = new SystemCpuWindow();
        double busy;
        Check(window.Observe(100, 300, 200, out busy) == SystemCpuWindowStatus.Warmup && busy == 0,
            "First observation must not fabricate machine CPU.");
        // Kernel includes idle: +1000 kernel (of which 600 idle) and +1000 user = 1400 busy of 2000.
        Check(window.Observe(700, 1300, 1200, out busy) == SystemCpuWindowStatus.Available && busy == 70,
            "Busy share is kernel plus user minus idle, over kernel plus user.");
        Check(window.Observe(1700, 2300, 1200, out busy) == SystemCpuWindowStatus.Available && busy == 0,
            "An all-idle interval reads zero, not unavailable.");
        Check(window.Observe(1700, 3300, 2200, out busy) == SystemCpuWindowStatus.Available && busy == 100,
            "An interval without idle time reads 100.");
        Check(window.Observe(1700, 3300, 2200, out busy) == SystemCpuWindowStatus.InvalidDelta,
            "No elapsed CPU time cannot produce a percentage.");
        Check(window.Observe(0, 0, 0, out busy) == SystemCpuWindowStatus.InvalidDelta,
            "Regressing counters must not wrap into huge activity.");
        Check(window.Observe(50, 100, 0, out busy) == SystemCpuWindowStatus.Available && busy == 50,
            "After an invalid sample, recover from a fresh baseline.");
        Check(window.Observe(500, 200, 0, out busy) == SystemCpuWindowStatus.InvalidDelta,
            "Idle beyond kernel plus user is inconsistent, not negative load.");
        window.Reset();
        Check(window.Observe(ulong.MaxValue - 100, ulong.MaxValue - 200, ulong.MaxValue - 200, out busy)
            == SystemCpuWindowStatus.Warmup, "Reset drops the previous capture epoch.");
        Check(window.Observe(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, out busy) == SystemCpuWindowStatus.Available
            && busy == 75, "Cumulative sums near the counter limit cannot overflow.");
    }

    internal static void PureHelpers()
    {
        var clocks = new ProcessorMhzSummary();
        Check(clocks.CurrentAverage == 0, "An empty summary has no average.");
        clocks.Add(3600, 3000, 3600);
        clocks.Add(3600, 1200, 2400);
        clocks.Add(4000, 1800, 3600);
        Check(clocks.Count == 3 && clocks.CurrentAverage == 2000 && clocks.CurrentMin == 1200 &&
            clocks.MaxMhz == 4000 && clocks.LimitMin == 2400, "Clock summary keeps the average, minima and maximum.");
        Check(SystemResources.PagesToBytes(ulong.MaxValue, 4096) > 7e22, "Page conversion cannot overflow.");
        var none = new VideoMemoryInfo();
        var some = new VideoMemoryInfo { Budget = 8UL << 30, CurrentUsage = 1UL << 30 };
        Check(DxgiVideoMemory.BudgetsPlausible(none, none, 0), "No dedicated memory: zero budgets are unavailable, not a fault.");
        Check(!DxgiVideoMemory.BudgetsPlausible(none, some, 8UL << 30), "Dedicated memory with a zero local budget is a fault.");
        Check(DxgiVideoMemory.BudgetsPlausible(some, some, 8UL << 30), "Positive budgets on a discrete adapter pass.");
        Check(!DxgiVideoMemory.BudgetsPlausible(new VideoMemoryInfo { Budget = 1, CurrentUsage = 5 }, some, 8UL << 30),
            "Usage far above the budget is a fault.");
        Check(SystemResources.AcLineLabel(0) == "offline" && SystemResources.AcLineLabel(1) == "online" &&
            SystemResources.AcLineLabel(255) == "unknown", "AC line labels follow SYSTEM_POWER_STATUS.");
        PowerSnapshot desktop = new PowerSnapshot { AcLine = 1, BatteryFlag = 128, BatteryPercent = 255 };
        PowerSnapshot draining = new PowerSnapshot { AcLine = 0, BatteryFlag = 1, BatteryPercent = 64 };
        PowerSnapshot charging = new PowerSnapshot { AcLine = 1, BatteryFlag = 8 | 2, BatteryPercent = 30 };
        PowerSnapshot full = new PowerSnapshot { AcLine = 1, BatteryFlag = 1, BatteryPercent = 100 };
        PowerSnapshot unknown = new PowerSnapshot { AcLine = 255, BatteryFlag = 255, BatteryPercent = 255 };
        Check(SystemResources.BatteryLabel(desktop) == "no_battery" && !SystemResources.HasBatteryPercent(desktop),
            "A desktop reports no battery and no percentage.");
        Check(SystemResources.BatteryLabel(draining) == "discharging" && SystemResources.HasBatteryPercent(draining),
            "A laptop off the charger is discharging, with its percentage.");
        Check(SystemResources.BatteryLabel(charging) == "charging", "The charging bit wins over the level bits.");
        Check(SystemResources.BatteryLabel(full) == "on_ac_not_charging", "A full battery on AC is not charging.");
        Check(SystemResources.BatteryLabel(unknown) == "unknown" && !SystemResources.HasBatteryPercent(unknown),
            "Unknown status reports no percentage.");
        Check(SystemResources.BatterySaverLabel(1) == "on" && SystemResources.BatterySaverLabel(0) == "off",
            "Battery saver is bit 0 of SystemStatusFlag.");
        long version = ((long)((32 << 16) | 0) << 32) | (uint)((15 << 16) | 6094);
        Check(DxgiVideoMemory.FormatDriverVersion(version) == "32.0.15.6094", "Driver version splits into four 16-bit parts.");
        Check(DxgiVideoMemory.FormatDriverVersion(unchecked((long)0xFFFF_FFFF_FFFF_FFFFUL)) == "65535.65535.65535.65535",
            "Driver version parts are unsigned.");
    }

    // Real native calls on this host: proves struct layouts and vtable slots, and bounds the cost
    // on CoreCLR. Mono marshalling in the game is a different runtime; this is not an in-game figure.
    internal static void NativeReaders()
    {
        if (!SystemResources.IsWindows) { Console.WriteLine("SKIP native system readers: not Windows"); return; }
        Check(SystemResources.TryReadMemory(out SystemMemorySnapshot memory), "GetPerformanceInfo must succeed.");
        Check(memory.PhysicalTotal > 512.0 * 1024 * 1024 && memory.PhysicalAvailable > 0 &&
            memory.PhysicalAvailable <= memory.PhysicalTotal, "Physical figures must be plausible bytes.");
        Check(memory.CommitTotal > 0 && memory.CommitTotal <= memory.CommitLimit && memory.CommitLimit >= memory.PhysicalTotal * 0.5,
            "Commit total must sit under the commit limit.");
        var cpu = new SystemCpuWindow();
        Check(SystemResources.TryReadCpuTimes(out ulong idle, out ulong kernel, out ulong user), "GetSystemTimes must succeed.");
        cpu.Observe(idle, kernel, user, out _);
        var spin = Stopwatch.StartNew();
        while (spin.ElapsedMilliseconds < 60) { }
        Check(SystemResources.TryReadCpuTimes(out idle, out kernel, out user), "GetSystemTimes must succeed twice.");
        Check(cpu.Observe(idle, kernel, user, out double busy) == SystemCpuWindowStatus.Available && busy > 0 && busy <= 100,
            "A spinning thread makes the machine measurably busy.");
        Check(SystemResources.UptimeMilliseconds() > 0, "GetTickCount64 reports uptime.");
        Check(SystemResources.TryReadPower(out PowerSnapshot power), "GetSystemPowerStatus must succeed.");
        using var clocks = new ProcessorMhzReader(Environment.ProcessorCount);
        Check(clocks.TryRead(out ProcessorMhzSummary mhz) && mhz.Count == Environment.ProcessorCount && mhz.MaxMhz > 100,
            "CallNtPowerInformation reports a maximum clock for every logical processor.");
        string foreign = SystemResources.ForeignModules(new[] { "dxgi.dll", "d3d11.dll", "kernel32.dll" });
        Check(foreign == "none", "System modules are not reported as proxies: " + foreign);
        using var video = DxgiVideoMemory.Open(0, 0);
        Console.WriteLine("  dxgi: status=" + video.Status + " match=" + video.AdapterMatch + " adapters=" + video.AdaptersSeen +
            " name=" + video.AdapterName + " driver=" + video.DriverVersion + " dedicated=" + video.DedicatedVideoMemory);
        bool videoAvailable = video.Status == "available";
        if (videoAvailable)
        {
            Check(video.TryQuery(out VideoMemoryInfo local, out VideoMemoryInfo nonLocal), "QueryVideoMemoryInfo must succeed.");
            Console.WriteLine("  dxgi budgets: local=" + local.Budget + " nonlocal=" + nonLocal.Budget + " usage=" + local.CurrentUsage);
            Check(DxgiVideoMemory.BudgetsPlausible(local, nonLocal, video.DedicatedVideoMemory),
                "An adapter with dedicated memory reports positive local and non-local budgets.");
            Check(video.DriverVersion != "unavailable" && video.AdapterMatch == "first_hardware_adapter",
                "The driver version reads and the fallback match is labelled.");
            using var mismatched = DxgiVideoMemory.Open(0xFFFF, 0xFFFF);
            Check(mismatched.AdapterMatch == "first_hardware_adapter_ids_unmatched", "Unmatched ids are labelled, not hidden.");
        }
        else Check(video.Status != "not_opened" && video.Status.Length > 0, "An unavailable DXGI path carries an explicit status.");

        // Cost and allocation of one interval's native reads, after warmup.
        const int Rounds = 500;
        for (int i = 0; i < 20; i++) OneInterval(cpu, clocks, video);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < Rounds; i++) OneInterval(cpu, clocks, video);
        timer.Stop();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        double meanMs = timer.Elapsed.TotalMilliseconds / Rounds;
        Console.WriteLine("  native interval cost (CoreCLR): mean " + meanMs.ToString("F4") + " ms over " + Rounds +
            " rounds; managed bytes allocated " + bytes + "; dxgi " + (videoAvailable ? "included" : "absent"));
        Console.WriteLine("  per reader (ms): memory " + Mean(() => SystemResources.TryReadMemory(out _)) +
            ", cpu " + Mean(() => SystemResources.TryReadCpuTimes(out _, out _, out _)) +
            ", clocks " + Mean(() => clocks.TryRead(out _)) + ", power " + Mean(() => SystemResources.TryReadPower(out _)) +
            ", dxgi " + Mean(() => video.TryQuery(out _, out _)));
        Check(bytes == 0, "Native interval reads must allocate no managed bytes after warmup: " + bytes);
        Check(meanMs < 0.5, "Native interval reads must stay well under 0.5 ms: " + meanMs);
    }

    private static string Mean(Func<bool> read)
    {
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < 200; i++) read();
        return (timer.Elapsed.TotalMilliseconds / 200).ToString("F4");
    }

    private static void OneInterval(SystemCpuWindow cpu, ProcessorMhzReader clocks, DxgiVideoMemory video)
    {
        SystemResources.TryReadMemory(out _);
        if (SystemResources.TryReadCpuTimes(out ulong idle, out ulong kernel, out ulong user)) cpu.Observe(idle, kernel, user, out _);
        clocks.TryRead(out _);
        SystemResources.TryReadPower(out _);
        SystemResources.UptimeMilliseconds();
        video.TryQuery(out _, out _);
    }
}
