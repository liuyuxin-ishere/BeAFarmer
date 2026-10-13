using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using YooAsset;
using YooAsset.Editor;
using Game.Framework.Resource;

namespace Game.Editor.YooAssetTools
{
    /// <summary>
    /// PoC #3：收集器配置、Prefab、构建并拷贝到 StreamingAssets、场景。
    /// </summary>
    public static class YooAssetPoCTools
    {
        public const string PackageName = "DefaultPackage";
        public const string CollectFolder = "Assets/Res/PoC";
        public const string PrefabPath = "Assets/Res/PoC/PatchCube.prefab";
        public const string ScenePath = "Assets/Scenes/YooAsset_PoC.unity";

        [MenuItem("Farming/YooAsset PoC/0. Run Full Setup (Collector + Prefab + Build v1 + Scene)")]
        public static void RunFullSetup()
        {
            EnsureCollector();
            EnsurePrefab("v1", Color.red);
            BuildAndCopyToStreamingAssets("1");
            EnsureScene();
            Debug.Log("[YooAsset PoC] Full setup done. Open YooAsset_PoC scene and Enter Play Mode.");
        }

        [MenuItem("Farming/YooAsset PoC/1. Ensure Collector Config")]
        public static void EnsureCollector()
        {
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "Res/PoC"));
            AssetDatabase.Refresh();

            var setting = BundleCollectorSettingData.Setting;
            var package = setting.Packages.FirstOrDefault(p => p.PackageName == PackageName);
            if (package == null)
            {
                package = BundleCollectorSettingData.CreatePackage(PackageName);
            }

            package.EnableAddressable = true;
            package.AutoCollectShaders = true;
            package.IgnoreRuleName = nameof(NormalIgnoreRule);
            BundleCollectorSettingData.ModifyPackage(package);

            var group = package.Groups.FirstOrDefault(g => g.GroupName == "PoC");
            if (group == null)
                group = BundleCollectorSettingData.CreateGroup(package, "PoC");

            group.ActiveRuleName = nameof(EnableGroup);
            BundleCollectorSettingData.ModifyGroup(package, group);

            var collector = group.Collectors.FirstOrDefault(c => c.CollectPath == CollectFolder);
            if (collector == null)
            {
                collector = new BundleCollector
                {
                    CollectPath = CollectFolder,
                    CollectorGUID = AssetDatabase.AssetPathToGUID(CollectFolder),
                    CollectorType = ECollectorType.MainAssetCollector,
                    AddressRuleName = nameof(AddressByFileName),
                    PackRuleName = nameof(PackSeparately),
                    FilterRuleName = nameof(CollectAll),
                    AssetTags = "poc"
                };
                BundleCollectorSettingData.CreateCollector(group, collector);
            }
            else
            {
                collector.CollectorGUID = AssetDatabase.AssetPathToGUID(CollectFolder);
                collector.CollectorType = ECollectorType.MainAssetCollector;
                collector.AddressRuleName = nameof(AddressByFileName);
                collector.PackRuleName = nameof(PackSeparately);
                collector.FilterRuleName = nameof(CollectAll);
                collector.AssetTags = "poc";
                BundleCollectorSettingData.ModifyCollector(group, collector);
            }

