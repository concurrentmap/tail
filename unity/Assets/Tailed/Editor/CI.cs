using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Tailed.EditorTools
{
    /// <summary>Batch-mode entry points, invoked by tools/unity.sh via -executeMethod.</summary>
    public static class CI
    {
        /// <summary>No-op: reaching this method means every assembly compiled.</summary>
        public static void CompileCheck()
        {
            Debug.Log("[Tailed.CI] Compile OK");
        }

        public static void BuildWindows()
        {
            string outDir = GetArg("-tailedOut") ?? "../out/build/win64";
            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = System.IO.Path.Combine(outDir, "Tailed.exe"),
                target = BuildTarget.StandaloneWindows64,
                options = GetArg("-tailedDev") != null ? BuildOptions.Development : BuildOptions.None,
            });
            Debug.Log($"[Tailed.CI] Build {report.summary.result}: {report.summary.totalErrors} errors, " +
                      $"{report.summary.totalSize / (1024 * 1024)} MB, {report.summary.totalTime}");
            if (report.summary.result != BuildResult.Succeeded) EditorApplication.Exit(1);
        }

        /// <summary>Dev build from the open editor (bridge: execute Tailed.EditorTools.CI.BuildFromEditor).</summary>
        public static string BuildFromEditor()
        {
            if (EditorApplication.isPlaying) return "stop play mode first";
            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = System.IO.Path.GetFullPath("../out/build/win64/Tailed.exe"),
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.Development,
            });
            return $"{report.summary.result}: {report.summary.totalErrors} errors, {report.summary.totalSize / (1024 * 1024)} MB, {report.summary.totalTime}";
        }

        static string GetArg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, name);
            if (i < 0) return null;
            return i + 1 < args.Length && !args[i + 1].StartsWith("-") ? args[i + 1] : string.Empty;
        }
    }
}
