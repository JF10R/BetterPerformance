#!/usr/bin/env python3
"""Compare two or three paired repetitions per arm from an independent loop observer."""

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
    # Frames and repeated windows within one process are not independent repeats.
    n = len(baseline)
    if n not in (2, 3) or len(candidate) != n or any(
            not math.isfinite(x) for x in baseline + candidate):
        raise ValueError('Expected two or three finite paired run values.')
    differences = [after - before for before, after in zip(baseline, candidate)]
    mean = statistics.mean(differences)
    # Two-sided Student t critical values, 95%, df = n - 1. Exploratory only.
    critical = {2: 12.7062047364, 3: 4.3026527299}[n]
    margin = critical * statistics.stdev(differences) / math.sqrt(n)
    return dict(n=n, mean_delta=mean, range=[min(differences), max(differences)],
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
    blocks = sorted({key[2] for key in indexed})
    if blocks not in ([1, 2], [1, 2, 3]):
        raise ValueError('Incomplete design: require two or three contiguous blocks.')
    expected = {(role, arm, block) for role in ('client', 'server') for arm in ARMS for block in blocks}
    if set(indexed) != expected:
        raise ValueError('Incomplete or unexpected design; require both roles and all three arms in every block.')
    return indexed


def compare(paths):
    indexed = index_records([json.loads(Path(path).read_text(encoding='utf-8-sig')) for path in paths])
    blocks = sorted({key[2] for key in indexed})
    n = len(blocks)
    lines = ['# Repeated measurement comparison', '',
             f'{n} runs per arm. Values come from the same independent loop observer in every arm. '
             'Each run, not each frame, is one repetition. Lower values indicate fewer/shorter loop stalls.', '',
             'Candidate minus baseline: a negative difference favors the candidate. '
             f'95% intervals use paired Student t estimates with df={n - 1}. '
             'They assume approximately normal, independent block differences; with this small sample this cannot be verified. '
             'Treat them as exploratory uncertainty estimates, not proof of equivalence or general performance gains.', '']
    for role in ('client', 'server'):
        lines += [f'### {role}: run-level observations', '',
                  '| Arm | Metric | Mean | Run minimum | Run maximum |',
                  '| --- | --- | ---: | ---: | ---: |']
        for arm in ARMS:
            for metric in METRICS:
                values = [indexed[role, arm, block][metric] for block in blocks]
                lines.append(f'| {arm} | {metric} | {statistics.mean(values):.3f} | {min(values):.3f} | {max(values):.3f} |')
        for baseline, candidate in (('old_ultra', 'new_ultra'), ('new_ultra', 'new_classic')):
            lines += ['', f'### {role}: {candidate} minus {baseline}', '',
                      '| Metric | Mean difference | Paired difference range | Exploratory 95% interval |',
                      '| --- | ---: | ---: | ---: |']
            for metric in METRICS:
                before = [indexed[role, baseline, block][metric] for block in blocks]
                after = [indexed[role, candidate, block][metric] for block in blocks]
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
