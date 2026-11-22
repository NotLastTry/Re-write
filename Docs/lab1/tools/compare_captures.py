#!/usr/bin/env python3
"""Сравнение двух замеров PerfCapture (до/после) по медианам прогонов.

Запуск:
    python compare_captures.py <stats_median_before.json> <stats_median_after.json> <каталог>

Строит:
    compare_stages.png   — p95 времени кадра по этапам, до/после;
    compare_columns.png  — стоимость одной колонки по подсистемам, до/после;
    compare_table.csv    — таблица метрик с абсолютными и относительными изменениями.
"""

import argparse
import json
import os

import numpy as np
import pandas as pd

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

STAGE_ORDER = ["wait_world", "streaming", "steady", "traversal", "editing"]

COMPONENTS = [
    ("Voxels.Terrain", "Рельеф", "#4472c4"),
    ("Voxels.Caves", "Пещеры", "#7030a0"),
    ("Voxels.Structures", "Структуры", "#a9d18e"),
    ("mesh", "Меши чанков", "#ed7d31"),
    ("Voxels.NeighbourRemesh", "Пересборка соседей", "#ff0000"),
    ("other", "Прочее", "#bfbfbf"),
]


def load(path):
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def stage_value(data, stage, key):
    return data["stages"].get(stage, {}).get(key)


def component_costs(data, stage):
    markers = data["markers"].get(stage, {}).get("per_column_ms", {})
    row = {
        "Voxels.Terrain": markers.get("Voxels.Terrain", 0.0),
        "Voxels.Caves": markers.get("Voxels.Caves", 0.0),
        "Voxels.Structures": markers.get("Voxels.Structures", 0.0),
        "Voxels.NeighbourRemesh": markers.get("Voxels.NeighbourRemesh", 0.0),
    }
    mesh_total = markers.get("Voxels.ChunkMesh", 0.0)
    row["mesh"] = max(0.0, mesh_total - row["Voxels.NeighbourRemesh"])
    total = markers.get("Voxels.Column", 0.0)
    row["other"] = max(0.0, total - sum(row.values()))
    row["total"] = total
    return row


def plot_stages(before, after, out_dir):
    stages = [stage for stage in STAGE_ORDER
              if stage in before["stages"] and stage in after["stages"]]
    if not stages:
        return

    titles = [before["stages"][stage]["title"] for stage in stages]
    before_p95 = [before["stages"][stage]["frame_p95_ms"] for stage in stages]
    after_p95 = [after["stages"][stage]["frame_p95_ms"] for stage in stages]

    x = np.arange(len(stages))
    width = 0.36
    fig, ax = plt.subplots(figsize=(9.0, 3.9))
    ax.bar(x - width / 2, before_p95, width, color="#c00000", label="до")
    ax.bar(x + width / 2, after_p95, width, color="#548235", label="после")

    for index, (left, right) in enumerate(zip(before_p95, after_p95)):
        ax.text(index - width / 2, left + 1.0, "{0:.1f}".format(left), ha="center", fontsize=7)
        ax.text(index + width / 2, right + 1.0, "{0:.1f}".format(right), ha="center", fontsize=7)

    ax.axhline(16.7, color="#7f7f7f", linestyle="--", linewidth=0.9, label="16.7 мс (60 FPS)")
    ax.set_xticks(x)
    ax.set_xticklabels(titles, rotation=10, ha="right")
    ax.set_ylabel("p95 времени кадра, мс")
    ax.set_title("Время кадра по этапам сценария: до и после оптимизаций")
    ax.legend()
    ax.grid(True, axis="y", alpha=0.25)
    fig.savefig(os.path.join(out_dir, "compare_stages.png"), dpi=150, bbox_inches="tight")
    plt.close(fig)


def plot_columns(before, after, out_dir, stage="streaming"):
    rows = []
    for label, data in (("до", before), ("после", after)):
        costs = component_costs(data, stage)
        rows.append((label, costs, data["markers"].get(stage, {}).get("columns", 0)))

    fig, ax = plt.subplots(figsize=(8.4, 4.0))
    x = np.arange(len(rows))
    bottom = np.zeros(len(rows))
    for key, title, color in COMPONENTS:
        values = np.array([row[1][key] for row in rows])
        ax.bar(x, values, 0.45, bottom=bottom, color=color, label=title)
        bottom += values

    for index, (label, costs, columns) in enumerate(rows):
        ax.text(index, bottom[index] + 1.0, "{0:.1f} мс".format(costs["total"]),
                ha="center", fontsize=9, fontweight="bold")

    ax.set_xticks(x)
    ax.set_xticklabels(["{0}\n({1:0.0f} колонок)".format(label, columns) for label, _costs, columns in rows])
    ax.set_ylabel("Время на одну колонку, мс")
    ax.set_title("Стоимость генерации колонки по подсистемам ({0})".format("загрузка" if stage == "streaming" else "телепорт"))
    ax.legend(ncol=3, fontsize=7.5, loc="upper center", bbox_to_anchor=(0.5, -0.18))
    ax.grid(True, axis="y", alpha=0.25)
    fig.savefig(os.path.join(out_dir, "compare_columns_{0}.png".format(stage)), dpi=150, bbox_inches="tight")
    plt.close(fig)


def write_table(before, after, out_dir):
    names = {
        "streaming": "Стартовая загрузка",
        "steady": "Спокойный обзор",
        "traversal": "Телепорт 256 блоков",
        "editing": "Правка блоков",
    }
    rows = []

    for stage, title in names.items():
        if stage not in before["stages"] or stage not in after["stages"]:
            continue

        row = {"Этап": title}
        for key, label in (("avg_fps", "Средний FPS"),
                           ("frame_p50_ms", "Кадр p50, мс"),
                           ("frame_p95_ms", "Кадр p95, мс"),
                           ("frame_max_ms", "Кадр max, мс"),
                           ("over_16_7_pct", "Кадров > 16.7 мс, %")):
            left = before["stages"][stage][key]
            right = after["stages"][stage][key]
            row[label + " (до)"] = round(left, 2)
            row[label + " (после)"] = round(right, 2)
            row[label + " Δ, %"] = round(100.0 * (right - left) / left, 1) if left else 0.0

        # Стоимость колонки и аллокации по этапу.
        columns_before = before["markers"].get(stage, {}).get("columns") or 0
        columns_after = after["markers"].get(stage, {}).get("columns") or 0
        if columns_before and columns_after:
            row["Колонка, мс (до)"] = round(component_costs(before, stage)["total"], 2)
            row["Колонка, мс (после)"] = round(component_costs(after, stage)["total"], 2)
            row["Колонка Δ, %"] = round(100.0 * (row["Колонка, мс (после)"] - row["Колонка, мс (до)"]) / row["Колонка, мс (до)"], 1)

        rows.append(row)

    pd.DataFrame(rows).to_csv(os.path.join(out_dir, "compare_table.csv"), index=False, encoding="utf-8-sig")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("before")
    parser.add_argument("after")
    parser.add_argument("out")
    args = parser.parse_args()

    os.makedirs(args.out, exist_ok=True)
    before = load(args.before)
    after = load(args.after)

    plot_stages(before, after, args.out)
    plot_columns(before, after, args.out, "streaming")
    plot_columns(before, after, args.out, "traversal")
    write_table(before, after, args.out)

    print("Готово:", args.out)


if __name__ == "__main__":
    main()
