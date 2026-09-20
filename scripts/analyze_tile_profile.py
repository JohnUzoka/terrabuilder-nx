#!/usr/bin/env python3
"""Validate NX_PROFILE captures and summarize one explicit state cohort.

No sample-time extrapolation or automatic FPS/speedup verdict. Run without the
Switch SDK. A cross-capture comparison is descriptive, not a controlled trial.
"""
from __future__ import annotations

import argparse
from collections import Counter
import hashlib
import json
import math
from pathlib import Path
import re
import sys


FIELD = re.compile(r'([A-Za-z_][A-Za-z0-9_]*)=("(?:[^"\\]|\\.)*"|\S+)')
INTEGER = re.compile(r'-?\d+\Z')
NUMBER = re.compile(r'-?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?\Z')
NATIVE_COUNT = re.compile(r'\b(tick|update|draw|poll|swap)=(\d+)/(\d+)')
STATUS_COUNTS = (
    'update_calls', 'no_draw_boundaries', 'partial_excluded',
    'repeated_epochs_excluded', 'invalid_storage', 'reset_dirty_used_excluded',
    'unregistered_used',
)
GROUPS = ('alloc_init', 'light_and_frame', 'texture_lookup', 'base_draw')


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def fields(text: str) -> dict:
    result = {}
    end = 0
    for match in FIELD.finditer(text):
        require(not text[end:match.start()].strip(), 'unexpected text between fields')
        key, value = match.groups()
        require(key not in result, f'duplicate field {key}')
        if value.startswith('"'):
            value = json.loads(value)
        elif value == 'null':
            value = None
        elif INTEGER.fullmatch(value):
            value = int(value)
        elif NUMBER.fullmatch(value):
            value = float(value)
            require(math.isfinite(value), f'nonfinite field {key}')
        result[key] = value
        end = match.end()
    require(not text[end:].strip(), 'unparsed trailing text')
    return result


def nonnegative_int(record: dict, key: str) -> int:
    value = record[key]
    require(isinstance(value, int) and value >= 0, f'{key} must be a nonnegative integer')
    return value


def close(actual: float, expected: float, name: str) -> None:
    require(isinstance(actual, (int, float)) and math.isfinite(actual), f'{name} is not finite')
    require(math.isclose(actual, expected, rel_tol=1e-10, abs_tol=1e-8), f'{name} arithmetic mismatch')


def validate_block(block: dict) -> None:
    require({'status', 'overhead', 'report_cost', 'end'} <= block.keys(), 'incomplete report sections')
    frequency = block['begin']['frequency']
    require(isinstance(frequency, int) and frequency > 0, 'invalid timer frequency')
    nonnegative_int(block['begin'], 'wall_ticks')
    for state in block['states'].values():
        frames = nonnegative_int(state, 'frames')
        require(frames > 0, 'empty STATE row')
        nonnegative_int(state, 'updates')
        require(state['bits'] == state['cohort'] % 16 and state['series'] == state['cohort'] // 16,
                'cohort bits/series mismatch')
        close(state['updates_per_frame'], state['updates'] / frames, 'updates_per_frame')
    for row in block['metrics']:
        state = block['states'].get(row['cohort'])
        require(state is not None and row['frames'] == state['frames'], 'metric/STATE frame mismatch')
        for key in ('used', 'invalid_negative', 'reset_dirty_used_excluded'):
            nonnegative_int(row, key)
        used = row['used'] - row['invalid_negative'] - row['reset_dirty_used_excluded']
        require(0 <= used <= row['used'] <= row['frames'], 'invalid used/excluded count')
        require(row['unit'] in ('ms', 'count'), 'unknown metric unit')
        require(isinstance(row['raw_total'], int) and isinstance(row['valid_raw_total'], int),
                'raw metric totals must be integers')
        require(row['valid_raw_total'] >= 0, 'negative valid total')
        scale = 1000 / frequency if row['unit'] == 'ms' else 1
        if used:
            close(row['mean_per_frame'], row['valid_raw_total'] * scale / row['frames'], 'mean_per_frame')
            mean = row['valid_raw_total'] * scale / used
            close(row['mean_per_valid_used_frame'], mean, 'mean_per_valid_used_frame')
            require(row['max'] + max(1e-8, abs(mean) * 1e-10) >= mean, 'maximum below mean')
        else:
            require(row['valid_raw_total'] == 0 and all(row[key] is None for key in
                    ('mean_per_frame', 'mean_per_valid_used_frame', 'max')),
                    'no valid timer samples must be represented as null, not zero')
        if row['invalid_negative'] == row['reset_dirty_used_excluded'] == 0:
            require(row['raw_total'] == row['valid_raw_total'], 'unexpected raw/valid total difference')


