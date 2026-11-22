#!/usr/bin/env python3
"""Разбор замера PerfCapture: графики и таблицы для отчёта по ЛР №1.

Запуск:
    python analyze_capture.py <каталог_замера> <каталог_для_графиков> [--label "до"]

На входе ожидаются файлы, которые пишет Assets/Scripts/Profiling/PerfCaptureRunner.cs:
frames.csv, events.csv, counters.csv, summary.txt, systeminfo.txt.
"""

import argparse
import csv
import json
import os
import re
import sys

import numpy as np
import pandas as pd

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.ticker import FuncFormatter

STAGE_ORDER = ["wait_world", "streaming", "steady", "traversal", "editing", "finished"]
STAGE_TITLES = {
    "wait_world": "Ожидание мира",
    "streaming": "Стартовая загрузка",
    "steady": "Спокойный обзор",
    "traversal": "Телепорт 256 блоков",
    "editing": "Правка блоков",
    "finished": "Завершение",
}
STAGE_COLORS = {
    "wait_world": "#d9d9d9",
    "streaming": "#f4b183",
    "steady": "#9dc3e6",
    "traversal": "#ffd966",
    "editing": "#c5e0b4",
    "finished": "#d9d9d9",
}

MARKERS = [
    ("Voxels.Column", "Колонка целиком"),
    ("Voxels.Terrain", "Рельеф (Burst)"),
    ("Voxels.Fill", "Заполнение вокселей"),
    ("Voxels.Caves", "Пещеры"),
    ("Voxels.Structures", "Структуры"),
    ("Voxels.ChunkMesh", "Меши чанков"),
    ("Voxels.NeighbourRemesh", "Пересборка соседей"),
    ("Voxels.ColliderBake", "Запекание коллайдеров"),
]

PLOT_STYLE = {
    "font.family": "DejaVu Sans",
    "font.size": 9,
    "axes.titlesize": 11,
    "axes.labelsize": 9,
    "axes.grid": True,
    "grid.alpha": 0.25,
    "grid.linewidth": 0.6,
    "figure.dpi": 140,
    "savefig.bbox": "tight",
    "legend.frameon": False,
}


def find_column(df, *needles):
    """Ищет колонку по подстроке (без учёта регистра)."""
    for needle in needles:
        needle_lower = needle.lower()
        for column in df.columns:
            if needle_lower in str(column).lower():
                return column
    return None


def build_unit_scales(counter_path):
    """Возвращает масштаб (во сколько раз умножить), чтобы получить миллисекунды/мегабайты."""
    scales = {}
    if not os.path.exists(counter_path):
        return scales

    counters = pd.read_csv(counter_path)
    for _, row in counters.iterrows():
        name = str(row["name"]).replace(",", "_")
        unit = str(row.get("unit", "")).lower()
        if "nanosecond" in unit:
            scales[name] = 1e-6
        elif "millisecond" in unit:
            scales[name] = 1.0
        elif "microsecond" in unit:
            scales[name] = 1e-3
        elif "byte" in unit:
            scales[name] = 1e-6  # в мегабайты
        else:
            scales[name] = 1.0
    return scales


def read_frames(frames_path):
    """Читает frames.csv, терпимо к строкам другой длины (счётчики могли добавиться позже)."""
    with open(frames_path, newline="", encoding="utf-8") as handle:
        reader = csv.reader(handle)
        header = next(reader)
        rows = []
        for row in reader:
            if not row:
                continue
            row = row[:len(header)]
            if len(row) < len(header):
                row += [""] * (len(header) - len(row))
            rows.append(row)

    frame_data = pd.DataFrame(rows, columns=header)
    for column in frame_data.columns:
        if column == "stage":
            continue
        frame_data[column] = pd.to_numeric(frame_data[column], errors="coerce")
    return frame_data


def estimate_frame_timing_lag(frames, max_lag=8):
    """FrameTiming приходит из буфера с задержкой в несколько кадров: находим её по корреляции."""
    reference = frames["dt_ms"].fillna(0.0).to_numpy(dtype=float)
    best_lag = 0
    best_score = -1.0

    for lag in range(0, max_lag + 1):
        candidate = frames["cpu_main_ms"].shift(-lag).fillna(0.0).to_numpy(dtype=float)
        denominator = float(np.linalg.norm(reference) * np.linalg.norm(candidate))
        if denominator <= 0.0:
            continue
        score = float(np.dot(reference, candidate) / denominator)
        if score > best_score:
            best_score = score
            best_lag = lag

    return best_lag


