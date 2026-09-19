#!/usr/bin/env python3
"""Summarize local BetterPerformance JSONL captures; Python standard library only."""

import argparse
import json
import math
from pathlib import Path


def named(entries):
    return {entry["name"]: entry["value"] for entry in entries}


def cell(value):
    return str(value).replace("|", "\\|").replace("\r", " ").replace("\n", " ")


def load_capture(path):
    records = []
    warnings = []
    with Path(path).open(encoding="utf-8-sig") as stream:
        for number, line in enumerate(stream, 1):
            if not line.strip():
                continue
            try:
                record = json.loads(line)
            except json.JSONDecodeError:
                # An interrupted write may truncate the tail; corruption in the middle is different.
                if stream.read().strip():
                    raise ValueError(f"Malformed JSON before the end of the capture at line {number}.")
                warnings.append("Truncated final JSON record was excluded.")
                break
            if record.get("schemaVersion") != 1:
                raise ValueError("Unsupported or missing capture schema version.")
            records.append(record)
    starts = [r for r in records if r.get("kind") == "start"]
    if len(starts) != 1 or records[0].get("kind") != "start":
        raise ValueError("Expected one capture-start record at the beginning of the file.")
    capture_id = starts[0]["captureId"]
    if any(r.get("captureId") != capture_id for r in records):
        raise ValueError("File contains mismatched capture IDs.")
    ends = [r for r in records if r.get("kind") == "writer_end"]
    if len(ends) != 1 or records[-1].get("kind") != "writer_end":
        warnings.append("Writer completion record missing or misplaced; capture may be incomplete.")
    for end in ends:
        gauges = named(end.get("gauges", []))
        if gauges.get("dropped_records", 0) > 0:
            warnings.append(f"Writer dropped {gauges['dropped_records']:g} records; summary covers retained data only.")
        reason = named(end.get("labels", [])).get("reason", "unknown")
        if reason != "completed":
            warnings.append(f"Writer stopped because of: {reason}.")
    if not any(r.get("kind") == "capture_end" for r in records):
        warnings.append("Capture-end record missing; final timing samples or stop reason may be unavailable.")
    for trailer in (r for r in records if r.get("kind") == "relay_end"):
        reason = named(trailer.get("labels", [])).get("reason", "unknown")
        warnings.append(f"Relayed copy closed by the server ({reason}); the client's local file is the complete one.")
    return records, warnings


def interval_context(record):
    gauges = named(record.get('gauges', []))
    signals = [('process_cpu_machine_percent', 'CPU', '%'), ('gc_gen0_collections', 'GC0', ''),
               ('gc_gen1_collections', 'GC1', ''), ('gc_gen2_collections', 'GC2', ''),
               ('peer_count', 'peers', ''), ('reported_send_queue_max', 'adjusted_queue', 'B'),
               ('steam_pending_reliable', 'native_pending_reliable', 'B'),
               ('steam_sent_unacked_reliable', 'native_unacked', 'B'),
               ('scene_instance_count', 'instances', '')]
    return '; '.join(f'{label}={gauges[name]:g}{unit}' for name, label, unit in signals if name in gauges) or 'unavailable'


def loot_report(observations):
    if not observations:
        return []
    def total(suffix):
        return sum(row.get('loot_queue_' + suffix, 0) for row in observations)
    def peak(suffix):
        return max(row.get('loot_queue_' + suffix, 0) for row in observations)
    completed, wait = total('creations_observed'), total('wait_sum')
    mean = f'{wait / completed:.3f} ms' if completed else 'unavailable (no observed completions)'
    maximum = f'{peak("wait_max"):.3f} ms' if completed else 'unavailable'
    pending = [row['loot_queue_pending'] for row in observations if 'loot_queue_pending' in row]
    return ['Loot queue observations (this capture only):', '',
            f'- {completed:g} completions; wait sum {wait:.3f} ms; mean lower bound {mean}; maximum lower bound {maximum}.',
            f'- Tracks started: {total("tracks_started"):g}; Censored tracks: {total("tracks_censored"):g}; '
            f'last pending: {pending[-1] if pending else "unavailable"}; peak pending: {max(pending) if pending else "unavailable"}.',
            f'- Priority opportunities: {total("priority_opportunities"):g} observations; same-tier non-loot ahead sum: '
            f'{total("same_tier_nonloot_ahead_sum"):g} sampled objects. Repeated observations are not unique items or proven savings.',
            f'- Coverage: {total("partial_passes"):g}/{total("scan_passes"):g} partial scan passes; '
            f'{total("track_capacity_skips"):g} capacity skips; {total("unknown_prefabs"):g} unknown-prefab observations.',
            f'- Untracked creations: {total("untracked_creations"):g} all-object creations, not missed loot.', '',
            'Waits run from first sampled local observation to local creation; they are lower bounds, not drop-to-pickup or network latency. '
            'Censored/pending tracks are excluded from completed means, never treated as zero waits. Coverage is sampled; retained records may omit activity.', '']


LOOT_VISIBILITY_BUCKETS = ('16', '32', '64', '128', '256', '512', '1024', 'over')


def loot_visibility_report(records):
    windows = observed_windows(records)
    statuses = label_values(windows, 'loot_visibility_status')
    legs = [('network', 'chunk disappears → item ZDO arrives'),
            ('creation', 'item ZDO arrives → item object created'),
            ('perceived', 'chunk disappears → item object created')]
    counts = {leg: summed(windows, f'loot_visibility_{leg}_count') for leg, _ in legs}
    if not statuses and not any(name.startswith('loot_visibility_') for gauges, _ in windows for name in gauges):
        return []
    output = ['### Loot visibility', '',
              'Three timestamps on this process\'s clock only. Attribution joins a destroyed hit area to a drop by '
              'position and time, never by identity, so a nearby unrelated drop can be attributed and a distant one '
              'is counted as unattributed instead. Every duration is a lower bound: the first timestamp is when the '
              'destruction was observed locally, not when the server applied it. An absent arrival means the local '
              'process created that drop itself and is the expected control case, not a missing measurement.', '']
    if statuses:
        output += ['Probe status: ' + cell(', '.join(statuses)) + '.', '']
    output += ['| Leg | Observations | Mean ms | Max ms |', '| --- | ---: | ---: | ---: |']
    for leg, description in legs:
        count = counts[leg]
        total = summed(windows, f'loot_visibility_{leg}_sum')
        peak = extent(windows, f'loot_visibility_{leg}_max')
        mean = f'{total / count:.3f}' if count and total is not None else 'unavailable'
        output.append(f'| {cell(description)} | {exact(count or 0)} | {mean} | '
                      + (f'{peak[1]:.3f}' if count and peak else 'unavailable') + ' |')
    output.append('')
    histogram = [(name, summed(windows, 'loot_visibility_perceived_bucket_' + name)) for name in LOOT_VISIBILITY_BUCKETS]
    histogram = [(name, value) for name, value in histogram if value is not None]
    if histogram:
        output += ['Perceived delay distribution (upper bound in ms, last bucket is everything above 1024): '
                   + '; '.join(f'≤{name}={exact(value)}' if name != 'over' else f'>1024={exact(value)}'
                               for name, value in histogram) + '.', '']
    accounting = [('loot_visibility_unattributed', 'drops matched to no destruction'),
                  ('loot_visibility_arrival_missing', 'matched drops created locally (no network arrival)'),
                  ('loot_visibility_locally_owned', 'matched drops this process owns'),
                  ('loot_visibility_destruction_overflow', 'destructions overwritten while still live'),
                  ('loot_visibility_arrival_capacity_skips', 'arrivals dropped at capacity'),
                  ('loot_visibility_non_monotonic', 'durations clamped to zero by a backward clock'),
                  ('loot_visibility_unknown_prefabs', 'unclassified prefabs'),
                  ('loot_visibility_destroyed_rock', 'rocks destroyed (t0 events)'),
                  ('loot_visibility_destroyed_tree', 'trees felled (t0 events)'),
                  ('loot_visibility_destroyed_log', 'logs destroyed (t0 events)'),
                  ('loot_visibility_destroyed_destructible', 'destructibles with drops destroyed (t0 events)'),
                  ('loot_visibility_probe_failures', 'probe failures')]
    rows = [(label, summed(windows, name)) for name, label in accounting]
    rows = [(label, value) for label, value in rows if value is not None]
    if rows:
        output += ['| Accounting | Observed total |', '| --- | ---: |']
        output += [f'| {cell(label)} | {exact(value)} |' for label, value in rows]
        output += ['', 'A skipped or overwritten entry is an unmeasured observation, not a fast one.', '']
    population = extent(windows, 'item_drop_instances')
    if population:
        output += [f'Dropped-item population: peak {exact(population[1])} live ItemDrop instances in one interval; '
                   'each one is a rigidbody the physics step pays for.', '']
    return output


