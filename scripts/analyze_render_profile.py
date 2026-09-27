#!/usr/bin/env python3
"""Strict build53 render_split packets; raw records remain authoritative."""
import argparse
import hashlib
import json
import math
import re
import sys
from pathlib import Path

SCHEMA_PATH = Path(__file__).with_name("patch_render_profile") / "render53-schema.json"
SCHEMA_BYTES = SCHEMA_PATH.read_bytes()
SCHEMA = json.loads(SCHEMA_BYTES)
FIELDS = SCHEMA["fields"]
LABELS = tuple(SCHEMA["tile_labels"])
BATCH_LABELS = tuple(SCHEMA["batch_labels"])
LAYERS = tuple(SCHEMA["layers"])
RANGES = tuple(SCHEMA["ranges"])
SUFFIXES = ("_first", "_last", "_min", "_max")
RANGE_FIELDS = frozenset(name + suffix for name in RANGES for suffix in SUFFIXES)
FLAGS = {"menu": 1, "paused": 2, "inventory": 4, "map": 8, "dead": 16,
         "ghost": 32, "spectator": 64, "invalid_player": 128,
         "autopause": 256, "invalid_scene": 512}
INT = re.compile(r"(?:0|[1-9][0-9]*)\Z")
FLOAT = re.compile(r"-?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?\Z")
MAX_INT = (1 << 63) - 1
LIMITS = [
    "Rotating deterministic 1-in-128 caller sampling, independently phased by layer/pass; not random and not guaranteed representative.",
    "Helper durations are sampled inclusive wall ticks, not exclusive CPU time or GPU time; no FPS or full-frame/per-tile extrapolation.",
    "Only completed eligible frames with stable boundary flags/player id/viewport/zoom/skip/day state donate pass measurements.",
    "Begin/end field snapshots cannot observe a toggle-and-return between boundaries. Unchanged coordinates do not establish scene identity.",
    "Player positions come from Main.player[Main.myPlayer], which is not guaranteed to be SceneMetrics.PerspectivePlayer.",
    "Camera positions are raw Main.screenPosition, exactly Camera.UnscaledPosition; zoom is raw Main.GameZoomTarget, not effective GameViewMatrix.Zoom, which DoDraw scales/clamps with ForcedMinimumZoom.",
    "Capture and transient render modes are not classified by these boundary snapshots; no getter-based mode detection or extra mode filter is applied.",
    "Position/day/player changes count adjacent accepted boundary samples within each packet; gaps and packet boundaries are not interpolated.",
    "Snapshot and clock-pair overhead are indicators, not deductions. Pass/sample timings include observer work; report cost excludes its footer.",
    "Window wall time starts at the prior packet's validated pre-footer clock and can include that footer. It is not observer-free CPU/frame cost; cooldown anchors later without moving the window start.",
    "frames counts complete snapshot pairs; capture_failure_frames separately counts root frames whose initialization/begin/end capture threw. Their incomplete snapshots and pass/batch metrics are not donated.",
    "Batch sampling is 1-in-1 for eligible frames, not solid-only: TileBatch.End and the four upload/indexed-submit callsites in RenderBatch/FlushLayered are observed.",
    "A coarse scope abort or observer failure discards all batch metrics for that frame. Batch means per frame use valid_frames, never all eligible frames or sampled tile counts.",
    "Batch inclusive scopes can overlap or nest; never add their inclusive totals to each other or to helper/pass totals. Uploads/submissions can occur outside End; no global child<=End assumption is valid.",
    "Batch exclusive ticks subtract immediate timed children only, retaining observer and untracked work. They are not pure CPU cost; uploads/submissions are game-side call durations, not GPU execution, completion, or utilization. No GPU fence/query is used.",
    "Unused operations have null means. Fixture or desktop timings are not Switch performance evidence.",
    "Any measurement_invalid flag invalidates performance interpretation; raw diagnostic records are retained and derived timing means are null.",
    "Interval selection never bypasses validation or hides capture-wide invalidity; final_seen refers to the complete input, selected_final_seen to the selected intervals.",
]


