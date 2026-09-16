# Engine markers and counters

Diagnostics only. `EngineTelemetry` reads Unity's own profiler metrics through `ProfilerRecorder`.
It writes no engine setting: frame cap, VSync, quality and garbage-collector mode are read back, never assigned.

### Configuration

- `Diagnostics.EngineMarkersEnabled` (default true) installs the recorders at startup. Requires restart.
- `Diagnostics.EngineGpuTimingEnabled` (default true) adds the GPU frame-time recorder only. GPU timing can add
  engine-side cost; that cost is not measured here, so it is separately switchable.

### What is exported

Timing markers export three gauges per metric, `engine_<marker>_count` (frames), `engine_<marker>_sum` and
`engine_<marker>_max`. Each recorder holds one sample per completed frame with every occurrence inside that frame
summed, so `max` is the worst frame of the interval and `sum` the interval total. Markers observed here:
`gfx_wait_for_present_on_gfx_thread`, `gfx_wait_for_render_thread`, `wait_for_target_fps`, `culling`,
`shader_create_gpuprogram`, `player_loop`, `animator`, `physics_simulate`, `physics_processing`, `gc_collect`.
All but `gc_collect`, `physics_processing` and `shader_create_gpuprogram` are filtered to the main thread; a
collection can run on any allocating thread, so `gc_collect` collects from all threads.

Counters export their latest value as `engine_counter_<name>`: `cpu_total_frame_time`, `cpu_main_thread_frame_time`,
`cpu_render_thread_frame_time`, `gpu_frame_time`, `gc_used_memory`, `total_used_memory`, `gc_allocated_in_frame`,
`gc_allocation_in_frame_count`, `draw_calls_count`, `batches_count`, `set_pass_calls_count`, `triangles_count`,
`shadow_casters_count`, `visible_skinned_meshes_count`.

Units come from the recorder's own `UnitType`, never from the metric name. Nanoseconds are converted to
milliseconds; bytes and counts are exported unchanged. A metric with an undeclared unit is exported as `raw`.

Fixed-step accounting is counted from the plugin's own `FixedUpdate`/`Update` callbacks, with no patch on the
engine loop: `frames_observed`, `fixed_steps_total`, `fixed_steps_max_per_frame`, `frames_with_multiple_fixed_steps`,
`frames_without_fixed_step` and `fixed_steps_pending`. Read-only engine settings accompany them as
`fixed_delta_time`, `maximum_delta_time`, `target_frame_rate`, `vsync_count` and `gc_incremental_time_slice`,
with labels `gc_mode` and `gc_incremental`.

### Reading the result

**GPU-bound** looks like a high `engine_gfx_wait_for_present_on_gfx_thread_sum` together with
`engine_counter_gpu_frame_time` at or above `engine_counter_cpu_main_thread_frame_time`: the main thread is waiting
for presentation. **CPU-bound** is the opposite, high main-thread or render-thread frame time with small present
waits. A large `engine_wait_for_target_fps_sum` is neither: it is the frame cap or VSync holding the loop, and it
should be read beside `target_frame_rate` and `vsync_count`.

`engine_gc_collect_max` is the actual collection pause, which loop-gap and frame-time numbers only contain
indirectly. A **fixed-step storm** after a stall shows up as `fixed_steps_max_per_frame` well above one with
`frames_with_multiple_fixed_steps` rising: the engine is replaying catch-up simulation steps, bounded by
`maximum_delta_time`.

### Limits

Every requested metric carries its own label `engine_marker_<name>` with value `available`, `unavailable`,
`invalid`, `headless` or `gpu_timing_disabled`, and the module reports `engine_markers_status`. Availability is
resolved once at startup against the installed player; a metric absent then stays absent for the session.

Headless captures (a `Null` graphics device, that is the dedicated server) skip rendering-only metrics.
Each timing recorder keeps the last 600 frames. A poll longer than that loses frames; the recorder reports that it
wrapped, exported as `engine_<marker>_wrapped`, but the engine does not report how many frames were lost, so no
dropped-frame count is estimated.

These are elapsed scope times, not charged CPU time, and the cost of the recorders themselves is not measured.
