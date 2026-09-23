import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "summarize_capture.py"
spec = importlib.util.spec_from_file_location("summary_loot_visibility", SCRIPT)
summary = importlib.util.module_from_spec(spec)
spec.loader.exec_module(summary)


def gauges(**values):
    return [{"name": name, "value": value} for name, value in values.items()]


def labels(**values):
    return [{"name": name, "value": value} for name, value in values.items()]


class LootVisibilitySummaryTests(unittest.TestCase):
    """Rendering of the loot-visibility section; synthetic records only, no capture files."""

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.path = Path(self.directory.name) / "synthetic.jsonl"
        self.records = [
            {"schemaVersion": 1, "kind": "start", "captureId": "abc", "utc": "2026-01-01T00:00:00Z",
             "labels": labels(role="client")},
            {"schemaVersion": 1, "kind": "interval", "captureId": "abc", "utc": "2026-01-01T00:00:01Z",
             "intervalSeconds": 1, "gauges": [], "timings": []},
            {"schemaVersion": 1, "kind": "capture_end", "captureId": "abc", "utc": "2026-01-01T00:00:02Z"},
            {"schemaVersion": 1, "kind": "writer_end", "captureId": "abc",
             "labels": labels(reason="completed"), "gauges": gauges(dropped_records=0)},
        ]

    def tearDown(self):
        self.directory.cleanup()

    def render(self):
        self.path.write_text("".join(json.dumps(record) + "\n" for record in self.records), encoding="utf-8")
        return summary.summarize([self.path])

    def test_section_absent_without_loot_visibility_fields(self):
        self.assertNotIn("### Loot visibility", self.render())

    def test_means_sum_across_intervals_and_maxima_are_not_summed(self):
        self.records[1]["gauges"] = gauges(**{
            "loot_visibility_network_count": 2, "loot_visibility_network_sum": 300,
            "loot_visibility_network_max": 200,
            "loot_visibility_creation_count": 2, "loot_visibility_creation_sum": 20,
            "loot_visibility_creation_max": 16,
            "loot_visibility_perceived_count": 3, "loot_visibility_perceived_sum": 420,
            "loot_visibility_perceived_max": 216,
            "loot_visibility_arrival_missing": 1, "loot_visibility_locally_owned": 1,
            "loot_visibility_unattributed": 4})
        self.records[2]["gauges"] = gauges(**{
            "loot_visibility_network_count": 2, "loot_visibility_network_sum": 100,
            "loot_visibility_network_max": 60,
            "loot_visibility_perceived_count": 2, "loot_visibility_perceived_sum": 120,
            "loot_visibility_perceived_max": 70})
        self.records[1]["labels"] = labels(loot_visibility_status="installed")
        report = self.render()
        self.assertIn("### Loot visibility", report)
        self.assertIn("Probe status: installed.", report)
        self.assertIn("| chunk disappears → item ZDO arrives | 4 | 100.000 | 200.000 |", report)
        self.assertIn("| item ZDO arrives → item object created | 2 | 10.000 | 16.000 |", report)
        self.assertIn("| chunk disappears → item object created | 5 | 108.000 | 216.000 |", report)
        self.assertIn("| drops matched to no destruction | 4 |", report)
        self.assertIn("| drops without an observed network arrival | 1 |", report)
        self.assertIn("Legacy or mixed attribution", report)
        self.assertIn("never by identity", report)
        self.assertIn("lower bound", report)

    def test_zero_completions_never_print_a_mean(self):
        self.records[1]["gauges"] = gauges(**{
            "loot_visibility_network_count": 0, "loot_visibility_network_sum": 0,
            "loot_visibility_network_max": 0,
            "loot_visibility_creation_count": 0, "loot_visibility_creation_sum": 0,
            "loot_visibility_perceived_count": 0, "loot_visibility_perceived_sum": 0,
            "loot_visibility_unattributed": 7})
        self.records[1]["labels"] = labels(loot_visibility_status="installed")
        report = self.render()
        section = report[report.index("### Loot visibility"):]
        self.assertIn("| chunk disappears → item ZDO arrives | 0 | unavailable | unavailable |", section)
        self.assertIn("| chunk disappears → item object created | 0 | unavailable | unavailable |", section)
        self.assertIn("| drops matched to no destruction | 7 |", section)
        self.assertNotIn("| 0.000 |", section)

    def test_section_renders_from_status_alone(self):
        self.records[1]["labels"] = labels(loot_visibility_status="dedicated-server")
        report = self.render()
        self.assertIn("### Loot visibility", report)
        self.assertIn("Probe status: dedicated-server.", report)

    def test_histogram_and_accounting_sum_across_intervals(self):
        self.records[1]["gauges"] = gauges(**{
            "loot_visibility_perceived_bucket_16": 1, "loot_visibility_perceived_bucket_32": 2,
            "loot_visibility_perceived_bucket_1024": 3, "loot_visibility_perceived_bucket_over": 1,
            "loot_visibility_destruction_overflow": 2, "loot_visibility_arrival_capacity_skips": 1,
            "loot_visibility_non_monotonic": 0, "loot_visibility_probe_failures": 0})
        self.records[2]["gauges"] = gauges(**{
            "loot_visibility_perceived_bucket_16": 4, "loot_visibility_perceived_bucket_over": 2,
            "loot_visibility_probe_failures": 1})
        report = self.render()
        self.assertIn("≤16=5; ≤32=2; ≤1024=3; >1024=3.", report)
        self.assertIn("| destructions overwritten while still live | 2 |", report)
        self.assertIn("| arrivals dropped at capacity | 1 |", report)
        self.assertIn("| probe failures | 1 |", report)
        self.assertIn("not a fast one", report)

    def test_malformed_loot_visibility_fields_do_not_crash(self):
        self.records[1]["gauges"] = gauges(**{
            "loot_visibility_perceived_count": "oops", "loot_visibility_network_count": 2,
            "loot_visibility_network_sum": float("nan"), "loot_visibility_unattributed": None,
            "loot_visibility_destruction_overflow": 3})
        report = self.render()
        self.assertIn("### Loot visibility", report)
        self.assertIn("| chunk disappears → item ZDO arrives | 2 | unavailable | unavailable |", report)
        self.assertIn("| destructions overwritten while still live | 3 |", report)
        self.assertNotIn("| drops matched to no destruction |", report)

    def test_v2_exclusions_are_not_reported_as_local_latency(self):
        self.records[1]['labels'] = labels(loot_visibility_status='installed',
            loot_visibility_attribution='network_arrival_single_candidate_v2')
        self.records[1]['gauges'] = gauges(loot_visibility_perceived_count=0,
            loot_visibility_arrival_missing=4, loot_visibility_ambiguous=2)
        report = self.render()
        self.assertIn('Attribution v2:', report)
        self.assertIn('Local creation latency is unavailable.', report)
        self.assertIn('| drops with multiple eligible destructions (excluded) | 2 |', report)
        self.assertNotIn('Legacy or mixed attribution', report)
        self.assertIn('| chunk disappears → item object created | 0 | unavailable | unavailable |', report)

    def test_unmarked_legacy_window_mixed_with_v2_is_not_claimed_as_v2(self):
        self.records[1]['gauges'] = gauges(loot_visibility_perceived_count=1,
            loot_visibility_perceived_sum=4000, loot_visibility_perceived_max=4000)
        self.records[2]['labels'] = labels(loot_visibility_attribution='network_arrival_single_candidate_v2')
        self.records[2]['gauges'] = gauges(loot_visibility_perceived_count=1,
            loot_visibility_perceived_sum=20, loot_visibility_perceived_max=20)
        report = self.render()
        self.assertIn('Legacy or mixed attribution', report)
        self.assertNotIn('Attribution v2:', report)
        self.assertIn('| chunk disappears → item object created | 2 | 2010.000 | 4000.000 |', report)

    def test_v3_outcomes_are_named_and_excluded(self):
        self.records[1]['labels'] = labels(loot_visibility_attribution=summary.LOOT_V3,
                                           loot_visibility_legs_status='installed')
        self.records[1]['gauges'] = gauges(loot_visibility_perceived_count=1, loot_visibility_perceived_sum=90,
                                           loot_visibility_perceived_max=90, loot_visibility_foreign=3,
                                           loot_visibility_out_of_radius=2, loot_visibility_owned_only=5,
                                           loot_visibility_owned_source_excluded=6)
        report = self.render()
        self.assertIn('Attribution v3:', report)
        self.assertNotIn('Attribution v2:', report)
        self.assertIn("| drops no candidate's drop table can spawn (v3, excluded) | 3 |", report)
        self.assertIn("| drops outside every matching source's spawn radius (v3, excluded) | 2 |", report)
        self.assertIn("| network drops near only this process's own destructions (v3, excluded) | 5 |", report)
        self.assertIn('| chunk disappears → item object created | 1 | 90.000 | 90.000 |', report)

    def test_mixed_v2_and_v3_windows_still_summarize(self):
        self.records[1]['labels'] = labels(loot_visibility_attribution=summary.LOOT_V2)
        self.records[1]['gauges'] = gauges(loot_visibility_perceived_count=1, loot_visibility_perceived_sum=4000,
                                           loot_visibility_perceived_max=4000)
        self.records[2]['labels'] = labels(loot_visibility_attribution=summary.LOOT_V3,
                                           loot_visibility_legs_status='installed')
        self.records[2]['gauges'] = gauges(loot_visibility_perceived_count=1, loot_visibility_perceived_sum=60,
                                           loot_visibility_perceived_max=60, loot_visibility_foreign=1)
        report = self.render()
        self.assertIn('Mixed v2/v3 attribution', report)
        self.assertNotIn('Attribution v3:', report)
        self.assertIn('| chunk disappears → item object created | 2 | 2030.000 | 4000.000 |', report)
        self.assertIn("(v3, excluded) | 1 |", report)

    def test_witnesses_sort_slowest_first_and_skip_malformed(self):
        self.records[1]['labels'] = labels(loot_visibility_attribution=summary.LOOT_V3, loot_visibility_witness=
            'CopperOre<area:rock4_copper_frac,d=1.2,net=1450,cre=18,cand=3/1,own=1;broken entry')
        self.records[1]['gauges'] = gauges(loot_visibility_witness_count=2)
        self.records[2]['labels'] = labels(loot_visibility_attribution=summary.LOOT_V3, loot_visibility_witness=
            'Wood<log:Beech_log,d=0.5,net=90,cre=20,cand=1/1,own=0;Stone<area:rock4_copper_frac,d=x,net=1,cre=1,cand=1/1,own=0')
        report = self.render()
        self.assertIn('Slowest timed matches (2 of 2 witnesses', report)
        rows = [line for line in report.splitlines() if line.startswith('| CopperOre') or line.startswith('| Wood')]
        self.assertEqual(rows, ['| CopperOre | area:rock4_copper_frac | 1.2 | 1450 | 18 | 3/1 | 1 |',
                                '| Wood | log:Beech_log | 0.5 | 90 | 20 | 1/1 | 0 |'])
        self.assertIn('2 malformed witness entries were skipped.', report)

    def test_client_owner_legs_render_and_server_leg_is_absent(self):
        self.records[1]['labels'] = labels(loot_visibility_attribution=summary.LOOT_V3, loot_visibility_legs_status='installed')
        self.records[1]['gauges'] = gauges(loot_visibility_perceived_count=0,
                                           loot_visibility_owner_instantiate_count=2, loot_visibility_owner_instantiate_sum=1,
                                           loot_visibility_owner_instantiate_max=0.6,
                                           loot_visibility_owner_send_count=2, loot_visibility_owner_send_sum=120,
                                           loot_visibility_owner_send_max=70, loot_visibility_owner_send_over_1s=0,
                                           loot_visibility_owner_send_expired_unsent=1)
        report = self.render()
        self.assertIn('| owner: own destruction → own Instantiate of its drop | 2 | 0.500 | 0.600 | unavailable |', report)
        self.assertIn('| owner: Instantiate → first ZDOData send to the server | 2 | 60.000 | 70.000 | 0 |', report)
        self.assertIn('| owner send: drops never sent within the window | 1 |', report)
        self.assertNotIn('server: first receipt', report)

    def test_server_capture_reports_its_leg_without_observer_zeros(self):
        self.records[1]['labels'] = labels(loot_visibility_status='dedicated-server', loot_visibility_legs_status='installed',
                                           loot_visibility_attribution=summary.LOOT_V3)
        self.records[1]['gauges'] = gauges(loot_visibility_server_send_count=3, loot_visibility_server_send_sum=4500,
                                           loot_visibility_server_send_max=4000, loot_visibility_server_send_over_1s=1,
                                           loot_visibility_server_send_scan_skipped=0, loot_visibility_leg_failures=0)
        report = self.render()
        self.assertIn('Observer timings: not measured by this process', report)
        self.assertNotIn('| chunk disappears → item ZDO arrives |', report)
        self.assertIn('| server: first receipt → first send to each other peer | 3 | 1500.000 | 4000.000 | 1 |', report)
        self.assertNotIn('owner: Instantiate', report)


if __name__ == "__main__":
    unittest.main()
