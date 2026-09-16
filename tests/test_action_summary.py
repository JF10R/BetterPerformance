import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('action_summary', Path(__file__).resolve().parents[1] / 'scripts' / 'summarize_capture.py')
summary = importlib.util.module_from_spec(spec)
spec.loader.exec_module(summary)


def window(values, kind='interval', coverage='bounded_local_observations'):
    return dict(kind=kind, gauges=[dict(name='action_pickup_' + key, value=value) for key, value in values.items()],
                labels=[dict(name='action_telemetry_coverage', value=coverage)])


class ActionSummaryTests(unittest.TestCase):
    def test_explicit_zero_omission_preserves_coverage_and_last_pending(self):
        for coverage in ('no_local_player', 'bounded_local_observations'):
            omitted = window({}, coverage=coverage)
            omitted['labels'].append(dict(name='action_zero_category_semantics',
                value='omitted_category_is_zero_observed_interval_activity; not_complete_coverage'))
            report = '\n'.join(summary.action_report([omitted]))
            self.assertIn('| pickup | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |', report)
            self.assertIn('unavailable (no observed completions)', report)
            self.assertIn('zero observed interval activity', report)
            self.assertNotIn('0.000 ms', report)
            report = '\n'.join(summary.action_report([window(dict(pending=5)), omitted]))
            self.assertIn('| pickup | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |', report)
        absent = '\n'.join(summary.action_report([window({})]))
        self.assertNotIn('| pickup | 0 |', absent)

    def test_weighted_completed_and_footer_without_averaging_means(self):
        records = [window(dict(confirmed=1, request_completed=1, request_wait_sum=100, pending=2)),
                   window(dict(confirmed=3, request_completed=3, request_wait_sum=60, censored=2, pending=4)),
                   window(dict(confirmed=1, request_completed=1, request_wait_sum=40, censored=3,
                               ambiguous_confirmed=2, rejected=1, timed_out=1, unmatched=2, capacity_skips=1, pending=0), 'capture_end')]
        report = '\n'.join(summary.action_report(records))
        self.assertIn('40.000 ms weighted mean (5 completions; 200.000 ms sum)', report)
        self.assertIn('| pickup | 5 | 1 | 5 | 2 | 1 | 2 | 1 | 0 |', report)
        self.assertIn('native false may still partially transfer stacks', report)
        self.assertIn('exclude censored and ambiguous timelines', report)

    def test_direct_and_ownership_have_their_own_denominators(self):
        report = '\n'.join(summary.action_report([window(dict(confirmed=7, request_completed=2, request_wait_sum=40,
            direct_completed=5, direct_wait_sum=5, ownership_completed=1, ownership_wait_sum=30))]))
        self.assertIn('20.000 ms weighted mean (2 completions', report)
        self.assertIn('1.000 ms weighted mean (5 completions', report)
        self.assertIn('30.000 ms weighted mean (1 completions', report)
        self.assertIn('Ownership-path wait is not RPC RTT', report)

    def test_no_local_server_does_not_invent_latency(self):
        report = '\n'.join(summary.action_report([window(dict(confirmed=0, request_completed=0,
            request_wait_sum=0, direct_completed=0, ownership_completed=0), coverage='no_local_player')]))
        self.assertIn('no_local_player', report)
        self.assertIn('unavailable (no observed completions)', report)
        self.assertNotIn('0.000 ms', report)

    def test_missing_invalid_waits_and_old_records(self):
        self.assertEqual([], summary.action_report([dict(kind='interval', gauges=[])]))
        for bad in (None, float('nan'), -1):
            values = dict(request_completed=1)
            if bad is not None:
                values['request_wait_sum'] = bad
            self.assertIn('unavailable (missing/invalid wait sums', '\n'.join(summary.action_report([window(values)])))
        self.assertIn('unavailable (wait sum without completion count)', '\n'.join(summary.action_report([
            window(dict(request_completed=1, request_wait_sum=10)), window(dict(request_wait_sum=90))])))

    def test_multiple_captures_keep_independent_timelines(self):
        with tempfile.TemporaryDirectory() as directory:
            paths = []
            for index, wait in enumerate((10, 1000)):
                path = Path(directory) / f'{index}.jsonl'
                records = [dict(schemaVersion=1, kind='start', captureId=str(index), labels=[dict(name='role', value='client')]),
                           window(dict(request_completed=1, request_wait_sum=wait)), dict(kind='capture_end'),
                           dict(kind='writer_end', labels=[dict(name='reason', value='completed')])]
                for record in records:
                    record.update(schemaVersion=1, captureId=str(index))
                path.write_text(''.join(json.dumps(record) + '\n' for record in records), encoding='utf-8')
                paths.append(path)
            report = summary.summarize(paths)
            self.assertEqual(2, report.count('Local action observations (this capture only)'))
            self.assertIn('10.000 ms weighted mean', report)
            self.assertIn('1000.000 ms weighted mean', report)
            self.assertNotIn('505.000 ms', report)