def load_capture(capture_dir):
    frames_path = os.path.join(capture_dir, "frames.csv")
    frames = read_frames(frames_path)
    frames = frames[frames["stage"].notna()]
    frames["t"] = frames["time_s"].astype(float)

    meta = {"frame_timing_lag": 0}
    if "cpu_main_ms" in frames.columns and "dt_ms" in frames.columns:
        lag = estimate_frame_timing_lag(frames)
        meta["frame_timing_lag"] = lag
        for column in ("cpu_frame_ms", "cpu_main_ms", "cpu_render_ms", "gpu_ms"):
            if column in frames.columns:
                frames[column] = frames[column].shift(-lag)

    scales = build_unit_scales(os.path.join(capture_dir, "counters.csv"))
    ms_columns = {}
    for marker, _title in MARKERS:
        column = find_column(frames, marker)
        if column is None:
            continue
        scale = scales.get(column, 1e-6)
        ms_name = marker + "_ms"
        frames[ms_name] = frames[column].astype(float) * scale
        ms_columns[marker] = ms_name

    for raw, ms in (("cpu_main_ms", "cpu_main_ms"), ("cpu_render_ms", "cpu_render_ms"),
                    ("cpu_frame_ms", "cpu_frame_ms"), ("gpu_ms", "gpu_ms")):
        if raw in frames.columns:
            frames[ms] = frames[raw].astype(float)

    if "gpu_ms" in frames.columns:
        # FrameTiming отдаёт 0, если GPU-тайминги недоступны на платформе.
        frames["gpu_ms"] = frames["gpu_ms"].replace(0.0, np.nan)

    # Кадры со снятием скриншота и первый кадр запуска искажают распределение.
    meta["startup_frame_ms"] = float(frames["dt_ms"].iloc[0]) if len(frames) else 0.0
    meta["startup_time_s"] = float(frames["time_s"].iloc[0]) if len(frames) else 0.0

    if "shot" in frames.columns:
        frames = frames[frames["shot"] != 1]

    frames = frames[frames["frame"] > 1]
    frames = frames.reset_index(drop=True)

    return frames, scales, meta


def stage_ranges(frames):
    ranges = []
    for stage in STAGE_ORDER:
        part = frames[frames["stage"] == stage]
        if len(part) == 0:
            continue
        ranges.append((stage, part["t"].min(), part["t"].max(), len(part)))
    return ranges


def shade_stages(ax, ranges):
    for stage, start, end, _count in ranges:
        ax.axvspan(start, end, color=STAGE_COLORS.get(stage, "#eeeeee"), alpha=0.35, linewidth=0)
        ax.text((start + end) / 2.0, 1.0, STAGE_TITLES.get(stage, stage),
                transform=ax.get_xaxis_transform(), ha="center", va="bottom",
                fontsize=7, color="#555555", clip_on=False)


def percentile(series, q):
    values = series.dropna().to_numpy()
    if len(values) == 0:
        return float("nan")
    return float(np.percentile(values, q))


def stage_stats(frames, stage):
    part = frames[frames["stage"] == stage]
    if len(part) == 0:
        return None

    dt = part["dt_ms"].astype(float)
    stats = {
        "stage": stage,
        "title": STAGE_TITLES.get(stage, stage),
        "frames": int(len(part)),
        "duration_s": float(part["t"].max() - part["t"].min()),
        "avg_fps": float(1000.0 / dt.mean()) if dt.mean() > 0 else 0.0,
        "frame_avg_ms": float(dt.mean()),
        "frame_p50_ms": percentile(dt, 50),
        "frame_p95_ms": percentile(dt, 95),
        "frame_p99_ms": percentile(dt, 99),
        "frame_max_ms": float(dt.max()),
        "over_16_7_pct": float(100.0 * (dt > 16.7).mean()),
        "over_33_3_pct": float(100.0 * (dt > 33.3).mean()),
        "columns": int(part["cols_new"].sum()),
        "chunks_final": int(part["chunks"].iloc[-1]),
    }

    for key in ("gpu_ms", "cpu_main_ms", "cpu_render_ms"):
        if key in part.columns:
            stats[key + "_avg"] = float(part[key].dropna().mean()) if part[key].notna().any() else None
            stats[key + "_p95"] = percentile(part[key], 95) if part[key].notna().any() else None

    return stats


def marker_costs(frames, stage, marker_ms_columns):
    part = frames[frames["stage"] == stage]
    columns = int(part["cols_new"].sum())
    costs = {}
    totals = {}
    for marker, column in marker_ms_columns.items():
        total = float(part[column].sum())
        totals[marker] = total
        costs[marker] = total / columns if columns > 0 else 0.0
    return costs, totals, columns


