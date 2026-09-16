import importlib.util
import copy
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

    def test_configuration_history_and_legacy_limitations(self):
        self.write()
        self.assertIn('Local graphics settings/history unavailable', summary.summarize([self.path]))
        self.records[1]['labels'] = [dict(name='config.graphics.active.SimulationDistance', value='3'),
                                    dict(name='config.simulation.synced_classic', value='true')]
        self.records[2]['configurationChanges'] = [dict(name='graphics.active.SimulationDistance', previous='3',
            current='4', elapsedSeconds=1.1, source='graphics_applied'), dict(name='graphics.active.SimulationDistance',
            previous='4', current='3', elapsedSeconds=1.2, source='graphics_applied')]
        self.records[2]['gauges'] = [dict(name='configuration_changes_dropped_total', value=2)]
        self.write()
        report = summary.summarize([self.path])
        self.assertIn('Retained field transitions: 2; dropped before export: 2', report)
        self.assertIn('| 1.100 | graphics_applied | graphics.active.SimulationDistance | 3 | 4 |', report)
        self.assertIn('| 1.200 | graphics_applied | graphics.active.SimulationDistance | 4 | 3 |', report)
        self.assertIn('interval containing a change is mixed', report)

    def test_budget_waits_are_weighted_and_unavailable_is_not_zero(self):
        self.records[1]['labels'] = [dict(name='budget_telemetry_status', value='available')]
        self.records[1]['gauges'] += [dict(name='budget_observed_batches', value=2),
            dict(name='budget_deferred_tracks_completed', value=2), dict(name='budget_post_yield_wait_sum', value=20)]
        self.records[2]['gauges'] = [dict(name='budget_observed_batches', value=1),
            dict(name='budget_deferred_tracks_completed', value=1), dict(name='budget_post_yield_wait_sum', value=40),
            dict(name='budget_post_yield_wait_max', value=40)]
        self.write()
        report = summary.summarize([self.path])
        self.assertIn('Completion-weighted mean wait: 20.000 ms; maximum: 40.000 ms', report)
        self.assertIn('not causal added latency or saved frame time', report)
        for record in self.records[1:3]:
            record['gauges'] = [dict(name='budget_observed_batches', value=0)]
        self.write()
        self.assertIn('unavailable (no completed tracks)', summary.summarize([self.path]))

    def test_configuration_report_caps_display_without_claiming_capture_loss(self):
        self.records[1]['labels'] = [dict(name='config.graphics.status', value='available')]
        self.records[1]['configurationChanges'] = [dict(name='graphics.active.LOD', previous='1', current='2',
            elapsedSeconds=i/100, source='poll') for i in range(70)]
        self.write()
        report = summary.summarize([self.path])
        self.assertEqual(report.count('| poll | graphics.active.LOD |'), 64)
        self.assertIn('Retained field transitions: 70; dropped before export: 0', report)

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

    def test_slow_windows_are_capped_with_context_and_total_call_semantics(self):
        interval = self.records[1]
        interval['gauges'] += [dict(name='process_cpu_machine_percent', value=12),
                               dict(name='gc_gen0_collections', value=2),
                               dict(name='scene_instance_count', value=450)]
        interval['slowOperations'] = [dict(operation=f'Operation{i}', totalCalls=20,
            maxMs=100+i, sumMs=200, thresholdMs=20, exceedingMax=True,
            stallsOver50Ms=1, failedCalls=0, interpretation='nested | elapsed') for i in range(15)]
        self.write()
        report = summary.summarize([self.path])
        self.assertEqual(sum('| Operation' in line for line in report.splitlines() if line.startswith('| client /')), 12)
        self.assertIn('| Operation14 | 114.000 | 20 |', report)
        self.assertNotIn('| Operation0 |', report)
        self.assertIn('CPU=12%', report)
        self.assertIn('GC0=2', report)
        self.assertIn('instances=450', report)
        self.assertIn('nested \\| elapsed', report)
        self.assertIn('total calls include calls below the threshold', report)

    def test_failure_only_slow_window_and_null_optional_array(self):
        self.records[1]['slowOperations'] = [dict(operation='SaveWorker', totalCalls=1,
            maxMs=5, sumMs=5, thresholdMs=250, exceedingMax=False,
            stallsOver50Ms=0, failedCalls=1, interpretation='asynchronous worker')]
        self.records[2]['slowOperations'] = None
        self.write()
        report = summary.summarize([self.path])
        self.assertIn('| SaveWorker | 5.000 | 1 | 5.000 | 250.000 | False | 0 | 1 |', report)

    def test_loot_without_completions_has_no_zero_wait_claim(self):
        self.records[1]['gauges'] = [dict(name='loot_queue_creations_observed', value=0),
            dict(name='loot_queue_wait_sum', value=0), dict(name='loot_queue_tracks_censored', value=4)]
        self.write()
        self.assertIn('mean lower bound unavailable (no observed completions)', summary.summarize([self.path]))

    def test_optional_slow_fields_preserve_old_captures(self):
        self.write()
        report = summary.summarize([self.path])
        self.assertNotIn('Largest structured slow-operation windows', report)
        self.assertNotIn('Loot queue observations', report)

    def test_segments_stay_separate_and_missing_segments_are_explicit(self):
        self.records[0]['labels'] += [dict(name='recording_session_id', value='session-a'),
                                      dict(name='segment_index', value='1')]
        self.write()
        second = copy.deepcopy(self.records)
        for record in second:
            record['captureId'] = 'def'
        second[0]['labels'][-1]['value'] = '3'
        second[1]['timings'][0]['p95UpperBoundMs'] = 160
        second_path = Path(self.directory.name) / 'second.jsonl'
        second_path.write_text('\n'.join(json.dumps(record) for record in second))
        report = summary.summarize([second_path, self.path])
        self.assertIn('Recording session: session-a; segment: 3', report)
        self.assertIn('missing between 1 and 3', report)
        self.assertEqual(report.count('| LoopInterval | 10 |'), 2)
        self.assertNotIn('| LoopInterval | 20 |', report)
        self.assertNotIn('| 120.000 |', report)

    def test_loot_waits_weight_by_completions_and_report_censored_coverage(self):
        for record, values in [(self.records[1], (2, 100, 4)), (self.records[2], (1, 200, 0))]:
            completed, wait_sum, pending = values
            record['gauges'] = [dict(name='loot_queue_creations_observed', value=completed),
                dict(name='loot_queue_wait_sum', value=wait_sum),
                dict(name='loot_queue_wait_max', value=wait_sum),
                dict(name='loot_queue_pending', value=pending),
                dict(name='loot_queue_tracks_censored', value=2),
                dict(name='loot_queue_priority_opportunities', value=5),
                dict(name='loot_queue_untracked_creations', value=50)]
        self.write()
        report = summary.summarize([self.path])
        self.assertIn('3 completions; wait sum 300.000 ms; mean lower bound 100.000 ms', report)
        self.assertIn('Censored tracks: 4', report)
        self.assertIn('last pending: 0; peak pending: 4', report)
        self.assertIn('Priority opportunities: 10 observations', report)
        self.assertIn('not missed loot', report)

    def test_collector_backoff_and_sampled_overhead_are_explicit(self):
        self.records[0]['labels'] += [
            dict(name='recorder_overhead_semantics', value='one_in_64_valid_records; aggregation_inside_lock_only; game_timings_not_sampled'),
            dict(name='collector_backoff', value='double interval after overrun')]
        for record, target, overruns in [(self.records[1], 1, 2), (self.records[2], 4, 3)]:
            record['gauges'] = [dict(name='collector_target_interval_at_poll', value=target),
                dict(name='collector_previous_poll_cost', value=2.5),
                dict(name='collector_poll_overruns_total', value=overruns),
                dict(name='loot_queue_scan_overruns', value=1),
                dict(name='loot_queue_cooldown_skips', value=2)]
            record['labels'] = [dict(name='writer_priority', value='below_normal'),
                                dict(name='loot_queue_coverage', value='partial/cooldown')]
        self.write()
        report = summary.summarize([self.path])
        self.assertIn('Poll targets observed: 1–4 seconds', report)
        self.assertIn('Recorded export-window range with poll readings: 1.000–1.000 seconds', report)
        self.assertIn('highest cumulative poll overrun count: 3', report)
        self.assertNotIn('highest cumulative poll overrun count: 5', report)
        self.assertIn('scan overruns: 2; cooldown skips: 4', report)
        self.assertIn('Writer priority: below_normal', report)
        self.assertIn('Loot coverage: partial/cooldown', report)
        self.assertIn('one in 64 valid records', report)
        self.assertIn('excludes lock waiting', report)
        self.assertIn('not total instrumentation cost', report)
        self.assertIn('Game timings are not sampled', report)

    def test_old_overhead_metadata_never_implies_new_sampling(self):
        self.records[1]['timings'][0]['name'] = 'TimingRecorder'
        self.write()
        report = summary.summarize([self.path])
        self.assertNotIn('one in 64', report)
        self.assertNotIn('excludes lock waiting', report)
        self.assertNotIn('Poll targets observed', report)
        self.assertIn('sampling semantics are not declared', report)
        self.records[0]['labels'].append(dict(name='recorder_overhead_semantics', value='aggregation_and_lock_wait'))
        self.write()
        report = summary.summarize([self.path])
        self.assertIn('Recorder overhead semantics: aggregation_and_lock_wait', report)
        self.assertNotIn('one in 64', report)
        self.assertNotIn('excludes lock waiting', report)


if __name__ == "__main__":
    unittest.main()
