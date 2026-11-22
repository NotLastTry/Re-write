using System.Collections.Generic;
using UnityEngine;

namespace Voxels
{
    public sealed class VoxelEngine : MonoBehaviour
    {
        [Header("Мир")]
        [SerializeField] int _seed = 1337;

        [Tooltip("Необязательный override. Если пусто — описания блоков грузятся из Assets/Resources/Blocks.")]
        [SerializeField] BlockDatabase _blockDatabase;

        [Header("Генерация")]
        [Tooltip("3D-плотность вместо карты высот: нависания и карнизы")]
        [SerializeField] bool _useDensity = true;

        [Tooltip("Амплитуда 3D-шума, блоков")]
        [SerializeField, Range(0f, 30f)] float _densityAmplitude = 10f;

        [Tooltip("Горизонтальная частота 3D-шума")]
        [SerializeField, Range(0.002f, 0.05f)] float _densityFrequency = 0.012f;

        [Tooltip("Вертикальная частота 3D-шума")]
        [SerializeField, Range(0.005f, 0.06f)] float _densityVerticalFrequency = 0.02f;

        [Tooltip("0 — прежний рельеф, 1 — сплайновый в духе 1.18")]
        [SerializeField, Range(0f, 1f)] float _densitySplineBlend = 0.5f;

        [Tooltip("Порог шумовых карверов: больше — меньше пещер, 1 — выключено")]
        [SerializeField, Range(0f, 1f)] float _densityCaveThreshold = 0.6f;

        [Tooltip("Частота шумовых карверов")]
        [SerializeField, Range(0.005f, 0.05f)] float _densityCaveFrequency = 0.02f;

        [Header("Структуры")]
        [Tooltip("Деревья, кактусы и валуны по биомам")]
        [SerializeField] bool _structuresEnabled = true;

        [Header("Стриминг")]
        [Tooltip("Радиус загруженных колонок вокруг цели")]
        [SerializeField] int _viewRadius = 3;

        [Tooltip("Сколько колонок сгенерировать сразу при старте")]
        [SerializeField] int _preloadRadius = 1;

        [Tooltip("Сколько колонок грузить за кадр")]
        [SerializeField] int _columnsPerFrame = 1;

        [Tooltip("За кем стримить. Если пусто — Main Camera")]
        [SerializeField] Transform _target;

        [Header("Вода")]
        [Tooltip("Ассет материала воды (HDRP/Lit Transparent). Создать: Tools → Voxels → Create Water Material")]
        [SerializeField] Material _waterMaterial;

        [Tooltip("Прозрачность воды: 0 — почти невидимая, 1 — плотная")]
        [SerializeField, Range(0f, 1f)] float _waterAlpha = 0.35f;

        [Tooltip("Цвет подводного тумана")]
        [SerializeField] Color _underwaterFogColor = new Color(0.05f, 0.22f, 0.35f);

        [Tooltip("Дистанция видимости под водой, м")]
        [SerializeField, Range(1f, 80f)] float _underwaterFogDistance = 12f;

        [Header("Пещеры")]
        [SerializeField] bool _cavesEnabled = true;

        [Tooltip("Итерации сглаживания автомата: 2 обычно достаточно")]
        [SerializeField, Range(1, 4)] int _caveIterations = 2;

        [Tooltip("Доля твёрдого в пещерных зонах: больше значение — меньше пещер")]
        [SerializeField, Range(0f, 1f)] float _caveFill = 0.55f;

        [Tooltip("Сколько блоков от поверхности не трогать")]
        [SerializeField, Range(0, 8)] int _caveSurfaceMargin = 5;

        [Tooltip("Порог маски зон: больше — меньше зон с пещерами")]
        [SerializeField, Range(0f, 1f)] float _caveZoneThreshold = 0.3f;

        [Header("Обрывы")]
        [SerializeField] bool _cliffsEnabled = true;

        [Tooltip("Итерации осыпи: больше — мягче склоны и шире конусы")]
        [SerializeField, Range(0, 6)] int _cliffIterations = 2;

