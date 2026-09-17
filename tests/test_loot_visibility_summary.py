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
        self.assertIn("| matched drops created locally (no network arrival) | 1 |", report)
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


if __name__ == "__main__":
    unittest.main()
