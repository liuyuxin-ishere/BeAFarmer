using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Framework.Home;
using Game.Framework.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using XLua;

namespace Game.Editor.M2
{
    // Root layout evidence: actual builders, camera, greybox, presenter and ViewHost; labeled controlled store/clock, no Native/economic claim.
    public static class T035FarmRootPreview
    {
        [Serializable] public sealed class PlotProof {public int id;public float x,y,z;public bool insideViewport,coveredByFarm,coveredByHud;}
        [Serializable] public sealed class Proof
        {
            public string scope="Editor production layout and camera with controlled read-only store/clock;1920x1080 Canvas reference units,uniform16:9 scaling;no Native/network claim";
            public string scenario;public int width,height,page,ownedClickSubscriptions;public List<PlotProof> plots=new List<PlotProof>();public List<T026UiRootPreview.TextProof> labels=new List<T026UiRootPreview.TextProof>();
        }
        public static void CaptureBatch()
        {
            string dir=Environment.GetCommandLineArgs().FirstOrDefault(x=>x.StartsWith("--m2-ui-proof-dir=",StringComparison.Ordinal));
            if(dir==null)throw new ArgumentException("proof directory required");dir=Path.GetFullPath(dir.Substring("--m2-ui-proof-dir=".Length));
            string allowed=Path.GetFullPath(Path.Combine(Application.dataPath,"../Docs/Acceptance/M2"))+Path.DirectorySeparatorChar;
            if(!dir.StartsWith(allowed,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("owned acceptance directory required");Directory.CreateDirectory(dir);
            Capture(dir,1280,720);Capture(dir,1920,1080);
        }
        static void Capture(string dir,int width,int height)
        {
            var owner=new GameObject("Root_T035_LayoutOnly");var canvasGo=new GameObject("Root_T035_Canvas",typeof(RectTransform),typeof(Canvas));canvasGo.transform.SetParent(owner.transform);
            var homeGo=new GameObject("Root_T035_HomeCamera",typeof(Camera));homeGo.transform.SetParent(owner.transform);var home=homeGo.GetComponent<Camera>();
            var uiGo=new GameObject("Root_T035_UICamera",typeof(Camera));uiGo.transform.SetParent(owner.transform);var ui=uiGo.GetComponent<Camera>();
            LuaEnv env=null;RenderTexture rt=null;Texture2D pixels=null;var previous=RenderTexture.active;int clicks=ClickSubscription.LiveCount;
            try
            {
                // Use the production camera placement helper against this explicitly owned main camera.
                typeof(HomeGreyboxSpawner).GetMethod("PlaceCamera",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic).Invoke(null,new object[]{home});home.cullingMask=(1<<28)|(1<<31);home.aspect=(float)width/height;home.clearFlags=CameraClearFlags.SolidColor;home.backgroundColor=new Color(.12f,.16f,.18f);
                var plots=HomeGreyboxSpawner.SpawnPlots(owner.transform,28);var visuals=plots.GetComponentsInChildren<HomePlotVisual>().ToDictionary(x=>x.InstanceId);
                var lightGo=new GameObject("Root_T035_Light",typeof(Light));lightGo.transform.SetParent(owner.transform);var light=lightGo.GetComponent<Light>();light.type=LightType.Directional;light.intensity=1.4f;light.cullingMask=1<<28;light.transform.rotation=Quaternion.Euler(50,-30,0);
                var canvas=canvasGo.GetComponent<Canvas>();canvas.renderMode=RenderMode.WorldSpace;canvas.worldCamera=home;var cr=(RectTransform)canvas.transform;cr.sizeDelta=new Vector2(1920,1080);cr.localScale=Vector3.one*(2*Mathf.Tan(home.fieldOfView*Mathf.Deg2Rad*.5f)/1080);cr.position=home.transform.TransformPoint(Vector3.forward);cr.rotation=home.transform.rotation;
                var hud=M2U01WindowBuilder.BuildHomeHud(canvas.transform);var farm=M2U01WindowBuilder.BuildFarm(canvas.transform);foreach(Transform child in canvasGo.GetComponentsInChildren<Transform>(true))child.gameObject.layer=31;
                ui.enabled=false; // One production-view camera avoids URP base-camera clears when composing two manual renders.
                env=new LuaEnv();string lua=Path.Combine(Application.dataPath,"Lua");env.AddLoader((ref string module)=>{string f=Path.Combine(lua,module.Replace('.','/')+".txt");return File.Exists(f)?File.ReadAllBytes(f):null;});
                env.Global.Set("ROOT_FARM_VIEW",new ViewHost(farm));env.Global.Set("ROOT_HUD_VIEW",new ViewHost(hud));
                env.Global.Set("ROOT_PAINT",(Action<string>)(text=>{foreach(string piece in text.Split(';')){var parts=piece.Split(':');if(parts.Length==2&&int.TryParse(parts[0],out int id)&&int.TryParse(parts[1],out int kind)&&visuals.TryGetValue(id,out var visual))visual.SetDisplay((HomePlotDisplayKind)kind);}}));
                env.DoString(@"
local snapshot={playerId='9007199254740993',nickname='受控布局预览',gold='5000',level=3,bagSlots=60,bag={{itemId=2001,count=12}},plots={},tools={}}
for id=1001,1024 do local st=(id-1001)%4;snapshot.plots[#snapshot.plots+1]={plotId=id,state=st,cropId=1,plantTime='1',readyTime=st==3 and '100' or '200'} end
local store={getSnapshot=function()return snapshot end,subscribe=function()return{dispose=function()end}end}
package.loaded['Models/ModelStore']=store
package.loaded['Services/FarmService']={isWritable=function()return true end,harvest=function(ids,done)local r={}for i,id in ipairs(ids)do r[#r+1]={plotId=id,code=ROOT_ALL_FAIL and 401 or (i%2==0 and 301 or 0)}end done(0,{disposition='Recorded',raw={results=r}})return nil end}
local Hud=require('UI/Windows/HomeHudWindow')
CS={Game={Framework={Home={LuaHomeFarmFacade={EnsureRuntime=function()end,AttachHandlers=function()return 1 end,DetachHandlers=function()end,SetInputEnabled=function()end,SetToolMode=function()end,CancelActiveGesture=function()end,ApplyPlotDisplays=function(s)ROOT_PAINT(s)end}},Gameplay={LuaGameplayFacade={IsClockSynchronized=function()return true end,RemainingMs=function(s)return s=='100' and '0' or '12000' end}}}}}
ROOT_HUD=Hud.new();ROOT_HUD:OnCreate(ROOT_HUD_VIEW);ROOT_HUD:OnOpen('')
ROOT_FARM=require('UI/Windows/FarmWindow').new();ROOT_FARM:OnCreate(ROOT_FARM_VIEW);ROOT_FARM:OnOpen('')
");
                Canvas.ForceUpdateCanvases();var proof=new Proof{width=width,height=height};var corners=new Vector3[4];((RectTransform)farm.transform).GetWorldCorners(corners);Vector3 min=home.WorldToViewportPoint(corners[0]),max=home.WorldToViewportPoint(corners[2]);((RectTransform)hud.transform).GetWorldCorners(corners);Vector3 hmin=home.WorldToViewportPoint(corners[0]),hmax=home.WorldToViewportPoint(corners[2]);
                foreach(var row in HomePlotLayout.All){var v=home.WorldToViewportPoint(new Vector3(row.PosX,row.PosY,row.PosZ));proof.plots.Add(new PlotProof{id=row.InstanceId,x=v.x*width,y=v.y*height,z=v.z,insideViewport=v.z>0&&v.x>=0&&v.x<=1&&v.y>=0&&v.y<=1,coveredByFarm=v.x>=min.x&&v.x<=max.x&&v.y>=min.y&&v.y<=max.y,coveredByHud=v.x>=hmin.x&&v.x<=hmax.x&&v.y>=hmin.y&&v.y<=hmax.y});}
                rt=new RenderTexture(width,height,24,RenderTextureFormat.ARGB32);home.targetTexture=rt;pixels=new Texture2D(width,height,TextureFormat.RGB24,false);var seen=new HashSet<int>();
                for(int step=1;step<=7;step++)
                {
                    bool feedback=step>=6;int page=Math.Min(step,5);if(feedback){env.Global.Set("ROOT_ALL_FAIL",step==7);env.DoString("local ids={}for id=1001,1024 do ids[#ids+1]=id end ROOT_FARM._presenter:harvest(ids)");}
                    Canvas.ForceUpdateCanvases();proof.scenario=feedback?(step==7?"controlled24-item-all-failed-layout-only":"controlled24-item-partial-response-layout-only"):"page-browsing";proof.page=page;proof.ownedClickSubscriptions=ClickSubscription.LiveCount-clicks;proof.labels.Clear();
                    foreach(Text label in canvasGo.GetComponentsInChildren<Text>(true)){string missing=new string(label.text.Where(c=>!char.IsControl(c)&&!label.font.HasCharacter(c)).Distinct().ToArray());proof.labels.Add(new T026UiRootPreview.TextProof{path=label.name,text=label.text,font=label.font.name,missingGlyphs=missing,width=label.rectTransform.rect.width,height=label.rectTransform.rect.height,preferredHeight=label.preferredHeight});}
                    string text=farm.transform.Find("CountdownLabel").GetComponent<Text>().text;
                    var ids=System.Text.RegularExpressions.Regex.Matches(text,@"地(\d+)").Cast<System.Text.RegularExpressions.Match>().Select(x=>int.Parse(x.Groups[1].Value)).ToArray();
                    if(!ids.SequenceEqual(Enumerable.Range(1001+(page-1)*5,page==5?4:5)))throw new InvalidOperationException("actual FarmWindow page wiring does not expose expected stable plot rows,page="+page);
                    if(!feedback)foreach(int id in ids)if(!seen.Add(id))throw new InvalidOperationException("page duplicates a plot");
                    home.Render();RenderTexture.active=rt;pixels.ReadPixels(new Rect(0,0,width,height),0,0);pixels.Apply();string stem=Path.Combine(dir,"farm-"+width+"x"+height+(feedback?(step==7?"-all-failed":"-feedback"):page==1?"":"-page"+page));File.WriteAllBytes(stem+".png",pixels.EncodeToPNG());File.WriteAllText(stem+".json",JsonUtility.ToJson(proof,true));
                    var next=farm.transform.Find("CountdownNextButton").GetComponent<Button>();if(next.interactable!=(page<5))throw new InvalidOperationException("page next button state mismatch");if(page<5&&!feedback)next.onClick.Invoke();
                }
                if(seen.Count!=24)throw new InvalidOperationException("not all24 plots were reachable");home.targetTexture=null;ui.targetTexture=null;Debug.Log("[T035 Root] actual FarmWindow five-page layout/wiring captured "+width+"x"+height);
            }
            finally
            {
                if(env!=null){try{env.DoString("if ROOT_FARM then ROOT_FARM:OnClose() ROOT_FARM:OnDestroy() end if ROOT_HUD then ROOT_HUD:OnClose() ROOT_HUD:OnDestroy() end ROOT_FARM=nil ROOT_HUD=nil ROOT_FARM_VIEW=nil ROOT_HUD_VIEW=nil ROOT_PAINT=nil collectgarbage('collect')");}finally{env.Dispose();}}
                RenderTexture.active=previous;if(pixels!=null)UnityEngine.Object.DestroyImmediate(pixels);if(rt!=null)UnityEngine.Object.DestroyImmediate(rt);UnityEngine.Object.DestroyImmediate(owner);
                if(ClickSubscription.LiveCount!=clicks)throw new InvalidOperationException("preview leaked owned click subscriptions");
            }
        }
    }
}
