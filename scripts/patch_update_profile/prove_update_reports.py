#!/usr/bin/env python3
"""Prove build56 parsing against emitted candidate reports and derived corruptions."""
import argparse
import hashlib
import importlib.util
import json
import re
import subprocess
import sys
import tempfile
from pathlib import Path


class ProofError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise ProofError(message)


def digest(path):
    result = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(block)
    return result.hexdigest()


def confined(root, relative):
    require(type(relative) is str and relative and not Path(relative).is_absolute(), "fixture name must be relative")
    path = (root / relative).resolve()
    require(path.is_relative_to(root) and path != root and path.is_file(), "missing/escaping fixture: " + relative)
    return path


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "duplicate manifest key: " + key)
        result[key] = value
    return result


def alter(text, kind, key, value, occurrence=0):
    rows = text.splitlines()
    matches = [index for index, row in enumerate(rows) if row.startswith("NX_PROFILE " + kind + " ")]
    require(0 <= occurrence < len(matches), "missing mutation row: " + kind)
    index = matches[occurrence]
    rows[index], count = re.subn(r"(?<!\S)" + re.escape(key) + r"=\S+", key + "=" + str(value), rows[index])
    require(count == 1, "missing/duplicate mutation key: " + key)
    return "\n".join(rows) + "\n"


