#!/usr/bin/env python3
"""Strict build49 tile-helper packets; raw records remain authoritative."""
import argparse
import json
import math
import re
import sys
from pathlib import Path

LABELS = ("GetColor", "GetTileDrawData", "GetTileOutlineInfo", "DrawTiles_GetLightOverride", "GetFinalLight", "clock_pair")
LAYERS = ("solid", "nonsolid")
RANGES = ("player_x", "player_y", "camera_x", "camera_y", "zoom", "width", "height", "skip", "world_time")
FIELDS = {
    "BEGIN": "interval version schema final frequency wall_ticks start_tick end_tick sample_denominator".split(),
    "STATE": "interval frames completed eligible excluded aborted state_changed capture_failure_frames invalid_player invalid_scene menu paused inventory map dead ghost spectator autopause first_flags last_flags union_flags first_frame_id last_frame_id ignored_threads_lifetime reentrant_frames_lifetime report_failures_lifetime snapshot_calls snapshot_ticks snapshot_max_ticks measurement_invalid".split(),
    "SCENE": "interval samples player_id_first player_id_last player_id_changes player_position_changes camera_position_changes day_samples night_samples day_changes".split() + [name + suffix for name in RANGES for suffix in ("_first", "_last", "_min", "_max")],
    "PASS": "interval layer passes completed_passes aborted_passes nested_passes invalid_passes visited eligible calls selected completed_samples valid_samples invalid_samples aborted_samples discarded_samples completed_loops loop_ticks loop_max_ticks pass_ticks pass_max_ticks sample_ticks sample_max_ticks".split(),
    "METRIC": "interval layer label operations total_ticks max_ticks".split(),
    "REPORT_COST": "interval ticks_before_footer lifetime_ticks_before_footers lifetime_max_ticks reports attempts failures_lifetime measurement_invalid".split(),
    "END": ["interval"],
}
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
    "frames counts complete snapshot pairs; capture_failure_frames separately counts root frames whose initialization/begin/end capture threw. Their incomplete snapshots and pass metrics are not donated.",
    "Unused operations have null means. Fixture or desktop timings are not Switch performance evidence.",
    "Any measurement_invalid flag invalidates performance interpretation; raw diagnostic records are retained.",
]
INT = re.compile(r"(?:0|[1-9][0-9]*)\Z")
FLOAT = re.compile(r"-?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?\Z")
MAX_INT = (1 << 63) - 1


class ParseError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise ParseError(message)


def _record(line, number):
    words = line.split()
    require(len(words) >= 3 and words[0] == "NX_PROFILE", f"line {number}: malformed row")
    kind = words[1]
    require(kind in FIELDS, f"line {number}: unknown/incompatible row {kind}")
    values = {}
    for word in words[2:]:
        require(word.count("=") == 1, f"line {number}: malformed token {word!r}")
        name, value = word.split("=", 1)
        require(name and value and name not in values, f"line {number}: empty/duplicate key {name!r}")
        values[name] = value
    require(set(values) == set(FIELDS[kind]), f"line {number}: {kind} keys mismatch: missing={sorted(set(FIELDS[kind]) - set(values))}, extra={sorted(set(values) - set(FIELDS[kind]))}")
    for key, value in list(values.items()):
        if key in ("schema", "layer", "label"):
            continue
        if kind == "SCENE" and any(key == name + suffix for name in RANGES for suffix in ("_first", "_last", "_min", "_max")):
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


def _validate(packet):
    b, s, scene, cost = (packet[key] for key in ("begin", "state", "scene", "report_cost"))
    require(b["version"] == 49 and b["schema"] == "tile_helpers", "incompatible version/schema")
    require(b["interval"] > 0 and b["frequency"] > 0 and b["sample_denominator"] == 128, "invalid BEGIN units/interval/sampling")
    require(b["final"] in (0, 1), "invalid final flag")
    require(b["end_tick"] >= b["start_tick"] and b["wall_ticks"] == b["end_tick"] - b["start_tick"], "inconsistent wall ticks")
    require(b["final"] == 1 or b["wall_ticks"] >= 5 * b["frequency"], "nonfinal report shorter than five seconds")
    require(s["measurement_invalid"] in (0, 1) and cost["measurement_invalid"] in (0, 1), "invalid measurement flag")
    require(s["capture_failure_frames"] == 0 or s["measurement_invalid"] == 1, "capture failures must invalidate measurement")
    n = s["frames"]
    require(s["completed"] + s["aborted"] == n, "inconsistent frame completion counts")
    require(s["eligible"] <= s["completed"] and s["excluded"] == n - s["eligible"], "inconsistent frame eligibility counts")
    for key in ("state_changed", "invalid_player", "invalid_scene", "menu", "paused", "inventory", "map", "dead", "ghost", "spectator", "autopause"):
        require(s[key] <= n, f"STATE {key} exceeds frames")
    require(s["snapshot_calls"] == n * 2, "snapshot count must cover both frame boundaries")
    _timing(s["snapshot_calls"], s["snapshot_ticks"], s["snapshot_max_ticks"], "snapshot")
    require(s["first_flags"] <= 1023 and s["last_flags"] <= 1023 and s["union_flags"] <= 1023, "unknown state flags")
    require((s["first_flags"] | s["last_flags"]) & ~s["union_flags"] == 0, "boundary flags absent from union")
    if n:
        require(s["first_frame_id"] > 0 and s["last_frame_id"] - s["first_frame_id"] + 1 == n, "inconsistent frame ids")
    else:
        require(s["first_frame_id"] == s["last_frame_id"] == s["union_flags"] == 0, "empty state has frame data")
    samples = scene["samples"]
    require(samples == 2 * s["eligible"] and scene["day_samples"] + scene["night_samples"] == samples, "inconsistent scene sample counts")
    for key in ("player_id_changes", "player_position_changes", "camera_position_changes", "day_changes"):
        require(scene[key] <= max(0, samples - 1), f"SCENE {key} exceeds transitions")
    for name in RANGES:
        first, last, minimum, maximum = (scene[name + suffix] for suffix in ("_first", "_last", "_min", "_max"))
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


