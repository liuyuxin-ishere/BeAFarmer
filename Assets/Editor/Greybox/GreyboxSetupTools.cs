using Game.Demo.Greybox;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Editor.Greybox
{
    public static class GreyboxSetupTools
    {
        const string Menu = "Farming/Greybox/";

        [MenuItem(Menu + "1. Setup Greybox Scene", priority = 100)]
        public static void SetupScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var host = new GameObject("GreyboxHost");
            host.AddComponent<GreyboxBootstrap>();
            Selection.activeGameObject = host;
            EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log("[Greybox] Scene ready. Start CloudIsle.Server then enter Play. GuideUI will show T01…");
        }

        [MenuItem(Menu + "2. Save As GreyboxHome", priority = 101)]
        public static void SaveScene()
        {
            string path = "Assets/Scenes/GreyboxHome.unity";
            if (!AssetDatabase.IsValidFolder("Assets/Scenes"))
                AssetDatabase.CreateFolder("Assets", "Scenes");
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), path);
            Debug.Log("[Greybox] Saved " + path);
        }
    }
}