def save_stats(frames, out_dir, label, meta):
    stats = {"label": label, "meta": meta, "stages": [], "markers": {}}

    for stage in STAGE_ORDER:
        item = stage_stats(frames, stage)
        if item:
            stats["stages"].append(item)

    marker_ms_columns = {marker: marker + "_ms" for marker, _ in MARKERS if marker + "_ms" in frames.columns}

    for stage in ("streaming", "traversal"):
        costs, totals, columns = marker_costs(frames, stage, marker_ms_columns)
        stats["markers"][stage] = {
            "columns": columns,
            "per_column_ms": costs,
            "total_ms": totals,
        }

    with open(os.path.join(out_dir, "stats.json"), "w", encoding="utf-8") as handle:
        json.dump(stats, handle, ensure_ascii=False, indent=2)

    rows = []
    for item in stats["stages"]:
        rows.append({
            "Этап": item["title"],
            "Кадров": item["frames"],
            "Длительность, с": round(item["duration_s"], 2),
            "Средний FPS": round(item["avg_fps"], 1),
            "Кадр avg, мс": round(item["frame_avg_ms"], 2),
            "Кадр p95, мс": round(item["frame_p95_ms"], 2),
            "Кадр max, мс": round(item["frame_max_ms"], 2),
            "Кадров > 16.7 мс, %": round(item["over_16_7_pct"], 1),
            "Колонок": item["columns"],
        })
    pd.DataFrame(rows).to_csv(os.path.join(out_dir, "stages.csv"), index=False, encoding="utf-8-sig")

    return stats


def plot_overview(frames, out_dir, label):
    ranges = stage_ranges(frames)
    fig, (ax_time, ax_fps) = plt.subplots(2, 1, figsize=(9.6, 5.4), sharex=True,
                                          gridspec_kw={"height_ratios": [2.1, 1.0]})

    t = frames["t"] - frames["t"].min()
    dt = frames["dt_ms"].astype(float).copy()
    fps = (1000.0 / frames["dt_ms"].clip(lower=0.001)).astype(float)
    gpu = frames["gpu_ms"].astype(float).copy() if "gpu_ms" in frames.columns else None

    # Пропущенные кадры (например, кадры со скриншотом) разрывают линию, а не соединяются.
    if "frame" in frames.columns:
        gaps = frames["frame"].diff().fillna(1) > 1
        dt[gaps] = np.nan
        fps[gaps] = np.nan
        if gpu is not None:
            gpu[gaps] = np.nan

    ax_time.plot(t, dt, color="#1f4e79", linewidth=0.7, label="Кадр (main thread), мс")
    if gpu is not None and gpu.notna().any():
        ax_time.plot(t, gpu, color="#c00000", linewidth=0.7, alpha=0.85, label="GPU, мс")

    ax_time.axhline(16.7, color="#548235", linewidth=0.8, linestyle="--", label="16.7 мс (60 FPS)")
    ax_time.axhline(33.3, color="#bf9000", linewidth=0.8, linestyle=":", label="33.3 мс (30 FPS)")
    ax_time.set_ylabel("Время кадра, мс")
    ax_time.set_title("Профиль кадров: " + label, pad=14)
    ax_time.legend(loc="upper right", ncol=2)
    peak = float(np.nanmax(frames["dt_ms"]))
    ax_time.set_ylim(0, min(peak * 1.08, 700))
    shade_stages(ax_time, ranges)

    fps_smooth = fps.rolling(31, min_periods=1, center=True).median()
    ax_fps.plot(t, fps_smooth, color="#2e75b6", linewidth=1.1, label="FPS (медиана по 31 кадру)")
    ax_fps.set_ylabel("FPS")
    ax_fps.set_xlabel("Время замера, с")
    ax_fps.set_ylim(0, min(160, max(10, fps.quantile(0.995) * 1.2)))
    shade_stages(ax_fps, ranges)

    for ax in (ax_time, ax_fps):
        ax.grid(True, axis="y")
    fig.savefig(os.path.join(out_dir, "01_frame_timeline.png"))
    plt.close(fig)


def plot_stage_bars(frames, out_dir, label):
    rows = [stage_stats(frames, stage) for stage in STAGE_ORDER]
    rows = [row for row in rows if row]
    if not rows:
        return

    names = [row["title"] for row in rows]
    avg = [row["frame_avg_ms"] for row in rows]
    p95 = [row["frame_p95_ms"] for row in rows]

    x = np.arange(len(names))
    width = 0.38
    fig, ax = plt.subplots(figsize=(8.6, 3.6))
    ax.bar(x - width / 2, avg, width, color="#8faadc", label="Среднее")
    ax.bar(x + width / 2, p95, width, color="#1f4e79", label="p95")
    ax.set_xticks(x)
    ax.set_xticklabels(names, rotation=10, ha="right")
    ax.set_ylabel("Время кадра, мс")
    ax.axhline(16.7, color="#548235", linestyle="--", linewidth=0.9, label="16.7 мс")
    ax.set_title("Время кадра по этапам сценария: " + label)
    ax.legend()
    fig.savefig(os.path.join(out_dir, "02_stage_bars.png"))
    plt.close(fig)


