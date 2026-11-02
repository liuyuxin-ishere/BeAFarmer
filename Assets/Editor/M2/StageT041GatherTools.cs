using System;
using System.IO;
using Game.Framework.UI;
using UnityEditor;
using UnityEngine;

namespace Game.Editor.M2
{
    /// <summary>
    /// T041：烘焙 Gather/Tool/HomeHud Prefab。由 Root 在验收前执行；禁止手造 YAML Prefab。
    /// </summary>
    public static class StageT041GatherTools
    {
        const string HomeHudPath = "Assets/Res/UI/HomeHudWindow.prefab";
        const string GatherPath = "Assets/Res/UI/GatherWindow.prefab";
        const string ToolPath = "Assets/Res/UI/ToolWindow.prefab";

        [MenuItem("Farming/M2/T041 Bake Gather Tool Prefabs")]
        public static void BakeMenu()
        {
            BakeGatherToolPrefabs();
            Debug.Log("[T041] Gather/Tool/HomeHud prefabs baked");
        }

        /// <summary>批处理：-executeMethod Game.Editor.M2.StageT041GatherTools.BakeGatherToolPrefabs</summary>
        public static void BakeGatherToolPrefabs()
        {
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "Res/UI"));
            SavePrefab(HomeHudPath, M2U01WindowBuilder.BuildHomeHud(null));
            SavePrefab(GatherPath, M2U01WindowBuilder.BuildGather(null));
            SavePrefab(ToolPath, M2U01WindowBuilder.BuildTool(null));
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
