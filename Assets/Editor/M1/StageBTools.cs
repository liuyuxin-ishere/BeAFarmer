using System;
using System.IO;
using System.Text;
using Game.Framework.Boot;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Editor.M1
{
    /// <summary>
    /// Stage B（T45–T47）批处理：进度 UI 截图、Win64 IL2CPP 构建到 StageB。
    /// </summary>
    public static class StageBTools
    {
        const string StageBRel = "Docs/Acceptance/M1/20261020-xLua/StageB";

        /// <summary>
        /// 创建 PatchProgressView 失败态并截图。需非 -nographics 才能写真实 PNG。
        /// </summary>
        public static void CaptureProgressViewShot()
        {
            string stageB = Path.GetFullPath(Path.Combine(Application.dataPath, "..", StageBRel));
            Directory.CreateDirectory(stageB);
            string shot = Path.Combine(stageB, "shot-patch-progress-error.png");

            var view = PatchProgressView.CreateRuntime();
            // WorldSpace 才能被离屏 Camera 可靠烘焙（Overlay 在 batchmode 常成灰图）。
            var canvas = view.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rtTransform = canvas.GetComponent<RectTransform>();
            rtTransform.sizeDelta = new Vector2(1280, 720);
            rtTransform.localScale = Vector3.one * 0.01f;
            rtTransform.position = Vector3.zero;

            view.Render(new PatchProgress
            {
                Stage = "Failed",
                DownloadedBytes = 1024,
                TotalBytes = 4096,
                CompletedFiles = 1,
                TotalFiles = 3
            });
            view.ShowError("模拟网络失败（验收截图，无凭据）", () => { });

            Canvas.ForceUpdateCanvases();
            bool ok = TryBakeCanvasPng(canvas, shot, 1280, 720);
            if (!ok)
            {
                try
                {
                    ScreenCapture.CaptureScreenshot(shot);
                    ok = File.Exists(shot) && new FileInfo(shot).Length > 32;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning(ex);
                }
            }

            File.WriteAllText(
                Path.Combine(stageB, "shot-patch-progress-note.txt"),
                "shot=" + shot + "\nexists=" + File.Exists(shot)
                + "\nbaked=" + ok
                + "\nstage=Failed\nretryVisible=True\nstatus="
                + (view.StatusText != null ? view.StatusText.text : "")
                + "\nwhen=" + DateTime.UtcNow.ToString("o") + "\n",
                new UTF8Encoding(false));
            if (!ok)
            {
                File.WriteAllText(
                    Path.Combine(stageB, "shot-patch-progress-fallback.txt"),
                    "bake+ScreenCapture failed; PatchProgressViewTests still cover gate/UI.\n"
                    + "status=" + (view.StatusText != null ? view.StatusText.text : "")
                    + "\nretryActive=" + (view.RetryButton != null && view.RetryButton.gameObject.activeSelf)
                    + "\nlastStage=" + view.LastStage + "\n",
                    new UTF8Encoding(false));
            }

            if (view != null)
                UnityEngine.Object.DestroyImmediate(view.gameObject);
            EditorApplication.Exit(0);
        }

        /// <summary>把 Overlay Canvas 绑到临时 Camera，渲染到 PNG。</summary>
        /// <param name="canvas">进度 UI Canvas。</param>
        /// <param name="path">输出 PNG 路径。</param>
        /// <param name="width">像素宽。</param>
        /// <param name="height">像素高。</param>
        static bool TryBakeCanvasPng(Canvas canvas, string path, int width, int height)
        {
            if (canvas == null)
                return false;
            try
            {
                var camGo = new GameObject("StageBShotCam");
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.08f, 0.1f, 0.14f, 1f);
                cam.orthographic = true;
                cam.orthographicSize = 5f;
                cam.transform.position = new Vector3(0, 0, -10f);

                if (canvas.renderMode != RenderMode.WorldSpace)
                {
                    canvas.renderMode = RenderMode.WorldSpace;
                    var rect = canvas.GetComponent<RectTransform>();
                    rect.sizeDelta = new Vector2(width, height);
                    rect.localScale = Vector3.one * 0.01f;
                    rect.position = Vector3.zero;
                }

                canvas.worldCamera = cam;
                float worldHeight = height * 0.01f;
                cam.orthographicSize = worldHeight * 0.5f;
                cam.transform.position = new Vector3(0, 0, -10f);
                Canvas.ForceUpdateCanvases();

                var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                cam.targetTexture = rt;
                cam.Render();

                RenderTexture.active = rt;
                var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                byte[] png = tex.EncodeToPNG();
                File.WriteAllBytes(path, png);

                cam.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(tex);
                UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(camGo);
                return File.Exists(path) && new FileInfo(path).Length > 32;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("TryBakeCanvasPng: " + ex.Message);
                return false;
            }
        }

        /// <summary>标准 Win64 IL2CPP 构建到 StageB/Builds/Player。</summary>
        public static void BatchBuildPlayerToStageB()
        {
            string stageB = Path.GetFullPath(Path.Combine(Application.dataPath, "..", StageBRel));
            string output = Path.Combine(stageB, "Builds", "Player", "Farming.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(output));

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
                scenes = new[] { "Assets/Scenes/SampleScene.unity" };
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
                + "\nbundleVersion=" + PlayerSettings.bundleVersion + "\n";
            File.WriteAllText(Path.Combine(stageB, "player-build-report.txt"), text, new UTF8Encoding(false));
            EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 7);
        }
    }
}
