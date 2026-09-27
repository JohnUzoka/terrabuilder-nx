#!/usr/bin/env python3
"""Prove strict53 parsing against emitted-runtime reports and derived corruptions."""
import argparse
import hashlib
import importlib.util
import json
import re
import subprocess
import sys
import tempfile
from pathlib import Path


def require(condition, message):
    if not condition:
        raise ValueError(message)


def confined(root, relative):
    require(isinstance(relative, str) and relative and not Path(relative).is_absolute(), "fixture name must be relative")
    path = (root / relative).resolve()
    require(path.is_relative_to(root) and path != root and path.is_file(), "missing/escaping fixture: " + relative)
    return path


def alter(text, kind, key, value, occurrence=0):
    rows = text.splitlines()
    matches = [index for index, row in enumerate(rows) if row.startswith("NX_PROFILE " + kind + " ")]
    require(occurrence < len(matches), "missing mutation row: " + kind)
    index = matches[occurrence]
    rows[index], count = re.subn(r"(?<!\S)" + re.escape(key) + r"=\S+", key + "=" + str(value), rows[index])
    require(count == 1, "missing/duplicate mutation key: " + key)
    return "\n".join(rows) + "\n"


def adversaries(analyzer, packet):
    """Corrupt an actual parsed packet; never synthesize a positive report."""
    text = packet["raw"]
    rows = text.splitlines()
    cases = {}

    def bad(name, kind, key, value, occurrence=0):
        cases[name] = alter(text, kind, key, value, occurrence)

    for index, row in enumerate(rows):
        kind = row.split()[1].lower()
        cases[f"missing_{index:02d}_{kind}"] = "\n".join(rows[:index] + rows[index + 1:]) + "\n"
        cases[f"duplicate_{index:02d}_{kind}"] = "\n".join(rows[:index] + [row] + rows[index:]) + "\n"
    cases["empty"] = "ordinary non-profiler log\n"
    cases["partial_token"] = text.replace("version=53", "version=", 1)
    cases["partial_profile_prefix"] = text + "NX_PROFILE\n"
    cases["malformed_prefixed_row"] = "prefix " + text
    cases["indented_row"] = " " + text
    cases["extra_unknown_row"] = text + "NX_PROFILE OTHER interval=1\n"
    cases["extra_field"] = text.replace(rows[-1], rows[-1] + " unexpected=1", 1)
    cases["duplicate_field"] = text.replace("version=53", "version=53 version=53", 1)
    cases["missing_field"] = text.replace(" version=53", "", 1)
    words = rows[0].split()
    words[2], words[3] = words[3], words[2]
    cases["field_order"] = " ".join(words) + "\n" + "\n".join(rows[1:]) + "\n"
    shuffled = rows.copy()
    shuffled[1], shuffled[2] = shuffled[2], shuffled[1]
    cases["row_order"] = "\n".join(shuffled) + "\n"
    cases["duplicate_packet"] = text + text
    cases["trailing_partial_packet"] = text + rows[0] + "\n"
    for value, name in ((49, "old49"), (51, "old51"), (52, "old52"), (54, "future")):
        bad(name + "_version", "BEGIN", "version", value)
    for value, name in ((-1, "negative"), ("+1", "plus"), ("-0", "negative_zero"), ("01", "leading_zero"),
                        ("1.0", "decimal"), (1 << 63, "overflow"), ("9" * 100, "huge_overflow")):
        bad(name + "_counter", "BATCH_STATE", "clock_calls", value)
    for kind, key, value in (("BEGIN", "schema", "tile_helpers"), ("BEGIN", "sample_denominator", 16),
                             ("BEGIN", "frequency", 0), ("BEGIN", "interval", 0), ("BEGIN", "final", 2),
                             ("STATE", "measurement_invalid", 2), ("REPORT_COST", "measurement_invalid", 2),
                             ("PASS", "layer", "other"), ("METRIC", "label", "other"),
                             ("BATCH_METRIC", "label", "other"), ("BATCH_STATE", "sample_denominator", 128)):
        bad(kind.lower() + "_" + key, kind, key, value)
    bad("interval_mismatch", "END", "interval", packet["begin"]["interval"] + 1)
    bad("wall_mismatch", "BEGIN", "wall_ticks", packet["begin"]["wall_ticks"] + 1)
    cases["backward_window"] = alter(alter(text, "BEGIN", "start_tick", 2), "BEGIN", "end_tick", 1)
    cases["short_nonfinal"] = alter(alter(alter(text, "BEGIN", "final", 0), "BEGIN", "wall_ticks", 0), "BEGIN", "end_tick", packet["begin"]["start_tick"])
    state, scene = packet["state"], packet["scene"]
    for key, value in (("completed", state["frames"] + 1), ("eligible", state["completed"] + 1),
                       ("excluded", state["excluded"] + 1), ("state_changed", state["frames"] + 1),
                       ("snapshot_calls", state["snapshot_calls"] + 1), ("snapshot_max_ticks", state["snapshot_ticks"] + 1),
                       ("union_flags", 1024), ("first_frame_id", 0), ("last_frame_id", state["last_frame_id"] + 1),
                       ("menu", state["frames"] + 1), ("capture_failure_frames", -1)):
        bad("state_" + key, "STATE", key, value)
    cases["unflagged_capture_failure"] = alter(alter(alter(text, "STATE", "capture_failure_frames", 1), "STATE", "measurement_invalid", 0), "REPORT_COST", "measurement_invalid", 0)
    cases["invalidity_disappears"] = alter(alter(text, "STATE", "measurement_invalid", 1), "REPORT_COST", "measurement_invalid", 0)
    for key, value in (("samples", scene["samples"] + 1), ("day_samples", scene["day_samples"] + 1),
                       ("player_id_changes", scene["samples"] + 1), ("camera_x_first", "nan"),
                       ("camera_x_last", "1e9999"), ("player_x_min", scene["player_x_max"] + 1),
                       ("zoom_min", 0), ("width_first", 0.5), ("player_y_first", "null")):
        bad("scene_" + key, "SCENE", key, value)
    p = packet["passes"]["solid"]
    for key, value in (("completed_passes", p["passes"] + 1), ("invalid_passes", p["passes"] + 1),
                       ("calls", p["eligible"] + 1), ("eligible", p["visited"] + 1), ("selected", p["calls"] + 1),
                       ("completed_samples", p["completed_samples"] + 1), ("discarded_samples", p["discarded_samples"] + 1),
                       ("completed_loops", p["completed_passes"] + 1), ("sample_ticks", p["loop_ticks"] + 1),
                       ("loop_ticks", p["pass_ticks"] + 1), ("pass_max_ticks", p["pass_ticks"] + 1),
                       ("calls", 1 << 63)):
        bad("pass_" + key + "_" + str(value), "PASS", key, value)
    for occurrence, label in enumerate(analyzer.LABELS):
        metric = packet["metrics"]["solid"][label]
        bad("metric_max_" + label, "METRIC", "max_ticks", metric["total_ticks"] + 1, occurrence)
        bad("metric_bound_" + label, "METRIC", "operations", p["valid_samples"] * 2 + 1, occurrence)
        bad("metric_negative_" + label, "METRIC", "total_ticks", -1, occurrence)
        if metric["total_ticks"]:
            bad("metric_unused_ticks_" + label, "METRIC", "operations", 0, occurrence)
    bad("duplicate_layer", "PASS", "layer", "solid", 1)
    bad("duplicate_helper_label", "METRIC", "label", analyzer.LABELS[0], 1)
    bad("duplicate_batch_label", "BATCH_METRIC", "label", analyzer.BATCH_LABELS[0], 1)
    batch = packet["batch_state"]
    for key, value in (("frames", batch["frames"] + 1), ("valid_frames", batch["valid_frames"] + 1),
                       ("discarded_frames", batch["discarded_frames"] + 1), ("clock_calls", 0),
                       ("aborted_scopes", -1), ("observer_failures", -1)):
        bad("batch_" + key, "BATCH_STATE", key, value)
    no_discard = alter(alter(text, "BATCH_STATE", "discarded_frames", 0), "BATCH_STATE", "valid_frames", batch["frames"])
    cases["batch_abort_without_discard"] = alter(no_discard, "BATCH_STATE", "aborted_scopes", 1)
    cases["batch_failure_without_discard"] = alter(no_discard, "BATCH_STATE", "observer_failures", 1)
    discarded = alter(alter(text, "BATCH_STATE", "valid_frames", 0), "BATCH_STATE", "discarded_frames", batch["frames"])
    cases["batch_invalid_frames_donate"] = discarded
    failed = alter(discarded, "BATCH_STATE", "observer_failures", 1)
    for occurrence in range(len(analyzer.BATCH_LABELS)):
        for key in ("calls", "inclusive_ticks", "exclusive_ticks", "inclusive_max_ticks", "exclusive_max_ticks"):
            failed = alter(failed, "BATCH_METRIC", key, 0, occurrence)
    no_invalid = alter(alter(failed, "STATE", "measurement_invalid", 0), "REPORT_COST", "measurement_invalid", 0)
    cases["batch_failure_without_invalidity"] = no_invalid
    with_invalid = alter(alter(failed, "STATE", "measurement_invalid", 1), "REPORT_COST", "measurement_invalid", 1)
    cases["batch_failure_frames_over_discarded"] = alter(with_invalid, "BATCH_STATE", "observer_failures", batch["frames"] + 1)
    for occurrence, label in enumerate(analyzer.BATCH_LABELS):
        metric = packet["batch_metrics"][label]
        for mode in ("inclusive", "exclusive"):
            bad(label + "_" + mode + "_max_over_total", "BATCH_METRIC", mode + "_max_ticks", metric[mode + "_ticks"] + 1, occurrence)
            bad(label + "_" + mode + "_total_over_bound", "BATCH_METRIC", mode + "_ticks", metric["calls"] * metric[mode + "_max_ticks"] + 1, occurrence)
            bad(label + "_" + mode + "_negative", "BATCH_METRIC", mode + "_ticks", -1, occurrence)
        bad(label + "_exclusive_over_inclusive", "BATCH_METRIC", "exclusive_ticks", metric["inclusive_ticks"] + 1, occurrence)
        bad(label + "_exclusive_max_over_inclusive", "BATCH_METRIC", "exclusive_max_ticks", metric["inclusive_max_ticks"] + 1, occurrence)
        bad(label + "_calls_overflow", "BATCH_METRIC", "calls", 1 << 63, occurrence)
        if metric["inclusive_ticks"]:
            bad(label + "_unused_ticks", "BATCH_METRIC", "calls", 0, occurrence)
    cost = packet["report_cost"]
    bad("report_attempt_identity", "REPORT_COST", "attempts", 0)
    bad("report_counter", "REPORT_COST", "reports", cost["reports"] + 1)
    bad("report_max", "REPORT_COST", "lifetime_max_ticks", cost["lifetime_ticks_before_footers"] + 1)
    bad("report_body", "REPORT_COST", "ticks_before_footer", cost["lifetime_max_ticks"] + 1)
    return cases