def adversaries(analyzer, packet):
    """Corrupt actual emitted bytes, never synthesize positive runtime evidence."""
    text = packet["raw"]
    rows = text.splitlines()
    cases = {}

    def bad(name, kind, key, value, occurrence=0):
        cases[name] = alter(text, kind, key, value, occurrence)

    for index, row in enumerate(rows):
        kind = row.split()[1].lower()
        cases[f"missing_{index:02d}_{kind}"] = "\n".join(rows[:index] + rows[index + 1:]) + "\n"
        cases[f"duplicate_{index:02d}_{kind}"] = "\n".join(rows[:index] + [row] + rows[index:]) + "\n"
        cases[f"truncated_{index:02d}_{kind}"] = "\n".join(rows[:index] + [row[:-1]]) + "\n"
    for kind, keys in analyzer.FIELDS.items():
        row_index = next(i for i, row in enumerate(rows) if row.startswith("NX_PROFILE " + kind + " "))
        for key in keys:
            changed = rows.copy()
            changed[row_index] = re.sub(r" " + re.escape(key) + r"=\S+", "", changed[row_index])
            cases["missing_key_" + kind + "_" + key] = "\n".join(changed) + "\n"
        changed = rows.copy()
        words = changed[row_index].split()
        changed[row_index] += " " + words[-1]
        cases["duplicate_key_" + kind] = "\n".join(changed) + "\n"
        changed[row_index] = rows[row_index] + " unexpected=1"
        cases["unknown_key_" + kind] = "\n".join(changed) + "\n"
        if len(keys) > 1:
            words[2], words[3] = words[3], words[2]
            changed[row_index] = " ".join(words)
            cases["key_order_" + kind] = "\n".join(changed) + "\n"
    cases["empty"] = "ordinary non-profiler log\n"
    cases["partial_profile_prefix"] = text + "NX_PROFILE\n"
    for length in range(1, len(analyzer.SCHEMA["prefix"])):
        cases["truncated_marker_" + str(length)] = text + analyzer.SCHEMA["prefix"][:length] + "\n"
    cases["malformed_prefixed_row"] = "prefix " + text
    cases["indented_row"] = " " + text
    cases["unknown_row"] = text + "NX_PROFILE OTHER interval=1\n"
    cases["duplicate_packet"] = text + text
    cases["trailing_partial_packet"] = text + rows[0] + "\n"
    cases["truncated_token"] = text.replace("version=56", "version=", 1)
    reordered = rows.copy()
    reordered[1], reordered[2] = reordered[2], reordered[1]
    cases["row_order"] = "\n".join(reordered) + "\n"
    cases["obsolete_no_ui_field"] = text.replace(" no_body_samples=", " no_ui_samples=", 1)
    cases["obsolete_draw_counter"] = text.replace(" attempted_updates=", " attempted_frames=", 1)
    for value, name in ((-1, "negative"), ("+1", "plus"), ("-0", "negative_zero"), ("01", "leading_zero"),
                        ("1.0", "decimal"), ("true", "boolean"), ("1e0", "exponent"), ("null", "null"),
                        (1 << 63, "overflow"), ("9" * 100, "huge_overflow")):
        bad(name + "_counter", "STATE", "snapshot_calls", value)
    for key, value in (("version", 51), ("version", 54), ("version", 55), ("schema", "recipe_costs"), ("schema", "ui_costs"),
                       ("sample_denominator", 1), ("sample_denominator", 16), ("sample_denominator", 63), ("sample_denominator", 65), ("metric_count", 11), ("metric_count", 18), ("metric_count", 20), ("cohort_count", 2),
                       ("frequency", 0), ("interval", 0), ("final", 2)):
        bad("begin_" + key + "_" + str(value), "BEGIN", key, value)
    for kind in ("STATE", "REPORT_COST"):
        bad(kind + "_measurement_flag", kind, "measurement_invalid", 2)
    bad("interval_mismatch", "END", "interval", packet["begin"]["interval"] + 1)
    bad("wall_mismatch", "BEGIN", "wall_ticks", packet["begin"]["wall_ticks"] + 1)
    cases["backward_window"] = alter(alter(text, "BEGIN", "start_tick", 2), "BEGIN", "end_tick", 1)
    cases["short_periodic"] = alter(alter(alter(text, "BEGIN", "final", 0), "BEGIN", "wall_ticks", 0), "BEGIN", "end_tick", packet["begin"]["start_tick"])
    state = packet["state"]
    for key, value in (("attempted_updates", state["attempted_updates"] + 1), ("completed", state["updates"] + 1),
                       ("aborted", state["aborted"] + 1), ("eligible", state["completed"] + 1),
                       ("excluded", state["excluded"] + 1), ("state_changed", state["excluded"] + 1),
                       ("capture_failure_updates", state["capture_failure_updates"] + 1),
                       ("selected_attempts", state["selected_attempts"] + 1), ("discarded_selected", state["discarded_selected"] + 1),
                       ("snapshot_calls", state["snapshot_calls"] + 1), ("snapshot_max_ticks", state["snapshot_ticks"] + 1),
                       ("union_flags", 1024), ("first_update_id", 0), ("last_update_id", state["last_update_id"] + 1),
                       ("max_depth_lifetime", 65)):
        bad("state_" + key, "STATE", key, value)
    for key in analyzer.FLAGS:
        bad("flag_counter_" + key, "STATE", key, state["updates"] + 1)
    for key in ("depth_overflows_lifetime", "unbalanced_scopes_lifetime", "scope_failures_lifetime"):
        cases["unflagged_" + key] = alter(alter(alter(text, "STATE", key, 1), "STATE", "measurement_invalid", 0), "REPORT_COST", "measurement_invalid", 0)
    cases["lost_packet_invalidity"] = alter(alter(text, "STATE", "measurement_invalid", 1), "REPORT_COST", "measurement_invalid", 0)
    for occurrence, name in enumerate(analyzer.COHORTS):
        cohort, scene = packet["cohorts"][name], packet["scenes"][name]
        for key, value in (("name", "other"), ("updates", cohort["updates"] + 1), ("selected_updates", cohort["selected_updates"] + 1),
                           ("valid_samples", cohort["valid_samples"] + 1), ("invalid_samples", cohort["invalid_samples"] + 1),
                           ("aborted_samples", cohort["aborted_samples"] + 1), ("no_body_samples", cohort["valid_samples"] + 1),
                           ("clock_pairs", cohort["clock_pairs"] + 1), ("clock_max_ticks", cohort["clock_ticks"] + 1)):
            bad(name + "_cohort_" + key, "COHORT", key, value, occurrence)
        for key, value in (("cohort", "other"), ("samples", scene["samples"] + 1), ("day_samples", scene["day_samples"] + 1),
                           ("player_id_first", -1), ("hover_prefix_first", 256), ("hover_type_first", 1 << 31)):
            bad(name + "_scene_" + key, "SCENE", key, value, occurrence)
        for key in analyzer.CHANGES:
            bad(name + "_changes_" + key, "SCENE", key, scene["samples"] + 1, occurrence)
        for key in analyzer.RANGES:
            if scene["samples"]:
                bad(name + "_range_null_" + key, "SCENE", key + "_first", "null", occurrence)
                bad(name + "_range_first_" + key, "SCENE", key + "_first", scene[key + "_max"] + 1, occurrence)
                bad(name + "_range_last_" + key, "SCENE", key + "_last", scene[key + "_min"] - 1, occurrence)
                bad(name + "_range_min_" + key, "SCENE", key + "_min", scene[key + "_max"] + 1, occurrence)
                bad(name + "_range_max_" + key, "SCENE", key + "_max", scene[key + "_min"] - 1, occurrence)
            else:
                bad(name + "_unused_range_" + key, "SCENE", key + "_first", 0, occurrence)
        for value in ("NaN", "Infinity", "-Infinity", "1e9999", "nan", "inf"):
            bad(name + "_nonfinite_" + value, "SCENE", "camera_x_first", value, occurrence)
        for key in sorted(analyzer.INTEGER_RANGES):
            for value, tag in (("0.5", "fraction"), (1 << 31, "overflow"), (-(1 << 31) - 1, "underflow"), ("-0", "negative_zero")):
                bad(name + "_integer_" + key + "_" + tag, "SCENE", key + "_first", value, occurrence)
        for metric_id, label in enumerate(analyzer.LABELS):
            metric = packet["scopes"][name][label]
            offset = occurrence * len(analyzer.LABELS) + metric_id
            for key, value in (("cohort", "other"), ("id", (metric_id + 1) % len(analyzer.LABELS)), ("label", "other"),
                               ("calls", 1 << 63), ("inclusive_ticks", -1), ("exclusive_ticks", metric["inclusive_ticks"] + 1),
                               ("max_inclusive_ticks", metric["inclusive_ticks"] + 1), ("max_exclusive_ticks", metric["exclusive_ticks"] + 1)):
                bad(name + "_" + label + "_" + key, "SCOPE", key, value, offset)
            if metric["inclusive_ticks"]:
                bad(name + "_" + label + "_unused_ticks", "SCOPE", "calls", 0, offset)
            if label == "update":
                bad(name + "_" + label + "_count", "SCOPE", "calls", metric["calls"] + 1, offset)
            if metric["exclusive_ticks"]:
                # Lowering the total and maximum preserves local timing bounds,
                # but violates the global exclusive conservation identity.
                changed = alter(text, "SCOPE", "exclusive_ticks", 0, offset)
                cases[name + "_" + label + "_conservation"] = alter(changed, "SCOPE", "max_exclusive_ticks", 0, offset)
    for occurrence, name in enumerate(analyzer.COHORTS):
        cohort, scopes = packet["cohorts"][name], packet["scopes"][name]
        body_offset = occurrence * len(analyzer.LABELS) + 1
        if scopes["do_update"]["calls"]:
            bad(name + "_all_no_body_with_body_calls", "COHORT", "no_body_samples", cohort["valid_samples"], occurrence)
        if cohort["valid_samples"] > cohort["no_body_samples"]:
            changed = text
            for key in ("calls", "inclusive_ticks", "exclusive_ticks", "max_inclusive_ticks", "max_exclusive_ticks"):
                changed = alter(changed, "SCOPE", key, 0, body_offset)
            cases[name + "_body_omits_nonempty_samples"] = changed
        for metric_id, label in enumerate(analyzer.LABELS[1:], 1):
            metric = scopes[label]
            if not metric["calls"]:
                continue
            # Preserve local timing/maxima bounds while violating root enclosure.
            changed = text
            maximum = scopes["update"]["max_inclusive_ticks"] + 1
            for key, value in (("calls", 1), ("inclusive_ticks", maximum), ("exclusive_ticks", 0),
                               ("max_inclusive_ticks", maximum), ("max_exclusive_ticks", 0)):
                changed = alter(changed, "SCOPE", key, value, occurrence * len(analyzer.LABELS) + metric_id)
            cases[name + "_" + label + "_exceeds_root_maximum"] = changed
    for occurrence, name in enumerate(analyzer.COHORTS):
        if any(packet["scopes"][name][label]["calls"] for label in analyzer.LABELS[1:]):
            bad("child_without_nested_depth", "STATE", "max_depth_lifetime", 1)
            break
    cost = packet["report_cost"]
    for key, value in (("reports", cost["reports"] + 1), ("attempts", cost["attempts"] + 1),
                       ("failures_lifetime", cost["failures_lifetime"] + 1),
                       ("ticks_before_footer", cost["lifetime_max_ticks"] + 1),
                       ("lifetime_max_ticks", cost["lifetime_ticks_before_footers"] + 1)):
        bad("report_" + key, "REPORT_COST", key, value)
    return cases


