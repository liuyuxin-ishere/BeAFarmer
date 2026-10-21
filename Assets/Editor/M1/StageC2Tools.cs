using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Editor.M1
{
    /// <summary>C2：Login/HomeHud Prefab 与 Lua Staging 同步。</summary>
    public static class StageC2Tools
    {
        const string EvidenceRel = "Docs/Acceptance/M1/20261020-xLua/StageC2";
        const string ReviewRel = "Docs/Acceptance/M1/20261020-xLua/StageC2/Review";

        [MenuItem("Farming/M1/StageC2/Create Login Home Prefabs")]
        public static void CreatePrefabsMenu() => BatchCreatePrefabs();

        /// <summary>
        /// CLI（须有图形，勿 -nographics）：打开 Login/HomeHud Prefab 并烘焙真实 PNG 到 Review/shots。
        /// 不覆盖旧 StageC2/shots/login-window.png。
        /// </summary>
        public static void BatchCaptureLoginHomeGui()
        {
            string review = Path.Combine(Application.dataPath, "..", ReviewRel, "shots");
            Directory.CreateDirectory(review);
            try
            {
                var login = PrefabUtility.LoadPrefabContents("Assets/Res/UI/LoginWindow.prefab");
                var home = PrefabUtility.LoadPrefabContents("Assets/Res/UI/HomeHudWindow.prefab");
                try
                {
                    var canvasGo = new GameObject("C2ReviewCanvas", typeof(Canvas), typeof(CanvasScaler));
                    var canvas = canvasGo.GetComponent<Canvas>();
                    canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    login.transform.SetParent(canvasGo.transform, false);
                    home.transform.SetParent(canvasGo.transform, false);
                    home.SetActive(false);

                    SetText(login.transform, "Title", "登录");
                    SetText(login.transform, "ErrorLabel", "用户名或密码错误");
                    bool loginOk = TryBake(canvas, Path.Combine(review, "login-error.png"), 1280, 720);

                    login.SetActive(false);
                    home.SetActive(true);
                    SetText(home.transform, "Title", "家园");
                    SetText(home.transform, "PlayerId", "ID: 9007199254740993");
                    SetText(home.transform, "Nickname", "NickReview");
                    SetText(home.transform, "Level", "Lv.7");
                    bool homeOk = TryBake(canvas, Path.Combine(review, "home-hud.png"), 1280, 720);

                    home.SetActive(false);
                    login.SetActive(true);
                    SetText(login.transform, "ErrorLabel", "");
                    SetText(login.transform, "Title", "登录");
                    bool loginReadyOk = TryBake(canvas, Path.Combine(review, "login-window.png"), 1280, 720);

                    File.WriteAllText(
                        Path.Combine(review, "gui-bake-report.txt"),
                        "loginError=" + loginOk + " home=" + homeOk + " loginReady=" + loginReadyOk
                        + "\nwhen=" + DateTime.UtcNow.ToString("o") + "\n",
                        new UTF8Encoding(false));
                    UnityEngine.Object.DestroyImmediate(canvasGo);
                    EditorApplication.Exit(loginOk && homeOk && loginReadyOk ? 0 : 1);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(login);
                    PrefabUtility.UnloadPrefabContents(home);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[C2][Review] " + ex);
                EditorApplication.Exit(2);
            }
        }

        static void SetText(Transform root, string name, string value)
        {
            var t = root.Find(name)?.GetComponent<Text>();
            if (t != null)
                t.text = value;
        }

        static bool TryBake(Canvas canvas, string path, int width, int height)
        {
            try
            {
                var camGo = new GameObject("C2ShotCam");
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.08f, 0.1f, 0.14f, 1f);
                cam.orthographic = true;
                canvas.renderMode = RenderMode.WorldSpace;
                var rect = canvas.GetComponent<RectTransform>();
                rect.sizeDelta = new Vector2(width, height);
                rect.localScale = Vector3.one * 0.01f;
                rect.position = Vector3.zero;
                canvas.worldCamera = cam;
                cam.orthographicSize = height * 0.01f * 0.5f;
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
                File.WriteAllBytes(path, tex.EncodeToPNG());
                cam.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(tex);
                UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(camGo);
                return File.Exists(path) && new FileInfo(path).Length > 32;
            }
            catch (Exception ex)
            {
                Debug.LogWarning(ex);
                return false;
            }
        }

        /// <summary>CLI：-executeMethod Game.Editor.M1.StageC2Tools.BatchCreatePrefabs</summary>
        public static void BatchCreatePrefabs()
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(Application.dataPath, "..", EvidenceRel));
                Directory.CreateDirectory("Assets/Res/UI");
                CreateLoginPrefab("Assets/Res/UI/LoginWindow.prefab");
                CreateHomeHudPrefab("Assets/Res/UI/HomeHudWindow.prefab");
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                File.WriteAllText(
                    Path.Combine(Application.dataPath, "..", EvidenceRel, "assets-created.txt"),
                    "LoginWindow HomeHudWindow\n" + DateTime.UtcNow.ToString("o") + "\n",
                    new UTF8Encoding(false));
                Debug.Log("[C2] prefabs created");
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                Debug.LogError("[C2] " + ex);
                EditorApplication.Exit(1);
            }
        }

        static void CreateLoginPrefab(string path)
        {
            var root = new GameObject("LoginWindow", typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)root.transform;
            rt.sizeDelta = new Vector2(900, 640);
            root.GetComponent<Image>().color = new Color(0.12f, 0.14f, 0.18f, 0.98f);

            CreateText(root.transform, "Title", "登录", new Vector2(0.1f, 0.82f), new Vector2(0.9f, 0.95f), 36);
            CreateInput(root.transform, "Username", "用户名", new Vector2(0.15f, 0.68f), new Vector2(0.85f, 0.78f), false);
            CreateInput(root.transform, "Password", "密码", new Vector2(0.15f, 0.54f), new Vector2(0.85f, 0.64f), true);
            CreateInput(root.transform, "Nickname", "昵称(注册)", new Vector2(0.15f, 0.40f), new Vector2(0.85f, 0.50f), false);
            CreateText(root.transform, "ErrorLabel", "", new Vector2(0.1f, 0.28f), new Vector2(0.9f, 0.38f), 24);
            CreateButton(root.transform, "RegisterButton", "注册", new Vector2(0.12f, 0.08f), new Vector2(0.45f, 0.22f));
            CreateButton(root.transform, "LoginButton", "登录", new Vector2(0.55f, 0.08f), new Vector2(0.88f, 0.22f));

            PrefabUtility.SaveAsPrefabAsset(root, path);
            UnityEngine.Object.DestroyImmediate(root);
        }

        static void CreateHomeHudPrefab(string path)
        {
            var root = new GameObject("HomeHudWindow", typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)root.transform;
            rt.sizeDelta = new Vector2(720, 280);
            root.GetComponent<Image>().color = new Color(0.10f, 0.16f, 0.12f, 0.95f);

            CreateText(root.transform, "Title", "家园", new Vector2(0.05f, 0.70f), new Vector2(0.95f, 0.95f), 34);
            CreateText(root.transform, "PlayerId", "ID:", new Vector2(0.08f, 0.45f), new Vector2(0.92f, 0.68f), 26);
            CreateText(root.transform, "Nickname", "昵称", new Vector2(0.08f, 0.28f), new Vector2(0.55f, 0.48f), 26);
            CreateText(root.transform, "Level", "Lv.0", new Vector2(0.55f, 0.28f), new Vector2(0.92f, 0.48f), 26);
            CreateButton(root.transform, "LogoutButton", "登出", new Vector2(0.30f, 0.05f), new Vector2(0.70f, 0.22f));

            PrefabUtility.SaveAsPrefabAsset(root, path);
            UnityEngine.Object.DestroyImmediate(root);
        }

        static void CreateText(Transform parent, string name, string value, Vector2 amin, Vector2 amax, int size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                        ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.text = value;
            text.fontSize = size;
            text.color = Color.white;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            var rt = (RectTransform)go.transform;
            rt.anchorMin = amin;
            rt.anchorMax = amax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        static void CreateInput(Transform parent, string name, string placeholder, Vector2 amin, Vector2 amax, bool password)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(InputField));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0.22f, 0.24f, 0.28f, 1f);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = amin;
            rt.anchorMax = amax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            var textGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
            textGo.transform.SetParent(go.transform, false);
            var text = textGo.GetComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                        ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize = 28;
            text.color = Color.white;
            text.supportRichText = false;
            var trt = (RectTransform)textGo.transform;
            trt.anchorMin = new Vector2(0.04f, 0.1f);
            trt.anchorMax = new Vector2(0.96f, 0.9f);
            trt.offsetMin = Vector2.zero;
            trt.offsetMax = Vector2.zero;

            var phGo = new GameObject("Placeholder", typeof(RectTransform), typeof(Text));
            phGo.transform.SetParent(go.transform, false);
            var ph = phGo.GetComponent<Text>();
            ph.font = text.font;
            ph.fontSize = 28;
            ph.color = new Color(1f, 1f, 1f, 0.35f);
            ph.text = placeholder;
            var prt = (RectTransform)phGo.transform;
            prt.anchorMin = trt.anchorMin;
            prt.anchorMax = trt.anchorMax;
            prt.offsetMin = Vector2.zero;
            prt.offsetMax = Vector2.zero;

            var input = go.GetComponent<InputField>();
            input.textComponent = text;
            input.placeholder = ph;
            if (password)
                input.contentType = InputField.ContentType.Password;
        }

        static void CreateButton(Transform parent, string name, string label, Vector2 amin, Vector2 amax)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0.25f, 0.55f, 0.35f, 1f);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = amin;
            rt.anchorMax = amax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            CreateText(go.transform, "Text", label, Vector2.zero, Vector2.one, 30);
        }
    }
}
