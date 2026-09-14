import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "summarize_capture.py"
spec = importlib.util.spec_from_file_location("summary", SCRIPT)
summary = importlib.util.module_from_spec(spec)
spec.loader.exec_module(summary)


class SummaryTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.path = Path(self.directory.name) / "synthetic.jsonl"
        self.records = [
            {"schemaVersion": 1, "kind": "start", "captureId": "abc", "utc": "2026-01-01T00:00:00Z",
             "labels": [{"name": "role", "value": "client"}]},
            {"schemaVersion": 1, "kind": "interval", "captureId": "abc", "utc": "2026-01-01T00:00:01Z",
             "intervalSeconds": 1, "gauges": [{"name": "reported_send_queue_max", "value": -20000}],
             "timings": [{"name": "LoopInterval", "count": 10, "sumMs": 200, "maxMs": 80,
                          "p95UpperBoundMs": 80, "stallsOver50Ms": 1, "failedCalls": 0}]},
            {"schemaVersion": 1, "kind": "capture_end", "captureId": "abc", "utc": "2026-01-01T00:00:02Z"},
            {"schemaVersion": 1, "kind": "writer_end", "captureId": "abc",
             "labels": [{"name": "reason", "value": "completed"}],
             "gauges": [{"name": "dropped_records", "value": 0}]},
        ]

    def tearDown(self):
        self.directory.cleanup()

    def write(self, tail=""):
        self.path.write_text("".join(json.dumps(record) + "\n" for record in self.records) + tail, encoding="utf-8")

    def test_aggregates_and_negative_queue(self):
        self.write()
        report = summary.summarize([self.path])
        self.assertIn("| LoopInterval | 10 | 20.000 | 80.000 |", report)
        self.assertIn("-20000 bytes", report)
        self.assertNotIn("may be incomplete", report)

    def test_truncated_tail_is_explicit(self):
        self.records.pop()
        self.write('{"schemaVersion":')
        report = summary.summarize([self.path])
        self.assertIn("Truncated final JSON", report)
        self.assertIn("may be incomplete", report)

    def test_corruption_in_middle_is_rejected(self):
        self.write()
        text = self.path.read_text()
        self.path.write_text(text.splitlines()[0] + "\nBROKEN\n" + "\n".join(text.splitlines()[1:]))
        with self.assertRaisesRegex(ValueError, "before the end"):
            summary.load_capture(self.path)

    def test_drop_warning_and_schema(self):
        self.records[-1]["gauges"][0]["value"] = 3
        self.write()
        self.assertIn("dropped 3 records", summary.summarize([self.path]))
        self.records[1]["schemaVersion"] = 99
        self.write()
        with self.assertRaisesRegex(ValueError, "schema"):
            summary.load_capture(self.path)

    def test_mixed_capture_ids_rejected(self):
        self.records[1]["captureId"] = "other"
        self.write()
        with self.assertRaisesRegex(ValueError, "mismatched"):
            summary.load_capture(self.path)

    def test_native_measurements_and_invalid_memory(self):
        self.records[1]['gauges'] += [
            dict(name='process_working_set', value=0),
            dict(name='steam_pending_reliable', value=120),
            dict(name='steam_sent_unacked_reliable', value=300),
        ]
        self.write()
        report = summary.summarize([self.path])
        self.assertIn('Invalid zero/nonpositive working-set', report)
        self.assertIn('| steam_pending_reliable | 120.000 |', report)
        self.assertIn('excludes game/mod managed queues', report)


if __name__ == "__main__":
    unittest.main()