def continuity_adversaries(analyzer, packets):
    first, second = packets[:2]
    left, right = first["raw"], second["raw"]
    cases = {}

    def add(name, a, b, expected):
        # Each member must be valid in isolation, so the failure really tests
        # cross-packet accounting rather than an unrelated row defect.
        analyzer.parse(a)
        analyzer.parse(b)
        cases[name] = (a + b, expected)

    add("packet_after_final", alter(left, "BEGIN", "final", 1), right, "packet follows final")
    frequency = second["begin"]["frequency"]
    changed = alter(right, "BEGIN", "frequency", frequency - 1 if frequency > 1 else 2)
    changed = alter(changed, "BEGIN", "final", 1)
    add("changed_frequency", left, changed, "frequency changed")
    start = first["begin"]["start_tick"]
    overlap = alter(alter(right, "BEGIN", "start_tick", start), "BEGIN", "end_tick", start + second["begin"]["wall_ticks"])
    if first["begin"]["end_tick"] > start:
        add("overlapping_windows", left, overlap, "overlapping/backward")
    interval = second["begin"]["interval"] + 1
    changed = re.sub(r"(?<!\S)interval=[0-9]+", "interval=" + str(interval), right)
    changed = alter(changed, "REPORT_COST", "reports", interval)
    changed = alter(changed, "REPORT_COST", "attempts", min(analyzer.MAX_INT, interval + second["report_cost"]["failures_lifetime"]))
    add("interval_gap", left, changed, "nonconsecutive/duplicate intervals")
    if first["state"]["updates"] and second["state"]["updates"]:
        changed = alter(alter(right, "STATE", "first_update_id", second["state"]["first_update_id"] + 1), "STATE", "last_update_id", second["state"]["last_update_id"] + 1)
        add("update_id_gap", left, changed, "nonconsecutive update ids")
    for key in ("ignored_threads_lifetime", "reentrant_updates_lifetime"):
        add("backward_" + key, alter(left, "STATE", key, second["state"][key] + 1), right, "lifetime state counter moved backwards")
    if second["state"]["max_depth_lifetime"] < 64:
        add("backward_max_depth", alter(left, "STATE", "max_depth_lifetime", 64), right, "lifetime state counter moved backwards")
    invalid_left = alter(alter(left, "STATE", "measurement_invalid", 1), "REPORT_COST", "measurement_invalid", 1)
    invalid_right = alter(alter(right, "STATE", "measurement_invalid", 1), "REPORT_COST", "measurement_invalid", 1)
    for key in ("depth_overflows_lifetime", "unbalanced_scopes_lifetime", "scope_failures_lifetime"):
        add("backward_" + key, alter(invalid_left, "STATE", key, second["state"][key] + 1), invalid_right, "lifetime state counter moved backwards")
    failures = second["report_cost"]["failures_lifetime"] + 1
    failed_left = alter(invalid_left, "REPORT_COST", "failures_lifetime", failures)
    failed_left = alter(failed_left, "REPORT_COST", "attempts", first["report_cost"]["reports"] + failures)
    add("backward_report_failure_footer", failed_left, invalid_right, "lifetime report failure counter moved backwards")
    failed_left = alter(failed_left, "STATE", "report_failures_lifetime", failures)
    add("backward_report_failure_state", failed_left, invalid_right, "lifetime state counter moved backwards")
    changed = left
    total = second["report_cost"]["lifetime_ticks_before_footers"] + 1
    for key in ("ticks_before_footer", "lifetime_ticks_before_footers", "lifetime_max_ticks"):
        changed = alter(changed, "REPORT_COST", key, total)
    add("backward_report_ticks", changed, right, "lifetime counter moved backwards: lifetime_ticks_before_footers")
    if second["report_cost"]["lifetime_max_ticks"] < second["report_cost"]["lifetime_ticks_before_footers"]:
        changed = left
        maximum = second["report_cost"]["lifetime_max_ticks"] + 1
        for key in ("ticks_before_footer", "lifetime_ticks_before_footers", "lifetime_max_ticks"):
            changed = alter(changed, "REPORT_COST", key, maximum)
        add("backward_report_maximum", changed, right, "lifetime counter moved backwards: lifetime_max_ticks")
    if not second["state"]["measurement_invalid"]:
        add("lost_sticky_invalidity", invalid_left, right, "sticky measurement invalidity disappeared")
    if first["report_cost"]["failures_lifetime"] == second["report_cost"]["failures_lifetime"]:
        changed = alter(right, "REPORT_COST", "lifetime_ticks_before_footers", second["report_cost"]["lifetime_ticks_before_footers"] + 1)
        changed = alter(changed, "REPORT_COST", "lifetime_max_ticks", second["report_cost"]["lifetime_max_ticks"] + 1)
        add("report_lifetime_delta", left, changed, "report lifetime delta inconsistent")
        if second["report_cost"]["lifetime_max_ticks"] < second["report_cost"]["lifetime_ticks_before_footers"]:
            changed = alter(right, "REPORT_COST", "lifetime_max_ticks", second["report_cost"]["lifetime_max_ticks"] + 1)
            add("report_maximum_delta", left, changed, "report maximum changed")
    return cases


