#!/usr/bin/env python3
"""Summarize local BetterPerformance JSONL captures; Python standard library only."""

import argparse
import json
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
    return records, warnings


def summarize(paths):
    output = ["# BetterPerformance capture report", "",
              "Timing values are elapsed milliseconds, not exclusive CPU time. "
              "Nested timings must not be added together. Percentiles are upper bounds per retained interval.", ""]
    windows = []
    for path in paths:
        records, warnings = load_capture(path)
        start = records[0]
        labels = named(start.get("labels", []))
        role = labels.get("role", "unknown")
        output += [f"### {cell(role)} — {cell(Path(path).name)}", "",
                   f"Game: {cell(labels.get('game_version', 'unknown'))}; "
                   f"plugin: {cell(labels.get('plugin_version', 'unknown'))}; "
                   f"started: {cell(start.get('utc', 'unknown'))}.", ""]
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
        for record in records:
            if record.get("kind") not in ("interval", "capture_end"):
                continue
            gauges = named(record.get("gauges", []))
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
        output += ["Not measured: " + cell(labels.get("unavailable", "see capture metadata")) + ".", ""]
    output += ["### Largest observed loop gaps", "",
               "UTC values mark interval ends, not the exact time of the worst gap. "
               "Use overlapping windows for local client/server comparison; coincidence alone does not prove causality.", "",
               "| Role | Capture | Interval end UTC | Window seconds | Max loop gap ms | Queue result bytes |",
               "| --- | --- | --- | ---: | ---: | ---: |"]
    for gap, role, utc, duration, queue, capture_id in sorted(windows, key=lambda row: row[0], reverse=True)[:12]:
        queue_text = "unavailable" if queue is None else f"{queue:g}"
        output.append(f"| {cell(role)} | {cell(capture_id[:8])} | {cell(utc)} | {duration:.3f} | {gap:.3f} | {queue_text} |")
    return "\n".join(output) + "\n"


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
