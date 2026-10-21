using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Game.Editor.M1
{
    /// <summary>
    /// Stage B 复核：独立输出到 StageB/Review/Builds/Player，不写旧 Migration AcceptRoot。
    /// 可通过 --m1-review-build-root= 参数化到 Root-NarrowFix/Native 等子目录；未指定时保持 Review。
    /// </summary>
    public static class StageBReviewTools
    {
        const string DefaultReviewRel = "Docs/Acceptance/M1/20261020-xLua/StageB/Review";
        const string BuildRootArg = "--m1-review-build-root=";

        /// <summary>标准 Win64 IL2CPP 构建到 Review/Builds/Player（或 CLI 指定根）。</summary>
        public static void BatchBuildPlayerToReview()
        {
            string review = ResolveReviewRoot();
            string output = Path.Combine(review, "Builds", "Player", "Farming.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(output) ?? review);

            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.IL2CPP);
            string[] scenes;
            if (EditorBuildSettings.scenes != null && EditorBuildSettings.scenes.Length > 0)
            {
                var list = new System.Collections.Generic.List<string>();
                foreach (var s in EditorBuildSettings.scenes)
                {
                    if (s.enabled && !string.IsNullOrEmpty(s.path))
                        list.Add(s.path);
                }

                scenes = list.ToArray();
            }
            else
            {
                scenes = new[] { "Assets/Scenes/xLua_PoC.unity" };
            }

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = output,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None
            };
            BuildReport report = BuildPipeline.BuildPlayer(options);
            string text = "result=" + report.summary.result
                + "\nerrors=" + report.summary.totalErrors
                + "\noutput=" + output
                + "\nbackend=" + PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone)
                + "\nbundleVersion=" + PlayerSettings.bundleVersion
                + "\nreviewRoot=" + review
                + "\nwhen=" + DateTime.UtcNow.ToString("o") + "\n";
            File.WriteAllText(Path.Combine(review, "player-build-report.txt"), text, new UTF8Encoding(false));
            EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 7);
        }

        /// <summary>
        /// 解析证据根：优先 --m1-review-build-root=绝对或相对工程根；否则默认 Review。
        /// </summary>
        static string ResolveReviewRoot()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            foreach (string arg in Environment.GetCommandLineArgs())
            {
                if (!arg.StartsWith(BuildRootArg, StringComparison.OrdinalIgnoreCase))
                    continue;
                string raw = arg.Substring(BuildRootArg.Length).Trim().Trim('"');
                if (string.IsNullOrEmpty(raw))
                    break;
                return Path.IsPathRooted(raw)
                    ? Path.GetFullPath(raw)
                    : Path.GetFullPath(Path.Combine(projectRoot, raw));
            }

            return Path.GetFullPath(Path.Combine(projectRoot, DefaultReviewRel));
        }
    }
}
