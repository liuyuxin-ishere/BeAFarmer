using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Framework.Home
{
    /// <summary>
    /// 在配置坐标生成 24 块可视灰盒 Plot，每块显式 Bind instanceId/kind。
    /// </summary>
    public static class HomeGreyboxSpawner
    {
        public const string RootName = "HomeGreyboxPlots";
        public const string CameraObjectName = "HomeFarmCamera";

        public const string NodesRootName = "HomeGreyboxNodes";

        public static Transform SpawnPlots(Transform parent, int layer = 0)
        {
            HomePlotLayout.ValidateOrThrow();
            var rootGo = new GameObject(RootName);
            if (parent != null)
                rootGo.transform.SetParent(parent, false);
            rootGo.transform.localPosition = Vector3.zero;

            IReadOnlyList<HomePlotLayout.PlotRow> rows = HomePlotLayout.All;
            for (int i = 0; i < rows.Count; i++)
            {
                HomePlotLayout.PlotRow row = rows[i];
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = "Plot_" + row.InstanceId;
                cube.layer = layer;
                cube.transform.SetParent(rootGo.transform, false);
                Vector3 display = HomeDisplayLayout.ToDisplayWorld(row);
                cube.transform.localPosition = display;
                cube.transform.localScale = new Vector3(1.1f, 0.2f, 1.1f);

                var binder = cube.AddComponent<HomeTargetBinder>();
                binder.BindExplicit(row.InstanceId, HomeInstanceKinds.Plot);

                var visual = cube.AddComponent<HomePlotVisual>();
                visual.Bind(row.InstanceId);

                // 材质所有权在 HomePlotVisual（Resources 模板 clone + OnDestroy）；禁止此处 .material=
                var renderer = cube.GetComponent<MeshRenderer>();
                if (renderer != null)
                    visual.AttachRenderer(renderer);
            }

            return rootGo.transform;
        }

        /// <summary>
        /// 生成 13 个可视灰盒 Node；每块显式 Bind instanceId/kind=Node。
        /// 同类不同实例独立 collider，不共享倒计时状态对象。
        /// </summary>
        public static Transform SpawnNodes(Transform parent, int layer = 0)
        {
            HomeNodeLayout.ValidateOrThrow();
            var rootGo = new GameObject(NodesRootName);
            if (parent != null)
                rootGo.transform.SetParent(parent, false);
            rootGo.transform.localPosition = Vector3.zero;

            IReadOnlyList<HomeNodeLayout.NodeRow> rows = HomeNodeLayout.All;
            for (int i = 0; i < rows.Count; i++)
            {
                HomeNodeLayout.NodeRow row = rows[i];
                PrimitiveType prim = PrimitiveForType(row.TypeId);
                var go = GameObject.CreatePrimitive(prim);
                go.name = "Node_" + row.InstanceId + "_" + row.Label;
                go.layer = layer;
                go.transform.SetParent(rootGo.transform, false);
                // 稳定锚点仍在 HomeNodeLayout；显示坐标经 HomeDisplayLayout 分区，避免遮挡地块
                Vector3 display = HomeDisplayLayout.ToDisplayWorld(row);
                go.transform.localPosition = new Vector3(
                    display.x, display.y + HeightForType(row.TypeId) * 0.5f, display.z);
                go.transform.localScale = ScaleForType(row.TypeId);

                var binder = go.AddComponent<HomeTargetBinder>();
                binder.BindExplicit(row.InstanceId, HomeInstanceKinds.Node);

                var visual = go.AddComponent<HomeNodeVisual>();
                visual.Bind(row.InstanceId, row.TypeId);
                var renderer = go.GetComponent<MeshRenderer>();
                if (renderer != null)
                    visual.AttachRenderer(renderer);
            }

            return rootGo.transform;
        }

        static PrimitiveType PrimitiveForType(int typeId)
        {
            switch (typeId)
            {
                case 1: return PrimitiveType.Cylinder; // 树木
                case 2: return PrimitiveType.Sphere;   // 浆果
                case 3: return PrimitiveType.Cube;     // 矿石
                case 4: return PrimitiveType.Capsule;  // 花丛
                default: return PrimitiveType.Cylinder;
            }
        }

        static Vector3 ScaleForType(int typeId)
        {
            switch (typeId)
            {
                case 1: return new Vector3(0.55f, 0.9f, 0.55f);
                case 2: return new Vector3(0.7f, 0.7f, 0.7f);
                case 3: return new Vector3(0.85f, 0.85f, 0.85f);
                case 4: return new Vector3(0.45f, 0.7f, 0.45f);
                default: return Vector3.one;
            }
        }

        static float HeightForType(int typeId)
        {
            switch (typeId)
            {
                case 1: return 1.8f;
                case 2: return 0.7f;
                case 3: return 0.85f;
                case 4: return 1.4f;
                default: return 1f;
            }
        }

        /// <summary>
        /// 仅选择/创建 Home runtime 所属 scene 内的相机；不碰外来/叠加场景 Camera.main。
        /// </summary>
        public static Camera EnsureHomeCamera(Transform parent = null)
        {
            Scene scene = ResolveHomeScene(parent);
            Camera owned = FindCameraInHomeScene(scene, parent);
            if (owned != null)
            {
                ConfigureOwnedCamera(owned);
                return owned;
            }

            var go = new GameObject(CameraObjectName);
            if (parent != null)
            {
                go.transform.SetParent(parent, false);
            }
            else if (scene.IsValid())
            {
                SceneManager.MoveGameObjectToScene(go, scene);
            }

            var cam = go.AddComponent<Camera>();
            ConfigureOwnedCamera(cam);
            return cam;
        }

        static Scene ResolveHomeScene(Transform parent)
        {
            if (parent != null)
            {
                Scene owned = parent.gameObject.scene;
                if (owned.IsValid())
                    return owned;
            }

            return SceneManager.GetActiveScene();
        }

        static Camera FindCameraInHomeScene(Scene scene, Transform parent)
        {
            if (!scene.IsValid() || !scene.isLoaded)
                return null;

            Camera named = null;
            Camera underParent = null;
            Camera anyInScene = null;

            GameObject[] roots = scene.GetRootGameObjects();
            for (int r = 0; r < roots.Length; r++)
            {
                GameObject root = roots[r];
                if (root == null)
                    continue;

                Camera[] cams = root.GetComponentsInChildren<Camera>(true);
                for (int i = 0; i < cams.Length; i++)
                {
                    Camera cam = cams[i];
                    if (cam == null)
                        continue;
                    // 严格限定本 scene；忽略外来/DDOL/叠加场景相机
                    if (!cam.gameObject.scene.IsValid() || cam.gameObject.scene != scene)
                        continue;

                    if (cam.gameObject.name == CameraObjectName)
                    {
                        if (parent != null && cam.transform.IsChildOf(parent))
                            return cam;
                        if (named == null)
                            named = cam;
                        continue;
                    }

                    if (parent != null && cam.transform.IsChildOf(parent) && underParent == null)
                        underParent = cam;

                    if (anyInScene == null)
                        anyInScene = cam;
                }
            }

            return named ?? underParent ?? anyInScene;
        }

        static void ConfigureOwnedCamera(Camera cam)
        {
            if (cam == null)
                return;

            if (cam.gameObject.name != CameraObjectName && cam.transform.parent == null)
                cam.gameObject.name = CameraObjectName;

            PlaceCamera(cam);
            cam.enabled = true;
            try
            {
                cam.tag = "MainCamera";
            }
            catch
            {
                // Tag 缺失时仍保留位姿；射线使用返回的 Camera 引用
            }

            if (cam.GetComponent<AudioListener>() == null)
                cam.gameObject.AddComponent<AudioListener>();
        }

        static void PlaceCamera(Camera cam)
        {
            // 略偏左/拉远：右侧 Farm 面板打开时 24 地仍落在可点屏幕区（16:10 与 16:9 共用）
            cam.transform.position = new Vector3(-1.2f, 10f, -11f);
            cam.transform.rotation = Quaternion.Euler(48f, 8f, 0f);
            cam.orthographic = false;
            cam.fieldOfView = 48f;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 100f;
        }
    }
}
