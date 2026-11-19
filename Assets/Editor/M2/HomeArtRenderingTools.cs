using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Game.Editor.M2
{
    /// <summary>Reproducible lighting and shadow receiver for the v03 home presentation.</summary>
    public static class HomeArtRenderingTools
    {
        const string GroundName = "CloudIslePresentationGround";
        const string MaterialPath = "Assets/Models/CloudIsle_v03/Materials/HomeGround.mat";

        [MenuItem("Farming/Art/Apply Home Lighting And Capture")]
        public static void ApplyAndCapture()
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Use Edit Mode.");
            var scene = EditorSceneManager.OpenScene("Assets/Scenes/M1/M1_Home.unity", OpenSceneMode.Single);
            Apply(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            ArtReplacementTools.VerifyAndCapture();
        }

        public static void Apply(Scene scene)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) throw new InvalidOperationException("URP Lit is required.");
            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, MaterialPath);
            }
            material.SetColor("_BaseColor", new Color(.48f, .66f, .36f));
            material.SetFloat("_Smoothness", 0);
            material.SetFloat("_Metallic", 0);
            EditorUtility.SetDirty(material);

            var ground = scene.GetRootGameObjects().FirstOrDefault(g => g.name == GroundName);
            if (ground == null)
            {
                ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
                ground.name = GroundName;
                SceneManager.MoveGameObjectToScene(ground, scene);
                Undo.RegisterCreatedObjectUndo(ground, "Create home shadow receiver");
            }
            Undo.RecordObject(ground.transform, "Position home shadow receiver");
            ground.transform.position = new Vector3(0, -.025f, 0);
            ground.transform.localScale = new Vector3(12, 1, 12);
            // Presentation only: it must never intercept farm input rays.
            foreach (var collider in ground.GetComponents<Collider>()) Undo.DestroyObjectImmediate(collider);
            var renderer = ground.GetComponent<MeshRenderer>();
            Undo.RecordObject(renderer, "Assign home ground material");
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = true;

            var sun = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Light>())
                .FirstOrDefault(l => l.type == LightType.Directional);
            if (sun == null) throw new InvalidOperationException("Home directional light is missing.");
            Undo.RecordObject(sun, "Style home sunlight");
            Undo.RecordObject(sun.transform, "Style home sunlight");
            sun.transform.rotation = Quaternion.Euler(48, -35, 0);
            sun.color = new Color(1f, .94f, .82f);
            sun.intensity = 1.15f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = .65f;
            sun.shadowBias = .025f;
            sun.shadowNormalBias = .15f;
            RenderSettings.sun = sun;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(.66f, .77f, .88f);
            RenderSettings.ambientEquatorColor = new Color(.55f, .62f, .47f);
            RenderSettings.ambientGroundColor = new Color(.38f, .34f, .25f);
            RenderSettings.reflectionIntensity = .25f;
            EditorSceneManager.MarkSceneDirty(scene);
        }
    }
}