def load_capture(path: Path) -> dict:
    digest = hashlib.sha256()
    blocks, labels, native, current = [], {}, [], None
    terminated = False
    report_cost = 0
    with path.open('rb') as stream:
        for line_number, raw in enumerate(stream, 1):
            digest.update(raw)
            line = raw.rstrip(b'\r\n')
            if line == b'Terminating application':
                terminated = True
            if line.startswith(b'NX_PHASE '):
                text = line.decode('utf-8')
                counts = {name: {'window': int(window), 'total': int(total)}
                          for name, window, total in NATIVE_COUNT.findall(text)}
                require(set(counts) == {'tick', 'update', 'draw', 'poll', 'swap'},
                        f'{path}:{line_number}: incomplete native counts')
                native.append({'final': text.startswith('NX_PHASE final '), 'counts': counts})
            if not line.startswith(b'NX_PROFILE '):
                continue
            try:
                kind, text = line.decode('utf-8')[len('NX_PROFILE '):].split(' ', 1)
                row = fields(text)
                interval = nonnegative_int(row, 'interval')
                if kind == 'BEGIN':
                    require(current is None, 'nested BEGIN or missing END')
                    require(interval == len(blocks) + 1, 'missing/reordered/duplicate interval')
                    current = {'begin': row, 'states': {}, 'metrics': [], 'keys': set()}
                else:
                    require(current is not None and interval == current['begin']['interval'],
                            'record outside matching BEGIN/END')
                    if kind == 'STATE':
                        cohort = nonnegative_int(row, 'cohort')
                        require(cohort not in current['states'], 'duplicate STATE')
                        current['states'][cohort] = row
                    elif kind == 'METRIC':
                        key = (row['cohort'], row['id'])
                        require(key not in current['keys'], 'duplicate metric')
                        current['keys'].add(key)
                        identity = (row['label'], row['unit'])
                        require(labels.setdefault(row['id'], identity) == identity, 'metric identity changed')
                        current['metrics'].append(row)
                    elif kind == 'END':
                        current['end'] = row
                        validate_block(current)
                        cost = current['report_cost']
                        report_cost += nonnegative_int(cost, 'ticks_before_footer')
                        require(cost['lifetime_ticks_before_footers'] == report_cost,
                                'report-cost lifetime mismatch')
                        require(cost['reports'] == interval, 'report-count mismatch')
                        del current['keys']
                        blocks.append(current)
                        current = None
                    else:
                        require(kind in ('STATUS', 'OVERHEAD', 'REPORT_COST'), f'unknown record {kind}')
                        require(kind.lower() not in current, f'duplicate {kind}')
                        current[kind.lower()] = row
            except (ValueError, KeyError, TypeError, UnicodeError) as error:
                raise ValueError(f'{path}:{line_number}: {error}') from error
    require(current is None, f'{path}: truncated final report')
    require(bool(blocks), f'{path}: no complete reports')
    require(all(not block['begin']['final'] for block in blocks[:-1]), 'report follows final=1')
    status = Counter({key: sum(nonnegative_int(block['status'], key) for block in blocks)
                      for key in STATUS_COUNTS})
    frames = sum(state['frames'] for block in blocks for state in block['states'].values())
    updates = sum(state['updates'] for block in blocks for state in block['states'].values())
    require(updates + status['no_draw_boundaries'] == status['update_calls'], 'unaccounted Update calls')
    native_match = None
    if native and native[-1]['final']:
        for name in native[-1]['counts']:
            require(sum(row['counts'][name]['window'] for row in native) == native[-1]['counts'][name]['total'],
                    f'native {name} window/total mismatch')
        native_match = frames == native[-1]['counts']['draw']['total']
        if not status['partial_excluded'] and not status['repeated_epochs_excluded']:
            require(native_match, 'profiler/native Draw mismatch without reported exclusions')
        require(status['update_calls'] == native[-1]['counts']['update']['total'], 'profiler/native Update mismatch')
    return {'path': str(path), 'sha256': digest.hexdigest(), 'blocks': blocks,
            'complete_reports': len(blocks), 'frames': frames, 'status_totals': dict(status),
            'registry_counts': sorted({block['status']['registry_count'] for block in blocks}),
            'registry_drops': max(block['status']['registry_dropped_lifetime'] for block in blocks),
            'report_failures': max(block['status']['report_failures_lifetime'] for block in blocks),
            'final_report': bool(blocks[-1]['begin']['final']), 'normal_termination': terminated,
            'native_draw_count_matches': native_match}