def verify_summary(analyzer, packets, summary):
    """Independently check weighted arithmetic, raw retention and unused nulls."""
    invalid = any(p["report_cost"]["measurement_invalid"] for p in packets)
    require(summary["structurally_valid"] is True, "structural validity missing")
    require(summary["measurement_invalid"] is invalid, "summary lost invalidity")
    require(summary["performance_interpretation_allowed"] is (not invalid), "invalid performance interpretation")
    require(summary["packets"] == packets, "summary lost authoritative raw packets")
    require(summary["final_seen"] is bool(packets[-1]["begin"]["final"]), "summary lost final status")
    require(summary["capture_complete"] is (bool(packets[-1]["begin"]["final"]) and packets[0]["begin"]["interval"] == 1), "partial capture misrepresented")
    frequency = packets[0]["begin"]["frequency"]
    for name in analyzer.COHORTS:
        cohort = summary["cohorts"][name]
        records = [p["cohorts"][name] for p in packets]
        samples = sum(r["valid_samples"] for r in records)
        require(cohort["valid_selected_update_denominator"] == samples, "valid sample denominator mismatch")
        for key in analyzer.FIELDS["COHORT"]:
            if key not in ("interval", "name"):
                expected = max(r[key] for r in records) if key == "clock_max_ticks" else sum(r[key] for r in records)
                require(cohort["raw"][key] == expected, "cohort raw aggregation mismatch: " + key)
        ticks = sum(r["clock_ticks"] for r in records)
        require(cohort["clock_mean_ticks_per_pair"] == (ticks / samples if samples and not invalid else None), "clock pair denominator mismatch")
        scene_records = [p["scenes"][name] for p in packets]
        used = [r for r in scene_records if r["samples"]]
        for key in ("samples", "day_samples", "night_samples", *analyzer.CHANGES):
            require(cohort["scene"][key] == sum(r[key] for r in scene_records), "scene counter aggregation mismatch")
        for key in analyzer.SCENE_IDS:
            expected = (used[0] if key.endswith("_first") else used[-1])[key] if used else 0
            require(cohort["scene"][key] == expected, "scene endpoint mismatch")
        for prefix in analyzer.RANGES:
            for suffix in analyzer.SUFFIXES:
                key = prefix + suffix
                if not used:
                    expected = None
                elif suffix in ("_first", "_last"):
                    expected = (used[0] if suffix == "_first" else used[-1])[key]
                else:
                    expected = (min if suffix == "_min" else max)(r[key] for r in used)
                require(cohort["scene"][key] == expected, "scene range aggregation mismatch: " + key)
        for metric_id, label in enumerate(analyzer.LABELS):
            metric = cohort["scopes"][label]
            records = [p["scopes"][name][label] for p in packets]
            calls = sum(r["calls"] for r in records)
            require(metric["id"] == metric_id and metric["calls"] == calls, "metric identity/call sum mismatch")
            for mode in ("inclusive", "exclusive"):
                ticks = sum(r[mode + "_ticks"] for r in records)
                mean = ticks / calls if calls and not invalid else None
                per_update = ticks / samples if calls and not invalid else None
                require(metric[mode + "_ticks"] == ticks, "metric raw tick sum mismatch")
                require(metric["max_" + mode + "_ticks"] == max(r["max_" + mode + "_ticks"] for r in records), "metric maximum mismatch")
                require(metric["mean_" + mode + "_ticks_per_call"] == mean, "weighted call mean mismatch")
                require(metric[mode + "_ticks_per_valid_selected_update"] == per_update, "valid sample mean mismatch")
                require(metric["mean_" + mode + "_microseconds_per_call"] == (mean * 1_000_000 / frequency if mean is not None else None), "call unit conversion mismatch")
                require(metric[mode + "_microseconds_per_valid_selected_update"] == (per_update * 1_000_000 / frequency if per_update is not None else None), "sample unit conversion mismatch")