def parse(text):
    """Return complete validated packets; never silently drop malformed/partial packets."""
    packets, rows, raw = [], [], []
    expected = ["BEGIN", "STATE", "SCENE"] + [kind for _ in LAYERS for kind in ("PASS", *("METRIC",) * len(LABELS))] + ["REPORT_COST", "END"]
    for number, line in enumerate(text.splitlines(), 1):
        if not line.startswith("NX_PROFILE"):
            continue
        kind, values = _record(line, number)
        require(len(rows) < len(expected) and kind == expected[len(rows)], f"line {number}: expected {expected[len(rows)] if len(rows) < len(expected) else 'BEGIN'}, got {kind}; incomplete/out-of-order packet")
        if rows:
            require(values["interval"] == rows[0][1]["interval"], f"line {number}: mismatched interval")
        rows.append((kind, values)); raw.append(line)
        if kind != "END":
            continue
        packet = {"begin": rows[0][1], "state": rows[1][1], "scene": rows[2][1], "passes": {}, "metrics": {}, "report_cost": rows[-2][1], "end": rows[-1][1], "raw": "\n".join(raw) + "\n"}
        index = 3
        for layer in LAYERS:
            p = rows[index][1]; index += 1
            require(p["layer"] == layer, "wrong/duplicate pass layer")
            packet["passes"][layer] = p
            packet["metrics"][layer] = {}
            for label in LABELS:
                metric = rows[index][1]; index += 1
                require(metric["layer"] == layer and metric["label"] == label, "wrong/duplicate metric layer or label")
                packet["metrics"][layer][label] = metric
        _validate(packet)
        if packets:
            previous = packets[-1]
            require(previous["begin"]["final"] == 0, "packet follows final report")
            require(packet["begin"]["interval"] == previous["begin"]["interval"] + 1, "nonconsecutive/duplicate intervals")
            require(packet["begin"]["frequency"] == previous["begin"]["frequency"], "clock frequency changed")
            require(packet["begin"]["start_tick"] >= previous["begin"]["end_tick"], "overlapping report windows")
            if packet["state"]["frames"] and previous["state"]["frames"]:
                require(packet["state"]["first_frame_id"] == previous["state"]["last_frame_id"] + 1, "nonconsecutive frame ids")
            for key in ("attempts", "failures_lifetime", "lifetime_ticks_before_footers", "lifetime_max_ticks", "measurement_invalid"):
                require(packet["report_cost"][key] >= previous["report_cost"][key], f"lifetime counter moved backwards: {key}")
        packets.append(packet); rows, raw = [], []
    require(not rows, "incomplete packet: missing END or required rows")
    require(bool(packets), "no complete tile_helpers packets")
    return packets


def _ratio(numerator, denominator):
    return numerator / denominator if denominator else None


def summarize(packets):
    """Aggregate raw sums before division, preserving packets/state and unused nulls."""
    frequency = packets[0]["begin"]["frequency"]
    result = {"version": 49, "schema": "tile_helpers", "frequency": frequency, "packet_count": len(packets),
              "measurement_invalid": any(p["report_cost"]["measurement_invalid"] for p in packets),
              "limits": LIMITS, "packets": packets, "layers": {}}
    for layer in LAYERS:
        totals = {key: sum(p["passes"][layer][key] for p in packets) for key in FIELDS["PASS"] if key not in ("interval", "layer")}
        for key in ("loop_max_ticks", "pass_max_ticks", "sample_max_ticks"):
            totals[key] = max(p["passes"][layer][key] for p in packets)
        summary = {"raw": totals, "calls_per_visited": _ratio(totals["calls"], totals["visited"]),
                   "eligible_per_visited": _ratio(totals["eligible"], totals["visited"]),
                   "selected_per_call": _ratio(totals["selected"], totals["calls"]), "metrics": {}}
        for label in LABELS:
            metrics = [p["metrics"][layer][label] for p in packets]
            operations = sum(m["operations"] for m in metrics)
            ticks = sum(m["total_ticks"] for m in metrics)
            mean = _ratio(ticks, operations)
            summary["metrics"][label] = {"operations": operations, "total_ticks": ticks,
                "max_ticks": max(m["max_ticks"] for m in metrics), "mean_ticks_per_operation": mean,
                "mean_microseconds_per_operation": mean * 1_000_000 / frequency if mean is not None else None,
                "ticks_per_valid_sample": _ratio(ticks, totals["valid_samples"]) if operations else None}
        result["layers"][layer] = summary
    return result


def main(argv=None):
    argument_parser = argparse.ArgumentParser(description=__doc__)
    argument_parser.add_argument("input", help="raw capture file, or - for stdin")
    argument_parser.add_argument("--output", type=Path, help="write validated raw packets and weighted summary as JSON")
    args = argument_parser.parse_args(argv)
    try:
        text = sys.stdin.read() if args.input == "-" else Path(args.input).read_text(encoding="utf-8")
        result = summarize(parse(text))
    except (OSError, UnicodeError, ParseError) as error:
        argument_parser.exit(2, f"tile_helpers: {error}\n")
    rendered = json.dumps(result, indent=2, allow_nan=False) + "\n"
    if args.output:
        args.output.write_text(rendered, encoding="utf-8")
    else:
        sys.stdout.write(rendered)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
