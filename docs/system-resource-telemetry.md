# System resource telemetry

Read-only machine figures beside the game's own, to tell apart video-memory pressure, system RAM pressure,
CPU contention and power state when frame times climb over a session. `SystemTelemetry` polls at the
capture interval; nothing is changed. Keys: `[Diagnostics] SystemResourcesEnabled` and
`VideoMemoryQueryEnabled` (both default on, restart required). Machine-wide figures include every other
process on the host.

## Per interval

| Gauge | Unit | Source | Question it answers |
|---|---|---|---|
| `video_memory_local_usage`, `video_memory_local_budget` | bytes | DXGI `QueryVideoMemoryInfo`, segment group local | This process's dedicated VRAM against the budget the OS grants it. Usage near budget = VRAM pressure: the OS starts demoting resources to system memory. |
| `video_memory_nonlocal_usage`, `video_memory_nonlocal_budget` | bytes | same, non-local | Resources living in shared system memory; growth here at stable local usage is demotion. |
| `engine_counter_video_memory_bytes`, `engine_counter_render_textures_bytes`/`_count`, `engine_counter_used_buffers_bytes`/`_count` | as the recorder reports | Unity `ProfilerRecorder` ([engine-telemetry.md](engine-telemetry.md)) | What Unity itself holds; a leak of render textures or buffers shows here before DXGI. |
| `system_physical_available` | bytes | `GetPerformanceInfo` | Free RAM on the machine. |
| `system_commit_total`, `system_commit_limit` | bytes | `GetPerformanceInfo` | Commit charge against the limit; near the limit, allocations page or fail. |
| `system_cpu_busy_percent` | percent | `GetSystemTimes` deltas | Whole-machine CPU busy share; compare with `process_cpu_machine_percent` to see other processes competing. |
| `cpu_mhz_current_avg`, `_current_min`, `cpu_mhz_max`, `cpu_mhz_limit_min` | MHz | `CallNtPowerInformation(ProcessorInformation)` | A limit below the maximum is the power manager capping the CPU (thermal or power policy). "Current" is as the power manager reports it and may not track turbo. |
| `battery_percent` (laptops only) | percent | `GetSystemPowerStatus` | Charge level. |
| `process_uptime` | seconds | `Time.realtimeSinceStartupAsDouble` | Time since the game started. |
| `system_uptime` | seconds | `GetTickCount64` | Time since boot. |
| `world_session_time` | seconds | Stopwatch | Time since the first capture start in this world; spans continuous-capture segments, whose `elapsed` restarts. |
| `system_probe_cost` | ms | Stopwatch | This module's own cost in the poll. |

Labels: `power_ac_line` (`online`/`offline`/`unknown`), `battery_state` (`no_battery`, `charging`,
`discharging`, `on_ac_not_charging`, `unknown`), `battery_saver` (`on`/`off`), and one status per source:
`system_memory_status`, `system_cpu_status` (first poll after start is primed, so normally `available`),
`cpu_mhz_status`, `power_status`, `video_memory_status`. A status other than `available` carries the
reason (`headless`, `disabled`, `unsupported_platform`, `adapter3_unsupported`, `native_api_unavailable:<type>`, ...).

## At capture start

`gpu_name`, `gpu_vendor`, `gpu_device_type` (Direct3D11/12, Vulkan), `gpu_api_version`, `gpu_driver_version`
(DXGI user-mode driver version, e.g. `32.0.16.1714`), `gpu_memory_size_reported` (MiB, Unity), `cpu_model`,
`cpu_nominal_frequency`, `system_physical_total`, `display_width`/`_height`/`_refresh_rate` (desktop mode),
`video_memory_adapter`, `video_memory_adapter_match`, `video_memory_dedicated_total`, `video_memory_adapters_seen`,
`graphics_proxy_modules` (which of `dxgi.dll`, `d3d11.dll`, `d3d12.dll`, `dinput8.dll`, `version.dll`,
`opengl32.dll` are loaded from outside the system directory: an injector such as ReShade; names only, never paths),
and `gpu_temperature_clock_status` = `unavailable_requires_vendor_sdk`.

## How the video memory is read

`dxgi.dll` is loaded from the system directory, so a proxy of that name in the game folder is bypassed.
The adapter is the one whose vendor and device ids match `SystemInfo.graphicsDeviceVendorID`/`graphicsDeviceID`
(`video_memory_adapter_match` = `vendor_device_id`); otherwise the first hardware adapter, labelled
`first_hardware_adapter` or `first_hardware_adapter_ids_unmatched`. COM is called through vtable slots
counted from the Windows SDK 10.0.26100 headers. Dedicated servers (`headless`) skip it. That the figure is
per process under Vulkan as under Direct3D is expected from WDDM, not verified.

## Not available

GPU temperature, GPU clocks and GPU utilisation (vendor SDKs or PDH, not added). Unity's `Gfx Used Memory`,
`Gfx Reserved Memory`, `Texture Memory` and `Mesh Memory` counters are absent from the release player
(`SystemResourceGameTests` asserts this so an update that adds them is noticed).

## Cost and tests

Offline (`SystemResourceTests.NativeReaders`, CoreCLR on the development machine, not Mono in game): one
interval's native reads cost 0.13-0.18 ms mean over 500 rounds (three runs, other builds running) and allocate
no managed bytes; `GetPerformanceInfo` (~0.07 ms) and `GetSystemTimes` (0.04-0.10 ms) dominate, the DXGI query
is 0.003 ms. The in-game figure is
`system_probe_cost`. `SystemResourceTests` also covers the CPU busy formula (warmup, idle, full load,
regressions, counter limits), clock summary, power labels and the driver-version format.
