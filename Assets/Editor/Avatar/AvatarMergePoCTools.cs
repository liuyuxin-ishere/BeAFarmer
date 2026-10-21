using System.Diagnostics;
using System.Threading.Tasks;
using Game.Demo.AvatarMerge;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace Game.Editor.Avatar
{
    public static class AvatarMergePoCTools
    {
        const string MenuRoot = "Farming/Avatar Merge PoC/";

        [MenuItem(MenuRoot + "1. Setup Demo Scene", priority = 100)]
        public static void SetupDemoScene()
        {
            EnsureDemoScene();
            var presenter = AvatarDemoPresenter.FindOrCreate();
            presenter.EnsureServices();

            // Clear previous rig under host
            var host = presenter.transform;
            for (int i = host.childCount - 1; i >= 0; i--)
            {
                var child = host.GetChild(i).gameObject;
                if (child.name.StartsWith("AvatarRig") || child.name.StartsWith("Crowd_"))
                    Object.DestroyImmediate(child);
            }

            EnsureLightingAndCamera();

            var rig = AvatarRigBuilder.BuildRig(host);
            presenter.BindRig(rig);
            Selection.activeGameObject = rig;
            SceneView.lastActiveSceneView?.FrameSelected();

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Debug.Log("[AvatarMerge] Setup Demo Scene complete: light/camera/rig ready.");
        }

        [MenuItem(MenuRoot + "2. Merge Default Outfit", priority = 101)]
        public static void MergeDefaultOutfit()
        {
            RunAsync(async () =>
            {
                var presenter = RequirePresenterWithRig();
                var sw = Stopwatch.StartNew();
                var result = await presenter.MergeDefaultAsync();
                sw.Stop();
                Debug.Log($"[AvatarMerge] Default outfit merged hash={result.OutfitHash} verts={result.CombinedMesh.vertexCount} ms={sw.ElapsedMilliseconds}");
                FrameBody(presenter);
            });
        }

        [MenuItem(MenuRoot + "3. Switch One Part", priority = 102)]
        public static void SwitchOnePart()
        {
            RunAsync(async () =>
            {
                var presenter = RequirePresenterWithRig();
                int cur = presenter.Outfit.TopId;
                int next = cur == 7201 ? 7202 : 7201;
                await presenter.SwitchPartAsync(AvatarPartType.Top, next);
                FrameBody(presenter);
            });
        }

        [MenuItem(MenuRoot + "3b. Cycle All Parts", priority = 103)]
        public static void CycleAllParts()
        {
            RunAsync(async () =>
            {
                var presenter = RequirePresenterWithRig();
                await presenter.CycleAllPartsAsync();
                FrameBody(presenter);
            });
        }

        [MenuItem(MenuRoot + "4. Spawn 40 Instances", priority = 104)]
        public static void Spawn40()
        {
            RunAsync(async () =>
            {
                var presenter = RequirePresenterWithRig();
                await presenter.SpawnCrowdAsync(40);
                if (SceneView.lastActiveSceneView != null)
                {
                    SceneView.lastActiveSceneView.LookAt(presenter.transform.position + new Vector3(0, 1, 4),
                        Quaternion.Euler(20f, 0f, 0f), 12f);
                }
            });
        }

        [MenuItem(MenuRoot + "5. Log Cache Stats", priority = 105)]
        public static void LogCacheStats()
        {
            var presenter = AvatarDemoPresenter.FindOrCreate();
            presenter.EnsureServices();
            presenter.LogCacheStats();
        }

        [MenuItem(MenuRoot + "6. Re-Merge Same Outfit (cache test)", priority = 106)]
        public static void RemergeSame()
        {
            RunAsync(async () =>
            {
                var presenter = RequirePresenterWithRig();
                var outfit = presenter.Outfit;
                var sw = Stopwatch.StartNew();
                await presenter.MergeAsync(outfit);
                sw.Stop();
                Debug.Log($"[AvatarMerge] Re-merge same outfit done in {sw.ElapsedMilliseconds}ms (expect cache hit)");
                presenter.LogCacheStats();
            });
        }

        [MenuItem(MenuRoot + "Generate Placeholder Catalog Note", priority = 200)]
        public static void LogCatalog()
        {
            var catalog = AvatarCatalogFactory.CreateDefault();
            Debug.Log($"[AvatarMerge] Catalog parts={catalog.Parts.Length} (procedural placeholders; no FBX required)");
            foreach (var p in catalog.Parts)
                Debug.Log($"  id={p.Id} part={p.Part} color={p.PlaceholderColor}");
        }

        static AvatarDemoPresenter RequirePresenterWithRig()
        {
            var presenter = AvatarDemoPresenter.FindOrCreate();
            presenter.EnsureServices();
            if (presenter.RigRoot == null)
            {
                SetupDemoScene();
                presenter = AvatarDemoPresenter.FindOrCreate();
            }
            if (presenter.RigRoot == null || presenter.BodyRenderer == null)
                throw new System.InvalidOperationException("Demo rig not ready. Run Setup Demo Scene first.");
            return presenter;
        }

        static void EnsureDemoScene()
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            }
        }

        static void EnsureLightingAndCamera()
        {
            if (Object.FindFirstObjectByType<Light>() == null)
            {
                var lightGo = new GameObject("Directional Light");
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.1f;
                lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            }

            var cam = Camera.main;
            if (cam == null)
            {
                var camGo = new GameObject("Main Camera");
                camGo.tag = "MainCamera";
                cam = camGo.AddComponent<Camera>();
            }
            cam.transform.position = new Vector3(0f, 1.4f, -3.2f);
            cam.transform.rotation = Quaternion.Euler(8f, 0f, 0f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.18f, 0.2f, 0.24f);
        }

        static void FrameBody(AvatarDemoPresenter presenter)
        {
            if (presenter.BodyRenderer != null)
            {
                Selection.activeGameObject = presenter.BodyRenderer.gameObject;
                SceneView.lastActiveSceneView?.FrameSelected();
            }
        }

        static void RunAsync(System.Func<Task> task)
        {
            try
            {
                var t = task();
                // Editor menu: pump synchronously for deterministic PoC
                while (!t.IsCompleted)
                {
                    if (t.Exception != null) break;
                    System.Threading.Thread.Sleep(1);
                }
                if (t.IsFaulted)
                    Debug.LogException(t.Exception?.InnerException ?? t.Exception);
                else if (t.IsCanceled)
                    Debug.LogWarning("[AvatarMerge] Task canceled");
            }
            catch (System.Exception ex)
            {
                Debug.LogException(ex);
            }
        }
    }
}
