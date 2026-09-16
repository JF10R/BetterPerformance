import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "summarize_capture.py"
spec = importlib.util.spec_from_file_location("summary_new", SCRIPT)
summary = importlib.util.module_from_spec(spec)
spec.loader.exec_module(summary)


def gauges(**values):
    return [{"name": name, "value": value} for name, value in values.items()]


def labels(**values):
    return [{"name": name, "value": value} for name, value in values.items()]


class NewTelemetrySummaryTests(unittest.TestCase):
    """Rendering of the 0.4.4 diagnostics; synthetic records only, no capture files."""

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.path = Path(self.directory.name) / "synthetic.jsonl"
        self.records = [
            {"schemaVersion": 1, "kind": "start", "captureId": "abc", "utc": "2026-01-01T00:00:00Z",
             "labels": labels(role="client")},
            {"schemaVersion": 1, "kind": "interval", "captureId": "abc", "utc": "2026-01-01T00:00:01Z",
             "intervalSeconds": 1, "gauges": [],
             "timings": [{"name": "LoopInterval", "count": 10, "sumMs": 200, "maxMs": 80,
                          "p95UpperBoundMs": 80, "stallsOver50Ms": 1, "failedCalls": 0}]},
            {"schemaVersion": 1, "kind": "capture_end", "captureId": "abc", "utc": "2026-01-01T00:00:02Z"},
            {"schemaVersion": 1, "kind": "writer_end", "captureId": "abc",
             "labels": labels(reason="completed"), "gauges": gauges(dropped_records=0)},
        ]

    def tearDown(self):
        self.directory.cleanup()

    def render(self):
        self.path.write_text("".join(json.dumps(record) + "\n" for record in self.records), encoding="utf-8")
        return summary.summarize([self.path])

    def no_none_cells(self, report):
        for line in report.splitlines():
            self.assertNotIn("None", line)
            if line.startswith("|"):
                self.assertTrue(line.endswith("|"), line)

    # --- old-schema regression -------------------------------------------------

    def test_old_schema_capture_renders_exactly_as_before(self):
        report = self.render()
        for heading in ("### Base simulation", "### Attribution", "### Engine markers",
                        "### Host and network path", "### Ownership and replication", "### Gameplay"):
            self.assertNotIn(heading, report)
        self.assertIn("| LoopInterval | 10 | 20.000 | 80.000 |", report)
        self.assertIn("### Largest observed loop gaps", report)
        self.no_none_cells(report)

    # --- base simulation -------------------------------------------------------

    def test_base_simulation_rows_skip_zero_calls_and_state_inclusive_semantics(self):
        self.records[1]["timings"] += [
            {"name": "WearBatch", "count": 4, "sumMs": 40, "maxMs": 22,
             "p95UpperBoundMs": 22, "stallsOver50Ms": 0, "failedCalls": 0},
            {"name": "DungeonGenerate", "count": 0, "sumMs": 0, "maxMs": 0,
             "p95UpperBoundMs": 0, "stallsOver50Ms": 0, "failedCalls": 0}]
        self.records[2]["timings"] = [
            {"name": "WearBatch", "count": 2, "sumMs": 10, "maxMs": 60,
             "p95UpperBoundMs": 60, "stallsOver50Ms": 1, "failedCalls": 0}]
        self.records[1]["gauges"] = gauges(population_wear_pieces=100, population_heightmaps=9)
        self.records[2]["gauges"] = gauges(population_wear_pieces=250, population_heightmaps=9)
        report = self.render()
        self.assertIn("| WearBatch | 6 | 50.000 | 60.000 | 1 |", report)
        self.assertNotIn("| DungeonGenerate |", report)
        self.assertIn("population_wear_pieces=100–250; population_heightmaps=9–9", report)
        self.assertIn("not summable across rows", report)
        self.assertIn("WearSupportUpdate recorded no calls", report)
        self.no_none_cells(report)

    def test_observed_wear_support_removes_the_mod_disabled_note(self):
        self.records[1]["timings"].append(
            {"name": "WearSupportUpdate", "count": 3, "sumMs": 6, "maxMs": 3,
             "p95UpperBoundMs": 3, "stallsOver50Ms": 0, "failedCalls": 0})
        report = self.render()
        self.assertIn("| WearSupportUpdate | 3 | 6.000 | 3.000 | 0 |", report)
        self.assertNotIn("WearSupportUpdate recorded no calls", report)

    # --- attribution -----------------------------------------------------------

    def attribution_rows(self, keys):
        return [{"group": "prefab_create", "key": key, "count": 1, "sumMs": value,
                 "maxMs": value, "bytes": 0} for key, value in keys]

    def test_attribution_aggregates_keys_and_conserves_totals(self):
        self.records[1]["attributions"] = [
            {"group": "prefab_create", "key": "Greydwarf", "count": 2, "sumMs": 10, "maxMs": 7, "bytes": 0},
            {"group": "prefab_create", "key": "other", "count": 5, "sumMs": 3, "maxMs": 1, "bytes": 0},
            {"group": "prefab_send_bytes", "key": "Player", "count": 1, "sumMs": 1, "maxMs": 1, "bytes": 800},
            {"group": "prefab_send_bytes", "key": "prefab:1234", "count": 1, "sumMs": 2, "maxMs": 2, "bytes": 90},
            {"group": "routed_rpc", "key": "ZNet.RPC_PeerInfo", "count": 4, "sumMs": 8, "maxMs": 5, "bytes": 0}]
        self.records[2]["attributions"] = [
            {"group": "prefab_create", "key": "Greydwarf", "count": 1, "sumMs": 5, "maxMs": 9, "bytes": 0}]
        report = self.render()
        self.assertIn("| Greydwarf | 3 | 15.000 | 9.000 | 0 |", report)
        self.assertIn("| other | 5 | 3.000 | 1.000 | 0 |", report)
        self.assertIn("Group total: 8 observations; 18.000 ms; 0 bytes.", report)
        self.assertIn("| Player | 1 | 1.000 | 1.000 | 800 |", report)
        self.assertIn("| ZNet.RPC_PeerInfo | 4 | 8.000 | 5.000 | 0 |", report)
        self.assertIn("before batching and compression", report)
        self.assertIn("unresolved names", report)
        self.no_none_cells(report)

    def test_prefab_send_bytes_ranks_by_bytes_not_time(self):
        self.records[1]["attributions"] = [
            {"group": "prefab_send_bytes", "key": "Big", "count": 1, "sumMs": 0.1, "maxMs": 0.1, "bytes": 9000},
            {"group": "prefab_send_bytes", "key": "Slow", "count": 1, "sumMs": 50, "maxMs": 50, "bytes": 10}]
        report_lines = [line for line in self.render().splitlines() if line.startswith("| Big |") or line.startswith("| Slow |")]
        self.assertEqual(report_lines[0].split("|")[1].strip(), "Big")
        self.assertEqual(report_lines[1].split("|")[1].strip(), "Slow")

    def test_attribution_keeps_other_row_and_folds_the_remainder(self):
        rows = [{"group": "routed_rpc", "key": f"Rpc{index:02d}", "count": 1,
                 "sumMs": 100 - index, "maxMs": 1, "bytes": 0} for index in range(20)]
        rows.append({"group": "routed_rpc", "key": "other", "count": 2, "sumMs": 0.5, "maxMs": 0.5, "bytes": 0})
        self.records[1]["attributions"] = rows
        report = self.render()
        self.assertIn("| Rpc00 | 1 | 100.000 | 1.000 | 0 |", report)
        self.assertNotIn("| Rpc15 |", report)
        self.assertIn("| other | 2 | 0.500 | 0.500 | 0 |", report)
        self.assertIn("| remaining 5 keys | 5 | 415.000 | 1.000 | 0 |", report)
        total = sum(100 - index for index in range(20)) + 0.5
        self.assertIn(f"Group total: 22 observations; {total:.3f} ms; 0 bytes.", report)

    def test_attribution_renders_the_target_split_group_ranked_by_time(self):
        rows = [{"group": "routed_rpc_target", "key": f"RPC_Damage→Prefab{index:02d}", "count": 1,
                 "sumMs": index, "maxMs": index, "bytes": 0} for index in range(1, 18)]
        rows.append({"group": "routed_rpc_target", "key": "RPC_Damage→(none)", "count": 3,
                     "sumMs": 40, "maxMs": 20, "bytes": 0})
        rows.append({"group": "routed_rpc", "key": "RPC_Damage", "count": 20, "sumMs": 193, "maxMs": 20, "bytes": 0})
        self.records[1]["attributions"] = rows
        report = self.render()
        self.assertIn("`routed_rpc_target` — 18 keys, ranked by sum ms.", report)
        self.assertIn("| RPC_Damage→(none) | 3 | 40.000 | 20.000 | 0 |", report)
        self.assertIn("| RPC_Damage→Prefab17 | 1 | 17.000 | 17.000 | 0 |", report)
        self.assertNotIn("| RPC_Damage→Prefab03 |", report)
        self.assertIn("| remaining 3 keys | 3 | 6.000 | 3.000 | 0 |", report)
        self.assertIn("Group total: 20 observations; 193.000 ms; 0 bytes.", report)
        self.assertIn("re-reports `routed_rpc` time", report)
        self.assertLess(report.index("`routed_rpc` —"), report.index("`routed_rpc_target` —"))
        self.no_none_cells(report)

    def test_attribution_absent_key_prints_no_section(self):
        self.assertNotIn("### Attribution", self.render())

    # --- engine markers --------------------------------------------------------

    def test_engine_markers_counters_and_gpu_reading(self):
        self.records[1]["gauges"] = gauges(**{
            "engine_gc_collect_count": 60, "engine_gc_collect_sum": 30, "engine_gc_collect_max": 12,
            "engine_player_loop_count": 60, "engine_player_loop_sum": 900, "engine_player_loop_max": 40,
            "engine_player_loop_wrapped": 1,
            "engine_counter_gpu_frame_time": 18, "engine_counter_cpu_main_thread_frame_time": 11,
            "fixed_steps_total": 20, "fixed_steps_max_per_frame": 3,
            "frames_with_multiple_fixed_steps": 2, "frames_observed": 60})
        self.records[1]["labels"] = labels(**{
            "engine_markers_status": "enabled", "engine_marker_gc_collect": "available",
            "engine_marker_physics_simulate": "unavailable", "gc_mode": "Enabled", "gc_incremental": "true"})
        self.records[2]["gauges"] = gauges(**{
            "engine_gc_collect_count": 30, "engine_gc_collect_sum": 10, "engine_gc_collect_max": 20,
            "engine_counter_gpu_frame_time": 25, "engine_counter_cpu_main_thread_frame_time": 9,
            "fixed_steps_total": 5, "fixed_steps_max_per_frame": 1,
            "frames_with_multiple_fixed_steps": 0, "frames_observed": 30})
        report = self.render()
        self.assertIn("Module status: enabled.", report)
        self.assertIn("| gc_collect | 90 | 40.000 | 20.000 | no |", report)
        self.assertIn("| player_loop | 60 | 900.000 | 40.000 | yes |", report)
        self.assertIn("| gpu_frame_time | 18.000 | 25.000 | 25.000 |", report)
        self.assertIn("GPU frame time maximum 25.000 against CPU main-thread frame time maximum 11.000", report)
        self.assertIn("do not establish it", report)
        self.assertIn("fixed_steps_total=25", report)
        self.assertIn("frames_observed=90", report)
        self.assertIn("frames_with_multiple_fixed_steps=2", report)
        self.assertIn("fixed_steps_max_per_frame=3", report)
        self.assertIn("Garbage collector: gc_mode=Enabled; gc_incremental=true.", report)
        self.assertIn("Metrics not available: physics_simulate=unavailable", report)
        self.assertNotIn("gc_collect=available", report)
        self.no_none_cells(report)

    def test_engine_section_absent_without_engine_fields(self):
        self.assertNotIn("### Engine markers", self.render())

    # --- host and network path -------------------------------------------------

    def test_host_network_reads_start_record_and_intervals(self):
        self.records[0]["labels"] += labels(process_priority_class="High",
                                            process_affinity_mask="0xFFF", power_scheme="Balanced")
        self.records[0]["gauges"] = gauges(host_qpc_frequency=10000000, host_qpc_timestamp=1000)
        self.records[1]["labels"] = labels(online_backend="Steamworks", steam_transport_path="relayed",
                                           steam_relay_pop="ord")
        self.records[1]["gauges"] = gauges(host_timer_resolution_current_ms=15.6,
                                           steam_connections_relayed=3, steam_connections_direct=0,
                                           peer_sockets_steam=3, host_qpc_timestamp=25000)
        self.records[2]["labels"] = labels(steam_transport_path="mixed")
        report = self.render()
        self.assertIn("- process_priority_class: High.", report)
        self.assertIn("- steam_transport_path: mixed, relayed.", report)
        self.assertIn("- power_scheme: Balanced.", report)
        self.assertIn("| host_timer_resolution_current_ms | 15.6 | 15.6 |", report)
        self.assertIn("| steam_connections_relayed | 3 | 3 |", report)
        self.assertIn("QPC frequency: 10000000 ticks per second.", report)
        self.assertIn("Shared host counter: first 1000 at 2026-01-01T00:00:00Z; "
                      "last 25000 at 2026-01-01T00:00:01Z.", report)
        self.assertIn("coarse sleep pacing", report)
        self.assertIn("relayed link adds relay hops", report)
        self.no_none_cells(report)

    def test_host_network_section_absent_without_host_fields(self):
        self.assertNotIn("### Host and network path", self.render())

    # --- ownership and replication ---------------------------------------------

    def test_ownership_totals_sum_and_manager_counters_report_range(self):
        self.records[1]["gauges"] = gauges(zdo_set_owner_calls=4, zdo_request_rpcs=2,
                                           item_request_own_rpcs=1, container_open_requests=0,
                                           zdoman_client_change_queue=10, zdoman_dead_zdos=100,
                                           ownership_other_thread_skips=1)
        self.records[2]["gauges"] = gauges(zdo_set_owner_calls=6, zdo_request_rpcs=3,
                                           zdoman_client_change_queue=40, zdoman_dead_zdos=90,
                                           ownership_probe_failures=0)
        self.records[1]["labels"] = labels(ownership_telemetry_status="installed",
                                           zdoman_counters_status="available")
        report = self.render()
        self.assertIn("Probe status: available, installed.", report)
        self.assertIn("| zdo_set_owner_calls | 10 |", report)
        self.assertIn("| zdo_request_rpcs | 5 |", report)
        self.assertIn("| container_open_requests | 0 |", report)
        self.assertIn("| zdoman_client_change_queue | 10 | 40 |", report)
        self.assertIn("| zdoman_dead_zdos | 90 | 100 |", report)
        self.assertIn("Off-thread skips: 1; probe failures: 0.", report)
        self.assertIn("not a completed transfer", report)
        self.no_none_cells(report)

    def test_ownership_section_absent_without_ownership_fields(self):
        self.assertNotIn("### Ownership and replication", self.render())

    # --- gameplay counters -----------------------------------------------------

    def test_gameplay_counters_sum_per_interval_and_group_by_surface(self):
        self.records[1]["gauges"] = gauges(container_open_requests_received=4,
                                           container_concurrent_open_conflicts=1,
                                           container_open_granted=3, container_changes=7,
                                           pieces_placed=2, tree_damage_rpcs=5,
                                           minimap_fog_applies=1, gameplay_observed_frames=600,
                                           gameplay_other_thread_skips=0, gameplay_probe_failures=0)
        self.records[2]["gauges"] = gauges(container_open_requests_received=2,
                                           container_concurrent_open_conflicts=1,
                                           container_open_granted=1, container_changes=3,
                                           pieces_placed=1, tree_damage_rpcs=4,
                                           minimap_fog_applies=2, gameplay_observed_frames=550)
        self.records[1]["labels"] = labels(gameplay_telemetry_status="installed")
        report = self.render()
        self.assertIn("### Gameplay", report)
        self.assertIn("Probe status: installed.", report)
        self.assertIn("| **Chests and inventory** | |", report)
        self.assertIn("| container_open_requests_received | 6 |", report)
        self.assertIn("| container_concurrent_open_conflicts | 2 |", report)
        self.assertIn("| container_changes | 10 |", report)
        self.assertIn("| **Building** | |", report)
        self.assertIn("| pieces_placed | 3 |", report)
        self.assertIn("| tree_damage_rpcs | 9 |", report)
        self.assertIn("| minimap_fog_applies | 3 |", report)
        self.assertIn("| gameplay_observed_frames | 1150 |", report)
        self.assertIn("Chest open requests reaching an owner: 6; refused because the chest was "
                      "already in use: 2.", report)
        self.assertIn("Off-thread skips: 0; probe failures: 0.", report)
        self.assertIn("not a frame-rate denominator", report)
        self.no_none_cells(report)

    def test_gameplay_sizes_are_ranges_and_never_summed(self):
        self.records[1]["gauges"] = gauges(inventory_items_max=12, ship_instances_max=2,
                                           smelter_catchup_items_max=5)
        self.records[2]["gauges"] = gauges(inventory_items_max=30, ship_instances_max=1,
                                           smelter_catchup_items_max=0)
        report = self.render()
        self.assertIn("| inventory_items_max | 12 | 30 |", report)
        self.assertIn("| smelter_catchup_items_max | 0 | 5 |", report)
        self.assertIn("| ship_instances_max | 1 | 2 |", report)
        self.assertNotIn("| inventory_items_max | 42 |", report)
        self.assertIn("these are lower bounds", report)
        self.no_none_cells(report)

    def test_gameplay_timings_render_in_declaration_order_and_skip_absent_metrics(self):
        self.records[1]["gauges"] = gauges(container_changes=1)
        self.records[1]["timings"] += [
            {"name": "PiecePlace", "count": 3, "sumMs": 30, "maxMs": 20,
             "p95UpperBoundMs": 20, "stallsOver50Ms": 0, "failedCalls": 0},
            {"name": "InventoryGuiUpdate", "count": 5, "sumMs": 25, "maxMs": 60,
             "p95UpperBoundMs": 60, "stallsOver50Ms": 1, "failedCalls": 0},
            {"name": "ShipBatch", "count": 0, "sumMs": 0, "maxMs": 0,
             "p95UpperBoundMs": 0, "stallsOver50Ms": 0, "failedCalls": 0}]
        self.records[2]["timings"] = [
            {"name": "PiecePlace", "count": 1, "sumMs": 5, "maxMs": 5,
             "p95UpperBoundMs": 5, "stallsOver50Ms": 0, "failedCalls": 0}]
        report = self.render()
        section = report[report.index("### Gameplay"):]
        self.assertIn("| InventoryGuiUpdate | 5 | 25.000 | 60.000 | 1 |", section)
        self.assertIn("| PiecePlace | 4 | 35.000 | 20.000 | 0 |", section)
        self.assertNotIn("| ShipBatch |", section)
        self.assertLess(section.index("| InventoryGuiUpdate |"), section.index("| PiecePlace |"))
        self.assertIn("not summable across rows", section)
        self.no_none_cells(report)

    def test_gameplay_section_reports_unavailable_and_skipped_probes(self):
        self.records[1]["gauges"] = gauges(container_changes=1)
        self.records[1]["labels"] = labels(gameplay_telemetry_status="partial",
                                           gameplay_probes_unavailable="smelter:InvalidOperationException",
                                           gameplay_skipped_counters="container_in_use_max:no_container_instance_list")
        self.records[2]["labels"] = labels(gameplay_probes_unavailable="none")
        report = self.render()
        self.assertIn("Probe status: partial.", report)
        self.assertIn("Gameplay probes unavailable: smelter:InvalidOperationException.", report)
        self.assertIn("Gameplay skipped counters: container_in_use_max:no_container_instance_list.", report)
        self.no_none_cells(report)

    def test_gameplay_section_absent_without_gameplay_fields(self):
        self.assertNotIn("### Gameplay", self.render())

    def test_gameplay_section_renders_from_timings_alone(self):
        self.records[1]["timings"] += [
            {"name": "HudUpdate", "count": 2, "sumMs": 2, "maxMs": 1,
             "p95UpperBoundMs": 1, "stallsOver50Ms": 0, "failedCalls": 0}]
        report = self.render()
        self.assertIn("### Gameplay", report)
        self.assertIn("| HudUpdate | 2 | 2.000 | 1.000 | 0 |", report)
        self.assertNotIn("| Counter | Observed total |", report[report.index("### Gameplay"):])
        self.no_none_cells(report)

    def test_malformed_gameplay_fields_do_not_crash(self):
        self.records[1]["gauges"] = gauges(container_changes="oops", inventory_items_max=None,
                                           pieces_placed=3)
        self.records[2]["gauges"] = gauges(pieces_placed=float("nan"), ship_instances_max="oops")
        report = self.render()
        self.assertIn("| pieces_placed | 3 |", report)
        self.assertNotIn("| container_changes |", report)
        self.assertNotIn("| ship_instances_max |", report)
        self.assertNotIn("| inventory_items_max |", report)
        self.no_none_cells(report)

    # --- ordering and robustness -----------------------------------------------

    def test_new_sections_precede_the_cross_capture_sections(self):
        self.records[1]["gauges"] = gauges(zdo_set_owner_calls=1, container_changes=1)
        report = self.render()
        self.assertLess(report.index("### Ownership and replication"),
                        report.index("### Gameplay"))
        self.assertLess(report.index("### Gameplay"),
                        report.index("### Largest observed loop gaps"))

    def test_malformed_new_fields_do_not_crash(self):
        self.records[1]["attributions"] = [
            {"group": "routed_rpc", "key": "Bad", "count": None, "sumMs": float("nan"),
             "maxMs": None, "bytes": None}]
        self.records[1]["gauges"] = gauges(engine_counter_gpu_frame_time="oops",
                                           host_qpc_timestamp=None, zdo_set_owner_calls=2)
        self.records[2]["attributions"] = None
        report = self.render()
        self.assertIn("| Bad | 0 | 0.000 | 0.000 | 0 |", report)
        self.assertIn("| zdo_set_owner_calls | 2 |", report)
        self.assertNotIn("### Engine markers", report)


if __name__ == "__main__":
    unittest.main()
