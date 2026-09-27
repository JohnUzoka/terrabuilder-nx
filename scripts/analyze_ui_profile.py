#!/usr/bin/env python3
"""Strict build51 UI-cost packets; raw records remain authoritative."""
import argparse
import hashlib
import json
import math
import re
import sys
from pathlib import Path

SCHEMA_PATH = Path(__file__).with_name("patch_ui_profile") / "ui51-schema.json"
SCHEMA_BYTES = SCHEMA_PATH.read_bytes()
SCHEMA = json.loads(SCHEMA_BYTES)
FIELDS = SCHEMA["rows"]
COHORTS = tuple(SCHEMA["cohorts"])
LABELS = tuple(SCHEMA["metrics"])
RANGES = tuple(key[:-4] for key in FIELDS["SCENE"] if key.endswith("_min"))
SUFFIXES = ("_first", "_last", "_min", "_max")
RANGE_FIELDS = frozenset(name + suffix for name in RANGES for suffix in SUFFIXES)
INTEGER_RANGES = frozenset(("width", "height", "skip", "mouse_x", "mouse_y"))
SCENE_IDS = tuple(key for key in FIELDS["SCENE"] if key.endswith(("_first", "_last")) and key not in RANGE_FIELDS)
CHANGES = tuple(key for key in FIELDS["SCENE"] if key.endswith("_changes"))
FLAGS = {"menu": 1, "paused": 2, "inventory": 4, "map": 8, "dead": 16,
         "ghost": 32, "spectator": 64, "invalid_player": 128,
         "autopause": 256, "invalid_scene": 512}
STATE_LIFETIMES = tuple(key for key in FIELDS["STATE"] if key.endswith("_lifetime"))
INT = re.compile(r"(?:0|[1-9][0-9]*)\Z")
SIGNED_INT = re.compile(r"-?(?:0|[1-9][0-9]*)\Z")
FLOAT = re.compile(r"-?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?\Z")
MAX_INT = (1 << 63) - 1
MAX_INT32 = (1 << 31) - 1
MIN_INT32 = -(1 << 31)
LIMITS = SCHEMA["limits"] + [
    "Deterministic sampling is not random or guaranteed representative; no 16x extrapolation, FPS prediction or savings estimate is produced.",
    "Timing means divide raw tick sums by completed calls or explicitly reported valid selected frames, never by all eligible frames.",
    "A global sticky invalid flag suppresses derived timing means for the entire capture; diagnostic raw sums remain available.",
    "State changes count adjacent accepted snapshots within each packet/cohort. Gaps and packet boundaries are not interpolated.",
    "The packet does not expose each sampling phase or every begin-only eligible attempt; only observable sampling bounds/accounting can be checked.",
    "Periodic-only captures are accepted as incomplete captures, not as proof of normal final flush.",
    "Fixture or desktop timings are not Switch performance evidence.",
]


class ParseError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise ParseError(message)


def _integer(value, signed, location):
    require(bool((SIGNED_INT if signed else INT).fullmatch(value)), f"{location}: invalid integer")
    require(len(value.lstrip("-")) <= 19, f"{location}: integer overflow")
    result = int(value)
    require(-MAX_INT <= result <= MAX_INT, f"{location}: integer overflow")
    return result


def _record(line, number):
    words = line.split()
    require(len(words) >= 3 and words[0] == SCHEMA["prefix"], f"line {number}: malformed profiler row")
    kind = words[1]
    require(kind in FIELDS, f"line {number}: unknown/incompatible row {kind}")
    values = {}
    for word in words[2:]:
        require(word.count("=") == 1, f"line {number}: malformed token {word!r}")
        key, value = word.split("=", 1)
        require(key and value and key not in values, f"line {number}: empty/duplicate key {key!r}")
        values[key] = value
    require(list(values) == FIELDS[kind], f"line {number}: {kind} fields/order mismatch")
    for key, value in tuple(values.items()):
        location = f"line {number}: {key}"
        if key in ("schema", "name", "cohort", "label"):
            continue
        if kind == "SCENE" and key in RANGE_FIELDS:
            if value == "null":
                values[key] = None
                continue
            require(bool(FLOAT.fullmatch(value)), f"{location}: malformed scene number")
            if key.rsplit("_", 1)[0] in INTEGER_RANGES:
                numeric = _integer(value, True, location)
                require(MIN_INT32 <= numeric <= MAX_INT32, f"{location}: out-of-range scene integer")
            else:
                numeric = float(value)
                require(math.isfinite(numeric), f"{location}: nonfinite scene number")
            values[key] = numeric
        else:
            values[key] = _integer(value, kind == "SCENE" and key in SCENE_IDS, location)
    return kind, values


