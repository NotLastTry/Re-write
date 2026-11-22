using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Voxels.EditorTools
{
    /// <summary>
    /// Сборка отдельного плеера для замеров производительности (ЛР №1).
    /// Запуск из командной строки без открытия редактора:
    ///   Unity.exe -batchmode -quit -projectPath &lt;проект&gt;
    ///             -executeMethod Voxels.EditorTools.PerfBuildTool.BuildPerfPlayer
    ///             -logFile &lt;лог&gt;
    /// </summary>
    public static class PerfBuildTool
    {
        const string ScenePath = "Assets/TestScene.unity";
        const string OutputDirectory = "Builds/Perf";
        const string OutputName = "Re-write-perf.exe";

        [MenuItem("Tools/Voxels/Собрать плеер для замеров (Perf)")]
        public static void BuildPerfPlayer()
        {
            string outputDirectory = Path.GetFullPath(OutputDirectory);
            Directory.CreateDirectory(outputDirectory);

            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = Path.Combine(outputDirectory, OutputName),
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.Development | BuildOptions.AllowDebugging
            };

            DateTime started = DateTime.UtcNow;
            BuildReport report = BuildPipeline.BuildPlayer(options);
            TimeSpan duration = DateTime.UtcNow - started;

            BuildSummary summary = report.summary;
            Debug.Log(string.Format(
                "[PerfBuild] result={0} size={1:0.0} MB duration={2:0.0}s errors={3} warnings={4} output={5}",
                summary.result, summary.totalSize / 1048576.0, duration.TotalSeconds,
                summary.totalErrors, summary.totalWarnings, outputDirectory));

            if (summary.result != BuildResult.Succeeded)
            {
                Debug.LogError("[PerfBuild] Сборка не удалась: " + summary.result);
                EditorApplication.Exit(1);
                return;
            }

            EditorApplication.Exit(0);
        }
    }
}