def summarize(capture: dict, cohort: int, first: int, last: int | None) -> dict:
    selected = [block for block in capture['blocks'] if block['begin']['interval'] >= first
                and (last is None or block['begin']['interval'] <= last) and cohort in block['states']]
    frames = sum(block['states'][cohort]['frames'] for block in selected)
    require(frames > 0, 'no frames match the requested cohort/interval range')
    metrics = {}
    for block in selected:
        frequency = block['begin']['frequency']
        for row in block['metrics']:
            if row['cohort'] != cohort:
                continue
            item = metrics.setdefault(row['label'], {'unit': row['unit'], 'used': 0, 'invalid_negative': 0,
                'reset_dirty_used_excluded': 0, 'valid_total': 0, 'reported_frames': 0, 'max': None})
            for key in ('used', 'invalid_negative', 'reset_dirty_used_excluded'):
                item[key] += row[key]
            item['valid_total'] += row['valid_raw_total'] * (1000 / frequency if row['unit'] == 'ms' else 1)
            item['reported_frames'] += row['frames']
            if row['max'] is not None:
                item['max'] = max(item['max'] or 0, row['max'])
    for item in metrics.values():
        used = item['used'] - item['invalid_negative'] - item['reset_dirty_used_excluded']
        item['mean_per_cohort_frame'] = item['valid_total'] / frames if used else None
        item['mean_per_valid_used_frame'] = item['valid_total'] / used if used else None
    result = {'cohort': cohort, 'bits': cohort % 16, 'series': cohort // 16,
              'intervals': [block['begin']['interval'] for block in selected], 'frames': frames,
              'updates': sum(block['states'][cohort]['updates'] for block in selected),
              'metrics': metrics, 'tile45': {}}
    for layer in ('solid', 'nonsolid'):
        prefix = f'tile45.{layer}.'
        if prefix + 'calls.selected' not in metrics:
            continue
        def count(suffix: str) -> int:
            item = metrics[prefix + suffix]
            require(item['unit'] == 'count', f'{prefix}{suffix}: expected count')
            return item['valid_total']
        attempted, completed = count('calls.selected'), count('calls.completed')
        invalid, omitted = count('calls.invalid_timing'), count('time_metrics.omitted_out_of_range')
        require(0 <= invalid <= completed <= attempted, 'invalid selected/completed timing counts')
        valid = completed - invalid
        require(count('alloc_init.operations') == valid,
                'allocation operations do not match valid completed samples')
        require(all(item['invalid_negative'] == 0 and item['reset_dirty_used_excluded'] == 0
                    for label, item in metrics.items() if label.startswith(prefix)),
                '45 emitted invalid time/counter rows instead of explicit diagnostics')
        prior = metrics.get(f'tile44.{layer}.DrawSingleTile.samples_completed_1in32')
        if prior is not None:
            require(prior['unit'] == 'count' and prior['valid_total'] == completed, '44/45 completed samples disagree')
        details = {'selected': attempted, 'completed': completed, 'invalid_timing': invalid,
                   'omitted_time_metrics': omitted, 'valid_completed_samples': valid,
                   'matches44_completed_samples': prior is not None, 'groups': {}}
        usable = invalid == 0 and omitted == 0 and valid > 0
        for group in GROUPS:
            operations = count(group + '.operations')
            duration = metrics.get(prefix + group + '.completed_samples')
            if operations == 0 and duration is not None:
                require(duration['valid_total'] == 0, f'time recorded without operations for {group}')
            if usable and operations:
                require(duration is not None and duration['unit'] == 'ms', f'missing time for {group}')
            usable_group = usable and duration is not None and duration['invalid_negative'] == 0
            details['groups'][group] = {'operations': operations,
                'mean_us_per_operation': duration['valid_total'] * 1000 / operations if usable_group and operations else None,
                'mean_ms_per_valid_selected_call': duration['valid_total'] / valid if usable_group else None}
        body = metrics.get(prefix + 'call_body.completed_samples')
        remainder = metrics.get(prefix + 'other_body_including_observers.completed_samples')
        if usable:
            require(body is not None and remainder is not None, 'missing matched body/remainder time')
            parts = sum(metrics.get(prefix + group + '.completed_samples', {}).get('valid_total', 0) for group in GROUPS)
            close(body['valid_total'], parts + remainder['valid_total'], 'matched sampled body partition')
        details['timing_comparison_usable'] = usable
        details['mean_sampled_body_us'] = body['valid_total'] * 1000 / valid if usable else None
        result['tile45'][layer] = details
    return result