def _timing(count, total, maximum, label):
    require(0 <= maximum <= total, f"{label}: maximum exceeds total")
    if count == 0:
        require(total == maximum == 0, f"{label}: unused count with ticks")
    else:
        require(total <= count * maximum, f"{label}: total exceeds count*maximum")


def _transition(first, last, changes, label):
    require(first == last or changes > 0, f"{label}: changed endpoints without a transition")
    require(changes != 1 or first != last, f"{label}: single transition has equal endpoints")


def _scene(scene, frames, name):
    samples = scene["samples"]
    require(samples == 2 * frames, f"{name}: scene must cover both eligible boundaries")
    require(scene["day_samples"] + scene["night_samples"] == samples, f"{name}: inconsistent day/night samples")
    require(scene["day_samples"] % 2 == scene["night_samples"] % 2 == 0, f"{name}: unstable day/night pair")
    for key in CHANGES:
        maximum = max(0, frames - 1) if key in ("player_id_changes", "day_changes") else max(0, samples - 1)
        require(scene[key] <= maximum, f"{name}: {key} exceeds transitions")
    require(scene["day_changes"] <= min(max(0, frames - 1), min(scene["day_samples"], scene["night_samples"])),
            f"{name}: impossible day transitions")
    require(bool(scene["day_samples"] and scene["night_samples"]) == bool(scene["day_changes"]), f"{name}: day/night population lacks matching transitions")
    for key in SCENE_IDS:
        value = scene[key]
        if not samples:
            require(value == 0, f"{name}: empty scene has {key}")
        elif key.startswith("player_id"):
            require(0 <= value <= MAX_INT32, f"{name}: invalid player id")
        elif "prefix" in key:
            require(-1 <= value <= 255, f"{name}: invalid item prefix")
        else:
            require(MIN_INT32 <= value <= MAX_INT32, f"{name}: invalid item type")
    for item in ("hover", "mouse_item"):
        for suffix in ("first", "last"):
            require(scene[f"{item}_prefix_{suffix}"] != -1 or scene[f"{item}_type_{suffix}"] == -1,
                    f"{name}: incoherent null {item}")
    for key in RANGES:
        values = [scene[key + suffix] for suffix in SUFFIXES]
        first, last, minimum, maximum = values
        if not samples:
            require(all(value is None for value in values), f"{name}: unused range {key} must be null")
            continue
        require(all(value is not None for value in values), f"{name}: used range {key} cannot be null")
        require(minimum <= first <= maximum and minimum <= last <= maximum, f"{name}: inconsistent range {key}")
        if key in ("zoom_target", "ui_scale", "width", "height"):
            require(minimum > 0, f"{name}: nonpositive {key}")
        if samples == 2:
            require(minimum == min(first, last) and maximum == max(first, last), f"{name}: impossible two-sample {key} range")
            if key in ("zoom_target", "ui_scale", "width", "height", "skip"):
                require(first == last, f"{name}: unstable single-frame {key}")
    if not samples:
        return
    _transition(scene["player_id_first"], scene["player_id_last"], scene["player_id_changes"], name + " player id")
    for item, changes in (("hover", "hover_changes"), ("mouse_item", "mouse_item_changes")):
        first = (scene[item + "_type_first"], scene[item + "_prefix_first"])
        last = (scene[item + "_type_last"], scene[item + "_prefix_last"])
        _transition(first, last, scene[changes], name + " " + item)
    for prefix, changes in (("player", "player_position_changes"), ("camera", "camera_position_changes"), ("mouse", "mouse_position_changes")):
        first = tuple(scene[prefix + axis + "_first"] for axis in ("_x", "_y"))
        last = tuple(scene[prefix + axis + "_last"] for axis in ("_x", "_y"))
        varying = any(scene[prefix + axis + "_min"] != scene[prefix + axis + "_max"] for axis in ("_x", "_y"))
        require(varying == (scene[changes] > 0), f"{name}: {prefix} movement/range mismatch")
        _transition(first, last, scene[changes], name + " " + prefix)


