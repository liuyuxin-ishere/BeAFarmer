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

        public static void CaptureResolutionMatrix()
        {
            foreach(var size in new[]{new Vector2Int(1280,720),new Vector2Int(1920,1080),new Vector2Int(1920,1200)})
                foreach(string name in new[]{"LoginWindow","HomeHudWindow","FarmWindow","GatherWindow","ToolWindow"})
                    CaptureUI(name,Evidence+"ResolutionMatrix/"+size.x+"x"+size.y+"-"+name+".png",size.x,size.y,true);
            Debug.Log("[ArtABC] Captured 15 UI resolution views; all Selectable bounds within viewport. Static prefab review only.");
        }

        public static void CaptureUI(string name, string path, int width = 1920, int height = 1080, bool matchRuntimeScale = false)
        {
            var scene = EditorSceneManager.NewPreviewScene();
            var canvasGo = new GameObject("ArtReview", typeof(Canvas), typeof(CanvasScaler));
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(canvasGo, scene);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rect = (RectTransform)canvas.transform;
            // Same geometric-mean scale as UiRoot's 1920x1080, match=0.5 CanvasScaler.
            float uiScale=matchRuntimeScale?Mathf.Sqrt((width/1920f)*(height/1080f)):1f;
            rect.sizeDelta = new Vector2(width/uiScale, height/uiScale);
            rect.localScale = Vector3.one * .01f;
            var go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Res/UI/" + name + ".prefab"), canvas.transform, false);
            var camGo = new GameObject("ReviewCamera", typeof(Camera));
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(camGo, scene);
            var cam = camGo.GetComponent<Camera>();
            cam.scene = scene;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(.69f, .81f, .78f);
            cam.orthographic = true;
            cam.orthographicSize = height/uiScale * .005f;
            cam.transform.position = new Vector3(0, 0, -10);
            canvas.worldCamera = cam;
            foreach (var text in go.GetComponentsInChildren<Text>(true)) text.font?.RequestCharactersInTexture(text.text, text.fontSize);
            Canvas.ForceUpdateCanvases();
            foreach(var fitter in go.GetComponentsInChildren<AspectRatioFitter>(true))
            {
                fitter.SetLayoutHorizontal();fitter.SetLayoutVertical();
            }
            Canvas.ForceUpdateCanvases();
            if(matchRuntimeScale)
            {
                foreach(var control in go.GetComponentsInChildren<Selectable>(true))
                {
                    var corners=new Vector3[4];((RectTransform)control.transform).GetWorldCorners(corners);
                    foreach(var corner in corners)
                    {
                        var local=rect.InverseTransformPoint(corner);
                        if(Mathf.Abs(local.x)>rect.rect.width*.5f+.5f || Mathf.Abs(local.y)>rect.rect.height*.5f+.5f)
                            throw new System.InvalidOperationException(name+" control outside viewport: "+control.name+" at "+width+"x"+height);
                    }
                }
            }
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