        [Tooltip("Доля излишка уклона, которая переносится за итерацию")]
        [SerializeField, Range(0f, 1f)] float _cliffStrength = 0.35f;

        [Tooltip("Максимальный перепад высоты на один блок")]
        [SerializeField, Range(0.5f, 4f)] float _cliffMaxSlope = 1.2f;

        [Tooltip("Высота структурных обрывов (плато), блоков")]
        [SerializeField, Range(0f, 30f)] float _cliffUplift = 14f;

        [Tooltip("Порог маски обрывов: выше — плато приподнято")]
        [SerializeField, Range(0f, 1f)] float _cliffThreshold = 0.25f;

        [Header("Производительность")]
        [Tooltip("Считать высоты и пещеры Burst-джобами (выключить для сравнения)")]
        [SerializeField] bool _useJobs = true;

        [Header("Отладка")]
        [Tooltip("Перестраивать мир при изменении ползунков в Play Mode")]
        [SerializeField] bool _liveRebuild = true;

        [Tooltip("Красить крутые склоны ярко-розовым блоком")]
        [SerializeField] bool _debugSlope = false;

        [Tooltip("Порог уклона для подсветки (блоков на блок)")]
        [SerializeField, Range(1, 16)] int _debugSlopeThreshold = 3;

        bool _pendingRebuild;
        float _rebuildAt;
        int _appliedSeed;

        public World CurrentWorld { get; private set; }
        public int PendingColumns => _pending.Count;

        public Color UnderwaterFogColor => _underwaterFogColor;
        public float UnderwaterFogDistance => _underwaterFogDistance;

        readonly List<Vector2Int> _pending = new List<Vector2Int>();
        readonly List<Vector2Int> _unloadScratch = new List<Vector2Int>();
        readonly System.Diagnostics.Stopwatch _loadWatch = new System.Diagnostics.Stopwatch();

        int _centerX = int.MinValue;
        int _centerZ = int.MinValue;
        bool _loading;
        float _lastLogTime = -10f;

        void Start()
        {
            _appliedSeed = _seed;
            CurrentWorld = new World(new TerrainGenerator(_seed));

            ApplyWorldSettings();
            CurrentWorld.Generator.ResetDebugStats();

            if (_target == null && Camera.main != null)
                _target = Camera.main.transform;

            PlaceTargetAboveTerrain();
            Preload();
        }

        void Update()
        {
            if (CurrentWorld == null)
                return;

            if (_pendingRebuild && Time.realtimeSinceStartup >= _rebuildAt)
            {
                _pendingRebuild = false;
                RebuildWorld();
            }

            UpdateCenter();

            int budget = Mathf.Max(1, _columnsPerFrame);
            while (budget > 0 && _pending.Count > 0)
            {
                int last = _pending.Count - 1;
                Vector2Int column = _pending[last];
                _pending.RemoveAt(last);

                if (CurrentWorld.HasColumn(column.x, column.y))
                    continue;

                CurrentWorld.CreateColumn(column.x, column.y, transform);
                budget--;
            }

            UpdateLoadTimer();
        }

        void UpdateCenter()
        {
            Vector3 position = _target != null ? _target.position : transform.position;
            int centerX = Mathf.FloorToInt(position.x) >> VoxelConstants.ChunkShift;
            int centerZ = Mathf.FloorToInt(position.z) >> VoxelConstants.ChunkShift;

            if (centerX == _centerX && centerZ == _centerZ)
                return;

            _centerX = centerX;
            _centerZ = centerZ;

            RebuildQueue();
            UnloadDistant();
        }

        void Preload()
        {
            Vector3 position = _target != null ? _target.position : transform.position;
            _centerX = Mathf.FloorToInt(position.x) >> VoxelConstants.ChunkShift;
            _centerZ = Mathf.FloorToInt(position.z) >> VoxelConstants.ChunkShift;

            int radius = Mathf.Max(0, _preloadRadius);
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dz = -radius; dz <= radius; dz++)
                {
                    if (InRadius(dx, dz, radius))
                        CurrentWorld.CreateColumn(_centerX + dx, _centerZ + dz, transform);
                }
            }

