#!/usr/bin/env python3
"""Compare three paired repetitions per arm from an independent loop observer."""

import argparse
import json
import math
from pathlib import Path
import statistics

ARMS = ('old_ultra', 'new_ultra', 'new_classic')
METRICS = ('mean_loop_ms', 'p95_loop_ms', 'max_loop_ms', 'stalls_per_minute')


def run_metrics(frames):
    if not frames or any(not math.isfinite(x) or x <= 0 for x in frames):
        raise ValueError('Expected finite positive loop intervals.')
    ordered = sorted(frames)
    return dict(mean_loop_ms=statistics.mean(frames),
                p95_loop_ms=ordered[math.ceil(len(ordered) * .95) - 1],
                max_loop_ms=max(frames),
                stalls_per_minute=sum(x > 50 for x in frames) * 60000 / sum(frames))


def paired_estimate(baseline, candidate):
    # This protocol has three complete blocks. Frames are not independent repeats.
    if len(baseline) != 3 or len(candidate) != 3 or any(
            not math.isfinite(x) for x in baseline + candidate):
        raise ValueError('Expected exactly three finite paired run values.')
    differences = [after - before for before, after in zip(baseline, candidate)]
    mean = statistics.mean(differences)
    # Two-sided Student t critical value, 95%, df = 2. Exploratory assumption only.
    margin = 4.3026527299 * statistics.stdev(differences) / math.sqrt(3)
    return dict(n=3, mean_delta=mean, range=[min(differences), max(differences)],
                ci95=[mean - margin, mean + margin])


def index_records(records):
    indexed = {}
    for record in records:
        key = (record['Role'], record['Variant'], record['Block'])
        if key in indexed:
            raise ValueError(f'Duplicate run identity: {key}')
        if record.get('Truncated', True):
            raise ValueError('Truncated observer output is not comparable.')
        indexed[key] = run_metrics(record['FramesMs'])
    expected = {(role, arm, block) for role in ('client', 'server') for arm in ARMS for block in (1, 2, 3)}
    if set(indexed) != expected:
        raise ValueError('Incomplete or unexpected design; require both roles, three arms and three blocks.')
    return indexed


def compare(paths):
    indexed = index_records([json.loads(Path(path).read_text(encoding='utf-8-sig')) for path in paths])
    lines = ['# Repeated measurement comparison', '',
             'Three runs per arm. Values come from the same independent loop observer in every arm. '
             'Each run, not each frame, is one repetition. Lower values indicate fewer/shorter loop stalls.', '',
             'Candidate minus baseline: a negative difference favors the candidate. '
             '95% intervals use paired Student t estimates with only two degrees of freedom. '
             'They assume approximately normal, independent block differences; with three blocks this cannot be verified. '
             'Treat them as exploratory uncertainty estimates, not proof of equivalence or general performance gains.', '']
    for role in ('client', 'server'):
        lines += [f'### {role}: run-level observations', '',
                  '| Arm | Metric | Mean | Run minimum | Run maximum |',
                  '| --- | --- | ---: | ---: | ---: |']
        for arm in ARMS:
            for metric in METRICS:
                values = [indexed[role, arm, block][metric] for block in (1, 2, 3)]
                lines.append(f'| {arm} | {metric} | {statistics.mean(values):.3f} | {min(values):.3f} | {max(values):.3f} |')
        for baseline, candidate in (('old_ultra', 'new_ultra'), ('new_ultra', 'new_classic')):
            lines += ['', f'### {role}: {candidate} minus {baseline}', '',
                      '| Metric | Mean difference | Paired difference range | Exploratory 95% interval |',
                      '| --- | ---: | ---: | ---: |']
            for metric in METRICS:
                before = [indexed[role, baseline, block][metric] for block in (1, 2, 3)]
                after = [indexed[role, candidate, block][metric] for block in (1, 2, 3)]
                estimate = paired_estimate(before, after)
                low, high = estimate['ci95']
                minimum, maximum = estimate['range']
                lines.append(f"| {metric} | {estimate['mean_delta']:.3f} | [{minimum:.3f}, {maximum:.3f}] | [{low:.3f}, {high:.3f}] |")
        lines.append('')
    lines += ['### Interpretation limits', '',
              '- Pacing, graphics mode, mod settings, fixture resets and external load must be checked against the experiment record.',
              '- The old/new comparison estimates the effect of the complete diagnostics change; it does not isolate any one probe.',
              '- The Classic/Ultra comparison changes simulation scope and is not a behavior-preserving mod optimization.',
              '- Per-frame p95 here is a nearest-rank quantile from observer samples, not a percentile averaged across capture intervals.',
              '- Loop timings are elapsed time and include pacing/scheduling. They are not exclusive CPU time or GPU time.',
              '- Small samples, order effects and concurrent applications limit causal attribution. No LAN-client latency is measured.', '']
    return '\n'.join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('observers', nargs='+', type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    if args.output.resolve() in {path.resolve() for path in args.observers}:
        parser.error('Output must differ from all observer inputs.')
    try:
        args.output.write_text(compare(args.observers), encoding='utf-8')
    except (OSError, ValueError, KeyError, TypeError) as error:
        parser.exit(1, f'Cannot compare runs: {error}\n')


if __name__ == '__main__':
    main()