class ParseError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise ParseError(message)


def _record(line, number):
    words = line.split()
    require(line.startswith("NX_PROFILE ") and len(words) >= 3 and words[0] == "NX_PROFILE", f"line {number}: malformed row")
    kind = words[1]
    require(kind in FIELDS, f"line {number}: unknown/incompatible row {kind}")
    values = {}
    for word in words[2:]:
        require(word.count("=") == 1, f"line {number}: malformed token {word!r}")
        name, value = word.split("=", 1)
        require(name and value and name not in values, f"line {number}: empty/duplicate key {name!r}")
        values[name] = value
    require(set(values) == set(FIELDS[kind]), f"line {number}: {kind} keys mismatch: missing={sorted(set(FIELDS[kind]) - set(values))}, extra={sorted(set(values) - set(FIELDS[kind]))}")
    require(list(values) == FIELDS[kind], f"line {number}: {kind} fields out of order")
    for key, value in list(values.items()):
        if key in ("schema", "layer", "label"):
            continue
        if kind == "SCENE" and key in RANGE_FIELDS:
            if value == "null":
                values[key] = None
            else:
                require(bool(FLOAT.fullmatch(value)), f"line {number}: malformed scene number {key}")
                values[key] = float(value)
                require(math.isfinite(values[key]), f"line {number}: nonfinite {key}")
        else:
            require(bool(INT.fullmatch(value)), f"line {number}: invalid nonnegative integer {key}")
            require(len(value) <= 19, f"line {number}: overflow {key}")
            values[key] = int(value)
            require(values[key] <= MAX_INT, f"line {number}: overflow {key}")
    return kind, values


def _timing(count, total, maximum, label):
    require(0 <= maximum <= total, f"{label}: maximum exceeds total")
    if count == 0:
        require(total == maximum == 0, f"{label}: unused count with ticks")
    else:
        require(total <= count * maximum, f"{label}: total exceeds count*maximum")


def _validate_batch(packet):
    batch, state, cost = (packet[key] for key in ("batch_state", "state", "report_cost"))
    require(batch["sample_denominator"] == 1, "invalid batch sampling denominator")
    require(batch["frames"] == state["eligible"], "batch frames differ from eligible frames")
    require(batch["valid_frames"] + batch["discarded_frames"] == batch["frames"], "inconsistent batch frame partition")
    require(not batch["aborted_scopes"] or batch["discarded_frames"] > 0, "aborted batch scopes without discarded frames")
    require(not batch["observer_failures"] or batch["discarded_frames"] > 0 and cost["measurement_invalid"] == 1, "batch observer failures require discarded frames and measurement invalidity")
    require(batch["observer_failures"] <= batch["discarded_frames"], "observer failure frames exceed discarded frames")
    if not batch["frames"]:
        require(all(value == 0 for key, value in batch.items() if key not in ("interval", "sample_denominator")), "excluded frames donated batch counters")
    calls = 0
    for label, metric in packet["batch_metrics"].items():
        calls += metric["calls"]
        for mode in ("inclusive", "exclusive"):
            _timing(metric["calls"], metric[mode + "_ticks"], metric[mode + "_max_ticks"], label + " " + mode)
        require(metric["exclusive_ticks"] <= metric["inclusive_ticks"], f"{label}: exclusive exceeds inclusive total")
        require(metric["exclusive_max_ticks"] <= metric["inclusive_max_ticks"], f"{label}: exclusive exceeds inclusive maximum")
        if not batch["valid_frames"]:
            require(all(value == 0 for key, value in metric.items() if key not in ("interval", "label")), f"{label}: invalid frames donated batch metrics")
    require(batch["clock_calls"] >= 2 * calls, "batch clock calls do not cover committed scopes")