def continuity_adversaries(packets):
    first, second = packets[:2]
    left, right = first["raw"], second["raw"]
    cases = {"packet_after_final": alter(left, "BEGIN", "final", 1) + right}
    frequency = second["begin"]["frequency"]
    cases["changed_frequency"] = left + alter(right, "BEGIN", "frequency", frequency - 1 if frequency > 1 else 2)
    start = first["begin"]["start_tick"]
    overlap = alter(alter(right, "BEGIN", "start_tick", start), "BEGIN", "end_tick", start + second["begin"]["wall_ticks"])
    cases["overlapping_windows"] = left + overlap
    for name, interval in (("interval_gap", second["begin"]["interval"] + 1), ("duplicate_interval", first["begin"]["interval"])):
        changed = re.sub(r"(?<!\S)interval=[0-9]+", "interval=" + str(interval), right)
        changed = alter(changed, "REPORT_COST", "reports", interval)
        changed = alter(changed, "REPORT_COST", "attempts", max(second["report_cost"]["attempts"], interval + second["report_cost"]["failures_lifetime"]))
        cases[name] = left + changed
    if first["state"]["frames"] and second["state"]["frames"]:
        shifted = alter(alter(right, "STATE", "first_frame_id", second["state"]["first_frame_id"] + 1), "STATE", "last_frame_id", second["state"]["last_frame_id"] + 1)
        cases["frame_id_gap"] = left + shifted
    for key in ("ignored_threads_lifetime", "reentrant_frames_lifetime"):
        cases["backward_" + key] = alter(left, "STATE", key, second["state"][key] + 1) + right
    cases["backward_attempts"] = alter(left, "REPORT_COST", "attempts", second["report_cost"]["attempts"] + 1) + right
    if not second["report_cost"]["measurement_invalid"]:
        cases["lost_sticky_invalidity"] = alter(alter(left, "STATE", "measurement_invalid", 1), "REPORT_COST", "measurement_invalid", 1) + right
    return cases


