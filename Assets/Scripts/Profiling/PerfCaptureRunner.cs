#if DEVELOPMENT_BUILD || UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace Voxels.Profiling
{
    /// <summary>
    /// Автономный сниматель метрик производительности для лабораторной работы.
    /// Запускается только с ключом -perfCapture, чтобы не влиять на обычный запуск:
    ///   Re-write-perf.exe -perfCapture -perfOut "C:\captures\before" [-perfRaw "C:\captures\before\capture.raw"]
    ///
    /// Пишет в каталог замера:
    ///   frames.csv        — покадровые метрики (FrameTiming, ProfilerRecorder, состояние мира);
    ///   events.csv        — переходы между этапами сценария;
    ///   systeminfo.txt    — конфигурация машины и список доступных счётчиков;
    ///   summary.txt       — сводка по этапам (FPS, перцентили кадрового времени, стоимость колонки);
    ///   shot_*.png        — скриншоты сцены.
    ///
    /// Сценарий: загрузка мира -> спокойный обзор -> телепорт на 256 блоков (стриминг)
    /// -> правка блоков. Так один и тот же замер повторяется до и после оптимизаций.
    /// </summary>
    [DefaultExecutionOrder(32000)]
    public sealed class PerfCaptureRunner : MonoBehaviour
    {
        const string FlagCapture = "-perfCapture";
        const string FlagOut = "-perfOut";
        const string FlagRaw = "-perfRaw";

        const float SteadySeconds = 8f;
        const float TraversalSettleSeconds = 3f;
        const float EditingSeconds = 5f;
        const float MaxStageSeconds = 120f;
        const int TraversalChunkDistance = 8; // 8 чанков = 256 блоков

        enum Stage
        {
            WaitWorld,
            Streaming,
            Steady,
            Traversal,
            Editing,
            Finished
        }

        sealed class CounterInfo
        {
            public string Name;
            public string Category;
            public string Unit;
            public string DataType;
            public ProfilerRecorder Recorder;
        }

        static readonly string[] InterestingKeywords =
        {
            "Voxels.",
            "CPU Main Thread Frame Time", "CPU Render Thread Frame Time", "GPU Frame Time",
            "Draw Calls", "Triangles Count", "SetPass Calls", "Batches Count",
            "GC Allocated In Frame", "Total Used Memory",
            "Physics Used Memory", "Physics Queries"
        };

        /// <summary>Потолок кадровой частоты: делает замеры сравнимыми и ограничивает накладные расходы снимателя.</summary>
        const int CaptureTargetFps = 120;

        readonly List<CounterInfo> _counters = new List<CounterInfo>();
        readonly List<ProfilerRecorderHandle> _handles = new List<ProfilerRecorderHandle>();
        readonly HashSet<string> _boundNames = new HashSet<string>();
        readonly FrameTiming[] _timings = new FrameTiming[1];
        readonly List<double>[] _stageFrameTimes = new List<double>[6];
        readonly StringBuilder _line = new StringBuilder(4096);

        string _outDir;
        string _rawPath;
        StreamWriter _frames;
        StreamWriter _events;
        StreamWriter _debugCounters;
        Stage _stage = Stage.WaitWorld;
        float _stageStartedAt;
        float _stageMaxSeconds;
        VoxelEngine _engine;
        PlayerController _player;
        Transform _playerTransform;
        int _lastChunkCount;
        int _columnsCreated;
        int _shots;
        int _shotFrame = -10000;
        float _spin;
        float _nextEditAt;
        int _editIndex;
        int _editX;
        int _editZ;
        int _editY;
        bool _editRemoved;
        bool _ready;
        bool _streamingSawEmpty;
        float _streamingEmptySince;
        bool _quitRequested;
        string _abortReason;
        float _awakeTime;
        float _firstUpdateTime;

        static string _pendingOutDir;
        static string _pendingRawPath;

        public static void Boot()
        {
            string[] args = Environment.GetCommandLineArgs();
            bool enabled = false;
            string outDir = null;
            string rawPath = null;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == FlagCapture)
                    enabled = true;
                else if (args[i] == FlagOut && i + 1 < args.Length)
                    outDir = args[i + 1];
                else if (args[i] == FlagRaw && i + 1 < args.Length)
                    rawPath = args[i + 1];
            }

            if (!enabled)
                return;

            // Каталог передаём до AddComponent: Awake вызывается синхронно внутри него.
            _pendingOutDir = string.IsNullOrEmpty(outDir)
                ? Path.Combine(Application.persistentDataPath, "perfcapture")
                : outDir;
            _pendingRawPath = rawPath;

            var go = new GameObject("[PerfCapture]");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<PerfCaptureRunner>();
        }

        void Awake()
        {
            _outDir = _pendingOutDir;
            _rawPath = _pendingRawPath;
            _awakeTime = Time.realtimeSinceStartup;

            for (int i = 0; i < _stageFrameTimes.Length; i++)
                _stageFrameTimes[i] = new List<double>(4096);

            try
            {
                Directory.CreateDirectory(_outDir);
            }
            catch (Exception exception)
            {
                Debug.LogError("[PerfCapture] Не удалось создать каталог " + _outDir + ": " + exception.Message);
                _quitRequested = true;
#if !UNITY_EDITOR
                Application.Quit(3);
#endif
                return;
            }

            Application.targetFrameRate = CaptureTargetFps;
            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;

            WriteSystemInfo();
            _events = new StreamWriter(Path.Combine(_outDir, "events.csv"), false, new UTF8Encoding(false));
            _events.WriteLine("time_s,stage,event,chunks,columns,pending");

#if DEVELOPMENT_BUILD
            try
            {
                Profiler.enabled = true;
                if (!string.IsNullOrEmpty(_rawPath))
                {
                    Profiler.logFile = _rawPath;
                    Profiler.enableBinaryLog = true;
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[PerfCapture] Профайлер недоступен: " + exception.Message);
            }
#endif

            Debug.Log("[PerfCapture] Каталог замера: " + _outDir);
        }

        void OnDestroy()
        {
            CloseLogs();
        }

        void Update()
        {
            if (_quitRequested)
                return;

            FrameTimingManager.CaptureFrameTimings();
            uint captured = FrameTimingManager.GetLatestTimings(1, _timings);

            if (!_ready)
            {
                if (Time.realtimeSinceStartup > 20f)
                {
                    Debug.LogError("[PerfCapture] VoxelEngine не найден в сцене за 20 с — замер отменён.");
                    _quitRequested = true;
#if !UNITY_EDITOR
                    Application.Quit(2);
#endif
                    return;
                }

                if (!Prepare())
                    return;
            }

            AdvanceStage();
            HoldPlayerAboveGround();
            RecordFrame(captured > 0 ? _timings[0] : default);
            MaybeFinish();
        }

        /// <summary>
        /// Держит камеру над поверхностью, пока колонки ещё грузятся: иначе игрок
        /// проваливается в незагруженный мир, и замер идёт под землёй (чёрный кадр).
        /// </summary>
        void HoldPlayerAboveGround()
        {
            if (_playerTransform == null || _engine == null || _engine.CurrentWorld == null)
                return;

            if (_stage != Stage.Streaming && _stage != Stage.Steady && _stage != Stage.Traversal)
                return;

            Vector3 position = _playerTransform.position;
            int x = Mathf.FloorToInt(position.x);
            int z = Mathf.FloorToInt(position.z);
            int surface = _engine.CurrentWorld.GetSurfaceHeight(x, z);

            _playerTransform.position = new Vector3(x + 0.5f, surface + 12f, z + 0.5f);

            if (_stage == Stage.Steady)
                _playerTransform.rotation = Quaternion.Euler(0f, _spin, 0f);

            if (_player != null && _player.CameraTransform != null)
                _player.CameraTransform.localRotation = Quaternion.Euler(-15f, 0f, 0f);
        }

        bool Prepare()
        {
            _engine = UnityEngine.Object.FindAnyObjectByType<VoxelEngine>();
            if (_engine == null)
                return false;

            _player = UnityEngine.Object.FindAnyObjectByType<PlayerController>();
            if (_player != null)
                _playerTransform = _player.transform;

            ScanCounters();

            _frames = new StreamWriter(Path.Combine(_outDir, "frames.csv"), false, new UTF8Encoding(false));
            _line.Clear();
            _line.Append("time_s,frame,stage,dt_ms,fps,cpu_frame_ms,cpu_main_ms,cpu_render_ms,gpu_ms,");
            _line.Append("chunks,columns,cols_new,pooled,renderers,submeshes,submesh_indices,mono_mb,total_mb,reserved_mb,");
            _line.Append("shot,player_x,player_y,player_z");

            for (int i = 0; i < _counters.Count; i++)
                _line.Append(',').Append(Sanitize(_counters[i].Name));

            _frames.WriteLine(_line.ToString());
            _frames.Flush();

            _stageStartedAt = Time.realtimeSinceStartup;
            _firstUpdateTime = _stageStartedAt;
            _lastChunkCount = 0;
            _ready = true;

            Event("capture_started", string.Format(CultureInfo.InvariantCulture,
                "engine_found; awake_at={0:0.000}; first_update_at={1:0.000}", _awakeTime, _firstUpdateTime));
            return true;
        }

        void ScanCounters()
        {
            _handles.Clear();
            ProfilerRecorderHandle.GetAvailable(_handles);

            for (int i = 0; i < _handles.Count; i++)
            {
                ProfilerRecorderHandle handle = _handles[i];
                if (!handle.Valid)
                    continue;

                ProfilerRecorderDescription description = ProfilerRecorderHandle.GetDescription(handle);
                string name = description.Name;
                if (string.IsNullOrEmpty(name) || _boundNames.Contains(name))
                    continue;

                if (!IsInteresting(name))
                    continue;

                ProfilerRecorder recorder;
                try
                {
                    recorder = ProfilerRecorder.StartNew(description.Category, name, 1);
                }
                catch (Exception)
                {
                    continue;
                }

                if (!recorder.Valid)
                    continue;

                _boundNames.Add(name);
                _counters.Add(new CounterInfo
                {
                    Name = name,
                    Category = description.Category.Name,
                    Unit = description.UnitType.ToString(),
                    DataType = description.DataType.ToString(),
                    Recorder = recorder
                });

                AppendDebugCounter(name, description.Category.Name, description.UnitType.ToString(), description.DataType.ToString());
            }
        }

        static bool IsInteresting(string name)
        {
            for (int i = 0; i < InterestingKeywords.Length; i++)
            {
                if (name.IndexOf(InterestingKeywords[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        void AdvanceStage()
        {
            float now = Time.realtimeSinceStartup;
            float elapsed = now - _stageStartedAt;

            switch (_stage)
            {
                case Stage.WaitWorld:
                    if (_engine != null && _engine.CurrentWorld != null)
                    {
                        _player = UnityEngine.Object.FindAnyObjectByType<PlayerController>();
                        _playerTransform = _player != null ? _player.transform : null;
                        ChangeStage(Stage.Streaming, MaxStageSeconds);
                        _streamingEmptySince = -1f;
                    }
                    break;

                case Stage.Streaming:
                    if (_engine.PendingColumns == 0)
                    {
                        if (_streamingEmptySince < 0f)
                            _streamingEmptySince = now;

                        if (now - _streamingEmptySince > 0.75f)
                        {
                            Event("streaming_done", "pending=" + _engine.PendingColumns +
                                ", columns=" + _engine.CurrentWorld.ColumnCount);
                            TakeScreenshot("steady");
                            ChangeStage(Stage.Steady, SteadySeconds);
                        }
                    }
                    else
                    {
                        _streamingEmptySince = -1f;
                    }

                    if (elapsed > _stageMaxSeconds)
                        ChangeStage(Stage.Steady, SteadySeconds);
                    break;

                case Stage.Steady:
                    _spin += 24f * Time.unscaledDeltaTime;
                    if (_playerTransform != null)
                        _playerTransform.rotation = Quaternion.Euler(0f, _spin, 0f);

                    if (elapsed >= _stageMaxSeconds)
                    {
                        TakeScreenshot("before_traversal");
                        Teleport();
                        Event("traversal_start", "teleport+" + (TraversalChunkDistance * VoxelConstants.ChunkSize));
                        ChangeStage(Stage.Traversal, MaxStageSeconds);
                        _streamingEmptySince = -1f;
                    }
                    break;

                case Stage.Traversal:
                    if (_engine.PendingColumns == 0)
                    {
                        if (_streamingEmptySince < 0f)
                            _streamingEmptySince = now;

                        if (now - _streamingEmptySince > TraversalSettleSeconds)
                        {
                            Event("traversal_done", "columns=" + _engine.CurrentWorld.ColumnCount);
                            TakeScreenshot("after_traversal");
                            BeginEditing();
                        }
                    }
                    else
                    {
                        _streamingEmptySince = -1f;
                    }

                    if (elapsed > _stageMaxSeconds)
                        BeginEditing();
                    break;

                case Stage.Editing:
                    if (now >= _nextEditAt && _engine.CurrentWorld != null)
                    {
                        NextEdit();
                        _nextEditAt = now + 0.1f;
                    }

                    if (elapsed >= EditingSeconds)
                    {
                        TakeScreenshot("editing");
                        ChangeStage(Stage.Finished, 0f);
                    }
                    break;
            }
        }

        void BeginEditing()
        {
            ChangeStage(Stage.Editing, EditingSeconds);
            _nextEditAt = Time.realtimeSinceStartup;
            _editIndex = 0;

            Vector3 position = _playerTransform != null ? _playerTransform.position : transform.position;
            _editX = Mathf.FloorToInt(position.x);
            _editZ = Mathf.FloorToInt(position.z);
            _editRemoved = false;

            Event("editing_start", "x=" + _editX + ", z=" + _editZ);
        }

        void NextEdit()
        {
            World world = _engine.CurrentWorld;
            if (world == null)
                return;

            if (_editRemoved)
            {
                world.SetBlock(_editX, _editY, _editZ, BlockType.Grass);
                _editRemoved = false;
            }
            else
            {
                // Каждая четвёртая правка — на границе чанка, чтобы задеть пересборку соседей.
                _editX += 1;
                _editY = world.GetSurfaceHeight(_editX, _editZ);
                world.SetBlock(_editX, _editY, _editZ, BlockType.Air);
                _editRemoved = true;
            }

            _editIndex++;
        }

        void Teleport()
        {
            if (_playerTransform == null)
            {
                _abortReason = "player_not_found";
                return;
            }

            int targetX = Mathf.FloorToInt(_playerTransform.position.x) +
                          TraversalChunkDistance * VoxelConstants.ChunkSize;
            int targetZ = Mathf.FloorToInt(_playerTransform.position.z);
            int surface = _engine.CurrentWorld.GetSurfaceHeight(targetX, targetZ);

            CharacterController controller = _playerTransform.GetComponent<CharacterController>();
            if (controller != null)
                controller.enabled = false;

            _playerTransform.position = new Vector3(targetX, surface + 12f, targetZ);

            if (controller != null)
                controller.enabled = true;
        }

        void RecordFrame(FrameTiming timing)
        {
            float dt = Time.unscaledDeltaTime;
            double dtMs = dt * 1000.0;
            int columns = _engine != null && _engine.CurrentWorld != null ? _engine.CurrentWorld.ColumnCount : 0;
            int chunks = _engine != null && _engine.CurrentWorld != null ? _engine.CurrentWorld.ChunkCount : 0;
            int newColumns = Mathf.Max(0, columns - _lastChunkCount);
            _lastChunkCount = columns;
            _columnsCreated += newColumns;

            int renderers = 0;
            int submeshes = 0;
            long submeshIndices = 0;

            if (Time.frameCount % 60 == 0)
                CountRenderStats(out renderers, out submeshes, out submeshIndices);
            else
            {
                renderers = -1;
                submeshes = -1;
                submeshIndices = -1;
            }

            _line.Clear();
            AddField(_line, Time.realtimeSinceStartup, 3);
            AddInt(_line, Time.frameCount);
            AddText(_line, StageName(_stage));
            AddField(_line, dtMs, 3);
            AddField(_line, dt > 0f ? 1.0 / dt : 0.0, 2);
            AddField(_line, timing.cpuFrameTime, 3);
            AddField(_line, timing.cpuMainThreadFrameTime, 3);
            AddField(_line, timing.cpuRenderThreadFrameTime, 3);
            AddField(_line, timing.gpuFrameTime, 3);
            AddInt(_line, chunks);
            AddInt(_line, columns);
            AddInt(_line, newColumns);
            AddInt(_line, _engine != null && _engine.CurrentWorld != null ? _engine.CurrentWorld.PooledChunkCount : 0);
            AddInt(_line, renderers);
            AddInt(_line, submeshes);
            AddInt(_line, submeshIndices);
            AddField(_line, Profiler.GetMonoUsedSizeLong() / 1048576.0, 2);
            AddField(_line, Profiler.GetTotalAllocatedMemoryLong() / 1048576.0, 2);
            AddField(_line, Profiler.GetTotalReservedMemoryLong() / 1048576.0, 2);
            AddInt(_line, Time.frameCount - _shotFrame < 3 ? 1 : 0);

            Vector3 playerPosition = _playerTransform != null ? _playerTransform.position : Vector3.zero;
            AddField(_line, playerPosition.x, 2);
            AddField(_line, playerPosition.y, 2);
            AddField(_line, playerPosition.z, 2);

            for (int i = 0; i < _counters.Count; i++)
            {
                ProfilerRecorder recorder = _counters[i].Recorder;
                if (recorder.Valid)
                    AddField(_line, recorder.LastValueAsDouble, 4);
                else
                    AddInt(_line, 0);
            }

            _frames.WriteLine(_line.ToString());

            int stageIndex = (int)_stage;
            if (stageIndex >= 0 && stageIndex < _stageFrameTimes.Length)
                _stageFrameTimes[stageIndex].Add(dtMs);
        }

        void CountRenderStats(out int renderers, out int submeshes, out long indices)
        {
            renderers = 0;
            submeshes = 0;
            indices = 0;

            if (_engine == null)
                return;

            MeshRenderer[] chunkRenderers = _engine.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < chunkRenderers.Length; i++)
            {
                MeshRenderer renderer = chunkRenderers[i];
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;

                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null)
                    continue;

                renderers++;

                int count = mesh.subMeshCount;
                for (int submesh = 0; submesh < count; submesh++)
                {
                    SubMeshDescriptor descriptor = mesh.GetSubMesh(submesh);
                    if (descriptor.indexCount > 0)
                    {
                        submeshes++;
                        indices += descriptor.indexCount;
                    }
                }
            }
        }

        void ChangeStage(Stage stage, float maxSeconds)
        {
            _stage = stage;
            _stageStartedAt = Time.realtimeSinceStartup;
            _stageMaxSeconds = maxSeconds;
            Event("stage", StageName(stage));
        }

        void MaybeFinish()
        {
            if (_stage != Stage.Finished || _quitRequested)
                return;

            _quitRequested = true;
            WriteSummary();
            CloseLogs();
            Debug.Log("[PerfCapture] Замер завершён: " + _outDir);

#if !UNITY_EDITOR
            Application.Quit(0);
#endif
        }

        void WriteSummary()
        {
            var text = new StringBuilder();
            text.AppendLine("PerfCapture summary");
            text.AppendLine("output: " + _outDir);
            text.AppendLine("scene: " + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
            text.AppendLine("unity: " + Application.unityVersion);
            text.AppendLine("raw: " + (_rawPath ?? "-"));
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "awake_s: {0:0.000}", _awakeTime));
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "first_update_s: {0:0.000}", _firstUpdateTime));
            if (!string.IsNullOrEmpty(_abortReason))
                text.AppendLine("abort: " + _abortReason);

            for (int i = 0; i < _stageFrameTimes.Length; i++)
            {
                List<double> frames = _stageFrameTimes[i];
                if (frames.Count == 0)
                    continue;

                frames.Sort();
                double total = 0.0;
                for (int f = 0; f < frames.Count; f++)
                    total += frames[f];

                double average = total / frames.Count;
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "stage={0} frames={1} duration_s={2:0.000} avg_fps={3:0.00} " +
                    "frame_ms_avg={4:0.000} p50={5:0.000} p95={6:0.000} p99={7:0.000} max={8:0.000}",
                    StageName((Stage)i), frames.Count, total / 1000.0, average > 0 ? 1000.0 / average : 0.0,
                    average, Percentile(frames, 0.50), Percentile(frames, 0.95),
                    Percentile(frames, 0.99), frames[frames.Count - 1]));
            }

            text.AppendLine("columns_created: " + _columnsCreated);
            text.AppendLine("chunks_final: " + (_engine != null && _engine.CurrentWorld != null ? _engine.CurrentWorld.ChunkCount : 0));
            text.AppendLine("counters: " + _counters.Count);

            for (int i = 0; i < _counters.Count; i++)
            {
                CounterInfo counter = _counters[i];
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "counter: {0} | category={1} | unit={2} | type={3}",
                    counter.Name, counter.Category, counter.Unit, counter.DataType));
            }

            File.WriteAllText(Path.Combine(_outDir, "summary.txt"), text.ToString());
        }

        static double Percentile(List<double> sorted, double percentile)
        {
            if (sorted.Count == 0)
                return 0.0;

            int index = (int)Math.Round((sorted.Count - 1) * percentile);
            index = Mathf.Clamp(index, 0, sorted.Count - 1);
            return sorted[index];
        }

        void TakeScreenshot(string name)
        {
            _shots++;
            _shotFrame = Time.frameCount;
            string path = Path.Combine(_outDir, "shot_" + _shots.ToString("00") + "_" + name + ".png");
            ScreenCapture.CaptureScreenshot(path);
        }

        void Event(string stage, string message)
        {
            if (_events == null)
                return;

            int chunks = _engine != null && _engine.CurrentWorld != null ? _engine.CurrentWorld.ChunkCount : 0;
            int columns = _engine != null && _engine.CurrentWorld != null ? _engine.CurrentWorld.ColumnCount : 0;
            int pending = _engine != null ? _engine.PendingColumns : 0;

            _events.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0:0.000},{1},{2},{3},{4},{5}",
                Time.realtimeSinceStartup, stage, message, chunks, columns, pending));
            _events.Flush();
        }

        void WriteSystemInfo()
        {
            var text = new StringBuilder();
            text.AppendLine("unity: " + Application.unityVersion);
            text.AppendLine("platform: " + Application.platform);
            text.AppendLine("device: " + SystemInfo.deviceModel);
            text.AppendLine("os: " + SystemInfo.operatingSystem);
            text.AppendLine("cpu: " + SystemInfo.processorType + " x" + SystemInfo.processorCount);
            text.AppendLine("cpu_mhz: " + SystemInfo.processorFrequency);
            text.AppendLine("ram_mb: " + SystemInfo.systemMemorySize);
            text.AppendLine("gpu: " + SystemInfo.graphicsDeviceName);
            text.AppendLine("gpu_api: " + SystemInfo.graphicsDeviceType);
            text.AppendLine("vram_mb: " + SystemInfo.graphicsMemorySize);
            text.AppendLine("screen: " + Screen.width + "x" + Screen.height + " @" + Screen.currentResolution.refreshRateRatio);
            text.AppendLine("quality: " + QualitySettings.names[QualitySettings.GetQualityLevel()]);
            text.AppendLine("vsync: " + QualitySettings.vSyncCount);
            text.AppendLine("target_fps: " + Application.targetFrameRate);            text.AppendLine("color_space: " + QualitySettings.activeColorSpace);
            text.AppendLine("batchmode_args: " + string.Join(" ", Environment.GetCommandLineArgs()));
            File.WriteAllText(Path.Combine(_outDir, "systeminfo.txt"), text.ToString());
        }

        void AppendDebugCounter(string name, string category, string unit, string dataType)
        {
            if (_debugCounters == null)
            {
                _debugCounters = new StreamWriter(Path.Combine(_outDir, "counters.csv"), false, new UTF8Encoding(false));
                _debugCounters.WriteLine("name,category,unit,type");
            }

            _debugCounters.WriteLine(Sanitize(name) + "," + category + "," + unit + "," + dataType);
            _debugCounters.Flush();
        }

        void CloseLogs()
        {
            if (_frames != null)
            {
                _frames.Flush();
                _frames.Dispose();
                _frames = null;
            }

            if (_events != null)
            {
                _events.Flush();
                _events.Dispose();
                _events = null;
            }

            if (_debugCounters != null)
            {
                _debugCounters.Flush();
                _debugCounters.Dispose();
                _debugCounters = null;
            }

            for (int i = 0; i < _counters.Count; i++)
            {
                if (_counters[i].Recorder.Valid)
                    _counters[i].Recorder.Dispose();
            }
        }

        static void AddField(StringBuilder builder, double value, int digits)
        {
            if (builder.Length > 0)
                builder.Append(',');

            builder.Append(value.ToString("F" + digits, CultureInfo.InvariantCulture));
        }

        static void AddInt(StringBuilder builder, long value)
        {
            if (builder.Length > 0)
                builder.Append(',');

            builder.Append(value);
        }

        static void AddText(StringBuilder builder, string value)
        {
            if (builder.Length > 0)
                builder.Append(',');

            builder.Append(value);
        }

        static string StageName(Stage stage)
        {
            switch (stage)
            {
                case Stage.WaitWorld: return "wait_world";
                case Stage.Streaming: return "streaming";
                case Stage.Steady: return "steady";
                case Stage.Traversal: return "traversal";
                case Stage.Editing: return "editing";
                default: return "finished";
            }
        }

        static string Sanitize(string name)
        {
            var builder = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                builder.Append(c == ',' || c == '"' ? '_' : c);
            }

            return builder.ToString();
        }
    }

    internal static class PerfCaptureBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Start()
        {
            PerfCaptureRunner.Boot();
        }
    }
}
#endif