def segment_report(segments):
    if not segments:
        return []
    output = ['### Recording segment coverage', '',
              'Files are summarized separately; timelines and percentiles are not merged. Rotation gaps are unmeasured. '
              'Consecutive indices do not prove continuous recording; omitted trailing segments cannot be detected.', '']
    for (role, session), indices in sorted(segments.items()):
        ordered = sorted(set(indices))
        issues = []
        if len(ordered) != len(indices):
            issues.append('duplicate supplied segment indices')
        if ordered[0] > 1:
            issues.append(f'initial segments before {ordered[0]} not supplied')
        issues += [f'segments missing between {left} and {right}' for left, right in zip(ordered, ordered[1:]) if right > left + 1]
        output.append(f'- {cell(role)} / {cell(session)}: supplied {", ".join(map(str, ordered))}; '
                      + ('; '.join(issues) if issues else 'no index gaps among supplied segments') + '.')
    return output + ['']


def collector_report(records, metadata, totals):
    output = []
    semantics = metadata.get('recorder_overhead_semantics')
    if semantics == 'one_in_64_valid_records; aggregation_inside_lock_only; game_timings_not_sampled':
        output += ['TimingRecorder samples one in 64 valid records, measuring aggregation inside the lock only. '
                   'It excludes lock waiting and is not total instrumentation cost. Game timings are not sampled by this policy.', '']
    elif semantics:
        output += ['Recorder overhead semantics: ' + cell(semantics) + '.', '']
    elif 'TimingRecorder' in totals:
        output += ['TimingRecorder sampling semantics are not declared in this capture; no sampling ratio is inferred. '
                   'It does not measure total instrumentation cost.', '']
    if 'collector_backoff' in metadata:
        output += ['Collector backoff policy: ' + cell(metadata['collector_backoff']) + '.', '']
    observations = [record for record in records if record.get('kind') in ('interval', 'capture_end')]
    gauges = [named(record.get('gauges', [])) for record in observations]
    def values(name):
        return [row[name] for row in gauges if name in row]
    targets = values('collector_target_interval_at_poll')
    if targets:
        output += [f'Poll targets observed: {min(targets):g}–{max(targets):g} seconds. '
                   'Targets are scheduling settings, not proof of actual sampling cadence.', '']
        windows = [record['intervalSeconds'] for record, row in zip(observations, gauges)
                   if 'collector_target_interval_at_poll' in row and record.get('intervalSeconds', 0) > 0]
        if windows:
            output += [f'Recorded export-window range with poll readings: {min(windows):.3f}–{max(windows):.3f} seconds. '
                       'Manual markers or final exports can shorten these windows.', '']
    costs, overruns = values('collector_previous_poll_cost'), values('collector_poll_overruns_total')
    if costs:
        output += [f'Previous-poll cost sampled range: {min(costs):.3f}–{max(costs):.3f} ms; values refer to the preceding poll.', '']
    if overruns:
        output += [f'Collector highest cumulative poll overrun count: {max(overruns):g}; cumulative readings are not summed.', '']
    scan_overruns, cooldown = values('loot_queue_scan_overruns'), values('loot_queue_cooldown_skips')
    if scan_overruns or cooldown:
        scan_text = f'{sum(scan_overruns):g}' if scan_overruns else 'unavailable'
        cooldown_text = f'{sum(cooldown):g}' if cooldown else 'unavailable'
        output += [f'Loot scan overruns: {scan_text}; cooldown skips: {cooldown_text} (retained interval totals). '
                   'Cooldown reduces observation coverage; skipped observations are not missed loot.', '']
    for key, title in [('writer_priority', 'Writer priority'), ('loot_queue_coverage', 'Loot coverage')]:
        states = {str(row[key]) for row in [metadata] + [named(record.get('labels', [])) for record in observations] if key in row}
        if states:
            output += [title + ': ' + cell(', '.join(sorted(states))) + '.', '']
    return output


def budget_report(records):
    samples = [named(record.get('gauges', [])) for record in records
               if record.get('kind') in ('interval', 'capture_end')]
    samples = [sample for sample in samples if 'budget_observed_batches' in sample]
    if not samples:
        return []
    statuses = {str(named(record.get('labels', [])).get('budget_telemetry_status')) for record in records
                if 'budget_telemetry_status' in named(record.get('labels', []))}
    total = lambda key: sum(sample.get(key, 0) for sample in samples)
    peak = lambda key: max((sample.get(key, 0) for sample in samples), default=0)
    completed = total('budget_deferred_tracks_completed')
    average = (f"{total('budget_post_yield_wait_sum') / completed:.3f} ms" if completed else 'unavailable (no completed tracks)')
    output = ['### Object-budget observations', '', 'Probe status: ' + cell(', '.join(sorted(statuses))) + '.', '',
            f"Observed batches: {total('budget_observed_batches'):g}; yielded batches: {total('budget_observed_yielded_batches'):g}; "
            f"near/distant loop exits: {total('budget_observed_near_yields'):g}/{total('budget_observed_distant_yields'):g}.", '',
            f"Individual creation calls: {total('budget_creation_calls'):g}; calls exceeding the entire batch allowance: "
            f"{total('budget_creation_over_allowance'):g}; maximum inclusive call: {peak('budget_creation_elapsed_max'):.3f} ms.", '',
            f"Post-yield tracks: {total('budget_deferred_tracks_started'):g} started, {completed:g} completed, "
            f"{total('budget_deferred_tracks_censored'):g} censored; capacity skips: {total('budget_deferred_capacity_skips'):g}. "
            f"Completion-weighted mean wait: {average}; maximum: "
            + (f"{peak('budget_post_yield_wait_max'):.3f} ms." if completed else 'unavailable.'), '',
            'A track follows one next candidate at an observed yield, not all deferred objects or necessarily ready work. '
            'Waits may span budget switches and include scheduling/readiness; they are not causal added latency or saved frame time. '
            'Creation and batch costs overlap. Disabled/unavailable probes do not establish zero cost.', '']
    prepared = total('budget_preparation_observed_batches')
    if prepared:
        output += [f"Preparation observed in {prepared:g} batches; allowance rebased in "
                   f"{total('budget_allowance_rebased_batches'):g}; unavailable batch boundaries: "
                   f"{total('budget_preparation_unavailable_batches'):g}.", '',
                   f"Preparation mean/max: {total('budget_preparation_elapsed_sum') / prepared:.3f}/"
                   f"{peak('budget_preparation_elapsed_max'):.3f} ms; following service mean/max: "
                   f"{total('budget_service_elapsed_sum') / prepared:.3f}/{peak('budget_service_elapsed_max'):.3f} ms.", '',
                   'Preparation includes outer setup, scanning, sorting and priority before the first near gate. '
                   'Service includes subsequent creation/readiness and distant work. When rebased, the allowance '
                   'excludes that leading preparation; a whole batch over the allowance is not necessarily a service-budget violation.', '']
    return output


def configuration_report(records):
    observed = {}
    changes = []
    dropped = 0
    for record in records:
        if record.get('kind') not in ('interval', 'capture_end'):
            continue
        observed.update({name: value for name, value in named(record.get('labels', [])).items()
                         if name.startswith('config.')})
        changes.extend(record.get('configurationChanges') or [])
        dropped = max(dropped, named(record.get('gauges', [])).get('configuration_changes_dropped_total', 0))
    if not observed:
        return ['Local graphics settings/history unavailable in this capture. '
                'Synchronized simulation distance must not be interpreted as the local graphics selection.', '']
    output = ['### Observed configuration', '',
              'Last observed values below. Active settings can differ from raw player preferences due to presets '
              'or background mode. Values are game setting IDs, not translated UI labels.', '',
              '| Setting | Last value |', '| --- | --- |']
    suffixes = ('SimulationDistance', 'LOD', 'Target3DResolutionVertical', 'UpscalingAlgorithm')
    for name, value in observed.items():
        if name.endswith(suffixes) or name.startswith('config.simulation.') or name == 'config.graphics.status':
            output.append(f'| {cell(name.removeprefix("config."))} | {cell(value)} |')
    output += ['', f'Retained field transitions: {len(changes)}; dropped before export: {dropped:g}. '
               'History is bounded to 128 field transitions per export; writer drops can additionally remove records.', '']
    if changes:
        output += ['Elapsed times mark observations since capture start. graphics_applied runs after the game applies settings; '
                   'poll detects a change at the next collection, not its exact occurrence. '
                   'An interval containing a change is mixed and must not be assigned wholly to its final setting. '
                   'Showing at most 64 transitions here; the capture retains the remaining exported transitions.', '',
                   '| Elapsed seconds | Source | Setting | Previous | Current |', '| ---: | --- | --- | --- | --- |']
        for change in changes[:64]:
            output.append(f'| {change["elapsedSeconds"]:.3f} | {cell(change["source"])} | {cell(change["name"])} | '
                          f'{cell(change["previous"])} | {cell(change["current"])} |')
        output.append('')
    return output


