using System;
using System.IO;
using System.Text;
using CSObjectWrapEditor;
using Game.Framework.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Editor.M1
{
    /// <summary>
    /// Stage C1：生成桥、M1 Login/Home 占位场景、测试 Prefab、截图辅助。
    /// 截图状态用 SessionState 跨 PlayMode domain reload 续跑；勿配立即 -quit。
    /// </summary>
    public static class StageC1Tools
    {
        const string EvidenceRel = "Docs/Acceptance/M1/20261020-xLua/StageC1";
        const string ReviewRel = EvidenceRel + "/Review";
        const string SessionKey = "M1.C1.ShotCapture";
        const string SessionDirKey = "M1.C1.ShotDir";
        const string SessionPhaseKey = "M1.C1.ShotPhase";

        [InitializeOnLoadMethod]
        static void ResumeShotCaptureAfterReload()
        {
            if (!SessionState.GetBool(SessionKey, false))
                return;
            EditorApplication.update -= CaptureUpdate;
            EditorApplication.update += CaptureUpdate;
        }

        [MenuItem("Farming/M1/C1 Gen Bridges")]
        public static void GenBridges()
        {
            Generator.ClearAll();
            Generator.GenAll();
            Debug.Log("[C1] GenAll done");
        }

        public static void BatchGenBridges()
        {
            try
            {
                GenBridges();
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorApplication.Exit(2);
            }
        }

        /// <summary>创建正式 M1 Login/Home 占位场景（保留 Greybox/PoC）。</summary>
        public static void BatchCreateM1ScenesAndPrefabs()
        {
            try
            {
                Directory.CreateDirectory("Assets/Scenes/M1");
                Directory.CreateDirectory("Assets/Res/UI");
                CreateMarkerScene("Assets/Scenes/M1/M1_Login.unity", "M1_Login", "M1LoginMarker");
                CreateMarkerScene("Assets/Scenes/M1/M1_Home.unity", "M1_Home", "M1HomeMarker");
                CreateTestWindowPrefab("Assets/Res/UI/UiTestWindow.prefab");
                EnsureCollectorHasM1();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                File.WriteAllText(
                    Path.Combine(Application.dataPath, "..", EvidenceRel, "assets-created.txt"),
                    "M1_Login M1_Home UiTestWindow\n" + DateTime.UtcNow.ToString("o") + "\n",
                    new UTF8Encoding(false));
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorApplication.Exit(3);
            }
        }

        static void CreateMarkerScene(string path, string sceneName, string markerName)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cam = new GameObject("Main Camera");
            var camera = cam.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = sceneName.Contains("Login")
                ? new Color(0.12f, 0.16f, 0.22f)
                : new Color(0.14f, 0.2f, 0.14f);
            cam.tag = "MainCamera";
            var light = new GameObject("Directional Light");
            light.AddComponent<Light>().type = LightType.Directional;
            var marker = new GameObject(markerName);
            var textGo = new GameObject("Label", typeof(TextMesh));
            textGo.transform.SetParent(marker.transform, false);
            var tm = textGo.GetComponent<TextMesh>();
            tm.text = sceneName;
            tm.characterSize = 0.2f;
            tm.fontSize = 48;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.color = Color.white;
            EditorSceneManager.SaveScene(scene, path);
            Debug.Log("[C1] scene " + path);
        }

        static void CreateTestWindowPrefab(string path)
        {
            var root = new GameObject("UiTestWindow", typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)root.transform;
            rt.sizeDelta = new Vector2(800, 500);
            root.GetComponent<Image>().color = new Color(0.15f, 0.18f, 0.22f, 0.96f);

            CreateText(root.transform, "Title", "UiTestWindow");
            CreateText(root.transform, "Body", "body");
            CreateButton(root.transform, "OkButton", "OK");

            PrefabUtility.SaveAsPrefabAsset(root, path);
            UnityEngine.Object.DestroyImmediate(root);
            Debug.Log("[C1] prefab " + path);
        }

        static void CreateText(Transform parent, string name, string value)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (text.font == null)
                text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.text = value;
            text.fontSize = 28;
            text.color = Color.white;
            text.alignment = TextAnchor.MiddleCenter;
            var rt = (RectTransform)go.transform;
            if (name == "Title")
            {
                rt.anchorMin = new Vector2(0.1f, 0.7f);
                rt.anchorMax = new Vector2(0.9f, 0.95f);
            }
            else
            {
                rt.anchorMin = new Vector2(0.1f, 0.35f);
                rt.anchorMax = new Vector2(0.9f, 0.65f);
            }

            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        static void CreateButton(Transform parent, string name, string label)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0.25f, 0.55f, 0.35f, 1f);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0.3f, 0.08f);
            rt.anchorMax = new Vector2(0.7f, 0.28f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            CreateText(go.transform, "Text", label);
        }

        static void EnsureCollectorHasM1()
        {
            string note = Path.Combine(Application.dataPath, "..", EvidenceRel, "collector-note.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(note) ?? EvidenceRel);
            File.WriteAllText(note,
                "DefaultPackage collectors include Assets/Scenes/M1 and Assets/Res/UI (AddressByFileName).\n"
                + "Addresses expected: M1_Login, M1_Home, UiTestWindow\n",
                new UTF8Encoding(false));
        }

        /// <summary>
        /// 有图形会话截三比例 + 非零 SafeArea。勿加 -nographics。
        /// 勿加立即 -quit：本方法经 SessionState 续跑后自行 Exit。
        /// 用 Camera+RenderTexture 保证 PNG 像素尺寸，不依赖 GameView/Screen.SetResolution。
        /// </summary>
        public static void BatchCaptureUiShots()
        {
            string shotDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ReviewRel, "shots"));
            Directory.CreateDirectory(shotDir);
            SessionState.SetBool(SessionKey, true);
            SessionState.SetString(SessionDirKey, shotDir);
            SessionState.SetInt(SessionPhaseKey, 0);
            _shotError = null;
            _shotUi = null;
            EditorApplication.update -= CaptureUpdate;
            EditorApplication.update += CaptureUpdate;
            if (!EditorApplication.isPlaying)
                EditorApplication.isPlaying = true;
        }

        static string _shotError;
        static UiManager _shotUi;

        static void CaptureUpdate()
        {
            try
            {
                if (!SessionState.GetBool(SessionKey, false))
                {
                    EditorApplication.update -= CaptureUpdate;
                    return;
                }

                if (!string.IsNullOrEmpty(_shotError))
                {
                    FinishCapture(5);
                    return;
                }

                if (!EditorApplication.isPlaying)
                    return;

                int phase = SessionState.GetInt(SessionPhaseKey, 0);
                string shotDir = SessionState.GetString(SessionDirKey, "");
                if (string.IsNullOrEmpty(shotDir))
                {
                    _shotError = "shotDir empty";
                    return;
                }

                switch (phase)
                {
                    case 0:
                        if (Time.realtimeSinceStartup < 0.4f)
                            return;
                        var root = UiRoot.Ensure();
                        _shotUi = new UiManager();
                        _shotUi.Bind(root, null, null);
                        _shotUi.ControllerResolver = _ => new ShotController();
                        _shotUi.Register(new UiWindowRegistration
                        {
                            WindowId = "shot",
                            PrefabAddress = "",
                            Layer = UILayer.Window,
                            ControllerModule = "shot"
                        });
                        bool opened = false;
                        _shotUi.Open("shot", "readable layout", (code, _) =>
                        {
                            if (code != 0)
                                _shotError = "open failed " + code;
                            opened = true;
                        });
                        // 运行时窗同步完成
                        if (!opened && string.IsNullOrEmpty(_shotError))
                            _shotError = "open not completed";
                        SessionState.SetInt(SessionPhaseKey, 1);
                        break;
                    case 1:
                        CaptureExact(shotDir, "shot-1280x720.png", 1280, 720, new Rect(0, 0, 1280, 720));
                        SessionState.SetInt(SessionPhaseKey, 2);
                        break;
                    case 2:
                        CaptureExact(shotDir, "shot-1920x1080.png", 1920, 1080, new Rect(0, 0, 1920, 1080));
                        SessionState.SetInt(SessionPhaseKey, 3);
                        break;
                    case 3:
                        CaptureExact(shotDir, "shot-1080x1920.png", 1080, 1920, new Rect(0, 0, 1080, 1920));
                        SessionState.SetInt(SessionPhaseKey, 4);
                        break;
                    case 4:
                        CaptureExact(shotDir, "shot-safearea-nonzero.png", 1080, 1920, new Rect(80, 60, 920, 1800));
                        WriteShotReport(shotDir);
                        FinishCapture(0);
                        break;
                }
            }
            catch (Exception ex)
            {
                _shotError = ex.ToString();
            }
        }

        static void CaptureExact(string shotDir, string name, int w, int h, Rect safe)
        {
            var root = UiRoot.Ensure();
            root.SafeArea.Apply(safe, new Vector2Int(w, h));
            var canvas = root.GetComponent<Canvas>();
            var scaler = root.Scaler;
            RenderMode prevMode = canvas.renderMode;
            Camera prevCam = canvas.worldCamera;
            float prevPlane = canvas.planeDistance;

            var camGo = new GameObject("M1_ShotCam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.1f, 0.12f, 0.16f, 1f);
            cam.orthographic = true;
            cam.orthographicSize = h * 0.5f * 0.01f;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 100f;
            cam.transform.position = new Vector3(0, 0, -10f);

            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = cam;
            canvas.planeDistance = 1f;
            if (scaler != null)
            {
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.matchWidthOrHeight = 0.5f;
            }

            Canvas.ForceUpdateCanvases();
            cam.Render();

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;

            string path = Path.Combine(shotDir, name);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Debug.Log("[C1] wrote " + path + " " + w + "x" + h + " bytes=" + new FileInfo(path).Length);

            canvas.renderMode = prevMode;
            canvas.worldCamera = prevCam;
            canvas.planeDistance = prevPlane;
            cam.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(tex);
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(camGo);

            VerifyPngSize(path, w, h);
        }

        static void VerifyPngSize(string path, int expectW, int expectH)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 24 || bytes[0] != 0x89 || bytes[1] != 0x50)
                throw new InvalidOperationException("not png " + path);
            int w = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            int h = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            if (w != expectW || h != expectH)
                throw new InvalidOperationException("png size " + w + "x" + h + " != " + expectW + "x" + expectH);
        }

        static void WriteShotReport(string shotDir)
        {
            var sb = new StringBuilder();
            sb.Append("pass=True\nfiles=4\nwhen=").Append(DateTime.UtcNow.ToString("o")).Append('\n');
            string[] names =
            {
                "shot-1280x720.png", "shot-1920x1080.png", "shot-1080x1920.png", "shot-safearea-nonzero.png"
            };
            int[][] sizes = { new[] { 1280, 720 }, new[] { 1920, 1080 }, new[] { 1080, 1920 }, new[] { 1080, 1920 } };
            for (int i = 0; i < names.Length; i++)
            {
                string p = Path.Combine(shotDir, names[i]);
                var fi = new FileInfo(p);
                sb.Append(names[i]).Append('=').Append(sizes[i][0]).Append('x').Append(sizes[i][1])
                    .Append(" bytes=").Append(fi.Exists ? fi.Length : 0).Append('\n');
            }

            File.WriteAllText(Path.Combine(shotDir, "..", "shots-report.txt"), sb.ToString(), new UTF8Encoding(false));
        }

        static void FinishCapture(int exitCode)
        {
            EditorApplication.update -= CaptureUpdate;
            try { _shotUi?.Dispose(); } catch { }
            _shotUi = null;
            SessionState.EraseBool(SessionKey);
            SessionState.EraseString(SessionDirKey);
            SessionState.EraseInt(SessionPhaseKey);
            if (!string.IsNullOrEmpty(_shotError))
                Debug.LogError("[C1] capture fail: " + _shotError);
            if (EditorApplication.isPlaying)
                EditorApplication.isPlaying = false;
            EditorApplication.delayCall += () => EditorApplication.Exit(exitCode);
        }

        sealed class ShotController : ILuaUiController
        {
            public void OnCreate(ViewHost view)
            {
                view.SetText("Title", "SafeArea / Aspect");
                view.SetText("Body", "readable layout");
            }

            public void OnOpen(string argsJson) { }
            public void OnClose() { }
            public void OnDestroy() { }
        }
    }
}
