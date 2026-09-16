import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('bottleneck_summary', Path(__file__).resolve().parents[1] / 'scripts/summarize_capture.py')
summary = importlib.util.module_from_spec(spec)
spec.loader.exec_module(summary)

class BottleneckReportTests(unittest.TestCase):
    def test_weighted_cpu_and_memory_not_added(self):
        rows = [{'kind': 'interval', 'gauges': [dict(name='main_thread_cpu_delta', value=100), dict(name='main_thread_cpu_window', value=1000),
                  dict(name='process_private_commit', value=1048576)]},
                {'kind': 'interval', 'gauges': [dict(name='main_thread_cpu_delta', value=900), dict(name='main_thread_cpu_window', value=9000),
                  dict(name='process_private_commit', value=2097152)]}]
        report = '\n'.join(summary.bottleneck_report(rows))
        self.assertIn('10.00%', report)
        self.assertIn('| process_private_commit | 1.00 | 2.00 | 2.00 | 1.00 |', report)
        self.assertIn('must not be added', report)
        self.assertIn('GPU samples: unavailable', report)

    def test_incidents_bounded_and_missing_context_explicit(self):
        rows = [dict(kind='interval', utc=f'window{i}', timings=[dict(name='LoopInterval', count=1, maxMs=100+i),
                 dict(name='CharacterSave', count=1, maxMs=50)]) for i in range(12)]
        report = '\n'.join(summary.bottleneck_report(rows))
        self.assertEqual(8, report.count('| window'))
        self.assertIn('unavailable', report)
        self.assertIn('CharacterSave=50.00', report)
        self.assertNotIn('CharacterSave=100', report)

    def test_does_not_invent_intervals(self):
        self.assertEqual([], summary.bottleneck_report([{'kind': 'start'}]))

if __name__ == '__main__': unittest.main()