def bottleneck_report(records):
    intervals = [r for r in records if r.get('kind') == 'interval']
    if not intervals:
        return []
    rows = [named(r.get('gauges', [])) for r in intervals]
    output = ['### Resource and incident evidence', '',
              'Intervals nominate bottlenecks; they do not prove a cause. Main-thread CPU is one thread, not machine utilization. '
              'Memory categories overlap and must not be added; growth during loading is not proof of a leak.', '']
    memory_keys = ['process_working_set', 'process_private_commit', 'unity_allocated_memory',
                   'unity_reserved_memory', 'unity_unused_reserved_memory', 'unity_managed_used',
                   'unity_managed_reserved', 'map_cache_retained_bytes']
    memory = [(k, [g[k] / (1024 * 1024) for g in rows if k in g]) for k in memory_keys]
    memory = [(k, values) for k, values in memory if values]
    if memory:
        output += ['| Memory signal | First MiB | Last MiB | Peak MiB | Last minus first MiB |',
                   '| --- | ---: | ---: | ---: | ---: |']
        output += [f'| {k} | {v[0]:.2f} | {v[-1]:.2f} | {max(v):.2f} | {v[-1]-v[0]:.2f} |' for k, v in memory]
        output.append('')
    cpu = [(g['main_thread_cpu_delta'], g['main_thread_cpu_window']) for g in rows
           if 'main_thread_cpu_delta' in g and g.get('main_thread_cpu_window', 0) > 0]
    if cpu:
        output += [f'Main-thread CPU across observed windows: {sum(c for c,w in cpu)/sum(w for c,w in cpu)*100:.2f}%. '
                   'Window-weighted charged CPU; wall-minus-CPU is not a scheduler-wait measurement.', '']
    render = [g['render_sample_gpu_frame'] for g in rows if 'render_sample_gpu_frame' in g]
    statuses = sorted({named(r.get('labels', [])).get('render_timing_status', 'unavailable') for r in intervals})
    output += ['Render timing status: ' + cell(', '.join(statuses)) + '. Sparse GPU samples: ' +
               (f'{len(render)}, maximum observed {max(render):.3f} ms' if render else 'unavailable') +
               '; no frame percentiles inferred.', '']
    candidates = []
    excluded = {'LoopInterval', 'LoopWithGcCollection', 'LoopAcrossPhaseBoundary', 'TimingRecorder', 'CollectorPoll'}
    for index, record in enumerate(intervals):
        timings = record.get('timings', [])
        loop = max((t.get('maxMs', 0) for t in timings if t['name'] == 'LoopInterval'), default=0)
        active = [(t.get('maxMs', 0), t['name']) for t in timings if t['name'] not in excluded and t.get('maxMs', 0) >= 20]
        if loop >= 100 or active:
            candidates.append((max([loop] + [v for v,k in active]), index, active, loop))
    if candidates:
        output += ['At most eight incident intervals, with adjacent retained loop windows. Overlapping stages are inclusive and must not be summed.', '',
                   '| UTC interval end | Prior / current / next loop maximum ms | Main-thread CPU % | Overlapping stages (max ms) | Phase |',
                   '| --- | --- | ---: | --- | --- |']
        for _, index, active, loop in sorted(candidates, key=lambda r: r[0], reverse=True)[:8]:
            record = intervals[index]
            def adjacent(at):
                if at < 0 or at >= len(intervals): return 'unavailable'
                values = [t['maxMs'] for t in intervals[at].get('timings', []) if t['name'] == 'LoopInterval' and t.get('count', 0)]
                return f'{max(values):.2f}' if values else 'unavailable'
            cpu_value = rows[index].get('main_thread_cpu_percent')
            cpu_text = f'{cpu_value:.2f}' if cpu_value is not None else 'unavailable'
            stages = '; '.join(f'{k}={v:.2f}' for v,k in sorted(active, reverse=True)[:4]) or 'unavailable'
            phase = named(record.get('labels', [])).get('phase', 'unmarked')
            output.append(f'| {cell(record.get("utc", "unknown"))} | {adjacent(index-1)} / {adjacent(index)} / {adjacent(index+1)} | '
                          f'{cpu_text} | {cell(stages)} | {cell(phase)} |')
        output.append('')
    return output


def action_report(records):
    windows = [record for record in records if record.get('kind') in ('interval', 'capture_end')]
    rows = [named(record.get('gauges', [])) for record in windows]
    labels = [named(record.get('labels', [])) for record in windows]
    if not any(any(key.startswith('action_') for key in row) for row in rows + labels):
        return []
    omission = 'omitted_category_is_zero_observed_interval_activity; not_complete_coverage'
    for row, label in zip(rows, labels):
        if label.get('action_zero_category_semantics') != omission:
            continue
        for kind in ('pickup', 'container'):
            prefix = 'action_' + kind + '_'
            if not any(key.startswith(prefix) for key in row):
                suffixes = ['confirmed', 'rejected', 'censored', 'ambiguous_confirmed', 'timed_out', 'unmatched', 'capacity_skips', 'pending']
                suffixes += [stage + suffix for stage in ('request', 'direct', 'ownership') for suffix in ('_completed', '_wait_sum')]
                row.update((prefix + suffix, 0) for suffix in suffixes)

    def valid(value):
        return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value) and value >= 0

    def total(name):
        values = [row[name] for row in rows if name in row]
        return sum(values) if values and all(valid(value) for value in values) else None

    def count(prefix, suffix):
        value = total(prefix + suffix)
        return f'{value:g}' if value is not None else 'unavailable'

    def delay(prefix, stage):
        count_key, sum_key = prefix + stage + '_completed', prefix + stage + '_wait_sum'
        if any(sum_key in row and count_key not in row for row in rows):
            return 'unavailable (wait sum without completion count)'
        completed = total(count_key)
        if not completed:
            return 'unavailable (no observed completions)' if completed == 0 else 'unavailable (missing completion counts)'
        completed_rows = [row for row in rows if row.get(count_key, 0)]
        if not all(valid(row.get(sum_key)) for row in completed_rows):
            return f'unavailable (missing/invalid wait sums; {completed:g} completions)'
        wait = sum(row[sum_key] for row in completed_rows)
        return f'{wait / completed:.3f} ms weighted mean ({completed:g} completions; {wait:.3f} ms sum)'

    coverage = sorted({str(row['action_telemetry_coverage']) for row in labels if 'action_telemetry_coverage' in row})
    output = ['Local action observations (this capture only):', '',
              'Coverage: ' + cell(', '.join(coverage) if coverage else 'unavailable') + '.', '',
              '| Action | Confirmed | Native false | Censored | Ambiguous confirmed | Timed out | Unmatched | Capacity skips | Last pending |',
              '| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |']
    for kind in ('pickup', 'container'):
        prefix = 'action_' + kind + '_'
        if not any(any(key.startswith(prefix) for key in row) for row in rows):
            continue
        pending = [row[prefix + 'pending'] for row in rows if valid(row.get(prefix + 'pending'))]
        values = [count(prefix, suffix) for suffix in ('confirmed', 'rejected', 'censored', 'ambiguous_confirmed',
                                                     'timed_out', 'unmatched', 'capacity_skips')]
        output.append('| ' + kind + ' | ' + ' | '.join(values) + f' | {pending[-1] if pending else "unavailable"} |')
    output.append('')
    for kind in ('pickup', 'container'):
        prefix = 'action_' + kind + '_'
        if any(any(key.startswith(prefix) for key in row) for row in rows):
            output.append(f'- {kind} request-entry wait: {delay(prefix, "request")}.')
            if kind == 'pickup':
                output += [f'- Pickup direct-attempt wait: {delay(prefix, "direct")}.',
                           f'- Pickup full request-to-acceptance wait for the RequestOwn subset: {delay(prefix, "ownership")}.']
    output += ['', 'Pickup confirmed means matching AddItem returned true at the probe; native false may still partially transfer stacks. '
               'It does not confirm drop disposal or peer visibility. Container confirmed means matching GUI state; native false is an open-response rejection.',
               'Completed-only means exclude censored and ambiguous timelines and can underrepresent long waits. '
               'Request/direct/RequestOwn-subset waits overlap; do not add them. Ownership-path wait is not RPC RTT. '
               'No local-player observations means latency is unavailable, not zero.', '']
    if any(label.get('action_zero_category_semantics') == omission for label in labels):
        output += ['Omitted action categories mean zero observed interval activity only where the explicit schema marker is present; '
                   'this does not establish complete coverage or zero action latency.', '']
    return output


