using System;
using System.IO;
using Game.Framework.UI;
using UnityEditor;
using UnityEngine;

namespace Game.Editor.M2
{
    /// <summary>
    /// T026：烘焙/刷新 Bag/Shop/HomeHud 生产 Prefab。由 Root 在验收前执行，禁止手造 YAML Prefab。
    /// </summary>
    public static class StageT026UiTools
    {
        const string HomePath = "Assets/Res/UI/HomeHudWindow.prefab";
        const string BagPath = "Assets/Res/UI/BagWindow.prefab";
        const string ShopPath = "Assets/Res/UI/ShopWindow.prefab";

        [MenuItem("Farming/M2/T026 Bake U01 Prefabs")]
        public static void BakeU01PrefabsMenu()
        {
            BakeU01Prefabs();
            Debug.Log("[T026] U01 prefabs baked: HomeHud/Bag/Shop");
        }

        /// <summary>批处理：-executeMethod Game.Editor.M2.StageT026UiTools.BakeU01Prefabs</summary>
        public static void BakeU01Prefabs()
        {
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "Res/UI"));
            SavePrefab(HomePath, M2U01WindowBuilder.BuildHomeHud(null));
            SavePrefab(BagPath, M2U01WindowBuilder.BuildBag(null));
            SavePrefab(ShopPath, M2U01WindowBuilder.BuildShop(null));
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
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