            RebuildQueue();
        }

        void RebuildQueue()
        {
            _pending.Clear();

            int radius = Mathf.Max(0, _viewRadius);
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dz = -radius; dz <= radius; dz++)
                {
                    if (!InRadius(dx, dz, radius))
                        continue;

                    int x = _centerX + dx;
                    int z = _centerZ + dz;
                    if (CurrentWorld.HasColumn(x, z))
                        continue;

                    _pending.Add(new Vector2Int(x, z));
                }
            }

            int centerX = _centerX;
            int centerZ = _centerZ;

            // дальние в начало списка, ближние в конец — берём с конца
            _pending.Sort((a, b) => DistanceSquared(b, centerX, centerZ)
                .CompareTo(DistanceSquared(a, centerX, centerZ)));
        }

        void UnloadDistant()
        {
            _unloadScratch.Clear();
            int radius = Mathf.Max(0, _viewRadius) + 1;

            foreach (KeyValuePair<ChunkId, Chunk> pair in CurrentWorld.Chunks)
            {
                ChunkId id = pair.Key;
                if (id.Y != 0)
                    continue;

                if (InRadius(id.X - _centerX, id.Z - _centerZ, radius))
                    continue;

                _unloadScratch.Add(new Vector2Int(id.X, id.Z));
            }

            for (int i = 0; i < _unloadScratch.Count; i++)
                CurrentWorld.RemoveColumn(_unloadScratch[i].x, _unloadScratch[i].y);
        }

        void UpdateLoadTimer()
        {
            if (_pending.Count > 0)
            {
                if (!_loading)
                {
                    _loading = true;
                    _loadWatch.Restart();
                }
                return;
            }

            if (!_loading)
                return;

            _loading = false;
            _loadWatch.Stop();

            // Не спамим в консоль при каждом подгрузе: не чаще раза в 2 секунды.
            if (Time.realtimeSinceStartup - _lastLogTime < 2f)
                return;

            _lastLogTime = Time.realtimeSinceStartup;

            Debug.Log($"[Voxels] Стриминг: колонок {CurrentWorld.ColumnCount}, " +
                      $"чанков {CurrentWorld.ChunkCount}, {_loadWatch.ElapsedMilliseconds} мс");

            if (_debugSlope && CurrentWorld.Generator.DebugScannedCells > 0)
            {
                TerrainGenerator generator = CurrentWorld.Generator;
                float percent = 100f * generator.DebugSteepCells / generator.DebugScannedCells;
                Debug.Log($"[Voxels] Крутых склонов (>{_debugSlopeThreshold} блоков): {percent:0.0}% " +
                          $"({generator.DebugSteepCells} из {generator.DebugScannedCells})");

                int total = 0;
                for (int i = 0; i < generator.BiomeCells.Length; i++)
                    total += generator.BiomeCells[i];

                if (total > 0)
                {
                    string[] names = { "равнины", "лес", "пустыня", "тундра", "пляж", "горы" };
                    var text = new System.Text.StringBuilder("[Voxels] Биомы: ");

                    for (int i = 0; i < generator.BiomeCells.Length; i++)
                    {
                        if (i > 0)
                            text.Append(", ");

                        float share = 100f * generator.BiomeCells[i] / total;
                        text.Append(names[i]).Append(' ').Append(share.ToString("0.0")).Append('%');
                    }

                    Debug.Log(text.ToString());
                }
            }
        }

        static bool InRadius(int dx, int dz, int radius) => dx * dx + dz * dz <= radius * radius;

        static int DistanceSquared(Vector2Int column, int centerX, int centerZ)
        {
            int dx = column.x - centerX;
            int dz = column.y - centerZ;
            return dx * dx + dz * dz;
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            if (!Application.isPlaying || !_liveRebuild || CurrentWorld == null)
                return;

            _pendingRebuild = true;
            _rebuildAt = Time.realtimeSinceStartup + 0.25f;
        }