def _cohort(packet, name):
    cohort = packet["cohorts"][name]
    scopes = packet["scopes"][name]
    valid = cohort["valid_samples"]
    require(cohort["selected_frames"] == valid + cohort["invalid_samples"] + cohort["aborted_samples"], f"{name}: incomplete selected accounting")
    require(cohort["selected_frames"] <= cohort["frames"], f"{name}: selected exceeds cohort frames")
    require(cohort["no_ui_samples"] <= valid, f"{name}: no-UI count exceeds valid samples")
    require(cohort["stopped_ui_samples"] <= valid - cohort["no_ui_samples"], f"{name}: stopped layer without a valid UI sample")
    require(cohort["stopped_ui_samples"] <= sum(scopes[label]["calls"] for label in LABELS[2:7]), f"{name}: stopped UI samples exceed completed layer calls")
    require(cohort["clock_pairs"] == valid, f"{name}: clock pairs differ from valid samples")
    _timing(cohort["clock_pairs"], cohort["clock_ticks"], cohort["clock_max_ticks"], name + " clock pairs")
    require(not cohort["invalid_samples"] or packet["state"]["measurement_invalid"], f"{name}: invalid samples without sticky invalidity")
    require(scopes["frame_draw"]["calls"] == valid, f"{name}: frame root count mismatch")
    require(scopes["ui"]["calls"] == valid - cohort["no_ui_samples"], f"{name}: UI root/no-UI count mismatch")
    for label, metric in scopes.items():
        calls = metric["calls"]
        _timing(calls, metric["inclusive_ticks"], metric["max_inclusive_ticks"], name + " " + label + " inclusive")
        _timing(calls, metric["exclusive_ticks"], metric["max_exclusive_ticks"], name + " " + label + " exclusive")
        require(metric["exclusive_ticks"] <= metric["inclusive_ticks"] and metric["max_exclusive_ticks"] <= metric["max_inclusive_ticks"],
                f"{name} {label}: exclusive exceeds inclusive")
        require(metric["inclusive_ticks"] - metric["exclusive_ticks"] >= metric["max_inclusive_ticks"] - metric["max_exclusive_ticks"],
                f"{name} {label}: maxima cannot belong to inclusive/exclusive call pairs")
        if label not in ("frame_draw", "ui"):
            require(not calls or scopes["ui"]["calls"], f"{name} {label}: child without UI")
            require(metric["max_inclusive_ticks"] <= scopes["ui"]["max_inclusive_ticks"], f"{name} {label}: call exceeds enclosing UI maximum")
    frame, ui = scopes["frame_draw"], scopes["ui"]
    require(ui["max_inclusive_ticks"] <= frame["max_inclusive_ticks"], f"{name}: UI maximum exceeds frame maximum")
    require(frame["exclusive_ticks"] + ui["inclusive_ticks"] == frame["inclusive_ticks"], f"{name}: frame/UI nesting sum mismatch")
    require(sum(metric["exclusive_ticks"] for metric in scopes.values()) == frame["inclusive_ticks"], f"{name}: exclusive conservation violated")
    require(ui["exclusive_ticks"] + sum(scopes[label]["exclusive_ticks"] for label in LABELS[2:]) == ui["inclusive_ticks"], f"{name}: UI child conservation violated")
    _scene(packet["scenes"][name], cohort["frames"], name)