def summarize(paths):
    output = ["# BetterPerformance capture report", "",
              "Timing values are elapsed milliseconds, not exclusive CPU time. "
              "Nested timings must not be added together. Percentiles are upper bounds per retained interval.", ""]
    windows = []
    slow_windows, segments = [], {}
    for path in paths:
        records, warnings = load_capture(path)
        start = records[0]
        labels = named(start.get("labels", []))
        role = labels.get("role", "unknown")
        output += [f"### {cell(role)} — {cell(Path(path).name)}", "",
                   f"Game: {cell(labels.get('game_version', 'unknown'))}; "
                   f"plugin: {cell(labels.get('plugin_version', 'unknown'))}; "
                   f"started: {cell(start.get('utc', 'unknown'))}.", ""]
        session, segment = labels.get('recording_session_id'), labels.get('segment_index')
        if session is not None or segment is not None:
            output += [f'Recording session: {cell(session or "unknown")}; segment: {cell(segment or "unknown")}.', '']
        if session is not None and segment is not None:
            try:
                index = int(segment)
                if index < 1:
                    raise ValueError('Segment index must be positive.')
                segments.setdefault((role, session), []).append(index)
            except (ValueError, TypeError):
                warnings.append('Invalid segment index; coverage continuity unavailable.')
        for warning in warnings:
            output += [f"- {warning}"]
        if warnings:
            output.append("")
        unavailable = [name.removeprefix("probe.") + "=" + str(value)
                       for name, value in labels.items() if name.startswith("probe.") and value != "enabled"]
        if unavailable:
            output += ["Unavailable/disabled probes: " + cell(", ".join(unavailable)) + ".", ""]
        totals = {}
        peak_queues = []
        instance_counts = []
        native = {}
        memory = []
        invalid_memory = False
        loot_observations = []
        loot_statuses = set()
        for record in records:
            if record.get("kind") not in ("interval", "capture_end"):
                continue
            gauges = named(record.get("gauges", []))
            probe_status = named(record.get('labels', [])).get('loot_queue_probe_status')
            if probe_status is not None:
                loot_statuses.add(str(probe_status))
            if any(name.startswith('loot_queue_') for name in gauges):
                loot_observations.append(gauges)
            for slow in record.get('slowOperations') or []:
                slow_windows.append((slow['maxMs'], role, start['captureId'], record, slow))
            slow_windows = sorted(slow_windows, key=lambda row: row[0], reverse=True)[:12]
            resident = gauges.get('process_working_set')
            if resident is not None:
                if resident > 0:
                    memory.append(resident / 1024**2)
                else:
                    invalid_memory = True
            for name in ('steam_pending_reliable', 'steam_pending_unreliable', 'steam_sent_unacked_reliable',
                         'steam_ping_max', 'steam_queue_time_max', 'steam_in_rate', 'steam_out_rate'):
                if name in gauges:
                    native[name] = max(native.get(name, gauges[name]), gauges[name])
            if "reported_send_queue_max" in gauges:
                peak_queues.append(gauges["reported_send_queue_max"])
            if "scene_instance_count" in gauges:
                instance_counts.append(gauges["scene_instance_count"])
            for timing in record.get("timings", []):
                if timing["count"] == 0:
                    continue
                name = timing["name"]
                total = totals.setdefault(name, {"count": 0, "sum": 0, "max": 0, "p95": 0, "stalls": 0, "failures": 0})
                total["count"] += timing["count"]
                total["sum"] += timing["sumMs"]
                total["max"] = max(total["max"], timing["maxMs"])
                total["p95"] = max(total["p95"], timing["p95UpperBoundMs"])
                total["stalls"] += timing["stallsOver50Ms"]
                total["failures"] += timing["failedCalls"]
                if name == "LoopInterval":
                    windows.append((timing["maxMs"], role, record["utc"], record.get("intervalSeconds", 0),
                                    gauges.get("reported_send_queue_max"), start["captureId"]))
        output += ["| Timing | Calls/samples | Mean ms | Max ms | Worst interval p95 upper bound ms | >50 ms | Failed calls |",
                   "| --- | ---: | ---: | ---: | ---: | ---: | ---: |"]
        for name, total in sorted(totals.items()):
            output.append(f"| {cell(name)} | {total['count']} | {total['sum'] / total['count']:.3f} | "
                          f"{total['max']:.3f} | {total['p95']:.3f} | {total['stalls']} | {total['failures']} |")
        if not totals:
            output.append("| No completed timing samples | 0 | — | — | — | — | — |")
        output.append("")
        output += collector_report(records, labels, totals)
        if peak_queues:
            output += [f"Largest sampled socket queue result: {max(peak_queues):g} bytes. "
                       "This may be adjusted by BetterNetworking; it is not raw backlog or action latency.", ""]
        if instance_counts:
            output += [f"Sampled scene instances: {min(instance_counts):g}–{max(instance_counts):g}. "
                       "This is an occupancy range, not a count of creations or removals.", ""]
        if memory:
            output += [f"Sampled process working set: {min(memory):.1f}–{max(memory):.1f} MiB (resident memory, not private allocation).", ""]
        if invalid_memory:
            output += ["Invalid zero/nonpositive working-set readings were excluded; they do not indicate zero memory usage.", ""]
        if native:
            output += ["Native Steam transport maxima; scope excludes game/mod managed queues. "
                       "Pending and unacknowledged bytes are distinct; ping/queue estimates are not action latency.", "",
                       "| Signal | Sampled maximum | Unit |", "| --- | ---: | --- |"]
            for name, value in native.items():
                unit = 'bytes_per_second' if name.endswith('_rate') else 'ms' if name in ('steam_ping_max', 'steam_queue_time_max') else 'bytes'
                output.append(f'| {name} | {value:.3f} | {unit} |')
            output.append('')
        markers = [record for record in records if record.get('kind') == 'marker']
        if markers:
            output += ['Scenario markers (elapsed seconds): ' + ', '.join(
                f"{cell(named(record.get('labels', [])).get('phase', 'unknown'))}={record['elapsedSeconds']:.3f}" for record in markers) + '.', '']
        if 'LoopWithGcCollection' in totals:
            output += ['LoopWithGcCollection is a subset of LoopInterval spanning a detected collection; it is not measured GC pause time.', '']
        if 'LoopAcrossPhaseBoundary' in totals:
            output += ['LoopAcrossPhaseBoundary retains gaps spanning scenario markers. Exclude boundary-crossing intervals from phase-specific attribution; whole-run loop totals still include them.', '']
        output += loot_report(loot_observations)
        output += loot_visibility_report(records)
        output += configuration_report(records)
        output += budget_report(records)
        output += bottleneck_report(records)
        output += action_report(records)
        output += loading_report(records)
        output += loading_details_report(records)
        output += initial_loading_report(records)
        output += simulation_report(records)
        output += attribution_report(records)
        output += engine_report(records)
        output += host_network_report(records)
        output += ownership_report(records)
        output += gameplay_report(records)
        if loot_statuses:
            output += ['Loot queue probe status: ' + cell(', '.join(sorted(loot_statuses))) + '.', '']
        output += ["Not measured: " + cell(labels.get("unavailable", "see capture metadata")) + ".", ""]
    output += segment_report(segments)
    if slow_windows:
        output += ['### Largest structured slow-operation windows', '',
                   'At most 12 retained windows, ranked by peak elapsed time. UTC marks interval ends; total calls include calls below the threshold. '
                   'Rows summarize whole intervals, not individual slow calls. Context is sampled, not causal evidence. '
                   'Nested operations overlap; asynchronous work is not a main-thread pause.', '',
                   '| Role / capture | Interval end UTC | Window seconds | Operation | Max ms | Total calls | Sum ms | Threshold ms | Peak meets threshold | >50 ms | Failures | Context | Interpretation |',
                   '| --- | --- | ---: | --- | ---: | ---: | ---: | ---: | --- | ---: | ---: | --- | --- |']
        for maximum, role, capture_id, record, slow in slow_windows:
            output.append(f'| {cell(role)} / {cell(capture_id)} | {cell(record.get("utc", "unknown"))} | '
                          f'{record.get("intervalSeconds", 0):.3f} | {cell(slow["operation"])} | {maximum:.3f} | '
                          f'{slow["totalCalls"]} | {slow["sumMs"]:.3f} | {slow["thresholdMs"]:.3f} | '
                          f'{cell(slow["exceedingMax"])} | {slow["stallsOver50Ms"]} | {slow["failedCalls"]} | '
                          f'{cell(interval_context(record))} | {cell(slow.get("interpretation", "unspecified"))} |')
        output.append('')
    output += ["### Largest observed loop gaps", "",
               "UTC values mark interval ends, not the exact time of the worst gap. "
               "Use overlapping windows for local client/server comparison; coincidence alone does not prove causality.", "",
               "| Role | Capture | Interval end UTC | Window seconds | Max loop gap ms | Queue result bytes |",
               "| --- | --- | --- | ---: | ---: | ---: |"]
    for gap, role, utc, duration, queue, capture_id in sorted(windows, key=lambda row: row[0], reverse=True)[:12]:
        queue_text = "unavailable" if queue is None else f"{queue:g}"
        output.append(f"| {cell(role)} | {cell(capture_id[:8])} | {cell(utc)} | {duration:.3f} | {gap:.3f} | {queue_text} |")
    return "\n".join(output) + "\n"