def _validate(packet):
    b, s, scene, cost = (packet[key] for key in ("begin", "state", "scene", "report_cost"))
    require(b["version"] == 53 and b["schema"] == "render_split", "incompatible version/schema")
    require(b["interval"] > 0 and b["frequency"] > 0 and b["sample_denominator"] == 128, "invalid BEGIN units/interval/sampling")
    require(b["final"] in (0, 1), "invalid final flag")
    require(b["end_tick"] >= b["start_tick"] and b["wall_ticks"] == b["end_tick"] - b["start_tick"], "inconsistent wall ticks")
    require(b["final"] == 1 or b["wall_ticks"] >= 5 * b["frequency"], "nonfinal report shorter than five seconds")
    require(s["measurement_invalid"] in (0, 1) and cost["measurement_invalid"] in (0, 1), "invalid measurement flag")
    require(s["capture_failure_frames"] == 0 or s["measurement_invalid"] == 1, "capture failures must invalidate measurement")
    n = s["frames"]
    require(s["completed"] + s["aborted"] == n, "inconsistent frame completion counts")
    require(s["eligible"] <= s["completed"] and s["excluded"] == n - s["eligible"], "inconsistent frame eligibility counts")
    for key in ("state_changed", *FLAGS):
        require(s[key] <= n, f"STATE {key} exceeds frames")
    require(s["state_changed"] <= s["excluded"], "changed frames donated eligible measurements")
    require(s["snapshot_calls"] == n * 2, "snapshot count must cover both frame boundaries")
    _timing(s["snapshot_calls"], s["snapshot_ticks"], s["snapshot_max_ticks"], "snapshot")
    require(s["first_flags"] <= 1023 and s["last_flags"] <= 1023 and s["union_flags"] <= 1023, "unknown state flags")
    require((s["first_flags"] | s["last_flags"]) & ~s["union_flags"] == 0, "boundary flags absent from union")
    for key, bit in FLAGS.items():
        require(bool(s["union_flags"] & bit) == bool(s[key]), f"STATE {key} counter/union mismatch")
        if n > 1:
            require(s[key] >= bool(s["first_flags"] & bit) + bool(s["last_flags"] & bit), f"STATE {key} omits boundary frames")
    if n:
        require(s["first_frame_id"] > 0 and s["last_frame_id"] - s["first_frame_id"] + 1 == n, "inconsistent frame ids")
        if n == 1:
            require(s["union_flags"] == s["first_flags"] | s["last_flags"], "single frame union differs from boundaries")
    else:
        require(all(s[key] == 0 for key in ("first_frame_id", "last_frame_id", "first_flags", "last_flags", "union_flags")), "empty state has frame data")
    samples = scene["samples"]
    require(samples == 2 * s["eligible"] and scene["day_samples"] + scene["night_samples"] == samples, "inconsistent scene sample counts")
    for key in ("player_id_changes", "player_position_changes", "camera_position_changes", "day_changes"):
        require(scene[key] <= max(0, samples - 1), f"SCENE {key} exceeds transitions")
    for name in RANGES:
        first, last, minimum, maximum = (scene[name + suffix] for suffix in SUFFIXES)
        if samples == 0:
            require(all(value is None for value in (first, last, minimum, maximum)), f"unused range {name} must be null")
        else:
            require(all(value is not None for value in (first, last, minimum, maximum)), f"used range {name} cannot be null")
            require(minimum <= first <= maximum and minimum <= last <= maximum, f"inconsistent range {name}")
            if name in ("zoom", "width", "height"):
                require(minimum > 0, f"invalid positive range {name}")
            if name in ("width", "height", "skip"):
                require(all(value == int(value) for value in (first, last, minimum, maximum)), f"noninteger range {name}")
    for layer in LAYERS:
        p, metrics = packet["passes"][layer], packet["metrics"][layer]
        require(p["completed_passes"] + p["aborted_passes"] + p["nested_passes"] == p["passes"], f"{layer}: inconsistent pass counts")
        require(p["invalid_passes"] <= p["completed_passes"] + p["aborted_passes"], f"{layer}: invalid passes exceed top-level passes")
        require(p["calls"] <= p["eligible"] <= p["visited"], f"{layer}: inconsistent tile counts")
        require(p["selected"] <= p["calls"], f"{layer}: selected exceeds calls")
        require(p["completed_samples"] == p["valid_samples"] + p["invalid_samples"] + p["discarded_samples"], f"{layer}: inconsistent sample validity")
        require(p["selected"] == p["completed_samples"] + p["aborted_samples"], f"{layer}: incomplete sample accounting")
        require(p["completed_loops"] <= p["completed_passes"], f"{layer}: loops exceed complete passes")
        _timing(p["completed_passes"], p["pass_ticks"], p["pass_max_ticks"], layer + " passes")
        _timing(p["completed_loops"], p["loop_ticks"], p["loop_max_ticks"], layer + " loops")
        _timing(p["valid_samples"], p["sample_ticks"], p["sample_max_ticks"], layer + " samples")
        require(p["loop_ticks"] <= p["pass_ticks"] and p["sample_ticks"] <= p["loop_ticks"], f"{layer}: timing containment violated")
        helper_ticks = 0
        for label, metric in metrics.items():
            _timing(metric["operations"], metric["total_ticks"], metric["max_ticks"], layer + " " + label)
            if label == "clock_pair":
                require(metric["operations"] == p["valid_samples"], f"{layer}: calibration count mismatch")
            else:
                require(metric["operations"] <= p["valid_samples"] * (2 if label == "GetColor" else 1), f"{layer} {label}: operation bound exceeded")
                helper_ticks += metric["total_ticks"]
        require(helper_ticks <= p["sample_ticks"], f"{layer}: helper ticks exceed sampled gross ticks")
        if not s["eligible"]:
            require(all(value == 0 for key, value in p.items() if key not in ("interval", "layer")), f"{layer}: excluded frames donated pass data")
    require(cost["reports"] == b["interval"] and cost["attempts"] >= cost["reports"] + cost["failures_lifetime"], "inconsistent report attempt counts")
    require(cost["failures_lifetime"] >= s["report_failures_lifetime"], "report failures moved backwards")
    require(cost["ticks_before_footer"] <= cost["lifetime_max_ticks"] <= cost["lifetime_ticks_before_footers"], "inconsistent report overhead")
    require(cost["measurement_invalid"] >= s["measurement_invalid"], "measurement invalidation disappeared")
    _validate_batch(packet)