def compare(current: dict, baseline: dict) -> dict:
    rows = {}
    for label, value in current['metrics'].items():
        previous = baseline['metrics'].get(label)
        if previous is None or previous['unit'] != value['unit']:
            continue
        before, after = previous['mean_per_cohort_frame'], value['mean_per_cohort_frame']
        rows[label] = {'unit': value['unit'], 'baseline_per_frame': before,
                       'current_per_frame': after,
                       'delta_percent': (after / before - 1) * 100 if before and after is not None else None}
    return {'kind': 'descriptive_only_not_a_controlled_speedup_claim', 'metrics': rows}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('log', type=Path)
    parser.add_argument('--cohort', type=int, default=16, help='16: ordinary world, A/B series1')
    parser.add_argument('--first-interval', type=int, default=1, help='choose after inspecting entry/loading intervals')
    parser.add_argument('--last-interval', type=int)
    parser.add_argument('--baseline', type=Path, help='optional descriptive comparison; same cohort')
    parser.add_argument('--baseline-first-interval', type=int, default=1)
    parser.add_argument('--baseline-last-interval', type=int)
    parser.add_argument('--output', type=Path, help='write JSON to a NEW file instead of stdout')
    args = parser.parse_args()
    require(args.cohort >= 0 and args.first_interval >= 1 and args.baseline_first_interval >= 1,
            'cohort/interval arguments out of range')
    capture = load_capture(args.log)
    summary = summarize(capture, args.cohort, args.first_interval, args.last_interval)
    result = {'capture': {key: value for key, value in capture.items() if key != 'blocks'}, 'selected': summary,
        'limits': ['Selected-call time is not extrapolated to all calls or the whole tile loop.',
                   'Operation timers and the remainder include observation overhead; these are not GPU timings.',
                   'Invalid/omitted45 timing groups are not given per-operation comparison values.',
                   'Negative original timers and startup/loading intervals require explicit interpretation.',
                   'Allocation timing does not include all later GC cost; matched-scene whole-frame comparison is required.',
                   'Multiplayer testing is deferred; potential semantic optimizations require user discussion first.']}
    if args.baseline:
        base_capture = load_capture(args.baseline)
        base_summary = summarize(base_capture, args.cohort, args.baseline_first_interval, args.baseline_last_interval)
        result['baseline'] = {'path': str(args.baseline), 'sha256': base_capture['sha256'],
                              'frames': base_summary['frames'], 'intervals': base_summary['intervals']}
        result['comparison'] = compare(summary, base_summary)
    encoded = json.dumps(result, indent=2, allow_nan=False) + '\n'
    if args.output:
        with args.output.open('x', encoding='utf-8') as stream:
            stream.write(encoded)
        print(f'Validated {capture["complete_reports"]} reports; selected {summary["frames"]} frames -> {args.output}')
    else:
        print(encoded, end='')
    return 0


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (OSError, ValueError, KeyError, TypeError) as error:
        print(f'Capture analysis failed: {error}', file=sys.stderr)
        raise SystemExit(1)