def loading_report(records):
    # Gauges are cumulative within a sequence. Keep the last observed snapshot;
    # summing repeated exports would invent calls and loading duration.
    sequences = {}
    for record in records:
        if record.get('kind') not in ('interval', 'capture_end'):
            continue
        gauges = named(record.get('gauges', []))
        sequence = gauges.get('loading_sequence', 0)
        if sequence > 0:
            sequences[sequence] = (gauges, named(record.get('labels', [])))
    if not sequences:
        return []
    output = ['### Observed loading timelines', '',
              'Monotonic wall time from the named boundary; partial origins exclude earlier loading. '
              'Native Spawned after uses accumulated game dt, not whole-join wall time. '
              'HUD release is not proof of input readiness or a displayed frame. '
              'Latest cumulative snapshot per sequence; missing milestones remain unobserved. '
              'Rapid episodes can be replaced between polls: this is not a complete attempt history.', '',
              '| Sequence / kind | Origin | State | Transition → scene ms | Start → spawn point ms | Start → player ms | Start → respawn complete ms | Start → HUD released ms |',
              '| --- | --- | --- | ---: | ---: | ---: | ---: | ---: |']
    def elapsed(gauges, milestone):
        value = gauges.get('loading_since_start_' + milestone)
        return f'{value:.3f}' if value is not None else 'unobserved'
    for sequence, (gauges, labels) in sorted(sequences.items()):
        state = labels.get('loading_timeline_state', 'unknown')
        if state == 'pending' and sequence < max(sequences):
            state = 'pending at last observation; superseded'
        scene = 'unobserved'
        transition = gauges.get('loading_since_start_SceneTransitionRequested')
        awake = gauges.get('loading_since_start_SceneAwake')
        if transition is not None and awake is not None and awake >= transition:
            scene = f'{awake - transition:.3f}'
        output.append(f'| {sequence:g} / {cell(labels.get("loading_timeline_kind", "unknown"))} | '
                      f'{cell(labels.get("loading_timeline_origin", "unknown"))} | '
                      f'{cell(state)} | {scene} | '
                      + ' | '.join(elapsed(gauges, name) for name in
                                   ('SpawnPointReady', 'PlayerSpawned', 'RespawnCompleted', 'HudReleased')) + ' |')
    replaced = max(gauges.get('loading_replaced_incomplete_total', 0) for gauges, _ in sequences.values())
    output += ['', f'Observed cumulative incomplete replacements: {replaced:g}; their final stages may not have been exported.', '',
               'Inclusive native calls within each sequence overlap; do not sum parent and child costs.', '',
               '| Sequence | Operation | Calls | Not ready / false | Failures | Inclusive sum ms | Maximum ms |',
               '| --- | --- | ---: | ---: | ---: | ---: | ---: |']
    for sequence, (gauges, _) in sorted(sequences.items()):
        for operation in ('FindSpawnPoint', 'AreaReady', 'SpawnPlayer', 'UpdateRespawn'):
            prefix = 'loading_operation_' + operation
            if prefix + '_calls' not in gauges:
                continue
            output.append(f'| {sequence:g} | {operation} | {gauges[prefix + "_calls"]:g} | '
                          f'{gauges.get(prefix + "_not_ready", 0):g} | {gauges.get(prefix + "_failures", 0):g} | '
                          f'{gauges.get(prefix + "_sum", 0):.3f} | {gauges.get(prefix + "_max", 0):.3f} |')
    return output + ['']


def initial_loading_report(records):
    observations = [(named(r.get('gauges', [])), named(r.get('labels', []))) for r in records
                    if r.get('kind') in ('interval', 'capture_end')]
    observations = [(g, labels) for g, labels in observations if 'initial_loading_status' in labels]
    if not observations:
        return []
    _, latest = observations[-1]
    output = ['### Initial loading acceleration', '',
              f'Latest status: {cell(latest["initial_loading_status"])}; enabled: {cell(latest.get("initial_loading_enabled", "unobserved"))}.', '',
              'Latest cumulative snapshot per observed initial-join sequence; capture rotation does not reset it. '
              'Extra successes are zone registrations advanced by additional passes, not objects or saved CPU time. '
              'Elapsed costs overlap existing native zone timings. The 8 ms budget is soft; one native operation can exceed it.', '']
    episodes = {}
    for gauges, labels in observations:
        if 'initial_loading_sequence' in gauges:
            episodes[gauges['initial_loading_sequence']] = (gauges, labels)
    if not episodes:
        return output + ['No initial-loading work episode observed; this does not establish zero native loading cost.', '']
    output += ['| Sequence | Extra zones | Extra calls | Native pass window total ms | Max window ms | Extra native total ms | Max extra call ms | Failures | First observation UTC |',
               '| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |']
    for seq, (gauges, labels) in episodes.items():
        def value(suffix):
            found = gauges.get('initial_loading_' + suffix)
            return f'{found:g}' if found is not None else 'unobserved'
        output.append(f'| {seq:g} | {value("extra_successes")} | {value("extra_calls")} | {value("total_ms")} | '
                      f'{value("max_ms")} | {value("extra_total_ms")} | {value("extra_max_ms")} | {value("failures")} | '
                      f'{cell(labels.get("initial_loading_started_utc", "unobserved"))} |')
    output += ['', 'Compare the separate loading timeline and loop-gap metrics across similar sessions. '
               'These counters alone do not measure a causal improvement. Earlier unexported episodes can be missing.', '']
    return output


def loading_details_report(records):
    observations = [(named(r.get('gauges', [])), named(r.get('labels', []))) for r in records
                    if r.get('kind') in ('interval', 'capture_end')]
    observations = [(g, labels) for g, labels in observations if 'loading_details_status' in labels]
    if not observations:
        return []
    gauges, labels = observations[-1]
    output = ['### Loading details', '',
              f'Status: {cell(labels["loading_details_status"])}; probe failures: {gauges.get("loading_details_probe_failures_total", "unobserved")}; '
              f'other-thread skips: {gauges.get("loading_details_other_thread_skips_total", "unobserved")}.', '',
              f'Cache outcomes: true={gauges.get("loading_biome_TryLoadCache_true_total", "unobserved")}; '
              f'false={gauges.get("loading_biome_TryLoadCache_false_total", "unobserved")}; '
              f'failures={gauges.get("loading_biome_TryLoadCache_failures_total", "unobserved")}.', '',
              'Process-cumulative counters, including observed pre-capture work; last exported snapshot only. '
              'These can cover multiple joins. Nested stage times overlap. A successful native cache load does not prove subsequent generation was skipped.', '',
              '| Biome stage | Calls | Inclusive total ms | Last ms | Maximum ms | Last start UTC |',
              '| --- | ---: | ---: | ---: | ---: | --- |']
    for stage in ('VerifyBiomeData', 'TryLoadCache', 'GenerateBiomePoints', 'GenerateSectors'):
        key = 'loading_biome_' + stage
        if key + '_calls_total' in gauges:
            output.append(f'| {stage} | {gauges[key + "_calls_total"]:g} | {gauges.get(key + "_sum_total", 0):.3f} | '
                          f'{gauges.get(key + "_last", 0):.3f} | {gauges.get(key + "_max", 0):.3f} | '
                          f'{cell(labels.get(key + "_last_started_utc", "unobserved"))} |')
    output += ['', 'Readiness reasons count observed native checks, not seconds of waiting. '
               'Loaded-zone/area-false is evidence of a missing valid instance in the verified native layout; foreign patches can change that interpretation.', '']
    for name, value in gauges.items():
        if name.startswith('loading_wait_') and name.endswith('_total'):
            output.append(f'- {cell(name[len("loading_wait_"):-len("_total")])}: {value:g} checks.')
    minimum = gauges.get('loading_native_respawn_minimum_last')
    if minimum is not None:
        output += ['', f'Last observed native minimum: {minimum:g} game-time seconds; not removable exclusive wall time.']
    return output + ['']


