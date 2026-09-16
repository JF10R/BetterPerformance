import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('capture_summary', Path(__file__).parents[1] / 'scripts/summarize_capture.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class LoadingSummaryTests(unittest.TestCase):
    def record(self, sequence=1, calls=2, state='pending', **extra):
        gauges = {'loading_sequence': sequence, 'loading_operation_AreaReady_calls': calls,
                  'loading_operation_AreaReady_sum': calls * 10, **extra}
        return {'kind': 'interval', 'gauges': [{'name': k, 'value': v} for k, v in gauges.items()],
                'labels': [{'name': 'loading_timeline_state', 'value': state},
                           {'name': 'loading_timeline_origin', 'value': 'game_awake_partial'}]}

    def test_repeated_cumulative_snapshots_are_not_added(self):
        report = '\n'.join(module.loading_report([self.record(), self.record(calls=3)]))
        self.assertIn('| 1 | AreaReady | 3 | 0 | 0 | 30.000 |', report)
        self.assertNotIn('| 1 | AreaReady | 5 |', report)
        self.assertIn('game_awake_partial', report)
        self.assertIn('unobserved', report)

    def test_sequences_and_completion_are_separate(self):
        report = '\n'.join(module.loading_report([
            self.record(state='hud_released', loading_since_start_HudReleased=35000),
            self.record(sequence=2, state='censored')]))
        self.assertIn('35000.000', report)
        self.assertIn('censored', report)
        self.assertEqual(report.count('| AreaReady |'), 2)

    def test_older_capture_has_no_invented_loading(self):
        self.assertEqual(module.loading_report([{'kind': 'interval'}]), [])

    def test_replacement_does_not_claim_old_attempt_still_running(self):
        report = '\n'.join(module.loading_report([self.record(), self.record(sequence=3, loading_replaced_incomplete_total=2)]))
        self.assertIn('pending at last observation; superseded', report)
        self.assertIn('incomplete replacements: 2', report)
        self.assertIn('not a complete attempt history', report)

    def test_details_cumulative_costs_not_duplicated(self):
        def observed(kind, cost):
            return {'kind': kind, 'labels': [{'name': 'loading_details_status', 'value': 'installed'}],
                    'gauges': [{'name': 'loading_biome_VerifyBiomeData_calls_total', 'value': 1},
                               {'name': 'loading_biome_VerifyBiomeData_sum_total', 'value': cost},
                               {'name': 'loading_native_respawn_minimum_last', 'value': 8}]}
        report = '\n'.join(module.loading_details_report([observed('interval', 500), observed('capture_end', 500)]))
        self.assertIn('| VerifyBiomeData | 1 | 500.000 |', report)
        self.assertIn('not seconds of waiting', report)
        self.assertIn('8 game-time seconds', report)
        self.assertNotIn('1000.000', report)
        self.assertEqual(module.loading_details_report([{'kind': 'interval'}]), [])

    def test_details_coverage_and_cache_outcomes_are_explicit(self):
        observation = {'kind': 'capture_end', 'labels': [{'name': 'loading_details_status', 'value': 'unavailable'}],
                       'gauges': [{'name': 'loading_details_probe_failures_total', 'value': 2},
                                  {'name': 'loading_details_other_thread_skips_total', 'value': 3},
                                  {'name': 'loading_biome_TryLoadCache_true_total', 'value': 1},
                                  {'name': 'loading_biome_TryLoadCache_false_total', 'value': 4}]}
        report = '\n'.join(module.loading_details_report([observation]))
        self.assertIn('Status: unavailable; probe failures: 2; other-thread skips: 3.', report)
        self.assertIn('Cache outcomes: true=1; false=4; failures=unobserved.', report)

    def test_initial_loading_snapshots_and_episodes_are_separate(self):
        def observed(seq, extra):
            return {'kind': 'interval', 'labels': [{'name': 'initial_loading_status', 'value': 'installed'},
                                                 {'name': 'initial_loading_enabled', 'value': 'true'}],
                    'gauges': [{'name': 'initial_loading_sequence', 'value': seq},
                               {'name': 'initial_loading_extra_successes', 'value': extra}]}
        report = '\n'.join(module.initial_loading_report([observed(1, 20), observed(1, 30), observed(2, 5)]))
        self.assertIn('| 1 | 30 |', report)
        self.assertIn('| 2 | 5 |', report)
        self.assertNotIn('| 1 | 50 |', report)
        self.assertIn('not objects or saved CPU time', report)
        self.assertEqual(module.initial_loading_report([{'kind': 'interval'}]), [])

    def test_initial_loading_disabled_is_not_a_zero_cost_claim(self):
        report = '\n'.join(module.initial_loading_report([{'kind': 'capture_end', 'labels': [
            {'name': 'initial_loading_status', 'value': 'disabled_at_startup'},
            {'name': 'initial_loading_enabled', 'value': 'false'}]}]))
        self.assertIn('disabled_at_startup', report)
        self.assertIn('No initial-loading work episode observed', report)
