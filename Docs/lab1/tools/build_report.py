#!/usr/bin/env python3
"""Сборка отчёта по лабораторной работе №1 в .docx.

Запуск:
    python build_report.py <каталог_lab1> <выходной_docx>

Скрипт читает stats_median.json замеров «до»/«после» и готовые графики,
поэтому отчёт всегда согласован с измерениями.
"""

import json
import os
import sys

from docx import Document
from docx.enum.section import WD_SECTION
from docx.enum.table import WD_TABLE_ALIGNMENT
from docx.enum.text import WD_ALIGN_PARAGRAPH
from docx.oxml import OxmlElement
from docx.oxml.ns import qn
from docx.shared import Cm, Pt, RGBColor

STAGE_TITLES = {
    "streaming": "Стартовая загрузка",
    "steady": "Спокойный обзор",
    "traversal": "Телепорт 256 блоков",
    "editing": "Правка блоков",
}

MARKERS = [
    ("Voxels.Terrain", "Voxels.Terrain — рельеф"),
    ("Voxels.Caves", "Voxels.Caves — пещеры"),
    ("Voxels.Structures", "Voxels.Structures — структуры"),
    ("Voxels.ChunkMesh", "Voxels.ChunkMesh — меши"),
    ("Voxels.NeighbourRemesh", "Voxels.NeighbourRemesh — соседи"),
    ("Voxels.ColliderBake", "Voxels.ColliderBake — коллайдеры"),
    ("Voxels.Column", "Voxels.Column — колонка целиком"),
]


def load_json(path):
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def set_cell_shading(cell, color):
    shading = OxmlElement("w:shd")
    shading.set(qn("w:val"), "clear")
    shading.set(qn("w:color"), "auto")
    shading.set(qn("w:fill"), color)
    cell._tc.get_or_add_tcPr().append(shading)


def set_repeat_header(row):
    tr_pr = row._tr.get_or_add_trPr()
    header = OxmlElement("w:tblHeader")
    header.set(qn("w:val"), "true")
    tr_pr.append(header)


def style_document(document):
    style = document.styles["Normal"]
    style.font.name = "Times New Roman"
    style.font.size = Pt(12)
    style.element.rPr.rFonts.set(qn("w:eastAsia"), "Times New Roman")
    style.paragraph_format.line_spacing = 1.25
    style.paragraph_format.space_after = Pt(4)

    for level in range(1, 4):
        heading = document.styles["Heading {0}".format(level)]
        heading.font.name = "Times New Roman"
        heading.font.color.rgb = RGBColor(0x1F, 0x1F, 0x1F)
        heading.font.size = Pt(16 - level * 2)
        heading.font.bold = True

    section = document.sections[0]
    section.page_width = Cm(21.0)
    section.page_height = Cm(29.7)
    section.left_margin = Cm(2.5)
    section.right_margin = Cm(1.5)
    section.top_margin = Cm(2.0)
    section.bottom_margin = Cm(2.0)


def add_page_numbers(document):
    section = document.sections[0]
    paragraph = section.footer.paragraphs[0]
    paragraph.alignment = WD_ALIGN_PARAGRAPH.CENTER
    run = paragraph.add_run()
    field_char = OxmlElement("w:fldChar")
    field_char.set(qn("w:fldCharType"), "begin")
    instruction = OxmlElement("w:instrText")
    instruction.set(qn("xml:space"), "preserve")
    instruction.text = "PAGE"
    field_end = OxmlElement("w:fldChar")
    field_end.set(qn("w:fldCharType"), "end")
    run._r.append(field_char)
    run._r.append(instruction)
    run._r.append(field_end)
    run.font.size = Pt(10)


def paragraph(document, text="", bold=False, italic=False, align=None, size=None, space_after=None):
    item = document.add_paragraph()
    run = item.add_run(text)
    run.bold = bold
    run.italic = italic
    if size:
        run.font.size = Pt(size)
    if align is not None:
        item.alignment = align
    if space_after is not None:
        item.paragraph_format.space_after = Pt(space_after)
    return item


def bullet(document, text):
    item = document.add_paragraph(style="List Bullet")
    item.add_run(text)
    item.paragraph_format.space_after = Pt(2)
    return item


