using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Game.Framework.Home;
using Game.Framework.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.Editor.M2
{
    /// <summary>Idempotent ABC art bake. Does not rebuild Bag/Shop or publish content.</summary>
    public static class ArtReplacementTools
    {
        const string Source = "ArtAssets/Production/Singles/";
        const string Environment = "Assets/Res/Environment/CloudIsle_v03/";
        const string Models = "Assets/Models/CloudIsle_v03/";
        const string SkinPath = "Assets/Resources/CloudIsle/ArtUiSkin_v03.asset";
        const string CatalogPath = Environment + "HomeVisualCatalog_v03.asset";
        static readonly StringBuilder Mapping = new StringBuilder();

        [MenuItem("Farming/Art/ABC Bake Login Home UI And Models")]
        public static void Bake()
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Bake in Edit Mode only.");
            Mapping.Clear();
            Mapping.AppendLine("# ABC 资源映射（工具生成）\n\n仅含 ABC。原文件 GUID 保留，新文件由 Unity 生成 .meta。\n\n|来源|正式资源|GUID|\n|---|---|---|");
            var skin = BakeSkin();
            var catalog = BakeModels();
            BakeWindows();
            BakeScene(catalog);
            ConfigureCollector();
            AssetDatabase.SaveAssets();
            Directory.CreateDirectory("Assets/ReferenceDocs/ArtReplacement");
            File.WriteAllText("Assets/ReferenceDocs/ArtReplacement/资源映射表.md", Mapping.ToString(), new UTF8Encoding(false));
            File.WriteAllText(ArtReplacementCapture.Evidence + "bake-result.txt", "ABC bake complete; skin sprites="+skin.sprites.Length+"; models="+catalog.entries.Length+"\nNot a Player/network acceptance.\n");
            Debug.Log("[ArtABC] Bake complete: " + skin.sprites.Length + " sprites / " + catalog.entries.Length + " models / 5 UI prefabs");
        }

        static ArtUiSkin BakeSkin()
        {
            Directory.CreateDirectory("Assets/Resources/CloudIsle");
            var selected = new List<string>();
            foreach (string dir in new[] { "UI/Backgrounds/U01_LoginBackground", "UI/Elements/U05_Panels", "UI/Elements/U06_Borders", "UI/Elements/U07_Buttons", "UI/Elements/U08_Controls", "UI/Elements/U09_Bars", "UI/Elements/U10_Guidance", "Icons/I00_CropsApproved", "Icons/I01_Seeds", "Icons/I04_GatherItems", "Icons/I05_Currency", "Icons/I06_Navigation", "Icons/I07_Actions", "Icons/I08_Status", "Icons/I10_Account" })
                selected.AddRange(Directory.GetFiles(Source+dir,"*.png"));
            foreach (string source in selected)
            {
                string relative=source.Substring(Source.Length).Replace('\\','/');
                string destination="Assets/Res/Art2D/"+relative;
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(source,destination,true);
            }
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            var entries=new List<ArtUiSkin.Entry>();
            foreach(string source in selected.OrderBy(x=>x,StringComparer.Ordinal))
            {
                string relative=source.Substring(Source.Length).Replace('\\','/');
                string destination="Assets/Res/Art2D/"+relative;
                string key=Path.GetFileNameWithoutExtension(destination);
                var importer=(TextureImporter)AssetImporter.GetAtPath(destination);
                importer.textureType=TextureImporterType.Sprite;
                importer.alphaIsTransparency=true;
                importer.mipmapEnabled=false;
                importer.wrapMode=TextureWrapMode.Clamp;
                importer.filterMode=FilterMode.Bilinear;
                importer.textureCompression=TextureImporterCompression.Uncompressed;
                importer.maxTextureSize=2048;
                importer.spritePixelsPerUnit=100;
                // Trim only the Sprite rect, preserving the original production image pixels.
                var probe=new Texture2D(2,2);probe.LoadImage(File.ReadAllBytes(destination));
                int minX=probe.width,minY=probe.height,maxX=-1,maxY=-1;
                var pixels=probe.GetPixels32();
                for(int y=0;y<probe.height;y++)for(int x=0;x<probe.width;x++)
                    if(pixels[y*probe.width+x].a>12){minX=Math.Min(minX,x);minY=Math.Min(minY,y);maxX=Math.Max(maxX,x);maxY=Math.Max(maxY,y);}
                if(maxX<0)throw new InvalidOperationException("Empty image: "+source);
                Rect rect=new Rect(minX,minY,maxX-minX+1,maxY-minY+1);
                float border=relative.StartsWith("UI/Elements/") && !key.StartsWith("guide_") ? Mathf.Min(rect.width,rect.height)*.22f:0;
                importer.spriteImportMode=SpriteImportMode.Multiple;
#pragma warning disable 618
                importer.spritesheet=new[]{new SpriteMetaData { name=key,rect=rect,alignment=9,pivot=new Vector2(.5f,.5f),border=new Vector4(border,border,border,border) }};
#pragma warning restore 618
                Object.DestroyImmediate(probe);
                importer.SaveAndReimport();
                var sprite=AssetDatabase.LoadAllAssetsAtPath(destination).OfType<Sprite>().FirstOrDefault();
                if(sprite==null)throw new InvalidOperationException("Sprite import failed: "+destination);
                if(entries.Any(e=>e.key==key))throw new InvalidOperationException("Duplicate art key: "+key);
                entries.Add(new ArtUiSkin.Entry{key=key,sprite=sprite});
                Record(source,destination);
            }
            var skin=GetOrCreate<ArtUiSkin>(SkinPath);
            skin.sprites=entries.ToArray();
            skin.chineseFont=AssetDatabase.LoadAssetAtPath<Font>("Assets/Res/Art2D/Fonts/SourceHanSansSC-Regular.otf");
            if(skin.chineseFont==null)throw new InvalidOperationException("Chinese font not imported");
            skin.chineseFont.RequestCharactersInTexture("用户登录注册家园",24);
            foreach(char c in "用户登录注册家园")if(!skin.chineseFont.HasCharacter(c))throw new InvalidOperationException("Missing font glyph: "+c);
            EditorUtility.SetDirty(skin);AssetDatabase.SaveAssets();
            return skin;
        }

        static HomeVisualCatalog BakeModels()
        {
            Directory.CreateDirectory(Environment+"Visuals");Directory.CreateDirectory(Models+"Materials");
            var shader=Shader.Find("Universal Render Pipeline/Lit");
            if(shader==null)throw new InvalidOperationException("URP Lit unavailable");
            var palette=AssetDatabase.LoadAssetAtPath<Texture2D>(Models+"Textures/CloudIsle_Palette.png");
            var paletteImporter=(TextureImporter)AssetImporter.GetAtPath(Models+"Textures/CloudIsle_Palette.png");
            paletteImporter.filterMode=FilterMode.Point;paletteImporter.mipmapEnabled=false;paletteImporter.textureCompression=TextureImporterCompression.Uncompressed;paletteImporter.SaveAndReimport();
            var material=AssetDatabase.LoadAssetAtPath<Material>(Models+"Materials/CloudIslePalette.mat");
            if(material==null){material=new Material(shader);AssetDatabase.CreateAsset(material,Models+"Materials/CloudIslePalette.mat");}
            material.shader=shader;material.SetTexture("_BaseMap",palette);material.SetColor("_BaseColor",Color.white);material.SetFloat("_Smoothness",.18f);material.SetFloat("_Metallic",0);
            EditorUtility.SetDirty(material);
            var entries=new List<HomeVisualCatalog.Entry>();
            var measurements=new StringBuilder("# v03 Unity 包围盒实测\n\nW=Unity X，D=Unity Z，H=Unity Y；视觉 Prefab 原点校准到包围盒底部中央，整体 Scale=1。\n\n|模型|W|D|H|\n|---|---|---|---|\n");
            foreach(string file in Directory.GetFiles(Models,"*.fbx",SearchOption.AllDirectories).OrderBy(x=>x,StringComparer.Ordinal))
            {
                string path=file.Replace('\\','/');string key=Path.GetFileNameWithoutExtension(path);
                var importer=(ModelImporter)AssetImporter.GetAtPath(path);importer.globalScale=1;importer.useFileScale=true;importer.importAnimation=false;importer.SaveAndReimport();
                var root=new GameObject("Visual_"+key);
                try
                {
                    var model=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(path),root.transform);
                    foreach(var renderer in model.GetComponentsInChildren<Renderer>())renderer.sharedMaterials=Enumerable.Repeat(material,renderer.sharedMaterials.Length).ToArray();
                    Bounds bounds=GetBounds(root);
                    model.transform.localPosition-=new Vector3(bounds.center.x,bounds.min.y,bounds.center.z);
                    string output=Environment+"Visuals/Visual_"+key+".prefab";
                    var prefab=PrefabUtility.SaveAsPrefabAsset(root,output);
                    entries.Add(new HomeVisualCatalog.Entry{key=key,prefab=prefab});
                    measurements.AppendLine($"|{key}|{bounds.size.x:F4}|{bounds.size.z:F4}|{bounds.size.y:F4}|");
                    Record(path,output);
                }
                finally{Object.DestroyImmediate(root);}
            }
            if(entries.Count!=44)throw new InvalidOperationException("Expected 44 v03 models, got "+entries.Count);
            var catalog=GetOrCreate<HomeVisualCatalog>(CatalogPath);catalog.entries=entries.ToArray();
            catalog.plotRoot=InteractionRoot("PlotRoot",new Vector3(1,.18f,1));
            catalog.nodeRoot=InteractionRoot("GatherRoot",Vector3.one);
            var indicator=AssetDatabase.LoadAssetAtPath<Material>(Models+"Materials/StateIndicator.mat");
            if(indicator==null){indicator=new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit"));AssetDatabase.CreateAsset(indicator,Models+"Materials/StateIndicator.mat");}
            catalog.indicatorMaterial=indicator;EditorUtility.SetDirty(catalog);
            Directory.CreateDirectory("Assets/ReferenceDocs/ArtReplacement");File.WriteAllText("Assets/ReferenceDocs/ArtReplacement/模型实测.md",measurements.ToString(),new UTF8Encoding(false));
            string index="ArtAssets/Production/Blender/v03/ModelIndex.md";
            string original=File.ReadAllText(index);File.WriteAllText(index,original.Replace("(../../../../Assets/","(../../../../../Assets/"),new UTF8Encoding(false));
            return catalog;
        }

        static GameObject InteractionRoot(string name,Vector3 size)
        {
            var go=new GameObject(name);var collider=go.AddComponent<BoxCollider>();collider.size=size;collider.center=new Vector3(0,size.y*.5f,0);
            try{return PrefabUtility.SaveAsPrefabAsset(go,Environment+name+".prefab");}
            finally{Object.DestroyImmediate(go);}
        }

        public static Bounds GetBounds(GameObject go)
        {
            var renderers=go.GetComponentsInChildren<Renderer>();
            if(renderers.Length==0)throw new InvalidOperationException("No renderers: "+go.name);
            var bounds=renderers[0].bounds;foreach(var renderer in renderers)bounds.Encapsulate(renderer.bounds);return bounds;
        }

        static void BakeWindows()
        {
            SaveUI("LoginWindow",ArtLoginBuilder.Build(null));
            SaveUI("HomeHudWindow",M2U01WindowBuilder.BuildHomeHud(null));
            SaveUI("FarmWindow",M2U01WindowBuilder.BuildFarm(null));
            SaveUI("GatherWindow",M2U01WindowBuilder.BuildGather(null));
            SaveUI("ToolWindow",M2U01WindowBuilder.BuildTool(null));
        }

        static void SaveUI(string name,GameObject go)
        {
            try{PrefabUtility.SaveAsPrefabAsset(go,"Assets/Res/UI/"+name+".prefab");}
            finally{Object.DestroyImmediate(go);}
        }

        static void BakeScene(HomeVisualCatalog catalog)
        {
            var scene=EditorSceneManager.OpenScene("Assets/Scenes/M1/M1_Home.unity",OpenSceneMode.Single);
            var runtime=Object.FindFirstObjectByType<HomeFarmRuntime>();
            if(runtime==null){var go=new GameObject(HomeFarmRuntime.HostObjectName);Undo.RegisterCreatedObjectUndo(go,"Art home runtime");runtime=go.AddComponent<HomeFarmRuntime>();}
            Undo.RecordObject(runtime,"Bind v03 visuals");runtime.VisualCatalog=catalog;EditorUtility.SetDirty(runtime);
            var old=scene.GetRootGameObjects().FirstOrDefault(x=>x.name=="CloudIsleBuildings_v03");
            if(old!=null)Undo.DestroyObjectImmediate(old);
            var buildings=new GameObject("CloudIsleBuildings_v03");Undo.RegisterCreatedObjectUndo(buildings,"Place v03 buildings");
            string[] names={"house","mill","workshop","oven","stove"};
            Vector3[] places={new Vector3(-5,0,7),new Vector3(1,0,8),new Vector3(5,0,7),new Vector3(7.5f,0,4.5f),new Vector3(7.5f,0,1.5f)};
            for(int i=0;i<names.Length;i++)
            {
                var model=(GameObject)PrefabUtility.InstantiatePrefab(catalog.Find(names[i]),buildings.transform);
                model.name=names[i];model.transform.localPosition=places[i];Undo.RegisterCreatedObjectUndo(model,"Place building");
            }
            foreach(var root in scene.GetRootGameObjects())if(root.name=="M1HomeMarker")
            {var label=root.transform.Find("Label");if(label!=null){Undo.RecordObject(label.gameObject,"Hide debug label");label.gameObject.SetActive(false);}}
            EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);
        }

        static void ConfigureCollector()
        {
            var asset=AssetDatabase.LoadMainAssetAtPath("Assets/BundleCollectorSetting.asset");var serialized=new SerializedObject(asset);
            var packages=serialized.FindProperty("Packages");
            for(int i=0;i<packages.arraySize;i++)
            {
                var package=packages.GetArrayElementAtIndex(i);if(package.FindPropertyRelative("PackageName").stringValue!="DefaultPackage")continue;
                var groups=package.FindPropertyRelative("Groups");SerializedProperty group=null;
                for(int g=0;g<groups.arraySize;g++)if(groups.GetArrayElementAtIndex(g).FindPropertyRelative("GroupName").stringValue=="CloudIsleArt")group=groups.GetArrayElementAtIndex(g);
                if(group==null){groups.InsertArrayElementAtIndex(groups.arraySize);group=groups.GetArrayElementAtIndex(groups.arraySize-1);}
                group.FindPropertyRelative("GroupName").stringValue="CloudIsleArt";group.FindPropertyRelative("GroupDesc").stringValue="ABC visual catalog; referenced dependencies";
                group.FindPropertyRelative("ActiveRuleName").stringValue="EnableGroup";group.FindPropertyRelative("AssetTags").stringValue="art";
                var collectors=group.FindPropertyRelative("Collectors");collectors.arraySize=1;var c=collectors.GetArrayElementAtIndex(0);
                c.FindPropertyRelative("CollectPath").stringValue=CatalogPath;c.FindPropertyRelative("CollectorGUID").stringValue=AssetDatabase.AssetPathToGUID(CatalogPath);
                c.FindPropertyRelative("CollectorType").intValue=0;c.FindPropertyRelative("AddressRuleName").stringValue="AddressByFileName";
                c.FindPropertyRelative("PackRuleName").stringValue="PackSeparately";c.FindPropertyRelative("FilterRuleName").stringValue="CollectAll";
                c.FindPropertyRelative("AssetTags").stringValue="art";c.FindPropertyRelative("UserData").stringValue="";
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();EditorUtility.SetDirty(asset);
        }

        static T GetOrCreate<T>(string path) where T:ScriptableObject
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));var asset=AssetDatabase.LoadAssetAtPath<T>(path);
            if(asset==null){asset=ScriptableObject.CreateInstance<T>();AssetDatabase.CreateAsset(asset,path);}return asset;
        }
        static void Record(string source,string target)=>Mapping.AppendLine("|`"+source.Replace('\\','/')+"`|`"+target+"`|`"+AssetDatabase.AssetPathToGUID(target)+"`|");

        public static void VerifyAndCapture()
        {
            var skin=AssetDatabase.LoadAssetAtPath<ArtUiSkin>(SkinPath);
            var catalog=AssetDatabase.LoadAssetAtPath<HomeVisualCatalog>(CatalogPath);
            if(skin==null||catalog==null||catalog.entries.Length!=44)throw new InvalidOperationException("Art bake incomplete");
            var login=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Res/UI/LoginWindow.prefab");
            if(login.transform.Find("Nickname")!=null)throw new InvalidOperationException("Nickname input must not exist");
            foreach(string name in new[]{"LoginWindow","HomeHudWindow","FarmWindow","GatherWindow","ToolWindow"})
                ArtReplacementCapture.CaptureUI(name,ArtReplacementCapture.Evidence+"After/"+name+".png");
            var scene=EditorSceneManager.OpenScene("Assets/Scenes/M1/M1_Home.unity",OpenSceneMode.Single);
            var runtime=Object.FindFirstObjectByType<HomeFarmRuntime>();
            var root=new GameObject("ArtPreviewOnly");
            var plots=HomeGreyboxSpawner.SpawnPlots(root.transform,0,catalog);var nodes=HomeGreyboxSpawner.SpawnNodes(root.transform,0,catalog);
            if(plots.childCount!=24||nodes.childCount!=13)throw new InvalidOperationException("Instance count mismatch");
            int n=0;
            foreach(var plot in plots.GetComponentsInChildren<HomePlotVisual>())
            {
                plot.GetComponent<HomeArtInstance>().SetPlotData(2,n%7+1,n%4,n%2==0,n%3==0);
                plot.SetDisplay(HomePlotDisplayKind.Growing);n++;
            }
            foreach(var node in nodes.GetComponentsInChildren<HomeNodeVisual>())node.SetDisplay(node.InstanceId%2==0?HomeNodeDisplayKind.Cooling:HomeNodeDisplayKind.Ready);
            var camera=HomeGreyboxSpawner.EnsureHomeCamera(runtime.transform);
            ArtReplacementCapture.Render(camera,ArtReplacementCapture.Evidence+"After/Home-game.png");
            camera.transform.position=new Vector3(0,26,0);camera.transform.rotation=Quaternion.Euler(90,0,0);camera.orthographic=true;camera.orthographicSize=13;
            ArtReplacementCapture.Render(camera,ArtReplacementCapture.Evidence+"After/Home-top.png");
            camera.transform.position=new Vector3(22,12,-2);camera.transform.LookAt(new Vector3(0,1,2));
            ArtReplacementCapture.Render(camera,ArtReplacementCapture.Evidence+"After/Home-side.png");
            Object.DestroyImmediate(root);
            // Never save the preview's crops, cooldowns or camera changes into production.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            Debug.Log("[ArtABC] Verified 44 visuals, 24 plots, 13 nodes and login without nickname input; screenshots captured.");
        }

        public static void SyncLuaAndBridges()
        {
            var current = JsonUtility.FromJson<Game.Framework.Lua.RuntimeCatalog>(File.ReadAllText("Assets/Lua/Staging/catalog.txt"));
            Game.Editor.Build.ClientBuild.StageFormalLua(int.Parse(current.resVersion), current.requiredAppVersion, new StringBuilder());
            CSObjectWrapEditor.Generator.GenAll();
            AssetDatabase.SaveAssets();
            Debug.Log("[ArtABC] Staging synced and xLua bridges generated; requires new base-package build before distribution.");
        }
    }
}