            BundleCollectorSettingData.SaveFile();
            Debug.Log("[YooAsset PoC] Collector configured: DefaultPackage / PoC / Assets/Res/PoC");
        }

        [MenuItem("Farming/YooAsset PoC/2. Ensure Prefab v1 (Red)")]
        public static void EnsurePrefabV1()
        {
            EnsurePrefab("v1", Color.red);
        }

        [MenuItem("Farming/YooAsset PoC/3. Switch Prefab to v2 (Blue)")]
        public static void SwitchPrefabToV2()
        {
            EnsurePrefab("v2", Color.blue);
        }

        [MenuItem("Farming/YooAsset PoC/4. Build Package + Copy StreamingAssets (version=1)")]
        public static void BuildV1()
        {
            EnsureCollector();
            EnsurePrefab("v1", Color.red);
            BuildAndCopyToStreamingAssets("1");
        }

        [MenuItem("Farming/YooAsset PoC/5. Build Package + Copy StreamingAssets (version=2)")]
        public static void BuildV2()
        {
            EnsureCollector();
            EnsurePrefab("v2", Color.blue);
            BuildAndCopyToStreamingAssets("2");
        }

        [MenuItem("Farming/YooAsset PoC/6. Ensure PoC Scene")]
        public static void EnsureScene()
        {
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "Scenes"));

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            var bootGo = new GameObject("YooAssetPoCBoot");
            bootGo.AddComponent<YooAssetPoCBoot>();

            // 简单光照，便于观察颜色
            if (Object.FindFirstObjectByType<Light>() == null)
            {
                var lightGo = new GameObject("Directional Light");
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            }

            var cam = Object.FindFirstObjectByType<Camera>();
            if (cam != null)
            {
                cam.transform.position = new Vector3(0f, 1.5f, -4f);
                cam.transform.LookAt(Vector3.zero);
            }

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();
            Debug.Log($"[YooAsset PoC] Scene saved: {ScenePath}");
        }

        [MenuItem("Farming/YooAsset PoC/7. Clear Yoo Cache (Library/yoo)")]
        public static void ClearYooCache()
        {
            string projectPath = Path.GetDirectoryName(Application.dataPath);
            string cacheRoot = Path.Combine(projectPath, "Library/yoo");
            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, true);
                Debug.Log("[YooAsset PoC] Cleared Library/yoo");
            }
            else
            {
                Debug.Log("[YooAsset PoC] Library/yoo not found (ok)");
            }
        }

        public static void EnsurePrefab(string version, Color color)
        {
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "Res/PoC"));
            AssetDatabase.Refresh();

            const string strayScript = "Assets/Res/PoC/PatchCubeMarker.cs";
            if (AssetDatabase.LoadAssetAtPath<MonoScript>(strayScript) != null)
                AssetDatabase.DeleteAsset(strayScript);

            const string matPath = "Assets/Res/PoC/PatchCubeMat.mat";
            var shader = Shader.Find("Universal Render Pipeline/Lit")
                         ?? Shader.Find("URP/Lit")
                         ?? Shader.Find("Standard");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, matPath);
                mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            }

            mat.color = color;
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", color);
            EditorUtility.SetDirty(mat);

            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "PatchCube";
            var col = cube.GetComponent<Collider>();
            if (col != null)
                Object.DestroyImmediate(col);

            cube.GetComponent<Renderer>().sharedMaterial = mat;

            var marker = cube.AddComponent<PatchCubeMarker>();
            marker.Version = version;
            marker.Color = color;

            PrefabUtility.SaveAsPrefabAsset(cube, PrefabPath);
            Object.DestroyImmediate(cube);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[YooAsset PoC] Prefab {PrefabPath} => {version} / {color}");
        }

        public static void BuildAndCopyToStreamingAssets(string packageVersion)
        {
            // 清缓存，避免编辑器 Play 仍读旧包
            ClearYooCache();

            var uniqueBundleName = BundleCollectorSettingData.Setting.UniqueBundleName;
            var packRuleResult = DefaultBundlePackRule.CreateShadersPackRuleResult();
            string builtinShaderBundleName = packRuleResult.GetBundleName(PackageName, uniqueBundleName);

            var buildParameters = new ScriptableBuildParameters
            {
                BuildOutputRoot = BundleBuilderHelper.GetDefaultBuildOutputRoot(),
                BundledFileRoot = BundleBuilderHelper.GetStreamingAssetsRoot(),
                BuildPipeline = EBuildPipeline.ScriptableBuildPipeline.ToString(),
                BuildBundleType = (int)EBundleType.AssetBundle,
                BuildTarget = EditorUserBuildSettings.activeBuildTarget,
                PackageName = PackageName,
                PackageVersion = packageVersion,
                EnableSharePackRule = true,
                VerifyBuildingResult = true,
                FileNameStyle = EFileNameStyle.HashName,
                BundledCopyOption = EBundledCopyOption.ClearAndCopyAll,
                BundledCopyParams = string.Empty,
                CompressOption = ECompressOption.LZ4,
                ClearBuildCacheFiles = true,
                UseAssetDependencyDB = true,
                BuiltinShadersBundleName = builtinShaderBundleName
            };

            var pipeline = new ScriptableBuildPipeline();
            var buildResult = pipeline.Run(buildParameters, true);
            if (!buildResult.Success)
            {
                Debug.LogError($"[YooAsset PoC] Build FAILED: {buildResult.ErrorInfo}");
                return;
            }

            AssetDatabase.Refresh();
            Debug.Log($"[YooAsset PoC] Build OK version={packageVersion}\n  Output: {buildResult.OutputPackageDirectory}\n  StreamingAssets: {BundleBuilderHelper.GetStreamingAssetsRoot()}");
        }
    }
}