def verify_summary(analyzer, packets, summary):
    """Check observable arithmetic, maxima, denominator populations and unused nulls."""
    invalid = any(p["report_cost"]["measurement_invalid"] for p in packets)
    require(summary["measurement_invalid"] is invalid, "summary lost invalidity")
    require(summary["final_seen"] is bool(packets[-1]["begin"]["final"]), "summary lost final status")
    require(summary["packets"] == packets, "summary lost raw packet/state records")
    require(summary["performance_interpretation_allowed"] is (not invalid), "invalid performance interpretation")
    frequency = packets[0]["begin"]["frequency"]
    for layer in analyzer.LAYERS:
        raw = summary["layers"][layer]["raw"]
        samples = sum(p["passes"][layer]["valid_samples"] for p in packets)
        require(raw["valid_samples"] == samples, "sample denominator mismatch")
        for label, count_key in (("pass", "completed_passes"), ("loop", "completed_loops"), ("sample", "valid_samples")):
            timing = summary["layers"][layer]["timings"][label]
            ticks = sum(p["passes"][layer][label + "_ticks"] for p in packets)
            count = sum(p["passes"][layer][count_key] for p in packets)
            require(timing["total_ticks"] == ticks and timing["count"] == count, "pass timing sum mismatch")
            require(timing["mean_ticks"] == (ticks / count if count and not invalid else None), "pass timing mean mismatch")
        for label in analyzer.LABELS:
            metric = summary["layers"][layer]["metrics"][label]
            records = [p["metrics"][layer][label] for p in packets]
            operations, ticks = sum(m["operations"] for m in records), sum(m["total_ticks"] for m in records)
            mean = ticks / operations if operations and not invalid else None
            require(metric["operations"] == operations and metric["total_ticks"] == ticks, "helper raw sum mismatch")
            require(metric["max_ticks"] == max(m["max_ticks"] for m in records), "helper maximum mismatch")
            require(metric["mean_ticks_per_operation"] == mean, "helper weighted mean mismatch")
            require(metric["mean_microseconds_per_operation"] == (mean * 1_000_000 / frequency if mean is not None else None), "helper units mismatch")
            require(metric["ticks_per_valid_sample"] == (ticks / samples if operations and not invalid else None), "helper sample denominator mismatch")
    frames = sum(p["batch_state"]["valid_frames"] for p in packets)
    require(summary["batch"]["valid_frame_denominator"] == frames, "batch valid frame denominator mismatch")
    for label in analyzer.BATCH_LABELS:
        metric = summary["batch"]["metrics"][label]
        records = [p["batch_metrics"][label] for p in packets]
        calls = sum(m["calls"] for m in records)
        require(metric["calls"] == calls, "batch call sum mismatch")
        for mode in ("inclusive", "exclusive"):
            ticks = sum(m[mode + "_ticks"] for m in records)
            mean = ticks / calls if calls and not invalid else None
            per_frame = ticks / frames if calls and not invalid else None
            require(metric[mode + "_ticks"] == ticks, "batch tick sum mismatch")
            require(metric[mode + "_max_ticks"] == max(m[mode + "_max_ticks"] for m in records), "batch maximum mismatch")
            require(metric["mean_" + mode + "_ticks_per_call"] == mean, "batch weighted mean mismatch")
            require(metric[mode + "_ticks_per_valid_frame"] == per_frame, "batch frame mean mismatch")
            require(metric["mean_" + mode + "_microseconds_per_call"] == (mean * 1_000_000 / frequency if mean is not None else None), "batch units mismatch")
            require(metric[mode + "_microseconds_per_valid_frame"] == (per_frame * 1_000_000 / frequency if per_frame is not None else None), "batch frame units mismatch")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("analyzer", type=Path)
    parser.add_argument("proof_root", type=Path, help="RenderProof output containing actual runtime logs and report-manifest.json")
    parser.add_argument("--output", type=Path, help="proof JSON (default: PROOF_ROOT/render-parser-proof.json)")
    args = parser.parse_args(argv)
    root = args.proof_root.resolve()
    analyzer_path = args.analyzer.resolve()
    spec = importlib.util.spec_from_file_location("render53_analyzer", analyzer_path)
    analyzer = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(analyzer)
    require(analyzer.SCHEMA["version"] == 53 and analyzer.SCHEMA["schema"] == "render_split", "wrong schema")
    manifest_path = root / "report-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    require(set(manifest) == {"candidateSha256", "accepted", "rejected"}, "unexpected manifest fields")
    require(bool(re.fullmatch(r"[0-9a-f]{64}", manifest["candidateSha256"])), "invalid candidate hash")
    require(bool(manifest["accepted"]), "no actual runtime positive reports")
    parsed, seen = [], set()
    for kind in ("accepted", "rejected"):
        for item in manifest[kind]:
            require(set(item) == ({"file", "measurement_invalid", "final_seen"} if kind == "accepted" else {"file"}), "unexpected manifest entry fields")
            path = confined(root, item["file"])
            require(path not in seen, "duplicate/aliased report fixture")
            seen.add(path)
            text = path.read_text(encoding="utf-8")
            if kind == "accepted":
                require(type(item["measurement_invalid"]) is bool and type(item["final_seen"]) is bool, "manifest flags must be boolean")
                packets = analyzer.parse(text)
                summary = analyzer.summarize(packets)
                verify_summary(analyzer, packets, summary)
                require(summary["measurement_invalid"] is item["measurement_invalid"] and summary["final_seen"] is item["final_seen"], "actual report flag mismatch: " + item["file"])
                parsed.append((item["file"], packets))
            else:
                try:
                    analyzer.parse(text)
                except analyzer.ParseError:
                    pass
                else:
                    raise ValueError("existing rejected fixture accepted: " + item["file"])
    healthy = [(name, p) for name, packets in parsed for p in packets if not p["report_cost"]["measurement_invalid"] and p["state"]["eligible"]]
    require(bool(healthy), "need an actual healthy eligible report")
    sources = [(name, p) for name, p in healthy if p["passes"]["solid"]["valid_samples"] > 0 and any(m["calls"] for m in p["batch_metrics"].values())]
    require(bool(sources), "need actual sampled helpers and batch scopes in the corruption source")
    source_name, source = max(sources, key=lambda item: sum(m["calls"] for m in item[1]["batch_metrics"].values()) + item[1]["passes"]["solid"]["valid_samples"])
    derived = root / "render-parser-adversaries"
    derived.mkdir(exist_ok=True)
    rejections = []
    known = {item["file"] for item in manifest["rejected"]}
    multipacket = [(name, packets) for name, packets in parsed if len(packets) >= 2]
    require(bool(multipacket), "need an actual multi-packet report for continuity and interval selection proof")
    cases = adversaries(analyzer, source)
    continuity_source, continuity_packets = multipacket[0]
    cases.update(continuity_adversaries(continuity_packets))
    for name, text in cases.items():
        relative = "render-parser-adversaries/" + name + ".log"
        try:
            analyzer.parse(text)
        except analyzer.ParseError as error:
            reason = str(error)
        else:
            raise ValueError("derived corruption accepted: " + name)
        (root / relative).write_text(text, encoding="utf-8")
        if relative not in known:
            manifest["rejected"].append({"file": relative})
            known.add(relative)
        rejections.append({"file": relative, "reason": reason})
    # Independently emitted records are aggregated only to prove arithmetic, not
    # represented as a contiguous capture or used as performance observations.
    healthy_packets = [p for _, p in healthy]
    verify_summary(analyzer, healthy_packets, analyzer.summarize(healthy_packets))
    invalid_packets = [p for _, packets in parsed for p in packets if p["report_cost"]["measurement_invalid"]]
    require(bool(invalid_packets), "need actual invalid diagnostic reports")
    all_packets = [p for _, packets in parsed for p in packets]
    require(any(p["state"]["frames"] == 0 for p in all_packets), "need actual empty report for unused null proof")
    require(any(p["batch_state"]["aborted_scopes"] for p in all_packets), "need actual aborted coarse scope report")
    require(any(p["batch_state"]["observer_failures"] for p in all_packets), "need actual observer failure report")
    require(any(p["begin"]["final"] for p in all_packets) and any(not p["begin"]["final"] for p in all_packets), "need actual final and periodic reports")
    receipts = []
    with tempfile.TemporaryDirectory(prefix="render-parser-cli-", dir=root) as temporary:
        temporary = Path(temporary)
        for kind in ("accepted", "rejected"):
            for index, item in enumerate(manifest[kind]):
                path = confined(root, item["file"])
                output = temporary / f"{kind}-{index}.json"
                process = subprocess.run([sys.executable, str(analyzer_path), str(path), "--output", str(output)], capture_output=True, text=True, timeout=60)
                if kind == "accepted":
                    require(process.returncode == 0 and output.is_file(), "CLI rejected actual runtime report: " + item["file"] + "\n" + process.stderr)
                    result = json.loads(output.read_text(encoding="utf-8"))
                    require(result["measurement_invalid"] is item["measurement_invalid"] and result["final_seen"] is item["final_seen"], "CLI flag mismatch")
                else:
                    require(process.returncode != 0 and not output.exists(), "CLI accepted malformed report or wrote rejected output: " + item["file"])
                receipts.append({"file": item["file"], "expected": kind, "exit_code": process.returncode,
                                 "sha256": hashlib.sha256(path.read_bytes()).hexdigest(), "stderr": process.stderr})
        source_path = confined(root, source_name)
        stdin_run = subprocess.run([sys.executable, str(analyzer_path), "-"], input=source_path.read_text(encoding="utf-8"), capture_output=True, text=True, timeout=60)
        require(stdin_run.returncode == 0 and json.loads(stdin_run.stdout)["schema"] == "render_split", "stdin CLI failed")
        for name, packets in parsed:
            if len(packets) < 2:
                continue
            interval = packets[0]["begin"]["interval"]
            selection = analyzer.summarize(packets, interval, interval)
            require(selection["selected_packet_count"] == 1 and selection["packets"] == packets, "selection lost raw input")
            require(selection["measurement_invalid"] is any(p["report_cost"]["measurement_invalid"] for p in packets), "selection hid invalidity")
            require(selection["final_seen"] is bool(packets[-1]["begin"]["final"]), "selection hid final status")
            selected_run = subprocess.run([sys.executable, str(analyzer_path), str(confined(root, name)), "--first-interval", str(interval), "--last-interval", str(interval)], capture_output=True, text=True, timeout=60)
            require(selected_run.returncode == 0 and json.loads(selected_run.stdout) == selection, "interval selection CLI differs")
        for index, arguments in enumerate((("--first-interval", "0"), ("--first-interval", "2", "--last-interval", "1"), ("--first-interval", str(analyzer.MAX_INT)))):
            output = temporary / f"invalid-selection-{index}.json"
            failed = subprocess.run([sys.executable, str(analyzer_path), str(source_path), "--output", str(output), *arguments], capture_output=True, text=True, timeout=60)
            require(failed.returncode != 0 and not output.exists(), "invalid selection emitted output")
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    result = {"passed": True, "candidate_sha256": manifest["candidateSha256"],
              "analyzer_sha256": hashlib.sha256(analyzer_path.read_bytes()).hexdigest(),
              "schema_sha256": hashlib.sha256(analyzer.SCHEMA_BYTES).hexdigest(),
              "actual_accepted_files": len(parsed), "actual_packets": len(all_packets), "derived_source": source_name,
              "derived_source_packet_sha256": hashlib.sha256(source["raw"].encode()).hexdigest(),
              "derived_rejections": rejections, "accepted": len(manifest["accepted"]), "rejected": len(manifest["rejected"]),
              "cli_receipts": receipts,
              "limits": "Positive reports must come from RenderProof's actual emitted candidate runtime. Corruptions derive from those bytes. Aggregation of independently emitted records proves arithmetic only, not a contiguous capture. No host or Switch performance claim."}
    output = args.output or root / "render-parser-proof.json"
    output.write_text(json.dumps(result, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    print(f"RENDER53 STRICT REPORT PROOF PASS actual_packets={len(all_packets)} accepted={len(manifest['accepted'])} rejected={len(manifest['rejected'])}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, UnicodeError, ValueError, subprocess.SubprocessError) as error:
        print("render53 report proof: " + str(error), file=sys.stderr)
        raise SystemExit(1)
