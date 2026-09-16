import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('capture_summary', Path(__file__).parents[1] / 'scripts/summarize_capture.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class PreparationSummaryTests(unittest.TestCase):
    def test_weighted_boundary_and_service_semantics(self):
        def record(count, preparation, service, kind):
            values = {'budget_observed_batches': count, 'budget_preparation_observed_batches': count,
                      'budget_preparation_elapsed_sum': preparation, 'budget_service_elapsed_sum': service,
                      'budget_allowance_rebased_batches': count}
            return {'kind': kind, 'gauges': [{'name': k, 'value': v} for k, v in values.items()]}
        report = '\n'.join(module.budget_report([record(2, 10, 4, 'interval'), record(8, 90, 16, 'capture_end')]))
        self.assertIn('Preparation mean/max: 10.000/', report)
        self.assertIn('following service mean/max: 2.000/', report)
        self.assertIn('not necessarily a service-budget violation', report)

    def test_old_capture_does_not_invent_preparation(self):
        report = '\n'.join(module.budget_report([{'kind': 'interval', 'gauges': [{'name': 'budget_observed_batches', 'value': 5}]}]))
        self.assertNotIn('Preparation mean/max', report)