def plot_marker_breakdown(frames, out_dir, label):
    marker_ms_columns = {marker: marker + "_ms" for marker, _ in MARKERS if marker + "_ms" in frames.columns}
    if not marker_ms_columns:
        return

    stages = [stage for stage in ("streaming", "traversal")
              if len(frames[frames["stage"] == stage]) > 0]
    if not stages:
        return

    children = ["Voxels.Terrain", "Voxels.Fill", "Voxels.Caves", "Voxels.Structures",
                "Voxels.ChunkMesh", "Voxels.NeighbourRemesh"]
    series = []
    for stage_index, stage in enumerate(stages):
        costs, _totals, columns = marker_costs(frames, stage, marker_ms_columns)
        row = {}
        for marker in children:
            row[marker] = costs.get(marker, 0.0)
        # Меш соседних колонок вложен в ChunkMesh и NeighbourRemesh одновременно:
        # вычитаем, чтобы не считать его дважды.
        row["Voxels.ChunkMesh"] = max(0.0, row["Voxels.ChunkMesh"] - row["Voxels.NeighbourRemesh"])
        other = costs.get("Voxels.Column", 0.0) - sum(row.values())
        row["other"] = max(0.0, other)
        series.append((stage, row, columns))

    labels = {
        "Voxels.Terrain": "Рельеф",
        "Voxels.Fill": "Заполнение",
        "Voxels.Caves": "Пещеры",
        "Voxels.Structures": "Структуры",
        "Voxels.ChunkMesh": "Меши",
        "Voxels.NeighbourRemesh": "Соседи",
        "other": "Прочее",
    }
    colors = {
        "Voxels.Terrain": "#4472c4",
        "Voxels.Fill": "#8faadc",
        "Voxels.Caves": "#7030a0",
        "Voxels.Structures": "#a9d18e",
        "Voxels.ChunkMesh": "#ed7d31",
        "Voxels.NeighbourRemesh": "#ff0000",
        "other": "#bfbfbf",
    }

    fig, ax = plt.subplots(figsize=(7.4, 3.8))
    x = np.arange(len(series))
    bottom = np.zeros(len(series))
    for key in children + ["other"]:
        values = np.array([row.get(key, 0.0) for _stage, row, _columns in series])
        ax.bar(x, values, 0.5, bottom=bottom, color=colors[key], label=labels[key])
        bottom += values

    tick_labels = []
    for stage, _row, columns in series:
        tick_labels.append("{0}\n{1} колонок".format(STAGE_TITLES.get(stage, stage), columns))
    ax.set_xticks(x)
    ax.set_xticklabels(tick_labels, fontsize=8)
    ax.set_ylabel("мс на одну колонку")
    ax.set_title("Стоимость генерации одной колонки: " + label)
    ax.legend(ncol=4, fontsize=7, loc="upper center", bbox_to_anchor=(0.5, -0.22))
    fig.savefig(os.path.join(out_dir, "03_column_breakdown.png"))
    plt.close(fig)


def plot_memory(frames, out_dir, label, scales):
    t = frames["t"] - frames["t"].min()
    ranges = stage_ranges(frames)

    fig, (ax_mem, ax_gc) = plt.subplots(2, 1, figsize=(9.6, 5.0), sharex=True)

    ax_mem.plot(t, frames["total_mb"], color="#1f4e79", linewidth=0.9, label="Всего выделено")
    ax_mem.plot(t, frames["reserved_mb"], color="#8faadc", linewidth=0.9, label="Зарезервировано")
    ax_mem.plot(t, frames["mono_mb"], color="#c00000", linewidth=0.9, label="Управляемая куча (Mono)")
    ax_mem.set_ylabel("Память, МБ")
    ax_mem.set_title("Память и аллокации: " + label)
    ax_mem.legend(loc="upper left")
    shade_stages(ax_mem, ranges)

    gc_column = find_column(frames, "GC Allocated In Frame", "GC Allocated")
    if gc_column is not None:
        scale = scales.get(gc_column, 1.0)
        # Масштаб приводит байты к мегабайтам; для графика нужны килобайты.
        gc_kb = frames[gc_column].astype(float) * scale * 1024.0
        ax_gc.plot(t, gc_kb, color="#7030a0", linewidth=0.8)
        ax_gc.set_ylabel("GC-аллокации, КБ/кадр")
    else:
        ax_gc.plot(t, frames["mono_mb"].diff().fillna(0.0) * 1024.0, color="#7030a0", linewidth=0.8)
        ax_gc.set_ylabel("Δ управляемой кучи, КБ/кадр")

    ax_gc.set_xlabel("Время замера, с")
    shade_stages(ax_gc, ranges)
    ax_gc.axhline(0.0, color="#999999", linewidth=0.6)
    fig.savefig(os.path.join(out_dir, "04_memory.png"))
    plt.close(fig)