def _validate(packet):
    begin, state, cost = (packet[key] for key in ("begin", "state", "report_cost"))
    require(begin["version"] == 51 and begin["schema"] == "ui_costs", "incompatible version/schema")
    require(begin["interval"] > 0 and begin["frequency"] > 0, "invalid interval/clock frequency")
    require(begin["sample_denominator"] == 16 and begin["metric_count"] == 12 and begin["cohort_count"] == 3, "incompatible sampling/metric/cohort counts")
    require(begin["final"] in (0, 1), "invalid final flag")
    require(begin["end_tick"] >= begin["start_tick"] and begin["wall_ticks"] == begin["end_tick"] - begin["start_tick"], "inconsistent/backward wall ticks")
    require(begin["final"] or begin["wall_ticks"] >= 5 * begin["frequency"], "nonfinal report shorter than five seconds")
    require(state["measurement_invalid"] in (0, 1) and cost["measurement_invalid"] in (0, 1), "invalid measurement flag")
    require(cost["measurement_invalid"] >= state["measurement_invalid"], "measurement invalidation disappeared within packet")
    frames, attempts = state["frames"], state["attempted_frames"]
    require(attempts == frames + state["capture_failure_frames"], "inconsistent capture/attempt accounting")
    require(state["completed"] + state["aborted"] == frames, "inconsistent frame completion accounting")
    require(state["eligible"] <= state["completed"] and state["excluded"] == frames - state["eligible"], "inconsistent frame eligibility accounting")
    require(state["state_changed"] <= state["excluded"], "changed frames donated eligible samples")
    require(state["eligible"] == sum(cohort["frames"] for cohort in packet["cohorts"].values()), "cohort frame partition mismatch")
    require(state["selected_attempts"] == state["discarded_selected"] + sum(cohort["selected_frames"] for cohort in packet["cohorts"].values()), "selected/discarded accounting mismatch")
    require(state["discarded_selected"] <= state["excluded"] + state["capture_failure_frames"], "discarded samples exceed undonated frames")
    maximum_selected = min(attempts, len(COHORTS) + max(0, attempts - len(COHORTS)) // 16)
    require(state["selected_attempts"] <= maximum_selected, "one-in16 sampling attempt bound exceeded")
    for name, cohort in packet["cohorts"].items():
        require(cohort["selected_frames"] <= (attempts + 15) // 16, f"{name}: one-in16 selected bound exceeded")
        if state["eligible"] == attempts:
            require(cohort["frames"] // 16 <= cohort["selected_frames"] <= (cohort["frames"] + 15) // 16, f"{name}: stable cohort sampling phase bound violated")
            if begin["interval"] == 1:
                require(cohort["selected_frames"] == (cohort["frames"] + 15) // 16, f"{name}: initial sampling phase must select frame zero")
    for key, bit in FLAGS.items():
        require(state[key] <= frames, f"STATE {key} exceeds complete frames")
        require(bool(state["union_flags"] & bit) == bool(state[key]), f"STATE {key} counter/union mismatch")
        if frames > 1:
            require(state[key] >= bool(state["first_flags"] & bit) + bool(state["last_flags"] & bit), f"STATE {key} omits boundary frames")
        if key not in ("paused", "inventory", "autopause"):
            require(state[key] <= state["excluded"], f"STATE {key} frames cannot be eligible")
    for key, baseline in (("inventory", packet["cohorts"]["inventory"]["frames"] + packet["cohorts"]["inventory_paused"]["frames"]),
                          ("paused", packet["cohorts"]["inventory_paused"]["frames"])):
        require(baseline <= state[key] <= baseline + state["excluded"], f"STATE {key} disagrees with cohorts")
    require(all(state[key] <= 1023 for key in ("first_flags", "last_flags", "union_flags")), "unknown state flags")
    require((state["first_flags"] | state["last_flags"]) & ~state["union_flags"] == 0, "boundary flags absent from union")
    if frames:
        require(state["first_frame_id"] > 0 and state["last_frame_id"] - state["first_frame_id"] + 1 == frames, "inconsistent frame ids")
        if frames == 1:
            require(state["union_flags"] == state["first_flags"] | state["last_flags"], "single frame union differs from boundaries")
    else:
        require(all(state[key] == 0 for key in ("first_frame_id", "last_frame_id", "first_flags", "last_flags", "union_flags")), "empty state has frame data")
    require(state["snapshot_calls"] == frames * 2, "snapshot count must cover both complete boundaries")
    _timing(state["snapshot_calls"], state["snapshot_ticks"], state["snapshot_max_ticks"], "snapshot")
    require(state["max_depth_lifetime"] <= 64, "reported stack depth exceeds fixed capacity")
    require(not any(state[key] for key in ("capture_failure_frames", "depth_overflows_lifetime", "unbalanced_scopes_lifetime", "scope_failures_lifetime", "report_failures_lifetime")) or state["measurement_invalid"], "observer failures without sticky invalidity")
    for name in COHORTS:
        _cohort(packet, name)
    if any(cohort["valid_samples"] for cohort in packet["cohorts"].values()):
        require(state["max_depth_lifetime"] >= 1, "valid frame without root stack depth")
    if any(packet["scopes"][name]["ui"]["calls"] for name in COHORTS):
        require(state["max_depth_lifetime"] >= 2, "valid UI without nested stack depth")
    require(cost["reports"] == begin["interval"], "report count/interval mismatch")
    require(cost["attempts"] == min(MAX_INT, cost["reports"] + cost["failures_lifetime"]), "inconsistent report attempt accounting")
    require(cost["failures_lifetime"] >= state["report_failures_lifetime"], "report failures moved backwards within packet")
    require(not cost["failures_lifetime"] or cost["measurement_invalid"], "report failure without sticky invalidity")
    require(cost["ticks_before_footer"] <= cost["lifetime_max_ticks"], "report body exceeds lifetime maximum")
    _timing(cost["attempts"], cost["lifetime_ticks_before_footers"], cost["lifetime_max_ticks"], "report lifetime")
    if cost["attempts"] == 1:
        require(cost["ticks_before_footer"] == cost["lifetime_ticks_before_footers"], "first report lifetime differs from its body cost")


def _continuity(previous, packet):
    begin, state, cost = (packet[key] for key in ("begin", "state", "report_cost"))
    old_begin, old_state, old_cost = (previous[key] for key in ("begin", "state", "report_cost"))
    require(not old_begin["final"], "packet follows final report")
    require(begin["interval"] == old_begin["interval"] + 1, "nonconsecutive/duplicate intervals")
    require(begin["frequency"] == old_begin["frequency"], "clock frequency changed")
    require(begin["start_tick"] >= old_begin["end_tick"], "overlapping/backward report windows")
    require(state["measurement_invalid"] >= old_cost["measurement_invalid"], "sticky measurement invalidity disappeared")
    for key in STATE_LIFETIMES:
        require(state[key] >= old_state[key], f"lifetime state counter moved backwards: {key}")
    require(state["report_failures_lifetime"] >= old_cost["failures_lifetime"], "lifetime report failure counter moved backwards")
    for key in ("attempts", "failures_lifetime", "lifetime_ticks_before_footers", "lifetime_max_ticks", "measurement_invalid"):
        require(cost[key] >= old_cost[key], f"lifetime counter moved backwards: {key}")
    require(cost["lifetime_ticks_before_footers"] >= min(MAX_INT, old_cost["lifetime_ticks_before_footers"] + cost["ticks_before_footer"]), "report lifetime omits current body ticks")
    if cost["failures_lifetime"] == old_cost["failures_lifetime"]:
        require(cost["lifetime_ticks_before_footers"] == min(MAX_INT, old_cost["lifetime_ticks_before_footers"] + cost["ticks_before_footer"]), "report lifetime delta inconsistent without failures")
        require(cost["lifetime_max_ticks"] == max(old_cost["lifetime_max_ticks"], cost["ticks_before_footer"]), "report maximum changed without a matching attempt")


def parse(text):
    """Return complete validated packets; never repair/drop malformed profiler rows."""
    packets, rows, raw, context, source_lines = [], [], [], [], []
    expected = ["BEGIN", "STATE"] + [kind for _ in COHORTS for kind in ("COHORT", "SCENE", *("SCOPE",) * len(LABELS))] + ["REPORT_COST", "END"]
    last_frame_id = None
    for number, line in enumerate(text.splitlines(), 1):
        if SCHEMA["prefix"] not in line:
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
        packet = {"begin": rows[0][1], "state": rows[1][1], "cohorts": {}, "scenes": {}, "scopes": {},
                  "report_cost": rows[-2][1], "end": rows[-1][1], "raw": "\n".join(raw) + "\n",
                  "source_lines": source_lines, "context": context}
        index = 2
        for name in COHORTS:
            cohort, scene = rows[index][1], rows[index + 1][1]
            index += 2
            require(cohort["name"] == name and scene["cohort"] == name, "wrong/duplicate/out-of-order cohort")
            packet["cohorts"][name], packet["scenes"][name], packet["scopes"][name] = cohort, scene, {}
            for metric_id, label in enumerate(LABELS):
                metric = rows[index][1]
                index += 1
                require(metric["cohort"] == name and metric["id"] == metric_id and metric["label"] == label, "wrong/duplicate/out-of-order scope cohort/id/label")
                packet["scopes"][name][label] = metric
        _validate(packet)
        if packets:
            _continuity(packets[-1], packet)
        if packet["state"]["frames"]:
            if last_frame_id is not None:
                require(packet["state"]["first_frame_id"] == last_frame_id + 1, "nonconsecutive frame ids across packets")
            last_frame_id = packet["state"]["last_frame_id"]
        packets.append(packet)
        rows, raw, context, source_lines = [], [], [], []
    require(not rows, "incomplete packet: missing END or required rows")
    require(bool(packets), "no complete ui_costs packets")
    packets[-1]["trailing_context"] = context
    return packets


def _ratio(numerator, denominator):
    return numerator / denominator if denominator else None


def _scene_summary(scenes):
    result = {key: sum(scene[key] for scene in scenes) for key in ("samples", "day_samples", "night_samples", *CHANGES)}
    used = [scene for scene in scenes if scene["samples"]]
    for key in SCENE_IDS:
        result[key] = (used[0] if key.endswith("_first") else used[-1])[key] if used else 0
    for name in RANGES:
        for suffix in SUFFIXES:
            key = name + suffix
            if not used:
                result[key] = None
            elif suffix == "_first":
                result[key] = used[0][key]
            elif suffix == "_last":
                result[key] = used[-1][key]
            else:
                result[key] = (min if suffix == "_min" else max)(scene[key] for scene in used)
    result["transition_scope"] = "sum of within-packet transitions only; no interpolation between packets"
    return result


def summarize(packets):
    """Weight by raw completed counts, keeping all three cohort populations separate."""
    require(bool(packets), "cannot summarize an empty capture")
    frequency = packets[0]["begin"]["frequency"]
    invalid = any(packet["report_cost"]["measurement_invalid"] for packet in packets)
    result = {"version": 51, "schema": "ui_costs", "schema_sha256": hashlib.sha256(SCHEMA_BYTES).hexdigest(),
              "frequency": frequency, "sample_denominator": 16, "packet_count": len(packets),
              "first_interval": packets[0]["begin"]["interval"], "last_interval": packets[-1]["begin"]["interval"],
              "final_seen": bool(packets[-1]["begin"]["final"]),
              "capture_complete": bool(packets[-1]["begin"]["final"]),
              "starts_at_first_interval": packets[0]["begin"]["interval"] == 1,
              "measurement_invalid": invalid, "performance_interpretation_allowed": not invalid,
              "limits": LIMITS, "packets": packets, "cohorts": {}}
    for name in COHORTS:
        cohorts = [packet["cohorts"][name] for packet in packets]
        totals = {key: sum(cohort[key] for cohort in cohorts) for key in FIELDS["COHORT"] if key not in ("interval", "name", "clock_max_ticks")}
        totals["clock_max_ticks"] = max(cohort["clock_max_ticks"] for cohort in cohorts)
        summary = {"raw": totals, "valid_selected_frame_denominator": totals["valid_samples"],
                   "selected_per_eligible_frame": _ratio(totals["selected_frames"], totals["frames"]),
                   "clock_mean_ticks_per_pair": _ratio(totals["clock_ticks"], totals["clock_pairs"]) if not invalid else None,
                   "scene": _scene_summary([packet["scenes"][name] for packet in packets]), "scopes": {}}
        for metric_id, label in enumerate(LABELS):
            records = [packet["scopes"][name][label] for packet in packets]
            metric = {"id": metric_id, "calls": sum(record["calls"] for record in records)}
            for mode in ("inclusive", "exclusive"):
                ticks = sum(record[mode + "_ticks"] for record in records)
                metric[mode + "_ticks"] = ticks
                metric["max_" + mode + "_ticks"] = max(record["max_" + mode + "_ticks"] for record in records)
                mean = _ratio(ticks, metric["calls"]) if not invalid else None
                per_frame = _ratio(ticks, totals["valid_samples"]) if not invalid and metric["calls"] else None
                metric["mean_" + mode + "_ticks_per_call"] = mean
                metric["mean_" + mode + "_microseconds_per_call"] = mean * 1_000_000 / frequency if mean is not None else None
                metric[mode + "_ticks_per_valid_selected_frame"] = per_frame
                metric[mode + "_microseconds_per_valid_selected_frame"] = per_frame * 1_000_000 / frequency if per_frame is not None else None
            summary["scopes"][label] = metric
        result["cohorts"][name] = summary
    return result


def main(argv=None):
    argument_parser = argparse.ArgumentParser(description=__doc__)
    argument_parser.add_argument("input", help="raw capture file, or - for stdin")
    argument_parser.add_argument("--output", type=Path, help="write validated raw packets and weighted summaries as JSON")
    args = argument_parser.parse_args(argv)
    try:
        text = sys.stdin.read() if args.input == "-" else Path(args.input).read_text(encoding="utf-8")
        result = summarize(parse(text))
        rendered = json.dumps(result, indent=2, allow_nan=False) + "\n"
        if args.output:
            args.output.write_text(rendered, encoding="utf-8")
        else:
            sys.stdout.write(rendered)
    except (OSError, UnicodeError, ParseError) as error:
        argument_parser.exit(2, f"ui_costs: {error}\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
