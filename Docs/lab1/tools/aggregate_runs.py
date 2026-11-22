#!/usr/bin/env python3
"""Медианы метрик по нескольким прогонам PerfCapture.

Запуск:
    python aggregate_runs.py <каталог_с_прогонами> <каталог_результата> --label "до"

Ожидается структура: <каталог_с_прогонами>/run1/frames.csv, run2/..., run3/...
Скрипт считает метрики каждого прогона тем же кодом, что analyze_capture.py,
и берёт медиану по прогонам: система шумит, одиночный прогон плохо воспроизводим.
"""

import argparse
import json
import os
import sys

import numpy as np
import pandas as pd

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import analyze_capture as ac  # noqa: E402


def run_metrics(capture_dir):
    frames, scales, meta = ac.load_capture(capture_dir)
    metrics = {"meta": meta, "stages": {}, "markers": {}}

    for stage in ac.STAGE_ORDER:
        item = ac.stage_stats(frames, stage)
        if item:
            metrics["stages"][stage] = item

    marker_columns = {marker: marker + "_ms" for marker, _ in ac.MARKERS if marker + "_ms" in frames.columns}
    for stage in ("streaming", "traversal"):
        costs, totals, columns = ac.marker_costs(frames, stage, marker_columns)
        metrics["markers"][stage] = {"columns": columns, "per_column_ms": costs, "total_ms": totals}

    gc_column = ac.find_column(frames, "GC Allocated In Frame")
    if gc_column is not None:
        scale = scales.get(gc_column, 1e-6)
        metrics["gc_mb"] = {}
        for stage in ac.STAGE_ORDER:
            part = frames[frames["stage"] == stage]
            if len(part) == 0:
                continue
            metrics["gc_mb"][stage] = float(part[gc_column].astype(float).sum() * scale)

    return metrics


def median_of(values):
    clean = [v for v in values if v is not None and not (isinstance(v, float) and np.isnan(v))]
    if not clean:
        return None
    return float(np.median(np.array(clean, dtype=float)))


def aggregate(runs, label):
    result = {"label": label, "runs": len(runs), "stages": {}, "markers": {}}

    for stage in ac.STAGE_ORDER:
        keys = None
        for run in runs:
            if stage in run["stages"]:
                keys = list(run["stages"][stage].keys())
                break
        if not keys:
            continue

        item = {}
        for key in keys:
            if key in ("title", "stage"):
                item[key] = runs[0]["stages"][stage][key]
                continue
            item[key] = median_of([run["stages"].get(stage, {}).get(key) for run in runs])
        result["stages"][stage] = item

    for stage in ("streaming", "traversal"):
        markers = {}
        all_markers = ac.MARKERS
        for marker, _title in all_markers:
            values = [run["markers"].get(stage, {}).get("per_column_ms", {}).get(marker) for run in runs]
            median = median_of(values)
            if median is not None:
                markers[marker] = median
        columns = median_of([run["markers"].get(stage, {}).get("columns") for run in runs])
        result["markers"][stage] = {"columns": columns, "per_column_ms": markers}

    if any("gc_mb" in run for run in runs):
        result["gc_mb"] = {}
        for stage in ac.STAGE_ORDER:
            values = [run.get("gc_mb", {}).get(stage) for run in runs]
            median = median_of(values)
            if median is not None:
                result["gc_mb"][stage] = median

    return result


def write_tables(result, out_dir):
    rows = []
    for stage in ac.STAGE_ORDER:
        item = result["stages"].get(stage)
        if not item:
            continue
        rows.append({
            "Этап": item["title"],
            "Кадров": int(item["frames"]),
            "Длительность, с": round(item["duration_s"], 2),
            "Средний FPS": round(item["avg_fps"], 1),
            "Кадр avg, мс": round(item["frame_avg_ms"], 2),
            "Кадр p50, мс": round(item["frame_p50_ms"], 2),
            "Кадр p95, мс": round(item["frame_p95_ms"], 2),
            "Кадр p99, мс": round(item["frame_p99_ms"], 2),
            "Кадр max, мс": round(item["frame_max_ms"], 2),
            "Кадров > 16.7 мс, %": round(item["over_16_7_pct"], 1),
            "Колонок": int(item["columns"]),
            "GC, МБ": round(result.get("gc_mb", {}).get(stage, 0.0), 1),
        })
    pd.DataFrame(rows).to_csv(os.path.join(out_dir, "stages_median.csv"), index=False, encoding="utf-8-sig")

    marker_rows = []
    for stage in ("streaming", "traversal"):
        data = result["markers"].get(stage)
        if not data:
            continue
        for marker, _title in ac.MARKERS:
            if marker in data["per_column_ms"]:
                marker_rows.append({
                    "Этап": ac.STAGE_TITLES.get(stage, stage),
                    "Маркер": marker,
                    "мс/колонку": round(data["per_column_ms"][marker], 2),
                    "Колонок": data["columns"],
                })
    pd.DataFrame(marker_rows).to_csv(os.path.join(out_dir, "markers_median.csv"), index=False, encoding="utf-8-sig")


def print_result(result):
    print("=== {0} (медиана по {1} прогонам) ===".format(result["label"], result["runs"]))
    for stage in ac.STAGE_ORDER:
        item = result["stages"].get(stage)
        if not item:
            continue
        print("  {0:24s} frames={1:5.0f} fps={2:6.1f} avg={3:7.2f} p95={4:7.2f} max={5:8.2f} >16.7={6:5.1f}%".format(
            item["title"], item["frames"], item["avg_fps"], item["frame_avg_ms"],
            item["frame_p95_ms"], item["frame_max_ms"], item["over_16_7_pct"]))
    for stage in ("streaming", "traversal"):
        data = result["markers"].get(stage)
        if not data:
            continue
        print("  [{0}] колонок: {1:0.0f}".format(stage, data["columns"]))
        for marker, _title in ac.MARKERS:
            if marker in data["per_column_ms"]:
                print("    {0:24s} {1:8.2f} мс/колонку".format(marker, data["per_column_ms"][marker]))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("captures")
    parser.add_argument("out")
    parser.add_argument("--label", default="замер")
    args = parser.parse_args()

    subdirs = sorted(
        os.path.join(args.captures, name)
        for name in os.listdir(args.captures)
        if name.startswith("run") and os.path.isdir(os.path.join(args.captures, name))
    )
    if not subdirs:
        raise SystemExit("Не найдены каталоги run* в " + args.captures)

    runs = [run_metrics(path) for path in subdirs]
    result = aggregate(runs, args.label)
    result["sources"] = subdirs

    os.makedirs(args.out, exist_ok=True)
    with open(os.path.join(args.out, "stats_median.json"), "w", encoding="utf-8") as handle:
        json.dump(result, handle, ensure_ascii=False, indent=2)
    write_tables(result, args.out)
    print_result(result)


if __name__ == "__main__":
    main()