def parse(text):
    """Return complete validated packets; never repair/drop malformed profiler rows."""
    packets, rows, raw, context, source_lines = [], [], [], [], []
    expected = SCHEMA["row_order"]
    last_frame_id = None
    for number, line in enumerate(text.splitlines(), 1):
        if "NX_PROFILE" not in line:
            context.append({"line": number, "text": line})
            continue
        kind, values = _record(line, number)
        require(kind == expected[len(rows)], f"line {number}: expected {expected[len(rows)]}, got {kind}; incomplete/out-of-order packet")
        if rows:
            require(values["interval"] == rows[0][1]["interval"], f"line {number}: mismatched interval")
        rows.append((kind, values))
        raw.append(line)
        source_lines.append(number)
        if kind != "END":
            continue
        packet = {"begin": rows[0][1], "state": rows[1][1], "scene": rows[2][1], "passes": {}, "metrics": {},
                  "batch_metrics": {}, "report_cost": rows[-2][1], "end": rows[-1][1],
                  "raw": "\n".join(raw) + "\n", "source_lines": source_lines, "context": context}
        index = 3
        for layer in LAYERS:
            p = rows[index][1]
            index += 1
            require(p["layer"] == layer, "wrong/duplicate pass layer")
            packet["passes"][layer] = p
            packet["metrics"][layer] = {}
            for label in LABELS:
                metric = rows[index][1]
                index += 1
                require(metric["layer"] == layer and metric["label"] == label, "wrong/duplicate metric layer or label")
                packet["metrics"][layer][label] = metric
        packet["batch_state"] = rows[index][1]
        index += 1
        for label in BATCH_LABELS:
            metric = rows[index][1]
            index += 1
            require(metric["label"] == label, "wrong/duplicate batch metric label")
            packet["batch_metrics"][label] = metric
        _validate(packet)
        if packets:
            previous = packets[-1]
            require(previous["begin"]["final"] == 0, "packet follows final report")
            require(packet["begin"]["interval"] == previous["begin"]["interval"] + 1, "nonconsecutive/duplicate intervals")
            require(packet["begin"]["frequency"] == previous["begin"]["frequency"], "clock frequency changed")
            require(packet["begin"]["start_tick"] >= previous["begin"]["end_tick"], "overlapping report windows")
            require(packet["state"]["measurement_invalid"] >= previous["report_cost"]["measurement_invalid"], "sticky measurement invalidity disappeared")
            for key in ("ignored_threads_lifetime", "reentrant_frames_lifetime", "report_failures_lifetime"):
                require(packet["state"][key] >= previous["state"][key], f"lifetime state counter moved backwards: {key}")
            require(packet["state"]["report_failures_lifetime"] >= previous["report_cost"]["failures_lifetime"], "report failure lifetime moved backwards between packets")
            for key in ("attempts", "failures_lifetime", "lifetime_ticks_before_footers", "lifetime_max_ticks", "measurement_invalid"):
                require(packet["report_cost"][key] >= previous["report_cost"][key], f"lifetime counter moved backwards: {key}")
        if packet["state"]["frames"]:
            if last_frame_id is not None:
                require(packet["state"]["first_frame_id"] == last_frame_id + 1, "nonconsecutive frame ids")
            last_frame_id = packet["state"]["last_frame_id"]
        packets.append(packet)
        rows, raw, context, source_lines = [], [], [], []
    require(not rows, "incomplete packet: missing END or required rows")
    require(bool(packets), "no complete render_split packets")
    packets[-1]["trailing_context"] = context
    return packets