#endif
        void ApplyWorldSettings()
        {
            if (CurrentWorld == null)
                return;

            BlockDatabase.SetCurrent(_blockDatabase != null ? _blockDatabase : BlockDatabase.Ensure());

            VoxelMaterials.ApplyWaterMaterial(_waterMaterial);
            VoxelMaterials.ApplyWaterAlpha(_waterAlpha);

            CurrentWorld.UseDensity = _useDensity;
            CurrentWorld.Structures.Enabled = _structuresEnabled;
            CurrentWorld.Density.Amplitude = _densityAmplitude;
            CurrentWorld.Density.Frequency = _densityFrequency;
            CurrentWorld.Density.VerticalFrequency = _densityVerticalFrequency;
            CurrentWorld.Density.SplineBlend = _densitySplineBlend;
            CurrentWorld.Density.CaveThreshold = _densityCaveThreshold;
            CurrentWorld.Density.CaveFrequency = _densityCaveFrequency;

            CurrentWorld.Caves.Enabled = _cavesEnabled;
            CurrentWorld.Caves.Iterations = _caveIterations;
            CurrentWorld.Caves.Fill = _caveFill;
            CurrentWorld.Caves.SurfaceMargin = _caveSurfaceMargin;
            CurrentWorld.Caves.RegionThreshold = _caveZoneThreshold;
            CurrentWorld.Caves.UseJobs = _useJobs;

            CurrentWorld.Generator.UseJobs = _useJobs;

            CurrentWorld.Generator.CliffsEnabled = _cliffsEnabled;
            CurrentWorld.Generator.CliffIterations = _cliffIterations;
            CurrentWorld.Generator.CliffStrength = _cliffStrength;
            CurrentWorld.Generator.CliffMaxSlope = _cliffMaxSlope;
            CurrentWorld.Generator.CliffUplift = _cliffUplift;
            CurrentWorld.Generator.CliffThreshold = _cliffThreshold;

            // Плотность использует климат генератора, поэтому обновляем его после смены настроек.
            CurrentWorld.Density.Configure(CurrentWorld.Generator.GetParams(), CurrentWorld.Generator.Seed);

            CurrentWorld.Generator.DebugSlope = _debugSlope;
            CurrentWorld.Generator.DebugSlopeThreshold = _debugSlopeThreshold;
        }

        /// <summary>Полная пересборка: выгружаем всё и даём стримингу сгенерировать заново.</summary>
        void RebuildWorld()
        {
            _pending.Clear();

            _unloadScratch.Clear();
            foreach (KeyValuePair<ChunkId, Chunk> pair in CurrentWorld.Chunks)
            {
                if (pair.Key.Y == 0)
                    _unloadScratch.Add(new Vector2Int(pair.Key.X, pair.Key.Z));
            }

            for (int i = 0; i < _unloadScratch.Count; i++)
                CurrentWorld.RemoveColumn(_unloadScratch[i].x, _unloadScratch[i].y, false);

            if (_appliedSeed != _seed)
            {
                CurrentWorld = new World(new TerrainGenerator(_seed));
                _appliedSeed = _seed;
            }

            ApplyWorldSettings();
            CurrentWorld.Generator.ResetDebugStats();

            // заставляем UpdateCenter пересчитать центр и заново наполнить очередь
            _centerX = int.MinValue;
            _centerZ = int.MinValue;
            _loading = false;
        }

        void PlaceTargetAboveTerrain()
        {
            Transform target = _target;

            PlayerController player = FindAnyObjectByType<PlayerController>();
            if (player != null)
                target = player.transform;

            if (target == null)
                return;

            Vector3 position = target.position;
            int surface = CurrentWorld.GetSurfaceHeight(
                Mathf.FloorToInt(position.x),
                Mathf.FloorToInt(position.z));

            if (position.y < surface + 3f)
            {
                target.position = new Vector3(position.x, surface + 12f, position.z);
                Debug.Log($"[Voxels] Спавн под землёй (y={position.y:0}, поверхность {surface}) — игрок поднят.");
            }
        }
    }
}