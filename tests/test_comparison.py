import importlib.util
from pathlib import Path
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / 'scripts' / 'compare_runs.py'
spec = importlib.util.spec_from_file_location('comparison', SCRIPT)
comparison = importlib.util.module_from_spec(spec)
spec.loader.exec_module(comparison)


class ComparisonTests(unittest.TestCase):
    def test_paired_run_uncertainty(self):
        estimate = comparison.paired_estimate([10, 20, 30], [9, 22, 35])
        self.assertEqual(estimate['n'], 3)
        self.assertEqual(estimate['mean_delta'], 2)
        self.assertEqual(estimate['range'], [-1, 5])
        self.assertAlmostEqual(estimate['ci95'][1], 2 + 4.3026527299 * 3 / 3**0.5)
        self.assertLess(estimate['ci95'][0], 0)

    def test_invalid_or_incomplete_pairs(self):
        for baseline, candidate in [([1], [2]), ([1, 2, 3], [2, 3]), ([1, 2, 3], [1, float('nan'), 2])]:
            with self.assertRaises(ValueError):
                comparison.paired_estimate(baseline, candidate)

    def test_run_metrics_threshold_and_quantile(self):
        metrics = comparison.run_metrics([10, 20, 30, 50, 100])
        self.assertEqual(metrics['max_loop_ms'], 100)
        self.assertEqual(metrics['p95_loop_ms'], 100)
        self.assertAlmostEqual(metrics['stalls_per_minute'], 60000 / 210)
        with self.assertRaises(ValueError):
            comparison.run_metrics([0, -1])

    def test_duplicate_and_incomplete_design_rejected(self):
        record = dict(Role='client', Variant='old_ultra', Block=1, Truncated=False, FramesMs=[33, 34])
        with self.assertRaisesRegex(ValueError, 'Duplicate'):
            comparison.index_records([record, record])
        with self.assertRaisesRegex(ValueError, 'Incomplete'):
            comparison.index_records([record])


if __name__ == '__main__':
    unittest.main()