def _ratio(numerator, denominator):
    return numerator / denominator if denominator else None


def summarize(packets, first_interval=1, last_interval=None):
    """Aggregate raw sums before division, preserving capture-wide flags and records."""
    require(bool(packets), "cannot summarize an empty capture")
    require(first_interval >= 1 and (last_interval is None or last_interval >= first_interval), "invalid interval selection")
    selected = [p for p in packets if p["begin"]["interval"] >= first_interval and (last_interval is None or p["begin"]["interval"] <= last_interval)]
    require(bool(selected), "interval selection contains no packets")
    frequency = packets[0]["begin"]["frequency"]
    require(all(p["begin"]["frequency"] == frequency for p in packets), "clock frequency changed")
    invalid = any(p["report_cost"]["measurement_invalid"] for p in packets)
    eligible_frames = sum(p["state"]["eligible"] for p in selected)
    result = {"version": 53, "schema": "render_split", "schema_sha256": hashlib.sha256(SCHEMA_BYTES).hexdigest(),
              "frequency": frequency, "sample_denominator": 128, "packet_count": len(packets),
              "selected_packet_count": len(selected), "first_interval": selected[0]["begin"]["interval"],
              "last_interval": selected[-1]["begin"]["interval"], "eligible_frame_denominator": eligible_frames,
              "final_seen": bool(packets[-1]["begin"]["final"]), "selected_final_seen": bool(selected[-1]["begin"]["final"]),
              "starts_at_first_interval": packets[0]["begin"]["interval"] == 1,
              "measurement_invalid": invalid, "performance_interpretation_allowed": not invalid,
              "limits": LIMITS, "packets": packets, "layers": {}}
    for layer in LAYERS:
        totals = {key: sum(p["passes"][layer][key] for p in selected) for key in FIELDS["PASS"] if key not in ("interval", "layer")}
        for key in ("loop_max_ticks", "pass_max_ticks", "sample_max_ticks"):
            totals[key] = max(p["passes"][layer][key] for p in selected)
        summary = {"raw": totals, "valid_sample_denominator": totals["valid_samples"],
                   "calls_per_visited": _ratio(totals["calls"], totals["visited"]),
                   "eligible_per_visited": _ratio(totals["eligible"], totals["visited"]),
                   "selected_per_call": _ratio(totals["selected"], totals["calls"]), "timings": {}, "metrics": {}}
        for label, count_key in (("pass", "completed_passes"), ("loop", "completed_loops"), ("sample", "valid_samples")):
            count = totals[count_key]
            mean = _ratio(totals[label + "_ticks"], count) if not invalid else None
            summary["timings"][label] = {"denominator": count_key, "count": count,
                "total_ticks": totals[label + "_ticks"], "max_ticks": totals[label + "_max_ticks"],
                "mean_ticks": mean, "mean_microseconds": mean * 1_000_000 / frequency if mean is not None else None}
        for label in LABELS:
            metrics = [p["metrics"][layer][label] for p in selected]
            operations = sum(m["operations"] for m in metrics)
            ticks = sum(m["total_ticks"] for m in metrics)
            mean = _ratio(ticks, operations) if not invalid else None
            summary["metrics"][label] = {"operations": operations, "total_ticks": ticks,
                "max_ticks": max(m["max_ticks"] for m in metrics), "mean_ticks_per_operation": mean,
                "mean_microseconds_per_operation": mean * 1_000_000 / frequency if mean is not None else None,
                "ticks_per_valid_sample": _ratio(ticks, totals["valid_samples"]) if operations and not invalid else None}
        result["layers"][layer] = summary
    batch = {key: sum(p["batch_state"][key] for p in selected) for key in FIELDS["BATCH_STATE"] if key not in ("interval", "sample_denominator")}
    batch_summary = {"sample_denominator": 1, "raw": batch, "valid_frame_denominator": batch["valid_frames"], "metrics": {}}
    for label in BATCH_LABELS:
        metrics = [p["batch_metrics"][label] for p in selected]
        metric = {"calls": sum(m["calls"] for m in metrics)}
        for mode in ("inclusive", "exclusive"):
            ticks = sum(m[mode + "_ticks"] for m in metrics)
            metric[mode + "_ticks"] = ticks
            metric[mode + "_max_ticks"] = max(m[mode + "_max_ticks"] for m in metrics)
            mean = _ratio(ticks, metric["calls"]) if not invalid else None
            per_frame = _ratio(ticks, batch["valid_frames"]) if metric["calls"] and not invalid else None
            metric["mean_" + mode + "_ticks_per_call"] = mean
            metric["mean_" + mode + "_microseconds_per_call"] = mean * 1_000_000 / frequency if mean is not None else None
            metric[mode + "_ticks_per_valid_frame"] = per_frame
            metric[mode + "_microseconds_per_valid_frame"] = per_frame * 1_000_000 / frequency if per_frame is not None else None
        batch_summary["metrics"][label] = metric
    result["batch"] = batch_summary
    return result


def main(argv=None):
    argument_parser = argparse.ArgumentParser(description=__doc__)
    argument_parser.add_argument("input", help="raw capture file, or - for stdin")
    argument_parser.add_argument("--output", type=Path, help="write validated raw packets and weighted summary as JSON")
    argument_parser.add_argument("--first-interval", type=int, default=1, help="first interval to summarize; the entire input is still validated")
    argument_parser.add_argument("--last-interval", type=int, help="last interval to summarize, inclusive")
    args = argument_parser.parse_args(argv)
    try:
        text = sys.stdin.read() if args.input == "-" else Path(args.input).read_text(encoding="utf-8")
        result = summarize(parse(text), args.first_interval, args.last_interval)
        rendered = json.dumps(result, indent=2, allow_nan=False) + "\n"
        if args.output:
            args.output.write_text(rendered, encoding="utf-8")
        else:
            sys.stdout.write(rendered)
    except (OSError, UnicodeError, ParseError) as error:
        argument_parser.exit(2, f"render_split: {error}\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
