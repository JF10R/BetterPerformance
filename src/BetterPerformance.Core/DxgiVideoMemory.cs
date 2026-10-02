using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace BetterPerformance.Core
{
    [StructLayout(LayoutKind.Sequential)]
    public struct VideoMemoryInfo
    {
        public ulong Budget, CurrentUsage, AvailableForReservation, CurrentReservation;
    }

    // This process's video memory on one adapter, from IDXGIAdapter3::QueryVideoMemoryInfo.
    // COM is called through vtable slots, not [ComImport], and dxgi.dll is loaded from the
    // system directory so a proxy in the game folder (ReShade ships as dxgi.dll) is bypassed.
    // Slots and IIDs are from the Windows SDK 10.0.26100 dxgi.h and dxgi1_4.h.
    public sealed class DxgiVideoMemory : IDisposable
    {
        private const int NotFound = unchecked((int)0x887A0002);
        private const uint SoftwareFlag = 2;
        private const int MaxAdapters = 16;
        private const int SlotQueryInterface = 0, SlotRelease = 2, SlotCheckInterfaceSupport = 9, SlotGetDesc1 = 10;
        private const int SlotEnumAdapters1 = 12, SlotQueryVideoMemoryInfo = 14;
        private static readonly Guid FactoryId = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        private static readonly Guid Adapter3Id = new Guid("645967A4-1392-4310-A798-8053CE3E93FD");
        private static readonly Guid DeviceId = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

        private IntPtr adapter;
        private QueryVideoMemoryInfoFn? query;
        private ReleaseFn? release;

        public string Status { get; private set; } = "not_opened";
        public string AdapterMatch { get; private set; } = "none";
        public string AdapterName { get; private set; } = "unavailable";
        public string DriverVersion { get; private set; } = "unavailable";
        public ulong DedicatedVideoMemory { get; private set; }
        public int AdaptersSeen { get; private set; }

        public static DxgiVideoMemory Open(uint vendorId, uint deviceId)
        {
            var result = new DxgiVideoMemory();
            try { result.Status = result.TryOpen(vendorId, deviceId); }
            catch (Exception exception) { result.Status = "native_api_unavailable:" + exception.GetType().Name; }
            return result;
        }

        public static DxgiVideoMemory Unavailable(string status) => new DxgiVideoMemory { Status = status };

        // Node 0 of a single-GPU adapter; segment group 0 is local (dedicated), 1 non-local (shared).
        public bool TryQuery(out VideoMemoryInfo local, out VideoMemoryInfo nonLocal)
        {
            local = nonLocal = default;
            if (query == null || adapter == IntPtr.Zero) return false;
            return query(adapter, 0, 0, out local) >= 0 && query(adapter, 0, 1, out nonLocal) >= 0;
        }

        // An adapter with dedicated memory reports positive local and non-local budgets. One without
        // (a VM's display-only Basic Render Driver) may report zero; that is unavailable, not a reader fault.
        public static bool BudgetsPlausible(VideoMemoryInfo local, VideoMemoryInfo nonLocal, ulong dedicatedVideoMemory)
        {
            if (local.Budget > 0 && local.CurrentUsage > local.Budget * 4) return false;
            return dedicatedVideoMemory == 0 || (local.Budget > 0 && nonLocal.Budget > 0);
        }

        // The user-mode driver version DXGI reports, as Windows prints it (e.g. 32.0.15.6094).
        public static string FormatDriverVersion(long version)
        {
            int high = (int)(version >> 32), low = (int)version;
            return ((high >> 16) & 0xFFFF).ToString(CultureInfo.InvariantCulture) + "." +
                (high & 0xFFFF).ToString(CultureInfo.InvariantCulture) + "." +
                ((low >> 16) & 0xFFFF).ToString(CultureInfo.InvariantCulture) + "." +
                (low & 0xFFFF).ToString(CultureInfo.InvariantCulture);
        }

        private string TryOpen(uint vendorId, uint deviceId)
        {
            if (!SystemResources.IsWindows) return "unsupported_platform";
            IntPtr module = LoadLibraryW(Path.Combine(Environment.SystemDirectory, "dxgi.dll"));
            if (module == IntPtr.Zero) return "dxgi_unavailable";
            // The module is never freed: Unity keeps it loaded, and the delegates below point into it.
            IntPtr create = GetProcAddress(module, "CreateDXGIFactory1");
            if (create == IntPtr.Zero) return "dxgi_unavailable";
            var createFactory = (CreateFactoryFn)Marshal.GetDelegateForFunctionPointer(create, typeof(CreateFactoryFn));
            Guid factoryId = FactoryId;
            if (createFactory(ref factoryId, out IntPtr factory) < 0 || factory == IntPtr.Zero) return "factory_failed";
            IntPtr chosen = IntPtr.Zero, fallback = IntPtr.Zero;
            AdapterDesc1 chosenDesc = default, fallbackDesc = default;
            try
            {
                var enumerate = Slot<EnumAdapters1Fn>(factory, SlotEnumAdapters1);
                for (uint index = 0; index < MaxAdapters; index++)
                {
                    int result = enumerate(factory, index, out IntPtr candidate);
                    if (result == NotFound || result < 0 || candidate == IntPtr.Zero) break;
                    AdaptersSeen++;
                    if (Slot<GetDesc1Fn>(candidate, SlotGetDesc1)(candidate, out AdapterDesc1 description) < 0 ||
                        (description.Flags & SoftwareFlag) != 0)
                    { Release(candidate); continue; }
                    if (chosen == IntPtr.Zero && vendorId != 0 && description.VendorId == vendorId && description.DeviceId == deviceId)
                    { chosen = candidate; chosenDesc = description; continue; }
                    if (fallback == IntPtr.Zero) { fallback = candidate; fallbackDesc = description; continue; }
                    Release(candidate);
                }
            }
            finally { Release(factory); }
            if (chosen != IntPtr.Zero) { AdapterMatch = "vendor_device_id"; if (fallback != IntPtr.Zero) Release(fallback); }
            else if (fallback != IntPtr.Zero)
            {
                // Unity did not name its device, or no adapter carries its ids: DXGI order puts the primary first.
                chosen = fallback; chosenDesc = fallbackDesc;
                AdapterMatch = vendorId == 0 ? "first_hardware_adapter" : "first_hardware_adapter_ids_unmatched";
            }
            else return "adapter_not_found";
            try
            {
                AdapterName = chosenDesc.Description ?? "unavailable";
                DedicatedVideoMemory = chosenDesc.DedicatedVideoMemory.ToUInt64();
                Guid deviceInterface = DeviceId;
                if (Slot<CheckInterfaceSupportFn>(chosen, SlotCheckInterfaceSupport)(chosen, ref deviceInterface, out long umd) >= 0)
                    DriverVersion = FormatDriverVersion(umd);
                Guid adapter3Id = Adapter3Id;
                if (Slot<QueryInterfaceFn>(chosen, SlotQueryInterface)(chosen, ref adapter3Id, out IntPtr adapter3) < 0 ||
                    adapter3 == IntPtr.Zero) return "adapter3_unsupported";
                adapter = adapter3;
                query = Slot<QueryVideoMemoryInfoFn>(adapter3, SlotQueryVideoMemoryInfo);
                release = Slot<ReleaseFn>(adapter3, SlotRelease);
                return "available";
            }
            finally { Release(chosen); }
        }

        public void Dispose()
        {
            IntPtr held = adapter;
            adapter = IntPtr.Zero;
            query = null;
            if (held != IntPtr.Zero && release != null) release(held);
            release = null;
            if (Status == "available") Status = "disposed";
        }

        private static T Slot<T>(IntPtr instance, int slot) where T : Delegate
        {
            IntPtr table = Marshal.ReadIntPtr(instance);
            return (T)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(table, slot * IntPtr.Size), typeof(T));
        }

        private static void Release(IntPtr instance)
        {
            if (instance != IntPtr.Zero) Slot<ReleaseFn>(instance, SlotRelease)(instance);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct AdapterDesc1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public uint VendorId, DeviceId, SubSysId, Revision;
            public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
            public uint LuidLow;
            public int LuidHigh;
            public uint Flags;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateFactoryFn(ref Guid id, out IntPtr factory);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint ReleaseFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryInterfaceFn(IntPtr self, ref Guid id, out IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumAdapters1Fn(IntPtr self, uint index, out IntPtr adapter);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDesc1Fn(IntPtr self, out AdapterDesc1 description);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CheckInterfaceSupportFn(IntPtr self, ref Guid id, out long umdVersion);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int QueryVideoMemoryInfoFn(IntPtr self, uint node, int segmentGroup, out VideoMemoryInfo info);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    }
}
