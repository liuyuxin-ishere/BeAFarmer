using System;
using System.IO;
using Game.Framework.Home;
using Game.Framework.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Editor.M2
{
    /// <summary>
    /// T035：烘焙 Farm/HomeHud Prefab、M2Farm Resources 材质，并在 M1_Home 装配灰盒地块根（显式 Bind）。
    /// 由 Root 验收前执行；禁止手造 YAML Prefab/Material。
    /// </summary>
    public static class StageT035HomeTools
    {
        const string HomeHudPath = "Assets/Res/UI/HomeHudWindow.prefab";
        const string FarmPath = "Assets/Res/UI/FarmWindow.prefab";
        const string HomeScenePath = "Assets/Scenes/M1/M1_Home.unity";
        const string ShaderAssetPath = "Assets/Framework/Home/Shaders/M2FarmPlotUnlit.shader";
        const string MaterialAssetPath = "Assets/Resources/M2Farm/M2FarmPlot.mat";
        const string ExpectedShaderName = "M2Farm/PlotUnlit";
        const string LegacyHomeMarkerRootName = "M1HomeMarker";
        const string LegacyHomeMarkerLabelName = "Label";

        [MenuItem("Farming/M2/T035 Bake Farm Prefabs And Home Greybox")]
        public static void BakeMenu()
        {
            // 资源先于 UI/场景：干净工程可重建；缺失则抛错，不可假成功
            EnsureM2FarmPlotResources();
            BakeFarmPrefabs();
            BakeHomeGreyboxIntoScene();
            Debug.Log("[T035] M2Farm resources + Farm prefabs + M1_Home greybox baked");
        }

        /// <summary>批处理：-executeMethod Game.Editor.M2.StageT035HomeTools.EnsureM2FarmPlotResources</summary>
        public static void EnsureM2FarmPlotResources()
        {
            string shaderFs = Path.Combine(Application.dataPath, "Framework/Home/Shaders/M2FarmPlotUnlit.shader");
            if (!File.Exists(shaderFs))
                throw new InvalidOperationException("m2farm-shader-source-missing: " + ShaderAssetPath);

            Directory.CreateDirectory(Path.Combine(Application.dataPath, "Resources/M2Farm"));
            AssetDatabase.ImportAsset(ShaderAssetPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderAssetPath);
            if (shader == null)
                throw new InvalidOperationException("m2farm-shader-import-failed: " + ShaderAssetPath);
            if (shader.name != ExpectedShaderName)
                throw new InvalidOperationException("m2farm-shader-name-mismatch: " + shader.name);

            Material mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialAssetPath);
            if (mat == null)
            {
                mat = new Material(shader)
                {
                    name = "M2FarmPlot"
                };
                AssetDatabase.CreateAsset(mat, MaterialAssetPath);
                mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialAssetPath);
            }

            if (mat == null)
                throw new InvalidOperationException("m2farm-material-create-failed: " + MaterialAssetPath);

            mat.shader = shader;
            Color seed = new Color(0.45f, 0.36f, 0.22f, 1f);
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", seed);
            if (mat.HasProperty("_Color"))
                mat.SetColor("_Color", seed);
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialAssetPath);
            if (mat == null || mat.shader == null || mat.shader.name != ExpectedShaderName)
                throw new InvalidOperationException("m2farm-material-shader-unresolved: " + MaterialAssetPath);
            if (mat.shader.name.Contains("InternalError"))
                throw new InvalidOperationException("m2farm-shader-compile-error: " + ShaderAssetPath);

            Material resourcesProbe = Resources.Load<Material>(HomePlotVisual.MaterialResourcePath);
            if (resourcesProbe == null)
                throw new InvalidOperationException(
                    "m2farm-resources-load-failed: Resources/" + HomePlotVisual.MaterialResourcePath);
        }

        /// <summary>批处理：-executeMethod Game.Editor.M2.StageT035HomeTools.BakeFarmPrefabs</summary>
        public static void BakeFarmPrefabs()
        {
            EnsureM2FarmPlotResources();
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "Res/UI"));
            SavePrefab(HomeHudPath, M2U01WindowBuilder.BuildHomeHud(null));
            SavePrefab(FarmPath, M2U01WindowBuilder.BuildFarm(null));
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        /// <summary>批处理：-executeMethod Game.Editor.M2.StageT035HomeTools.BakeHomeGreyboxIntoScene</summary>
        public static void BakeHomeGreyboxIntoScene()
        {
            EnsureM2FarmPlotResources();
            HomePlotLayout.ValidateOrThrow();
            Scene scene = EditorSceneManager.OpenScene(HomeScenePath, OpenSceneMode.Single);
            // 停用本 scene 根 M1HomeMarker 下旧调试 Label（巨大白字挡地）；不动 Marker 父与其它物体。
            DeactivateLegacyM1HomeMarkerLabel(scene);

            var existing = GameObject.Find(HomeGreyboxSpawner.RootName);
            if (existing != null)
                Undo.DestroyObjectImmediate(existing);

            var host = GameObject.Find(HomeFarmRuntime.HostObjectName);
            if (host == null)
            {
                host = new GameObject(HomeFarmRuntime.HostObjectName);
                Undo.RegisterCreatedObjectUndo(host, "Create HomeFarmRuntime");
            }

            if (host.GetComponent<HomeFarmRuntime>() == null)
                Undo.AddComponent<HomeFarmRuntime>(host);

            // 配置本 scene 既有/自有相机；不创建第二份、不碰外来场景相机
            HomeGreyboxSpawner.EnsureHomeCamera(host.transform);

            // Runtime.Awake 会再 Spawn；场景预烘焙仅确保相机锚点，避免双份地块根。
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// 仅在已打开的 Home 场景内，找精确根名 M1HomeMarker 的直接子 Label 并 SetActive(false)。
        /// 不用 GameObject.Find 跨场景；不毁 Marker 父、不改相机/runtime。
        /// </summary>
        static void DeactivateLegacyM1HomeMarkerLabel(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded)
                return;

            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                GameObject root = roots[i];
                if (root == null || root.name != LegacyHomeMarkerRootName)
                    continue;

                Transform labelTf = null;
                for (int c = 0; c < root.transform.childCount; c++)
                {
                    Transform child = root.transform.GetChild(c);
                    if (child != null && child.name == LegacyHomeMarkerLabelName)
                    {
                        labelTf = child;
                        break;
                    }
                }

                if (labelTf == null)
                    return;

                GameObject label = labelTf.gameObject;
                if (!label.activeSelf)
                    return;

                Undo.RecordObject(label, "Deactivate legacy M1HomeMarker Label");
                label.SetActive(false);
                EditorUtility.SetDirty(label);
                return;
            }
        }

        static void SavePrefab(string assetPath, GameObject root)
        {
            if (root == null)
                throw new InvalidOperationException("builder-null " + assetPath);
            try
            {
                PrefabUtility.SaveAsPrefabAsset(root, assetPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
    }
}