def verify_capture(text, packets):
    """Every source line remains either a raw profiler row or contextual evidence."""
    captured = {}

    def keep(number, line):
        require(type(number) is int and number > 0 and number not in captured, "duplicate/invalid source line")
        captured[number] = line

    for packet in packets:
        rows = packet["raw"].splitlines()
        require(len(rows) == len(packet["source_lines"]), "raw row/source line mismatch")
        require(packet["source_lines"] == sorted(packet["source_lines"]), "raw source order changed")
        for number, line in zip(packet["source_lines"], rows):
            keep(number, line)
        for entry in packet["context"]:
            keep(entry["line"], entry["text"])
    for entry in packets[-1]["trailing_context"]:
        keep(entry["line"], entry["text"])
    require(captured == dict(enumerate(text.splitlines(), 1)), "raw/context evidence was changed or dropped")


def run_proof(args):
    root, candidate, output = args.fixtures.resolve(), args.candidate.resolve(), args.output.resolve()
    analyzer_path = Path(__file__).resolve().parents[1] / "analyze_update_profile.py"
    spec = importlib.util.spec_from_file_location("update56_analyzer", analyzer_path)
    require(spec is not None and spec.loader is not None, "analyzer import unavailable")
    analyzer = importlib.util.module_from_spec(spec)
    analyzer_hash = digest(analyzer_path)
    proof_hash = digest(Path(__file__))
    spec.loader.exec_module(analyzer)
    require(analyzer.SCHEMA["version"] == 56 and analyzer.SCHEMA["schema"] == "update_costs" and analyzer.SCHEMA["sample_denominator"] == 64 and len(analyzer.LABELS) == 19, "wrong analyzer/schema contract")
    manifest_path = root / "manifest.json"
    manifest_bytes = manifest_path.read_bytes()
    manifest_hash = hashlib.sha256(manifest_bytes).hexdigest()
    manifest = json.loads(manifest_bytes, object_pairs_hook=unique_object)
    require(type(manifest) is dict and set(manifest) == {"schema", "candidate_sha256", "entries"}, "unexpected fixture manifest fields")
    require(manifest["schema"] == "update56-report-fixtures-v1", "wrong fixture manifest schema")
    candidate_hash = digest(candidate)
    require(type(manifest["candidate_sha256"]) is str and re.fullmatch(r"[0-9a-f]{64}", manifest["candidate_sha256"]) and manifest["candidate_sha256"] == candidate_hash, "fixture candidate hash mismatch")
    require(type(manifest["entries"]) is list and manifest["entries"], "no actual runtime fixture entries")
    parsed, fixtures, seen, inodes = [], [], set(), set()
    protected = {candidate, analyzer_path, analyzer.SCHEMA_PATH.resolve(), manifest_path.resolve(), Path(__file__).resolve()}
    for entry in manifest["entries"]:
        require(type(entry) is dict and set(entry) == {"path", "structurally_valid", "measurement_valid", "scenario"}, "unexpected manifest entry fields")
        require(type(entry["structurally_valid"]) is bool and type(entry["measurement_valid"]) is bool, "manifest flags must be boolean")
        require(entry["structurally_valid"] or not entry["measurement_valid"], "malformed fixture cannot be measurement valid")
        require(type(entry["scenario"]) is str and entry["scenario"].strip(), "missing fixture scenario")
        path = confined(root, entry["path"])
        inode = (path.stat().st_dev, path.stat().st_ino)
        require(path not in seen and inode not in inodes, "duplicate/aliased report fixture")
        seen.add(path)
        inodes.add(inode)
        protected.add(path)
        text = path.read_text(encoding="utf-8")
        reason = None
        try:
            packets = analyzer.parse(text)
        except analyzer.ParseError as error:
            require(not entry["structurally_valid"], "actual emitted report unexpectedly rejected: " + entry["path"] + ": " + str(error))
            reason = str(error)
        else:
            require(entry["structurally_valid"], "malformed emitted fixture accepted: " + entry["path"])
            verify_capture(text, packets)
            summary = analyzer.summarize(packets)
            verify_summary(analyzer, packets, summary)
            require(summary["measurement_invalid"] is (not entry["measurement_valid"]), "actual report invalidity differs from manifest: " + entry["path"])
            parsed.append((entry["path"], packets))
        fixtures.append({**entry, "sha256": digest(path), "reason": reason})
    require(output not in protected and not any(output.exists() and output.samefile(p) for p in protected), "proof output aliases an input")
    all_packets = [p for _, packets in parsed for p in packets]
    healthy = [(name, p) for name, packets in parsed for p in packets if not p["report_cost"]["measurement_invalid"] and p["state"]["eligible"]]
    require(healthy, "need actual healthy eligible reports")
    require(any(not entry["structurally_valid"] for entry in fixtures), "need actual malformed writer-failure fixture")
    require(any(not p["state"]["updates"] for p in all_packets), "need actual empty report for unused ranges")
    require(any(p["report_cost"]["measurement_invalid"] for p in all_packets), "need actual invalid diagnostic report")
    require(any(p["begin"]["final"] for p in all_packets) and any(not p["begin"]["final"] for p in all_packets), "need actual periodic and final reports")
    for name in analyzer.COHORTS:
        require(any(p["cohorts"][name]["valid_samples"] for _, p in healthy), "missing actual healthy cohort: " + name)
        for label in analyzer.LABELS:
            require(any(p["scopes"][name][label]["calls"] for _, p in healthy), "missing actual sampled metric: " + name + "/" + label)
    for key in ("guide_type", "available_recipes", "net_mode", "world_width", "world_height"):
        require(any(p["scenes"][name][key + "_min"] is not None and p["scenes"][name][key + "_min"] < p["scenes"][name][key + "_max"] for _, p in healthy for name in analyzer.COHORTS), "need actual changing " + key + " range")
    require(any(p["scenes"][name]["guide_type_min"] == -1 for _, p in healthy for name in analyzer.COHORTS), "need actual null guide sentinel")
    no_body_other_work = [(p, name) for _, p in healthy for name in analyzer.COHORTS
                          if p["cohorts"][name]["valid_samples"]
                          and p["cohorts"][name]["no_body_samples"] == p["cohorts"][name]["valid_samples"]
                          and p["scopes"][name]["main_thread_actions"]["calls"]]
    require(no_body_other_work, "need actual all-no-body sample with other timed root work")
    repeated_body = [(p, name) for _, p in healthy for name in analyzer.COHORTS
                     if p["scopes"][name]["do_update"]["calls"] > p["cohorts"][name]["valid_samples"] - p["cohorts"][name]["no_body_samples"]]
    require(repeated_body, "need actual repeated body scopes")
    nested_body = [(p, name) for _, p in healthy for name in analyzer.COHORTS
                   if p["scopes"][name]["do_update"]["inclusive_ticks"] > p["scopes"][name]["update"]["inclusive_ticks"]]
    require(nested_body, "need actual same-ID nested body inclusive overlap exceeding root total")
    for key in ("world_width", "world_height"):
        require(any(p["scenes"][name][key + "_min"] == 0 for _, p in healthy for name in analyzer.COHORTS), "need actual zero " + key + " context")
    source_name, source = max(healthy, key=lambda item: sum(m["calls"] for scopes in item[1]["scopes"].values() for m in scopes.values()))
    multipacket = [(name, packets) for name, packets in parsed if len(packets) >= 2 and all(p["state"]["updates"] for p in packets[:2])]
    require(multipacket, "need actual nonempty multi-packet continuity fixture")
    continuity_name, continuity_packets = max(multipacket, key=lambda item: sum(bool(p["state"]["updates"]) for p in item[1][:2]))
    cases = {name: (text, None) for name, text in adversaries(analyzer, source).items()}
    cases.update(continuity_adversaries(analyzer, continuity_packets))
    # Empty-cohort ranges are checked against an actual empty report, not fake
    # zero records. Every added value makes that unused range malformed.
    empty = next(p for p in all_packets if not p["state"]["updates"])
    for occurrence, name in enumerate(analyzer.COHORTS):
        for key in analyzer.RANGES:
            cases["empty_" + name + "_" + key] = (alter(empty["raw"], "SCENE", key + "_first", 0, occurrence), None)
    # Complete-body absence is legal, but it cannot hide donated body calls.
    no_body_packet, no_body_cohort = no_body_other_work[0]
    body_offset = analyzer.COHORTS.index(no_body_cohort) * len(analyzer.LABELS) + 1
    cases["no_body_with_zero_tick_completed_body"] = (alter(no_body_packet["raw"], "SCOPE", "calls", 1, body_offset), "all-no-body samples contain body calls")
    cases["no_body_repaired_as_nonempty"] = (alter(no_body_packet["raw"], "COHORT", "no_body_samples", 0, analyzer.COHORTS.index(no_body_cohort)), "body calls omit nonempty samples")
    for occurrence, name in enumerate(analyzer.COHORTS):
        for metric_id, label in enumerate(analyzer.LABELS[1:], 1):
            cases["inactive_" + name + "_" + label] = (alter(empty["raw"], "SCOPE", "calls", 1, occurrence * len(analyzer.LABELS) + metric_id), None)
    output.parent.mkdir(parents=True, exist_ok=True)
    derived = output.parent / (output.stem + "-adversaries")
    derived.mkdir(exist_ok=True)
    rejections = []
    for name, (text, expected_reason) in cases.items():
        path = derived / (name + ".log")
        require(path.resolve() not in protected and not path.is_symlink() and not any(path.exists() and path.samefile(p) for p in protected), "adversary output aliases an input")
        try:
            analyzer.parse(text)
        except analyzer.ParseError as error:
            reason = str(error)
            require(expected_reason is None or expected_reason in reason, "wrong continuity rejection for " + name + ": " + reason)
        else:
            raise ProofError("derived corruption accepted: " + name)
        path.write_text(text, encoding="utf-8")
        rejections.append({"name": name, "path": str(path.relative_to(output.parent)), "sha256": digest(path), "reason": reason})
    # Summary arithmetic is checked only with equal-frequency raw records.
    # Independent fixture packets are not presented as a contiguous capture.
    by_frequency = {}
    for _, p in healthy:
        by_frequency.setdefault(p["begin"]["frequency"], []).append(p)
    for packets in by_frequency.values():
        verify_summary(analyzer, packets, analyzer.summarize(packets))
    invalid = next(p for p in all_packets if p["report_cost"]["measurement_invalid"])
    mixed = [p for _, p in healthy if p["begin"]["frequency"] == invalid["begin"]["frequency"]] + [invalid]
    verify_summary(analyzer, mixed, analyzer.summarize(mixed))
    cli_receipts = []
    with tempfile.TemporaryDirectory(prefix="update56-parser-cli-", dir=output.parent) as temporary:
        temporary = Path(temporary)
        runs = [(confined(root, f["path"]), f["path"], f["structurally_valid"], f["measurement_valid"]) for f in fixtures]
        runs.extend((output.parent / r["path"], r["path"], False, False) for r in rejections)
        for index, (path, label, structural, measurement) in enumerate(runs):
            destination = temporary / (str(index) + ".json")
            process = subprocess.run([sys.executable, str(analyzer_path), str(path), "--output", str(destination)], capture_output=True, text=True, timeout=60)
            if structural:
                require(process.returncode == 0 and destination.is_file(), "CLI rejected actual runtime fixture: " + label + ": " + process.stderr)
                summary = json.loads(destination.read_text(encoding="utf-8"))
                require(summary["structurally_valid"] is True and summary["measurement_invalid"] is (not measurement), "CLI fixture classification mismatch")
                verify_summary(analyzer, analyzer.parse(path.read_text(encoding="utf-8")), summary)
            else:
                require(process.returncode == 2 and not destination.exists(), "CLI accepted corrupt input or wrote rejected output: " + label)
            cli_receipts.append({"path": label, "exit_code": process.returncode, "sha256": digest(path), "stderr": process.stderr})
        source_text = confined(root, source_name).read_text(encoding="utf-8")
        stdin_run = subprocess.run([sys.executable, str(analyzer_path), "-"], input=source_text, capture_output=True, text=True, timeout=60)
        require(stdin_run.returncode == 0 and json.loads(stdin_run.stdout) == analyzer.summarize(analyzer.parse(source_text)), "stdin CLI differs from file parsing")
        context_text = "host context before capture\n\n" + "\n".join(row + "\nhost context after row " + str(index) for index, row in enumerate(source_text.splitlines())) + "\ntrailing host context\n"
        context_packets = analyzer.parse(context_text)
        verify_capture(context_text, context_packets)
        require([p["raw"] for p in context_packets] == [p["raw"] for p in analyzer.parse(source_text)], "context changed emitted profiler rows")
        context_run = subprocess.run([sys.executable, str(analyzer_path), "-"], input=context_text, capture_output=True, text=True, timeout=60)
        require(context_run.returncode == 0 and json.loads(context_run.stdout) == analyzer.summarize(context_packets), "contextual CLI capture differs from parser")
        preserved = temporary / "preserved.log"
        preserved.write_text(source_text, encoding="utf-8")
        original_hash = digest(preserved)
        alias_run = subprocess.run([sys.executable, str(analyzer_path), str(preserved), "--output", str(preserved)], capture_output=True, text=True, timeout=60)
        require(alias_run.returncode == 2 and digest(preserved) == original_hash, "analyzer overwrote raw input")
        for alias_kind in ("hardlink", "symlink"):
            alias = temporary / (alias_kind + ".log")
            if alias_kind == "hardlink":
                alias.hardlink_to(preserved)
            else:
                alias.symlink_to(preserved)
            alias_process = subprocess.run([sys.executable, str(analyzer_path), str(preserved), "--output", str(alias)], capture_output=True, text=True, timeout=60)
            require(alias_process.returncode == 2 and digest(preserved) == original_hash, "analyzer overwrote raw input through " + alias_kind)
        unicode_path = temporary / "invalid-utf8.log"
        unicode_path.write_bytes(source_text.encode("utf-8") + b"\xff")
        unicode_output = temporary / "invalid-utf8.json"
        unicode_run = subprocess.run([sys.executable, str(analyzer_path), str(unicode_path), "--output", str(unicode_output)], capture_output=True, text=True, timeout=60)
        require(unicode_run.returncode == 2 and not unicode_output.exists(), "non-UTF8 capture silently repaired")
    for fixture in fixtures:
        require(digest(confined(root, fixture["path"])) == fixture["sha256"], "fixture changed during proof: " + fixture["path"])
    require(digest(candidate) == candidate_hash, "candidate changed during proof")
    require(digest(manifest_path) == manifest_hash, "fixture manifest changed during proof")
    require(digest(analyzer_path) == analyzer_hash and digest(analyzer.SCHEMA_PATH) == hashlib.sha256(analyzer.SCHEMA_BYTES).hexdigest(), "analyzer/schema changed during proof")
    require(digest(Path(__file__)) == proof_hash, "proof source changed during execution")
    result = {"passed": True, "schema": "update56-report-proof-v1", "candidate_sha256": candidate_hash,
              "analyzer_sha256": analyzer_hash, "schema_sha256": hashlib.sha256(analyzer.SCHEMA_BYTES).hexdigest(),
              "proof_sha256": proof_hash, "manifest_sha256": manifest_hash,
              "actual_fixtures": len(fixtures), "actual_structurally_valid": len(parsed),
              "actual_measurement_valid": sum(f["measurement_valid"] for f in fixtures),
              "actual_structurally_rejected": sum(not f["structurally_valid"] for f in fixtures),
              "actual_measurement_invalid": sum(f["structurally_valid"] and not f["measurement_valid"] for f in fixtures),
              "actual_packets": len(all_packets), "derived_rejected": len(rejections),
              "actual_no_body_other_work": len(no_body_other_work), "actual_repeated_body": len(repeated_body),
              "actual_nested_body_overlap": len(nested_body), "actual_metric_cohort_pairs": len(analyzer.COHORTS) * len(analyzer.LABELS),
              "cli_runs": len(cli_receipts) + 6, "fixtures": fixtures, "rejections": rejections, "cli_receipts": cli_receipts,
              "derived_source": source_name, "derived_source_packet_sha256": hashlib.sha256(source["raw"].encode()).hexdigest(),
              "continuity_source": continuity_name,
              "command": [sys.executable, str(Path(__file__).resolve()), "--fixtures", str(root), "--candidate", str(candidate), "--output", str(output)],
              "limits": "Positive evidence is actual candidate runtime output pinned by manifest and candidate hash. Derived corruptions test rejection, not runtime behavior. Independent equal-frequency packets are aggregated only to verify arithmetic. No per-entity, GPU, whole-game, host or Switch performance claim; updates are not rendered frames."}
    output.write_text(json.dumps(result, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    print(f"UPDATE56 STRICT REPORT PROOF PASS fixtures={len(fixtures)} packets={len(all_packets)} rejected={len(rejections)} cli_runs={len(cli_receipts) + 6}")
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixtures", required=True, type=Path, help="actual runtime report-fixtures directory containing manifest.json")
    parser.add_argument("--candidate", required=True, type=Path, help="exact emitted candidate assembly")
    parser.add_argument("--output", required=True, type=Path, help="reproducible JSON proof receipt")
    args = parser.parse_args(argv)
    return run_proof(args)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, UnicodeError, ValueError, subprocess.SubprocessError) as error:
        print("update56 report proof: " + str(error), file=sys.stderr)
        raise SystemExit(1)