SIMULATION_TIMINGS = ('WearBatch', 'WearSupportUpdate', 'HeightmapLateBatch', 'HeightmapRegenerate',
                      'HeightmapApplyModifiers', 'HeightmapCollisionRebuild', 'HeightmapRenderRebuild',
                      'TerrainCompApply', 'PlantUpdate', 'SmelterUpdate', 'FireplaceUpdate',
                      'CookingStationUpdate', 'BeehiveUpdate', 'SapCollectorUpdate', 'FermenterUpdate',
                      'WindmillUpdate', 'LocationSpawn', 'VegetationPlace', 'ZoneSpawn',
                      'ZonePlaceLocations', 'DungeonGenerate', 'DungeonSpawn')

SIMULATION_POPULATIONS = ('population_wear_pieces', 'population_heightmaps',
                          'population_terrain_modifiers_legacy', 'population_slow_update_objects')

ATTRIBUTION_GROUPS = ('prefab_create', 'prefab_send_bytes', 'routed_rpc', 'routed_rpc_target')


def observed_windows(records):
    """Gauge/label pairs for every exported observation window, in file order."""
    return [(named(record.get('gauges', [])), named(record.get('labels', [])))
            for record in records if record.get('kind') in ('interval', 'capture_end')]


def numeric(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def extent(windows, name):
    values = [gauges[name] for gauges, _ in windows if numeric(gauges.get(name))]
    return (min(values), max(values)) if values else None


def summed(windows, name):
    values = [gauges[name] for gauges, _ in windows if numeric(gauges.get(name))]
    return sum(values) if values else None


def exact(value):
    """Counter ticks must stay readable digit for digit; %g would round them into exponents."""
    return str(int(value)) if float(value).is_integer() else repr(value)


def label_values(windows, name):
    return sorted({str(labels[name]) for _, labels in windows if name in labels})


def simulation_report(records):
    order = {name: index for index, name in enumerate(SIMULATION_TIMINGS)}
    totals = {}
    for record in records:
        if record.get('kind') not in ('interval', 'capture_end'):
            continue
        for timing in record.get('timings') or []:
            if timing.get('name') not in order:
                continue
            row = totals.setdefault(timing['name'], {'count': 0, 'sum': 0.0, 'max': 0.0, 'stalls': 0})
            row['count'] += timing.get('count', 0) or 0
            row['sum'] += timing.get('sumMs', 0) or 0
            row['max'] = max(row['max'], timing.get('maxMs', 0) or 0)
            row['stalls'] += timing.get('stallsOver50Ms', 0) or 0
    windows = observed_windows(records)
    ranges = [(name, extent(windows, name)) for name in SIMULATION_POPULATIONS]
    ranges = [(name, span) for name, span in ranges if span is not None]
    active = {name: row for name, row in totals.items() if row['count']}
    if not active and not ranges:
        return []
    output = ['### Base simulation', '',
              'Inclusive elapsed; not summable across rows. Batches contain the per-object stages they drive, '
              'and rows count observed calls only, not complete simulation coverage.', '']
    if active:
        output += ['| Stage | Calls | Sum ms | Max ms | >50 ms |', '| --- | ---: | ---: | ---: | ---: |']
        for name in sorted(active, key=lambda item: order[item]):
            row = active[name]
            output.append(f'| {cell(name)} | {exact(row["count"])} | {row["sum"]:.3f} | '
                          f'{row["max"]:.3f} | {exact(row["stalls"])} |')
        output.append('')
    else:
        output += ['No base-simulation stage recorded a call; this does not establish zero simulation cost.', '']
    if ranges:
        output += ['Sampled populations: ' + '; '.join(f'{name}={low:g}–{high:g}' for name, (low, high) in ranges)
                   + '. Occupancy ranges at poll time, not creations, removals or per-frame work.', '']
    if totals.get('WearSupportUpdate', {}).get('count', 0) == 0:
        output += ['WearSupportUpdate recorded no calls: support checks were not observed in this capture '
                   '(a mod may disable wear). Absence of observations is not proof that wear is inactive.', '']
    return output


def attribution_report(records):
    groups = {}
    for record in records:
        if record.get('kind') not in ('interval', 'capture_end'):
            continue
        for row in record.get('attributions') or []:
            entry = groups.setdefault(str(row.get('group', 'unknown')), {}).setdefault(
                str(row.get('key', 'unknown')), {'count': 0, 'sumMs': 0.0, 'maxMs': 0.0, 'bytes': 0})
            for field in ('count', 'sumMs', 'bytes'):
                value = row.get(field, 0)
                entry[field] += value if numeric(value) else 0
            maximum = row.get('maxMs', 0)
            entry['maxMs'] = max(entry['maxMs'], maximum if numeric(maximum) else 0)
    if not groups:
        return []
    output = ['### Attribution', '',
              'Per-key totals aggregated across this capture only; interval boundaries are lost here. '
              'Elapsed time is inclusive and covers work other plugins add to the same call. '
              'Bytes are the serialized payload before batching and compression, not wire bytes. '
              'Keys reading `prefab:<hash>` or `hash:<int>` are unresolved names, and a routed-RPC key can be '
              'the registry handler name rather than the registered name. A `routed_rpc_target` key names the '
              'RPC and its target prefab; it re-reports `routed_rpc` time for the allow-listed RPCs and is not '
              'additional cost.', '']
    for group in list(ATTRIBUTION_GROUPS) + sorted(set(groups) - set(ATTRIBUTION_GROUPS)):
        rows = groups.get(group)
        if not rows:
            continue
        by_bytes = group == 'prefab_send_bytes'
        field = 'bytes' if by_bytes else 'sumMs'
        ordered = sorted(rows.items(), key=lambda item: (item[1][field], item[0]), reverse=True)
        shown = ordered[:15]
        chosen = {key for key, _ in shown}
        if 'other' in rows and 'other' not in chosen:
            shown.append(('other', rows['other']))
            chosen.add('other')
        rest = [item for item in ordered if item[0] not in chosen]
        output += [f'`{cell(group)}` — {len(rows)} keys, ranked by {"bytes" if by_bytes else "sum ms"}. '
                   'The `other` row holds observations the plugin could not keep individually.', '',
                   '| Key | Count | Sum ms | Max ms | Bytes |', '| --- | ---: | ---: | ---: | ---: |']
        for key, entry in shown:
            output.append(f'| {cell(key)} | {exact(entry["count"])} | {entry["sumMs"]:.3f} | '
                          f'{entry["maxMs"]:.3f} | {exact(entry["bytes"])} |')
        if rest:
            output.append(f'| remaining {len(rest)} keys | {exact(sum(e["count"] for _, e in rest))} | '
                          f'{sum(e["sumMs"] for _, e in rest):.3f} | {max(e["maxMs"] for _, e in rest):.3f} | '
                          f'{exact(sum(e["bytes"] for _, e in rest))} |')
        output += ['', f'Group total: {exact(sum(e["count"] for e in rows.values()))} observations; '
                   f'{sum(e["sumMs"] for e in rows.values()):.3f} ms; '
                   f'{exact(sum(e["bytes"] for e in rows.values()))} bytes. '
                   'Rows above conserve these totals; the maximum column is a per-key maximum and is not summable.', '']
    return output


def engine_report(records):
    windows = observed_windows(records)
    markers, counters, wrapped = {}, {}, set()
    for gauges, _ in windows:
        for name, value in gauges.items():
            if not name.startswith('engine_') or not numeric(value):
                continue
            if name.startswith('engine_counter_'):
                counters.setdefault(name[len('engine_counter_'):], []).append(value)
                continue
            body = name[len('engine_'):]
            for suffix, reduce in (('_count', 'sum'), ('_sum', 'sum'), ('_max', 'max'), ('_wrapped', 'flag')):
                if not body.endswith(suffix):
                    continue
                marker = body[:-len(suffix)]
                if not marker:
                    break
                if reduce == 'flag':
                    if value:
                        wrapped.add(marker)
                    break
                row = markers.setdefault(marker, {'count': 0, 'sum': 0.0, 'max': 0.0})
                key = suffix.lstrip('_')
                row[key] = row[key] + value if reduce == 'sum' else max(row[key], value)
                break
    statuses = label_values(windows, 'engine_markers_status')
    degraded = {}
    for _, labels in windows:
        for name, value in labels.items():
            if name.startswith('engine_marker_') and str(value) != 'available':
                degraded.setdefault(name[len('engine_marker_'):], set()).add(str(value))
    steps = [('fixed_steps_total', summed(windows, 'fixed_steps_total')),
             ('frames_observed', summed(windows, 'frames_observed')),
             ('frames_with_multiple_fixed_steps', summed(windows, 'frames_with_multiple_fixed_steps'))]
    peak_steps = extent(windows, 'fixed_steps_max_per_frame')
    if not (markers or counters or statuses or degraded or peak_steps or any(v is not None for _, v in steps)):
        return []
    output = ['### Engine markers', '',
              'Unity profiler recorders, aggregated across retained windows. Timing markers are one sample per '
              'completed frame with every occurrence in that frame summed, so a maximum is the worst frame, not '
              'the worst call. These are elapsed scope times, not charged CPU time, and they overlap each other.', '']
    if statuses:
        output += ['Module status: ' + cell(', '.join(statuses)) + '.', '']
    if markers:
        output += ['| Marker | Frames | Sum ms | Max frame ms | Ring wrapped |',
                   '| --- | ---: | ---: | ---: | --- |']
        for marker in sorted(markers):
            row = markers[marker]
            output.append(f'| {cell(marker)} | {exact(row["count"])} | {row["sum"]:.3f} | {row["max"]:.3f} | '
                          f'{"yes" if marker in wrapped else "no"} |')
        output += ['', 'A wrapped ring lost frames; the engine does not report how many, so no dropped-frame '
                   'count is inferred and that marker\'s totals are lower bounds.', '']
    if counters:
        output += ['| Counter | Min sample | Max sample | Last sample |', '| --- | ---: | ---: | ---: |']
        for name in sorted(counters):
            values = counters[name]
            output.append(f'| {cell(name)} | {min(values):.3f} | {max(values):.3f} | {values[-1]:.3f} |')
        output += ['', 'Counters are latest-value readings at each poll, not a distribution: no percentile, mean '
                   'or per-frame rate is derived from them. Units follow the recorder (milliseconds, bytes or counts).', '']
    gpu, cpu = counters.get('gpu_frame_time'), counters.get('cpu_main_thread_frame_time')
    if gpu and cpu:
        output += [f'GPU frame time maximum {max(gpu):.3f} against CPU main-thread frame time maximum {max(cpu):.3f} '
                   '(same units). A high present wait together with GPU at or above CPU is consistent with a '
                   'GPU-bound frame; these samples alone do not establish it.', '']
    facts = [f'{name}={exact(value)}' for name, value in steps if value is not None]
    if peak_steps:
        facts.append(f'fixed_steps_max_per_frame={exact(peak_steps[1])}')
    if facts:
        output += ['Fixed-step accounting: ' + '; '.join(facts) + '. Counted from the plugin\'s own callbacks; '
                   'maxima are the largest observed window value and are not summed.', '']
    collector = [f'{name}={cell(", ".join(label_values(windows, name)))}'
                 for name in ('gc_mode', 'gc_incremental') if label_values(windows, name)]
    if collector:
        output += ['Garbage collector: ' + '; '.join(collector) + '.', '']
    if degraded:
        output += ['Metrics not available: ' + cell('; '.join(
            f'{name}={", ".join(sorted(values))}' for name, values in sorted(degraded.items())))
            + '. An unavailable metric is unmeasured, not zero.', '']
    return output


def host_network_report(records):
    windows = observed_windows(records)
    start = records[0]
    start_pair = (named(start.get('gauges', [])), named(start.get('labels', [])))
    all_windows = [start_pair] + windows
    text_names = ('process_priority_class', 'process_affinity_mask', 'power_scheme',
                  'online_backend', 'steam_transport_path', 'steam_relay_pop', 'host_net_status')
    texts = [(name, label_values(all_windows, name)) for name in text_names]
    texts = [(name, values) for name, values in texts if values]
    gauge_names = ('host_timer_resolution_current_ms', 'steam_connections_relayed', 'steam_connections_direct',
                   'peer_sockets_steam', 'peer_sockets_playfab', 'peer_sockets_other',
                   # 0.4.10/0.4.11 Steam link figures: a client reports its server link, a server reads each
                   # ready peer through the game-server API (the native aggregate returned zeros there).
                   'host_net_ping_ms', 'host_net_ping_max_ms', 'host_net_quality_local', 'host_net_quality_remote',
                   'host_net_in_bytes_per_sec', 'host_net_out_bytes_per_sec', 'host_net_pending_bytes_max',
                   'host_net_peers_ready', 'host_net_peers_measured', 'host_net_peers_unmeasured')
    spans = [(name, extent(all_windows, name)) for name in gauge_names]
    spans = [(name, span) for name, span in spans if span is not None]
    clock = [(record.get('utc', 'unknown'), named(record.get('gauges', []))['host_qpc_timestamp'])
             for record in records if record.get('kind') in ('start', 'interval', 'capture_end')
             and numeric(named(record.get('gauges', [])).get('host_qpc_timestamp'))]
    frequency = extent(all_windows, 'host_qpc_frequency')
    if not (texts or spans or clock or frequency):
        return []
    output = ['### Host and network path', '',
              'Host and transport context observed at poll time, never written by this plugin. '
              'A relayed link adds relay hops to every message and is not comparable with a direct link; '
              'a timer resolution near 15.6 ms means coarse sleep pacing, which widens loop gaps on its own.', '']
    for name, values in texts:
        output.append(f'- {cell(name)}: {cell(", ".join(values))}.')
    if texts:
        output.append('')
    if spans:
        output += ['| Signal | Minimum | Maximum |', '| --- | ---: | ---: |']
        output += [f'| {cell(name)} | {low:g} | {high:g} |' for name, (low, high) in spans]
        output += ['', 'Connection and socket counts are occupancy at poll time; a maximum is not a total of '
                   'distinct peers over the capture. `host_net_pending_bytes_max` above the send-queue cap '
                   '(10240 bytes native, 32 KB with BetterNetworking) means `ZDOMan.SendZDOs` skipped ticks for '
                   'that peer; a server reports the worst peer, a client its own server link.', '']
    if frequency:
        output += [f'QPC frequency: {exact(frequency[0])}'
                   + (f'–{exact(frequency[1])} (inconsistent readings)' if frequency[1] != frequency[0] else '')
                   + ' ticks per second.', '']
    if clock:
        output += [f'Shared host counter: first {exact(clock[0][1])} at {cell(clock[0][0])}; '
                   f'last {exact(clock[-1][1])} at {cell(clock[-1][0])}. '
                   'Two captures on this host can be aligned by hand on these ticks. That alignment is unvalidated '
                   'and does not by itself prove two events were simultaneous.', '']
    return output


OWNERSHIP_TOTALS = ('zdo_set_owner_calls', 'zdo_set_owner_calls_release_to_zero',
                    'zdo_set_owner_calls_release_claim_peer', 'zdo_set_owner_calls_release_server_pass',
                    'zdo_set_owner_calls_zdo_data_reapply', 'zdo_set_owner_calls_disconnect_sweep',
                    'zdo_set_owner_calls_invalid_prefab_destroy', 'zdo_set_owner_calls_other',
                    'release_cycles', 'release_cycle_released', 'release_cycle_reclaimed',
                    'release_cycle_net_changes', 'release_cycle_capacity_skips',
                    'zdo_request_rpcs', 'item_request_own_rpcs', 'container_open_requests')

OWNERSHIP_PEAKS = ('zdoman_client_change_queue', 'zdoman_dead_zdos', 'zdoman_zdos_sent_total',
                   'zdoman_zdos_recv_total', 'zdoman_zdos_sent_last_sec', 'zdoman_zdos_recv_last_sec')


def ownership_report(records):
    windows = observed_windows(records)
    totals = [(name, summed(windows, name)) for name in OWNERSHIP_TOTALS]
    totals = [(name, value) for name, value in totals if value is not None]
    peaks = [(name, extent(windows, name)) for name in OWNERSHIP_PEAKS]
    peaks = [(name, span) for name, span in peaks if span is not None]
    statuses = label_values(windows, 'ownership_telemetry_status') + label_values(windows, 'zdoman_counters_status')
    if not (totals or peaks or statuses):
        return []
    output = ['### Ownership and replication', '',
              'Per-interval call counters summed across retained windows, and manager counters read at poll time. '
              'Cumulative `_total` counters are reported as observed ranges, never summed. '
              'A transfer request is not a completed transfer and carries no latency.', '']
    if statuses:
        output += ['Probe status: ' + cell(', '.join(sorted(set(statuses)))) + '.', '']
    if totals:
        output += ['| Counter | Observed total |', '| --- | ---: |']
        output += [f'| {cell(name)} | {exact(value)} |' for name, value in totals]
        output.append('')
    if peaks:
        output += ['| Manager signal | Minimum | Maximum |', '| --- | ---: | ---: |']
        output += [f'| {cell(name)} | {exact(low)} | {exact(high)} |' for name, (low, high) in peaks]
        output += ['', 'Queue and dead-object counts are occupancy, not throughput. Retained windows may omit '
                   'activity, so these are lower bounds.', '']
    skips = summed(windows, 'ownership_other_thread_skips')
    failures = summed(windows, 'ownership_probe_failures')
    if skips is not None or failures is not None:
        output += [f'Off-thread skips: {f"{skips:g}" if skips is not None else "unobserved"}; '
                   f'probe failures: {f"{failures:g}" if failures is not None else "unobserved"}. '
                   'Skipped observations are unmeasured calls, not zero calls.', '']
    return output


GAMEPLAY_GROUPS = (
    ('Chests and inventory', ('container_open_requests_received', 'container_concurrent_open_conflicts',
                              'container_open_granted', 'container_open_requests_rejected_in_use',
                              'container_changes', 'container_gui_open_frames', 'inventory_gui_open_frames',
                              'inventory_moves', 'inventory_item_stacks')),
    ('Stations', ('smelter_updates', 'smelter_catchup_items_sum', 'smelter_spawns', 'fireplace_fuel_adds',
                  'cooking_spawns', 'beehive_extracts')),
    ('Building', ('placement_ghost_updates', 'placement_ghost_frames', 'pieces_placed', 'pieces_removed',
                  'snap_pieces_scanned', 'snap_points_enumerated', 'ghost_clipping_tests',
                  'placement_update_over_10ms')),
    ('Clutter', ('clutter_patches_generated', 'clutter_rebuild_all_frames', 'clutter_ground_queries',
                 'clutter_objects_instantiated', 'clutter_heightmap_not_ready_frames',
                 'clutter_patches_timed_out')),
    ('Gathering and combat', ('tree_damage_rpcs', 'tree_logs_spawned', 'rock_damage_rpcs', 'rock_area_destroys',
                              'destructible_destroys', 'attacks_started', 'hits_dealt',
                              'drop_on_destroyed_events', 'drops_spawned')),
    ('Map', ('minimap_explore_updates', 'minimap_explore_scans', 'minimap_fog_applies',
             'minimap_fog_pixels_explored', 'minimap_large_map_frames')),
    ('Vehicles and items', ('gameplay_observed_frames', 'player_on_ship_frames', 'item_drops_autostacked',
                            'item_drop_slow_updates')),
)

GAMEPLAY_PEAKS = ('inventory_items_max', 'smelter_catchup_items_max', 'ship_instances_max',
                  'vagon_instances_max', 'zsfx_instances_max')

GAMEPLAY_TIMINGS = ('InventoryGuiUpdate', 'InventoryGridUpdate', 'ContainerGridUpdate', 'InventoryGuiShow',
                    'ContainerInteract', 'ContainerChanged', 'ContainerCheckForChanges', 'InventoryAddItem',
                    'InventoryMoveItem', 'PlacementGhostUpdate', 'PlacementUpdate', 'PiecePlace',
                    'PieceRemove', 'PieceCopy', 'BuildMenuOpen', 'BuildGuiUpdate', 'MinimapUpdate', 'MinimapExploreUpdate', 'MinimapLargeMapUpdate',
                    'MinimapSetMapMode', 'ShipFixedUpdate', 'VagonFixedUpdate', 'TreeDamage', 'TreeLogDamage',
                    'TreeDestroy', 'MineRockDamage', 'MineRockDamageArea', 'DestructibleDamage',
                    'DestructibleDestroy', 'WearDamage', 'CharacterDamage', 'CharacterApplyDamage',
                    'AttackStart', 'PieceDropResources', 'DropTableDrop', 'SmelterSpawn', 'CraftingStationBatch',
                    'SfxBatch', 'InstanceRendererBatch', 'SmokeBatch', 'FloatingBatch', 'ShipBatch',
                    'ZSyncTransformBatch', 'ZSyncAnimationBatch', 'ItemDropSlowUpdate', 'ItemAutoStack',
                    'PickableInteract', 'PlayerUpdate', 'HudUpdate', 'ClutterLateUpdate',
                    'ClutterGeneratePatches', 'ClutterGenerateVegPatch', 'WaterStaticUpdate')


def gameplay_timings(records):
    """Per-metric totals for the gameplay call sites, in declaration order."""
    order = {name: index for index, name in enumerate(GAMEPLAY_TIMINGS)}
    totals = {}
    for record in records:
        if record.get('kind') not in ('interval', 'capture_end'):
            continue
        for timing in record.get('timings') or []:
            if timing.get('name') not in order:
                continue
            row = totals.setdefault(timing['name'], {'count': 0, 'sum': 0.0, 'max': 0.0, 'stalls': 0})
            row['count'] += timing.get('count', 0) or 0
            row['sum'] += timing.get('sumMs', 0) or 0
            maximum = timing.get('maxMs', 0) or 0
            row['max'] = max(row['max'], maximum)
            row['stalls'] += timing.get('stallsOver50Ms', 0) or 0
    return sorted(((name, row) for name, row in totals.items() if row['count']), key=lambda item: order[item[0]])


def gameplay_report(records):
    windows = observed_windows(records)
    groups = [(title, [(name, summed(windows, name)) for name in names]) for title, names in GAMEPLAY_GROUPS]
    groups = [(title, [(name, value) for name, value in rows if value is not None]) for title, rows in groups]
    groups = [(title, rows) for title, rows in groups if rows]
    peaks = [(name, extent(windows, name)) for name in GAMEPLAY_PEAKS]
    peaks = [(name, span) for name, span in peaks if span is not None]
    timings = gameplay_timings(records)
    statuses = label_values(windows, 'gameplay_telemetry_status')
    if not (groups or peaks or timings or statuses):
        return []
    output = ['### Gameplay', '',
              'Per-interval counters summed across retained windows, and sizes read at poll time. '
              'Counters are observed calls, not complete coverage of the activity they name: a zero means '
              'nothing was observed, never that nothing happened. Frame gauges count only frames in which the '
              'observed per-frame method ran, so they are not a frame-rate denominator.', '']
    if statuses:
        output += ['Probe status: ' + cell(', '.join(sorted(set(statuses)))) + '.', '']
    for name in ('gameplay_probes_unavailable', 'gameplay_skipped_counters'):
        values = [value for value in label_values(windows, name) if value and value != 'none']
        if values:
            output += [name.replace('_', ' ').capitalize() + ': ' + cell('; '.join(values)) + '.', '']
    if groups:
        output += ['| Counter | Observed total |', '| --- | ---: |']
        for title, rows in groups:
            output.append(f'| **{cell(title)}** | |')
            output += [f'| {cell(name)} | {exact(value)} |' for name, value in rows]
        output.append('')
    if peaks:
        output += ['| Sampled size | Minimum | Maximum |', '| --- | ---: | ---: |']
        output += [f'| {cell(name)} | {exact(low)} | {exact(high)} |' for name, (low, high) in peaks]
        output += ['', 'Occupancy and queue sizes read at poll time, not creations, removals or throughput. '
                   'A poll can miss a peak between two windows, so these are lower bounds.', '']
    if timings:
        output += ['Elapsed time at the same call sites. Inclusive and not summable across rows; nested rows '
                   'overlap, and a batch row contains the per-object rows it drives.', '',
                   '| Call site | Calls | Sum ms | Max ms | >50 ms |', '| --- | ---: | ---: | ---: | ---: |']
        output += [f'| {cell(name)} | {exact(row["count"])} | {row["sum"]:.3f} | '
                   f'{row["max"]:.3f} | {exact(row["stalls"])} |' for name, row in timings]
        output.append('')
    conflicts = summed(windows, 'container_concurrent_open_conflicts')
    received = summed(windows, 'container_open_requests_received')
    if conflicts is not None and received is not None:
        output += [f'Chest open requests reaching an owner: {exact(received)}; refused because the chest was '
                   f'already in use: {exact(conflicts)}. Conflicts are counted where the owning client handled '
                   'the request, so a capture from one machine sees only its own share.', '']
    skips = summed(windows, 'gameplay_other_thread_skips')
    failures = summed(windows, 'gameplay_probe_failures')
    if skips is not None or failures is not None:
        output += [f'Off-thread skips: {f"{skips:g}" if skips is not None else "unobserved"}; '
                   f'probe failures: {f"{failures:g}" if failures is not None else "unobserved"}. '
                   'Skipped observations are unmeasured calls, not zero calls.', '']
    return output


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("captures", nargs="+", type=Path)
    parser.add_argument("--output", type=Path, help="Write Markdown to this path instead of stdout.")
    args = parser.parse_args()
    try:
        report = summarize(args.captures)
        if args.output:
            # Avoid overwriting any input capture through a mistaken output argument.
            if args.output.resolve() in {path.resolve() for path in args.captures}:
                parser.error("Output path must differ from all input captures.")
            args.output.write_text(report, encoding="utf-8")
        else:
            print(report, end="")
    except (OSError, ValueError, KeyError, TypeError) as error:
        parser.exit(1, f"Cannot summarize capture: {error}\n")


if __name__ == "__main__":
    main()
