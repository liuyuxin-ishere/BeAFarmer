using System;
using System.Collections.Generic;
using System.IO;
using Game.Framework.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using XLua;

namespace Game.Editor.M2
{
    // Root-owned: actual Unity render of production builders/presenters with a controlled read-only store.
    // This is Editor layout evidence; it cannot prove Native or a network transaction.
    public static class T026UiRootPreview
    {
        public static void PrepareCaptureAndStageBatch()
        {
            ExportGameplayDisplayCatalogData.Export();
            StageT026UiTools.BakeU01Prefabs();
            CaptureBatch();
            Game.Editor.Build.ClientBuild.StageM2LuaForVerification();
        }

        [Serializable] public sealed class TextProof
        {
            public string path, text, font, missingGlyphs;
            public float width, height, preferredHeight;
        }
        [Serializable] public sealed class Proof
        {
            public string scope = "Editor controlled snapshot; production builder/presenter/ViewHost; no Native/network claim";
            public string kind;
            public int width, height;
            public int ownedClickSubscriptionsWhileVisible;
            public List<TextProof> labels = new List<TextProof>();
        }

        public static void CaptureBatch()
        {
            string directory = null;
            string[] args = Environment.GetCommandLineArgs();
            foreach (string arg in args)
                if (arg.StartsWith("--m2-ui-proof-dir=", StringComparison.Ordinal))
                    directory = Path.GetFullPath(arg.Substring("--m2-ui-proof-dir=".Length));
            string allowed = Path.GetFullPath(Path.Combine(Application.dataPath, "../Docs/Acceptance/M2")) + Path.DirectorySeparatorChar;
            if (directory == null || !directory.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("a child --m2-ui-proof-dir under Docs/Acceptance/M2 is required");
            Directory.CreateDirectory(directory);
            Capture(directory, "bag", 1920, 1080, false);
            Capture(directory, "shop", 1920, 1080, false);
            Capture(directory, "bag-stress", 1280, 720, true);
            Capture(directory, "hud", 1280, 720, false);
        }

        static void Capture(string directory, string kind, int width, int height, bool stress)
        {
            var canvasGo = new GameObject("Root_T026_ControlledPreview", typeof(RectTransform), typeof(Canvas));
            var cameraGo = new GameObject("Root_T026_PreviewCamera", typeof(Camera));
            var previous = RenderTexture.active;
            RenderTexture target = null;
            Texture2D pixels = null;
            LuaEnv env = null;
            int clicksBefore = ClickSubscription.LiveCount;
            try
            {
                var canvas = canvasGo.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                var rect = (RectTransform)canvasGo.transform;
                rect.sizeDelta = new Vector2(width, height);
                rect.localScale = Vector3.one * .01f;
                bool shop = kind == "shop";
                bool hud = kind == "hud";
                var root = hud ? M2U01WindowBuilder.BuildHomeHud(canvas.transform)
                    : shop ? M2U01WindowBuilder.BuildShop(canvas.transform) : M2U01WindowBuilder.BuildBag(canvas.transform);
                foreach (Transform child in canvasGo.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = 31;
                var camera = cameraGo.GetComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(.06f, .08f, .1f, 1);
                camera.orthographic = true;
                camera.orthographicSize = height * .005f;
                camera.aspect = (float)width / height;
                camera.cullingMask = 1 << 31;
                camera.transform.position = new Vector3(0, 0, -10);
                canvas.worldCamera = camera;
                env = new LuaEnv();
                string lua = Path.Combine(Application.dataPath, "Lua");
                env.AddLoader((ref string name) => {
                    string path = Path.Combine(lua, name.Replace('.', '/') + ".txt");
                    return File.Exists(path) ? File.ReadAllBytes(path) : null;
                });
                env.Global.Set("ROOT_PREVIEW_VIEW", new ViewHost(root));
                env.Global.Set("ROOT_PREVIEW_SHOP", shop);
                env.Global.Set("ROOT_PREVIEW_STRESS", stress);
                env.Global.Set("ROOT_PREVIEW_HUD", hud);
                env.DoString(@"
local catalog=require('Services/GameplayDisplayCatalog')
assert(catalog.isReady(),'display data not ready')
local bag={{itemId=1001,count=198},{itemId=2001,count=12},{itemId=1,count=7}}
if ROOT_PREVIEW_STRESS then
 bag={} for _,r in ipairs(catalog.listItems()) do
  if r.type~='Currency' then bag[#bag+1]={itemId=r.id,count=1} end
 end
end
local snapshot={playerId='9007199254740993',nickname='预览角色',
 gold='9223372036854775807',level=ROOT_PREVIEW_HUD and 20 or 3,bagSlots=60,bag=bag,
 plots={},tools={{toolId=5101},{toolId=5102},{toolId=5103}}}
local store={getSnapshot=function() return snapshot end,
 subscribe=function() return {dispose=function() end} end}
if ROOT_PREVIEW_HUD then
 package.loaded['Models/ModelStore']=store
 ROOT_PREVIEW_PRESENTER=require('UI/Windows/HomeHudWindow').new()
else
 local name=ROOT_PREVIEW_SHOP and 'UI/Presenters/ShopPresenter' or 'UI/Presenters/BagPresenter'
 ROOT_PREVIEW_PRESENTER=require(name).new({},store,nil,catalog)
end
ROOT_PREVIEW_PRESENTER:OnCreate(ROOT_PREVIEW_VIEW)
ROOT_PREVIEW_PRESENTER:OnOpen('')
if ROOT_PREVIEW_STRESS then for i=1,#bag-1 do ROOT_PREVIEW_PRESENTER:selectNext() end end
");
                Canvas.ForceUpdateCanvases();
                var proof = new Proof { kind = kind, width = width, height = height,
                    ownedClickSubscriptionsWhileVisible = ClickSubscription.LiveCount - clicksBefore };
                foreach (Text label in root.GetComponentsInChildren<Text>(true))
                {
                    var missing = new System.Text.StringBuilder();
                    foreach (char c in label.text)
                        if (!char.IsControl(c) && !label.font.HasCharacter(c) && missing.ToString().IndexOf(c) < 0) missing.Append(c);
                    proof.labels.Add(new TextProof { path = label.name, text = label.text, font = label.font.name,
                        missingGlyphs = missing.ToString(), width = label.rectTransform.rect.width,
                        height = label.rectTransform.rect.height, preferredHeight = label.preferredHeight });
                }
                target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                pixels = new Texture2D(width, height, TextureFormat.RGB24, false);
                pixels.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                pixels.Apply();
                File.WriteAllBytes(Path.Combine(directory, kind + ".png"), pixels.EncodeToPNG());
                File.WriteAllText(Path.Combine(directory, kind + ".json"), JsonUtility.ToJson(proof, true));
                camera.targetTexture = null;
                Debug.Log("[T026 Root] Editor preview captured " + kind);
            }
            finally
            {
                try
                {
                    if (env != null)
                    {
                        try { env.DoString("if ROOT_PREVIEW_PRESENTER then ROOT_PREVIEW_PRESENTER:OnClose() ROOT_PREVIEW_PRESENTER:OnDestroy() end ROOT_PREVIEW_PRESENTER=nil ROOT_PREVIEW_VIEW=nil collectgarbage('collect')"); }
                        finally { env.Dispose(); }
                    }
                }
                finally
                {
                    RenderTexture.active = previous;
                    if (pixels != null) UnityEngine.Object.DestroyImmediate(pixels);
                    if (target != null) UnityEngine.Object.DestroyImmediate(target);
                    UnityEngine.Object.DestroyImmediate(cameraGo);
                    UnityEngine.Object.DestroyImmediate(canvasGo);
                }
                if (ClickSubscription.LiveCount != clicksBefore)
                    throw new InvalidOperationException("Root preview left owned click subscriptions: " + kind);
            }
        }
    }
}
