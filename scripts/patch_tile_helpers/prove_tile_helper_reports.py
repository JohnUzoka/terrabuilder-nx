#!/usr/bin/env python3
"""Validate real serialized-runtime reports and reject accounting adversaries."""
import importlib.util
import json
import re
import sys
from pathlib import Path


def main():
    analyzer_path, output = Path(sys.argv[1]), Path(sys.argv[2])
    spec = importlib.util.spec_from_file_location("tile49_analyzer", analyzer_path)
    analyzer = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(analyzer)
    text = (output / "tile-helper-report.log").read_text()
    packets = analyzer.parse(text)
    assert len(packets) == 1 and packets[0]["raw"] == text
    summary = analyzer.summarize(packets)
    assert summary["layers"]["solid"]["raw"]["valid_samples"] == 3
    assert summary["layers"]["nonsolid"]["raw"]["valid_samples"] == 2
    for layer in analyzer.LAYERS:
        for label in analyzer.LABELS:
            metric = summary["layers"][layer]["metrics"][label]
            if metric["operations"] == 0:
                assert metric["mean_ticks_per_operation"] is None
                assert metric["mean_microseconds_per_operation"] is None
                assert metric["ticks_per_valid_sample"] is None
            else:
                assert metric["mean_ticks_per_operation"] == metric["total_ticks"] / metric["operations"]
                assert metric["ticks_per_valid_sample"] == metric["total_ticks"] / summary["layers"][layer]["raw"]["valid_samples"]
    recovered = analyzer.parse((output / "tile-helper-recovered.log").read_text())
    assert recovered[0]["state"]["frames"] == 1
    assert recovered[0]["report_cost"]["failures_lifetime"] == 1
    capture_reports = []
    for boundary in ("begin", "end"):
        for clock_offset in (1, 2):
            file = f"tile-helper-capture_{boundary}_clock_{clock_offset}.log"
            capture = analyzer.parse((output / file).read_text())
            state = capture[0]["state"]
            assert state["frames"] == state["eligible"] == 2
            assert state["snapshot_calls"] == 4 and state["capture_failure_frames"] == 1
            assert state["measurement_invalid"] == 1
            capture_reports.append({"file": file, "state": state})
    report_clock_reports = []
    for clock_offset in (1, 2, 3, 4):
        file = f"tile-helper-report-clock-{clock_offset}.log"
        report = analyzer.parse((output / file).read_text())
        assert report[0]["state"]["frames"] == report[0]["state"]["eligible"] == 1
        assert report[0]["state"]["snapshot_calls"] == 2
        assert report[0]["state"]["measurement_invalid"] == 1
        report_clock_reports.append({"file": file, "state": report[0]["state"], "report_cost": report[0]["report_cost"]})
    empty_final_packets = analyzer.parse((output / "tile-helper-periodic-empty-final.log").read_text())
    assert len(empty_final_packets) == 2
    assert empty_final_packets[0]["begin"]["final"] == 0
    empty = empty_final_packets[1]
    assert empty["begin"]["final"] == 1 and empty["begin"]["interval"] == 2
    assert empty["state"]["frames"] == empty["state"]["snapshot_calls"] == empty["scene"]["samples"] == 0
    assert empty["scene"]["player_x_first"] is None
    empty_summary = analyzer.summarize([empty])
    assert all(metric["mean_ticks_per_operation"] is None for layer in empty_summary["layers"].values() for metric in layer["metrics"].values())
    regression_reports = []
    for boundary in ("start", "body", "footer"):
        file = f"tile-helper-report-regression-{boundary}.log"
        regression = analyzer.parse((output / file).read_text())
        assert regression[0]["report_cost"]["measurement_invalid"] == 1
        assert analyzer.summarize(regression)["measurement_invalid"] is True
        regression_reports.append({"file": file, "report_cost": regression[0]["report_cost"]})
    slow_footer_packets = analyzer.parse((output / "tile-helper-slow-footer-periodic.log").read_text())
    assert len(slow_footer_packets) == 2
    assert all(packet["begin"]["final"] == 0 for packet in slow_footer_packets)
    assert slow_footer_packets[1]["begin"]["end_tick"] - slow_footer_packets[0]["begin"]["end_tick"] >= 15 * slow_footer_packets[0]["begin"]["frequency"]
    rejected = []

    def bad(name, value):
        try:
            analyzer.parse(value)
        except analyzer.ParseError as error:
            rejected.append({"name": name, "reason": str(error)})
        else:
            raise AssertionError("accepted malformed packet: " + name)

    def alter(kind, key, value, occurrence=0):
        rows = text.splitlines()
        matches = [i for i, row in enumerate(rows) if row.startswith("NX_PROFILE " + kind + " ")]
        index = matches[occurrence]
        changed, count = re.subn(r"(?<!\S)" + re.escape(key) + r"=\S+", key + "=" + str(value), rows[index])
        assert count == 1, (kind, key)
        rows[index] = changed
        return "\n".join(rows) + "\n"

    bad("incomplete_actual_footer_failure", (output / "tile-helper-incomplete.log").read_text())
    bad("missing_end", "\n".join(text.splitlines()[:-1]))
    bad("missing_metric", "\n".join(row for row in text.splitlines() if "label=GetFinalLight" not in row))
    bad("duplicate_packet_after_final", text + text)
    bad("wrong_version", alter("BEGIN", "version", 48))
    bad("wrong_schema", alter("BEGIN", "schema", "cost45"))
    bad("wrong_sampling", alter("BEGIN", "sample_denominator", 32))
    bad("zero_frequency", alter("BEGIN", "frequency", 0))
    bad("mismatched_wall", alter("BEGIN", "wall_ticks", 0))
    bad("negative_ticks", alter("METRIC", "total_ticks", -1))
    bad("nan_scene", alter("SCENE", "camera_x_first", "nan"))
    bad("reversed_range", alter("SCENE", "player_x_min", 999999))
    bad("frame_completion", alter("STATE", "completed", 2))
    bad("frame_eligibility", alter("STATE", "eligible", 2))
    bad("missing_snapshot", alter("STATE", "snapshot_calls", 1))
    bad("scene_sample_count", alter("SCENE", "samples", 3))
    bad("pass_completion", alter("PASS", "completed_passes", 2))
    bad("tile_count_order", alter("PASS", "eligible", 0))
    bad("selected_over_calls", alter("PASS", "selected", 9999))
    bad("sample_completion", alter("PASS", "completed_samples", 0))
    bad("sample_validity", alter("PASS", "discarded_samples", 1))
    bad("sample_clock_containment", alter("PASS", "sample_ticks", 999999999))
    bad("metric_count_bound", alter("METRIC", "operations", 99999))
    bad("metric_max_over_total", alter("METRIC", "max_ticks", 999999))
    bad("zero_ops_nonzero_ticks", alter("METRIC", "operations", 0))
    bad("calibration_count", alter("METRIC", "operations", 0, 5))
    bad("wrong_layer", alter("PASS", "layer", "other"))
    bad("wrong_label", alter("METRIC", "label", "light_and_frame"))
    bad("interval_mismatch", alter("END", "interval", 2))
    bad("invalid_counter_overflow", alter("PASS", "calls", 1 << 63))
    bad("report_attempt_identity", alter("REPORT_COST", "attempts", 0))
    bad("unknown_field", text.replace("NX_PROFILE END interval=1", "NX_PROFILE END interval=1 unexpected=1"))
    bad("duplicate_field", text.replace("version=49", "version=49 version=49", 1))
    bad("capture_failure_count_negative", alter("STATE", "capture_failure_frames", -1))
    bad("post_body_clock_failure_incomplete", (output / "tile-helper-report-clock-incomplete.log").read_text())
    result = {"passed": True, "actual_packets": 17, "capture_failure_reports": capture_reports, "report_clock_reports": report_clock_reports, "regression_reports": regression_reports, "empty_final_packets": empty_final_packets, "slow_footer_packets": slow_footer_packets, "rejected_count": len(rejected), "rejections": rejected, "summary": summary,
              "limits": "Reports are emitted by actual serialized candidate runtime under deterministic host boundaries; parsing is actual analyzer code. This establishes packet/accounting behavior, not Switch performance."}
    (output / "tile-helper-parser-proof.json").write_text(json.dumps(result, indent=2) + "\n")
    print(f"TILE49 STRICT REPORT PROOF PASS actual_packets=17 adversaries={len(rejected)}")


if __name__ == "__main__":
    main()
