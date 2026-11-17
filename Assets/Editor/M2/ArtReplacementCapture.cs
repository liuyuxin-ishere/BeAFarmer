using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Editor.M2
{
    public static class ArtReplacementCapture
    {
        public const string Evidence = "ArtAssets/Production/Reviews/ArtReplacement/";

        public static void Baseline()
        {
            Directory.CreateDirectory(Evidence + "Before");
            foreach (string name in new[] { "LoginWindow", "HomeHudWindow", "FarmWindow", "GatherWindow", "ToolWindow" })
                CaptureUI(name, Evidence + "Before/" + name + ".png");
            Debug.Log("[ArtABC] baseline captured; no production scene modified");
        }

        public static void CaptureUI(string name, string path, int width = 1920, int height = 1080)
        {
            var scene = EditorSceneManager.NewPreviewScene();
            var canvasGo = new GameObject("ArtReview", typeof(Canvas), typeof(CanvasScaler));
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(canvasGo, scene);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rect = (RectTransform)canvas.transform;
            rect.sizeDelta = new Vector2(width, height);
            rect.localScale = Vector3.one * .01f;
            var go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Res/UI/" + name + ".prefab"), canvas.transform, false);
            var camGo = new GameObject("ReviewCamera", typeof(Camera));
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(camGo, scene);
            var cam = camGo.GetComponent<Camera>();
            cam.scene = scene;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(.69f, .81f, .78f);
            cam.orthographic = true;
            cam.orthographicSize = height * .005f;
            cam.transform.position = new Vector3(0, 0, -10);
            canvas.worldCamera = cam;
            foreach (var text in go.GetComponentsInChildren<Text>(true)) text.font?.RequestCharactersInTexture(text.text, text.fontSize);
            Canvas.ForceUpdateCanvases();
            try { Render(cam, path, width, height); }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }

        public static void Render(Camera camera, string path, int width = 1920, int height = 1080)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var previous = RenderTexture.active;
            var previousTarget = camera.targetTexture;
            var target = new RenderTexture(width, height, 24);
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previous;
                Object.DestroyImmediate(texture);
                Object.DestroyImmediate(target);
            }
        }
    }
}