def plot_rendering(frames, out_dir, label):
    if "submeshes" not in frames.columns or (frames["submeshes"] < 0).all():
        return

    sampled = frames[frames["submeshes"] >= 0].copy()
    if sampled.empty:
        return

    t = sampled["t"] - frames["t"].min()
    fig, (ax_draw, ax_chunk) = plt.subplots(2, 1, figsize=(9.6, 4.6), sharex=True)

    ax_draw.plot(t, sampled["submeshes"], color="#ed7d31", linewidth=1.0,
                 marker="o", markersize=2, label="Сабмеши (оценка draw calls)")
    ax_draw.set_ylabel("Сабмешей")
    ax_draw.set_title("Нагрузка на рендер: " + label)
    ax_draw.legend(loc="upper left")

    per_chunk = sampled["submeshes"] / sampled["chunks"].replace(0, np.nan)
    ax_chunk.plot(t, per_chunk, color="#1f4e79", linewidth=1.0, marker="o", markersize=2)
    ax_chunk.set_ylabel("Сабмешей на чанк")
    ax_chunk.set_xlabel("Время замера, с")

    fig.savefig(os.path.join(out_dir, "05_rendering.png"))
    plt.close(fig)


def summarize_markers(frames, out_dir, label):
    marker_ms_columns = {marker: marker + "_ms" for marker, _ in MARKERS if marker + "_ms" in frames.columns}
    rows = []
    for stage in ("streaming", "traversal"):
        costs, totals, columns = marker_costs(frames, stage, marker_ms_columns)
        for marker, _title in MARKERS:
            if marker not in costs:
                continue
            rows.append({
                "Этап": STAGE_TITLES.get(stage, stage),
                "Маркер": marker,
                "Всего, мс": round(totals[marker], 1),
                "Колонок": columns,
                "мс/колонку": round(costs[marker], 3),
            })
    if rows:
        pd.DataFrame(rows).to_csv(os.path.join(out_dir, "markers.csv"), index=False, encoding="utf-8-sig")


def print_report(stats):
    print("=== {0} ===".format(stats["label"]))
    meta = stats.get("meta", {})
    print("  задержка FrameTiming: {0} кадров; первый кадр: {1:0.0} мс".format(
        meta.get("frame_timing_lag", 0), meta.get("startup_frame_ms", 0.0)))
    for item in stats["stages"]:
        print("  {0:24s} frames={1:5d} fps={2:6.1f} avg={3:7.2f} p95={4:7.2f} max={5:7.2f} >16.7={6:5.1f}%".format(
            item["title"], item["frames"], item["avg_fps"], item["frame_avg_ms"],
            item["frame_p95_ms"], item["frame_max_ms"], item["over_16_7_pct"]))
    for stage, data in stats["markers"].items():
        if data["columns"] == 0:
            continue
        print("  [{0}] columns={1}".format(stage, data["columns"]))
        for marker, _title in MARKERS:
            if marker in data["per_column_ms"]:
                print("    {0:24s} {1:8.2f} мс/колонку".format(marker, data["per_column_ms"][marker]))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("capture")
    parser.add_argument("out")
    parser.add_argument("--label", default="замер")
    args = parser.parse_args()

    os.makedirs(args.out, exist_ok=True)
    plt.rcParams.update(PLOT_STYLE)

    frames, scales, meta = load_capture(args.capture)
    stats = save_stats(frames, args.out, args.label, meta)
    print_report(stats)

    plot_overview(frames, args.out, args.label)
    plot_stage_bars(frames, args.out, args.label)
    plot_marker_breakdown(frames, args.out, args.label)
    plot_memory(frames, args.out, args.label, scales)
    plot_rendering(frames, args.out, args.label)
    summarize_markers(frames, args.out, args.label)

    print("Готово:", args.out)


if __name__ == "__main__":
    main()