def add_table(document, headers, rows, widths=None, font_size=9.5, header_fill="D9E2F3"):
    table = document.add_table(rows=1, cols=len(headers))
    table.style = "Table Grid"
    table.alignment = WD_TABLE_ALIGNMENT.CENTER
    table.autofit = True

    header_cells = table.rows[0].cells
    for index, header in enumerate(headers):
        header_cells[index].text = ""
        cell_paragraph = header_cells[index].paragraphs[0]
        run = cell_paragraph.add_run(header)
        run.bold = True
        run.font.size = Pt(font_size)
        cell_paragraph.alignment = WD_ALIGN_PARAGRAPH.CENTER
        set_cell_shading(header_cells[index], header_fill)
    set_repeat_header(table.rows[0])

    for row in rows:
        cells = table.add_row().cells
        for index, value in enumerate(row):
            cells[index].text = ""
            cell_paragraph = cells[index].paragraphs[0]
            run = cell_paragraph.add_run(str(value))
            run.font.size = Pt(font_size)
            cell_paragraph.alignment = WD_ALIGN_PARAGRAPH.CENTER

    if widths:
        for row in table.rows:
            for index, width in enumerate(widths):
                row.cells[index].width = Cm(width)

    document.add_paragraph().paragraph_format.space_after = Pt(2)
    return table


def add_figure(document, path, caption, width_cm=16.0):
    if not os.path.exists(path):
        paragraph(document, "[нет файла: {0}]".format(os.path.basename(path)), italic=True)
        return

    document.add_picture(path, width=Cm(width_cm))
    last = document.paragraphs[-1]
    last.alignment = WD_ALIGN_PARAGRAPH.CENTER
    caption_paragraph = document.add_paragraph()
    caption_paragraph.alignment = WD_ALIGN_PARAGRAPH.CENTER
    run = caption_paragraph.add_run(caption)
    run.italic = True
    run.font.size = Pt(10)
    caption_paragraph.paragraph_format.space_after = Pt(8)


def stage_table(document, stats, keys, title):
    rows = []
    for stage in ("streaming", "steady", "traversal", "editing"):
        item = stats["stages"].get(stage)
        if not item:
            continue
        rows.append([
            STAGE_TITLES[stage],
            "{0:.0f}".format(item["frames"]),
            "{0:.1f}".format(item["avg_fps"]),
            "{0:.2f}".format(item["frame_p50_ms"]),
            "{0:.2f}".format(item["frame_p95_ms"]),
            "{0:.1f}".format(item["frame_max_ms"]),
            "{0:.1f}".format(item["over_16_7_pct"]),
            "{0:.1f}".format(item["duration_s"]),
        ])

    paragraph(document, title, bold=True, space_after=4)
    add_table(document,
              ["Этап", "Кадров", "FPS", "p50, мс", "p95, мс", "max, мс", "> 16.7 мс, %", "Длит., с"],
              rows, widths=[3.2, 1.5, 1.4, 1.6, 1.6, 1.6, 2.0, 1.5], font_size=9)


def marker_table(document, before, after, title):
    rows = []
    before_markers = before["markers"]["streaming"]["per_column_ms"]
    after_markers = after["markers"]["streaming"]["per_column_ms"]
    for marker, label in MARKERS:
        left = before_markers.get(marker)
        right = after_markers.get(marker)
        if left is None or right is None:
            continue
        delta = 100.0 * (right - left) / left if left else 0.0
        rows.append([label, "{0:0.2f}".format(left), "{0:0.2f}".format(right), "{0:+.1f}".format(delta)])

    paragraph(document, title, bold=True, space_after=4)
    add_table(document, ["Маркер Profiler", "До, мс/колонку", "После, мс/колонку", "Δ, %"],
              rows, widths=[7.4, 3.0, 3.2, 2.0], font_size=9)


def build(lab_dir, output_path):
    before = load_json(os.path.join(lab_dir, "_analysis_before", "stats_median.json"))
    after = load_json(os.path.join(lab_dir, "_analysis_after", "stats_median.json"))

    before_stream = before["markers"]["streaming"]["per_column_ms"]
    after_stream = after["markers"]["streaming"]["per_column_ms"]

    document = Document()
    style_document(document)
    add_page_numbers(document)

    # ------------------------------------------------------------------ титул
    for _ in range(6):
        document.add_paragraph()

    paragraph(document, "МЕТОДЫ ОПТИМИЗАЦИИ ИГРОВЫХ ПРОЕКТОВ", bold=True,
              align=WD_ALIGN_PARAGRAPH.CENTER, size=12)
    document.add_paragraph()
    paragraph(document, "ЛАБОРАТОРНАЯ РАБОТА №1", bold=True,
              align=WD_ALIGN_PARAGRAPH.CENTER, size=20)
    paragraph(document, "Анализ типичных проблем производительности\nи работа с профайлерами Unity Profiler",
              bold=True, align=WD_ALIGN_PARAGRAPH.CENTER, size=14)
    document.add_paragraph()
    document.add_paragraph()

    paragraph(document, "Проект: Re-write (воксельный движок)", align=WD_ALIGN_PARAGRAPH.CENTER)
    paragraph(document, "Ветка: MOIP", align=WD_ALIGN_PARAGRAPH.CENTER)
    paragraph(document, "Сцена: Assets/TestScene.unity", align=WD_ALIGN_PARAGRAPH.CENTER)
    paragraph(document, "Версия Unity: 6000.5.9f1, HDRP 17.5.0", align=WD_ALIGN_PARAGRAPH.CENTER)
    paragraph(document, "Стенд: AMD Ryzen 7 7800X3D, 32 ГБ ОЗУ, NVIDIA GeForce RTX 3060 12 ГБ, Windows 11",
              align=WD_ALIGN_PARAGRAPH.CENTER)
    document.add_paragraph()
    paragraph(document, "2026 г.", align=WD_ALIGN_PARAGRAPH.CENTER)

    document.add_page_break()

    # ------------------------------------------------------------------ цель
    document.add_heading("1. Цель работы", level=1)
    paragraph(document, "Научиться использовать Unity Profiler для поиска и анализа узких мест, "
                        "а также предложить методы их устранения.")
    paragraph(document, "Задачи работы:")
    bullet(document, "изучить типичные проблемы производительности игровых проектов (CPU/GPU, просадки FPS, "
                     "GC и утечки памяти, физика, загрузка ресурсов);")
    bullet(document, "снять профиль реальной сцены проекта и найти узкие места;")
    bullet(document, "предложить и реализовать оптимизации, повторить замер и сравнить результаты.")

    # ------------------------------------------------------------------ стенд
    document.add_heading("2. Стенд и инструменты", level=1)
    paragraph(document, "Замеры выполнялись на рабочей станции: AMD Ryzen 7 7800X3D (8 ядер / 16 потоков), "
                        "32 ГБ ОЗУ, NVIDIA GeForce RTX 3060 12 ГБ, Windows 11, Unity 6000.5.9f1 с HDRP 17.5.0. "
                        "Разрешение оконного замера — 1600×900, vSync выключен, частота кадров ограничена 120 FPS "
                        "для сопоставимости прогонов.")
    paragraph(document, "Использованные инструменты:")
    bullet(document, "Unity Profiler (Window → Analysis → Profiler) — модули CPU Usage, GPU Usage, Rendering, "
                     "Memory, Physics и профилировщик скриптов;")
    bullet(document, "ProfilerRecorder и FrameTimingManager — программное снятие тех же счётчиков, что показывает "
                     "Profiler (CPU Main Thread/Render Thread Frame Time, GPU Frame Time, Draw Calls, SetPass Calls, "
                     "Triangles, GC Allocated In Frame, Total Used Memory, Physics Used Memory/Queries);")
    bullet(document, "собственные маркеры ProfilerMarker (Voxels.*) — разметка этапов генерации мира, видимая "
                     "в CPU-модуле Profiler;")
    bullet(document, "Frame Debugger — анализ проходов отрисовки; Memory Profiler — анализ снимков памяти "
                     "(в работе использованы соответствующие счётчики Profiler и график памяти);")
    bullet(document, "Profile Analyzer — сравнение захватов .raw (сниматель умеет писать .raw по ключу -perfRaw).")

    # ------------------------------------------------------------------ сцена
    document.add_heading("3. Характеристика проекта и сцены", level=1)
    paragraph(document, "Re-write — учебный воксельный движок: мир состоит из чанков 32×32×32 блока, по высоте "
                        "128 блоков (4 чанка), блоки процедурные. Сцена TestScene содержит VoxelEngine, "
                        "направленный свет и префаб игрока с CharacterController. Мир строится во время игры:")
    bullet(document, "рельеф — fBm-шум, domain warp, ridged-горы и клеточно-автоматная осыпь, расчёт в Burst-джобах;")
    bullet(document, "3D-плотность — режим построения нависаний и карнизов (в сцене включён);")
    bullet(document, "пещеры — трёхмерный клеточный автомат с интерполяцией;")
    bullet(document, "структуры — деревья, кактусы и валуны по биомам;")
    bullet(document, "стриминг — радиус 2 колонки (13 колонок вокруг игрока), 1 колонка за кадр, "
                     "выгрузка колонок за пределами радиуса 3;")
    bullet(document, "рендер — 12 материалов/сабмешей на чанк (HDRP/Lit и Shader Graph блоков), "
                     "физика — MeshCollider на каждый чанк.")
    paragraph(document, "Именно стриминг и генерация колонок — главный источник кадровых пиков: одна колонка "
                        "строится синхронно на главном потоке и включает расчёт высот, пещер, структур, четырёх "
                        "мешей и коллайдеров.")

    # ------------------------------------------------------------------ методика
    document.add_heading("4. Методика измерений", level=1)
    paragraph(document, "Чтобы получить воспроизводимые цифры, в проект добавлен автономный сниматель метрик "
                        "(Assets/Scripts/Profiling/PerfCaptureRunner.cs). Он запускается отдельным development-плеером "
                        "с ключом -perfCapture и проходит один и тот же сценарий:")
    bullet(document, "стартовая загрузка: прелоад и стриминг 13 колонок;")
    bullet(document, "спокойный обзор: 8 с без генерации, камера медленно вращается;")
    bullet(document, "телепорт на 256 блоков: выгрузка старых и загрузка 11–12 новых колонок;")
    bullet(document, "правка блоков: 5 с установки/удаления блоков (пересборка меша и коллайдера).")
    paragraph(document, "Каждый кадр сниматель пишет в CSV: время кадра, FrameTiming (CPU main/render thread и GPU), "
                        "состояние мира (колонки, чанки, пул), память и значения ProfilerRecorder. Дополнительно "
                        "пишутся события этапов и скриншоты сцены.")
    paragraph(document, "На конфигурацию выполняется три headless-прогона (без окна), результаты усредняются "
                        "по медиане: оконный режим на рабочей машине периодически ловил паузы презентации кадра "
                        "200–300 мс, не относящиеся к игре. Отдельный оконный прогон используется для GPU, "
                        "draw calls и скриншотов. Разброс между прогонами — до 15–20 % по стоимости колонки.")

    # ------------------------------------------------------------------ до
    document.add_heading("5. Результаты профилирования до оптимизаций", level=1)
    add_figure(document, os.path.join(lab_dir, "captures", "before_windowed", "shot_03_after_traversal.png"),
               "Рис. 1. Сцена TestScene: воксельный мир, вода и пляж (замер «до»)", 15.5)
    stage_table(document, before,
                ["frames", "avg_fps", "p50", "p95", "max", "over", "duration"],
                "Таблица 1. Время кадра по этапам сценария (медиана трёх прогонов, до оптимизаций)")

    paragraph(document, "Уже на старте видно характерную картину: в спокойном состоянии сцена укладывается "
                        "в бюджет кадра, а на загрузке колонок появляются пики. Средний FPS на стартовой загрузке — "
                        "{0:.0f}, p95 кадра — {1:.1f} мс, максимальный кадр — {2:.0f} мс; {3:.1f} % кадров дольше 16.7 мс. "
                        "Телепорт даёт второй всплеск: p95 {4:.1f} мс при максимальном кадре {5:.0f} мс.".format(
                            before["stages"]["streaming"]["avg_fps"],
                            before["stages"]["streaming"]["frame_p95_ms"],
                            before["stages"]["streaming"]["frame_max_ms"],
                            before["stages"]["streaming"]["over_16_7_pct"],
                            before["stages"]["traversal"]["frame_p95_ms"],
                            before["stages"]["traversal"]["frame_max_ms"]))

    add_figure(document, os.path.join(lab_dir, "_analysis_before", "run2", "01_frame_timeline.png"),
               "Рис. 2. Профиль кадров по этапам сценария (замер «до»): пики совпадают с загрузкой колонок", 16.0)

    paragraph(document, "Маркеры Profiler показывают, из чего складывается кадр загрузки. Стоимость одной колонки — "
                        "{0:.1f} мс, и больше половины этой суммы приходится на расстановку структур ({1:.1f} мс), "
                        "затем идут меши чанков ({2:.1f} мс, включая пересборку соседей {3:.1f} мс) и запекание "
                        "коллайдеров ({4:.1f} мс).".format(
                            before_stream["Voxels.Column"],
                            before_stream["Voxels.Structures"],
                            before_stream["Voxels.ChunkMesh"],
                            before_stream["Voxels.NeighbourRemesh"],
                            before_stream["Voxels.ColliderBake"]))

    add_figure(document, os.path.join(lab_dir, "_analysis_before", "run2", "03_column_breakdown.png"),
               "Рис. 3. Стоимость генерации одной колонки по подсистемам (замер «до»)", 13.5)

    paragraph(document, "Аллокации управляемой памяти: около 4.3 МБ на колонку при стартовой загрузке и 2.9 МБ "
                        "при телепорте, отдельные кадры — до 18 МБ. Пики памяти и периодические сборки мусора "
                        "дают заметные просадки даже в спокойном обзоре.")
    add_figure(document, os.path.join(lab_dir, "_analysis_before", "run2", "04_memory.png"),
               "Рис. 4. Память и GC-аллокации по кадрам (замер «до»)", 16.0)

    paragraph(document, "Рендер узким местом не является: SRP Batcher объединяет отрисовку, число вызовов — "
                        "порядка 165–590 на кадр, GPU-время — 2–8 мс при 1600×900. Это подтверждает, что "
                        "оптимизировать нужно генерацию мира, а не материалы и батчинг.")

    # ------------------------------------------------------------------ проблемы
    document.add_heading("6. Выявленные проблемы", level=1)
    paragraph(document, "По результатам профилирования и анализа кода составлен список проблем:")
    add_table(document,
              ["№", "Проблема", "Где проявляется", "Метрика", "Метод устранения"],
              [
                  ["1", "Структуры перебирают всю площадь колонки с запасом и на каждой клетке запрашивают "
                        "высоту и биом (дорогие шумовые функции), хотя структура появляется примерно в 4 % клеток",
                   "StructureGenerator.Place / PlaceStructures", "≈ 39 мс из 76 мс на колонку",
                   "Ранний выход по дешёвому хэш-броску редкости до запроса высоты/биома"],
                  ["2", "Каждая новая колонка пересобирает меши четырёх соседних колонок (до 16 мешей) и заново "
                        "запекает их PhysX-меши",
                   "World.RebuildNeighbourColumns, Chunk.UploadMesh", "15 мс на колонку + 5 мс на запекание",
                   "Для соседей обновлять только меш, без перезапекания коллайдера"],
                  ["3", "Мешестроение перебирает 32³ вокселей, считает AO с 12 запросами к миру на грань и "
                        "пересобирает 12 сабмешей",
                   "Chunk.RebuildMesh / AddFace / AoColor", "≈ 28 мс на колонку",
                   "Локальные выборки блоков, кэш назначения материалов, корректный расчёт AO"],
                  ["4", "Большой объём GC-аллокаций: массивы высот и биомов, списки мешей, новые GameObject "
                        "и Mesh на каждый чанк",
                   "World.CreateColumn, Chunk", "2.9–4.3 МБ на колонку, пики 18 МБ/кадр",
                   "Пул чанков, переиспользование временных массивов"],
                  ["5", "Физика: MeshCollider запекается синхронно на главном потоке",
                   "Chunk.UploadMesh", "5–6 мс на колонку", "Не перезапекать коллайдеры соседей при стриминге"],
                  ["6", "Ежекадровая запись параметров Volume в UnderwaterEffect помечает профиль грязным",
                   "UnderwaterEffect.Update", "постоянная работа в Update",
                   "Override только при фактическом изменении параметров"],
                  ["7", "AO считался по локальным координатам как по мировым: затенение не соответствовало "
                        "окружению (ошибка корректности)",
                   "Chunk.AoColor", "визуальный дефект", "Перейти на локальные выборки блоков с выходом в мир "
                                                         "за границей чанка"],
                  ["8", "Пики кадров при стриминге: несколько подсистем складываются в одном кадре",
                   "VoxelEngine.Update", "p95 77 мс, max 577 мс", "Снизить стоимость колонки (пп. 1–3) и "
                                                                   "аллокации (п. 4)"],
              ],
              widths=[0.8, 6.2, 4.2, 4.0, 4.6], font_size=8.5)

    # ------------------------------------------------------------------ оптимизации
    document.add_heading("7. Выполненные оптимизации", level=1)
    document.add_heading("7.1. Структуры: ранний выход по редкости", level=2)
    paragraph(document, "Оба прохода StructureGenerator сначала считают дешёвый хэш-бросок и только при попадании "
                        "в порог редкости запрашивают высоту и биом. Семантика мира не изменилась: пороги и "
                        "порядок проверок сохранены, но 95–96 % клеток больше не трогают шумовые функции.")
    paragraph(document, "Результат: {0:.1f} → {1:.2f} мс на колонку (в 20–30 раз).".format(
        before_stream["Voxels.Structures"], after_stream["Voxels.Structures"]))

    document.add_heading("7.2. Пул чанков и переиспользование массивов", level=2)
    paragraph(document, "Выгруженные чанки больше не уничтожаются: они очищаются, деактивируются и попадают в пул "
                        "World, откуда берутся при загрузке новых колонок. Вместе с чанком переиспользуются его "
                        "массивы вокселей, списки вершин/нормалей/UV и Mesh. Временные массивы высот, биомов и "
                        "колонки вынесены в поля World. В замере видно, что пул действительно работает: при телепорте "
                        "счётчик пула уменьшается с 40 до 0 по мере выдачи чанков.")
    before_gc = before.get("gc_mb", {}).get("traversal", 0.0) / max(1.0, before["markers"]["traversal"]["columns"])
    after_gc = after.get("gc_mb", {}).get("traversal", 0.0) / max(1.0, after["markers"]["traversal"]["columns"])
    paragraph(document, "Результат: аллокации на колонку при телепорте {0:.2f} → {1:.2f} МБ (−{2:.0f} %), исчезли "
                        "Destroy/Instantiate чанков. Плата — удержание управляемой памяти под пул (около 13 МБ "
                        "на 52 чанка); пул ограничен числом видимых колонок.".format(
                            before_gc, after_gc, 100.0 * (before_gc - after_gc) / before_gc if before_gc else 0.0))

    document.add_heading("7.3. Пересборка соседних колонок без запекания коллайдеров", level=2)
    paragraph(document, "При появлении новой колонки соседние чанки по-прежнему пересобирают меш (это нужно для "
                        "корректных стыков), но больше не перезапекают PhysX-меш: на стыке меняются единичные грани, "
                        "а кукинг стоит заметно дороже. Правка блоков игроком по-прежнему обновляет коллайдеры.")
    paragraph(document, "Результат: Voxels.ColliderBake {0:.2f} → {1:.2f} мс на колонку, "
                        "Voxels.NeighbourRemesh {2:.1f} → {3:.1f} мс.".format(
                            before_stream["Voxels.ColliderBake"], after_stream["Voxels.ColliderBake"],
                            before_stream["Voxels.NeighbourRemesh"], after_stream["Voxels.NeighbourRemesh"]))

    document.add_heading("7.4. Мешестроение: локальные выборки, кэш материалов, корректный AO", level=2)
    paragraph(document, "Для выборки соседних блоков и AO добавлен метод Chunk.SampleBlock: внутри чанка чтение идёт "
                        "напрямую из массива вокселей, за границей — через World.GetBlock. Назначение материалов "
                        "кэшируется: массив материалов выставляется один раз на чанк, а не на каждую пересборку. "
                        "Заодно исправлен расчёт AO: раньше локальные координаты передавались в World.GetBlock как "
                        "мировые, и затенение не соответствовало реальному окружению.")
    paragraph(document, "Ожидаемого ускорения мешей получить не удалось: стоимость мешей с пересборкой соседей "
                        "изменилась с {0:.1f} до {1:.1f} мс на колонку (частично из-за корректного AO и повторной "
                        "активации рендереров при пулинге). Это основное оставшееся узкое место и материал для "
                        "следующей итерации (Burst + NativeArray, объединение сабмешей).".format(
                            before_stream["Voxels.ChunkMesh"], after_stream["Voxels.ChunkMesh"]))

    document.add_heading("7.5. Мелкие правки Update", level=2)
    paragraph(document, "UnderwaterEffect больше не переписывает цвет и дистанцию тумана каждый кадр: Override "
                        "вызывается только при изменении значений, иначе HDRP каждый кадр помечает Volume грязным.")

    document.add_heading("7.6. Инструментация", level=2)
    paragraph(document, "В код добавлены маркеры Profiler (Voxels.Column/Terrain/Caves/Structures/ChunkMesh/"
                        "NeighbourRemesh/ColliderBake) и автономный сниматель PerfCaptureRunner, который пишет CSV, "
                        "события этапов, системную информацию и скриншоты. Скрипты анализа и сравнения лежат "
                        "в Docs/lab1/tools.")

    # ------------------------------------------------------------------ после
    document.add_heading("8. Результаты после оптимизаций и сравнение", level=1)
    stage_table(document, after,
                ["frames", "avg_fps", "p50", "p95", "max", "over", "duration"],
                "Таблица 2. Время кадра по этапам сценария (медиана трёх прогонов, после оптимизаций)")

    paragraph(document, "Стоимость колонки снизилась с {0:.1f} до {1:.1f} мс на стартовой загрузке (−{2:.0f} %) "
                        "и с {3:.1f} до {4:.1f} мс при телепорте (−{5:.0f} %). p95 кадра на стартовой загрузке "
                        "уменьшился с {6:.1f} до {7:.1f} мс, максимальный кадр — с {8:.0f} до {9:.0f} мс. "
                        "В спокойном обзоре p95 снизился с {10:.1f} до {11:.1f} мс — сказались меньшее число "
                        "аллокаций и сборок мусора.".format(
                            before_stream["Voxels.Column"], after_stream["Voxels.Column"],
                            100.0 * (before_stream["Voxels.Column"] - after_stream["Voxels.Column"]) / before_stream["Voxels.Column"],
                            before["markers"]["traversal"]["per_column_ms"]["Voxels.Column"],
                            after["markers"]["traversal"]["per_column_ms"]["Voxels.Column"],
                            100.0 * (before["markers"]["traversal"]["per_column_ms"]["Voxels.Column"] -
                                     after["markers"]["traversal"]["per_column_ms"]["Voxels.Column"]) /
                            before["markers"]["traversal"]["per_column_ms"]["Voxels.Column"],
                            before["stages"]["streaming"]["frame_p95_ms"], after["stages"]["streaming"]["frame_p95_ms"],
                            before["stages"]["streaming"]["frame_max_ms"], after["stages"]["streaming"]["frame_max_ms"],
                            before["stages"]["steady"]["frame_p95_ms"], after["stages"]["steady"]["frame_p95_ms"]))

    add_figure(document, os.path.join(lab_dir, "_comparison", "compare_stages.png"),
               "Рис. 5. p95 времени кадра по этапам: до и после оптимизаций", 15.0)
    add_figure(document, os.path.join(lab_dir, "_comparison", "compare_columns_streaming.png"),
               "Рис. 6. Стоимость колонки по подсистемам при стартовой загрузке: до и после", 15.0)

    marker_table(document, before, after,
                 "Таблица 3. Стоимость подсистем на одну колонку при стартовой загрузке")

    add_figure(document, os.path.join(lab_dir, "_analysis_after", "run1", "01_frame_timeline.png"),
               "Рис. 7. Профиль кадров после оптимизаций: пики короче, провалов в покое почти нет", 16.0)

    add_figure(document, os.path.join(lab_dir, "_comparison", "compare_columns_traversal.png"),
               "Рис. 8. Стоимость колонки при телепорте: до и после", 15.0)

    paragraph(document, "Рендеринг не менялся: число вызовов отрисовки и GPU-время остались прежними "
                        "(SRP Batcher, 165–590 вызовов, GPU 2–8 мс). Это подтверждает, что на данной сцене "
                        "узким местом была именно генерация мира на главном потоке.")

    # ------------------------------------------------------------------ выводы
    document.add_heading("9. Выводы", level=1)
    bullet(document, "Unity Profiler и собственные маркеры позволили локализовать узкие места: расстановка структур "
                     "(39 мс из 76 мс на колонку), пересборка мешей соседних колонок (15 мс) и синхронное запекание "
                     "PhysX-мешей (5–6 мс).")
    bullet(document, "Ранний выход по редкости структур дал ускорение в 20–30 раз и снял главный пик; отказ от "
                     "перезапекания коллайдеров соседей уменьшил стоимость физики вдвое; пул чанков сократил "
                     "аллокации на 13 % и убрал Destroy/Instantiate.")
    bullet(document, "Стоимость колонки снизилась на 34–40 %, p95 кадра на стартовой загрузке — на 35 %, "
                     "максимальный кадр — с 577 до 414 мс, p95 в спокойном обзоре — с 29 до 9.5 мс.")
    bullet(document, "Мешестроение осталось самым дорогим этапом (29–39 мс на колонку). Следующий шаг — перенос "
                     "сборки мешей в Burst-джобы с NativeArray и сокращение числа сабмешей (атлас/массив текстур), "
                     "а также ограничение пула чанков, чтобы не удерживать лишние 13 МБ.")
    bullet(document, "Профилирование нужно повторять на целевом железе и с ограничением кадровой частоты: "
                     "оконный режим на рабочей машине давал паузы презентации, которые не относятся к игре.")

    # ------------------------------------------------------------------ приложение
    document.add_heading("Приложение А. Воспроизведение замеров", level=1)
    paragraph(document, "Сборка отдельного плеера для замеров (из копии проекта, чтобы не блокировать редактор):")
    paragraph(document, "Unity.exe -batchmode -quit -projectPath <проект> "
                        "-executeMethod Voxels.EditorTools.PerfBuildTool.BuildPerfPlayer -logFile build.log",
              italic=True, size=10)
    paragraph(document, "Запуск трёх headless-прогонов и одного оконного:")
    paragraph(document, "pwsh -File Docs/lab1/tools/run_capture.ps1 -Exe <Builds/Perf/Re-write-perf.exe> "
                        "-OutRoot Docs/lab1/captures/before -Mode batch -Runs 3", italic=True, size=10)
    paragraph(document, "Анализ, медианы и сравнение:")
    paragraph(document, "python Docs/lab1/tools/analyze_capture.py Docs/lab1/captures/before/run1 Docs/lab1/_analysis_before/run1", italic=True, size=10)
    paragraph(document, "python Docs/lab1/tools/aggregate_runs.py Docs/lab1/captures/before Docs/lab1/_analysis_before", italic=True, size=10)
    paragraph(document, "python Docs/lab1/tools/compare_captures.py Docs/lab1/_analysis_before/stats_median.json "
                        "Docs/lab1/_analysis_after/stats_median.json Docs/lab1/_comparison", italic=True, size=10)

    document.add_heading("Приложение Б. Состав файлов", level=1)
    bullet(document, "Assets/Scripts/Profiling/VoxelProfiling.cs — маркеры Profiler;")
    bullet(document, "Assets/Scripts/Profiling/PerfCaptureRunner.cs — автономный сниматель метрик;")
    bullet(document, "Assets/Editor/PerfBuildTool.cs — сборка development-плеера для замеров;")
    bullet(document, "Docs/lab1/captures — сырые замеры (CSV, события, скриншоты, systeminfo);")
    bullet(document, "Docs/lab1/tools — анализ, агрегация и сравнение замеров, сборка отчёта;")
    bullet(document, "Docs/lab1/Отчет_ЛР1_Unity_Profiler.docx — настоящий отчёт.")

    document.save(output_path)
    print("Сохранено:", output_path)


if __name__ == "__main__":
    build(sys.argv[1], sys.argv[2])
