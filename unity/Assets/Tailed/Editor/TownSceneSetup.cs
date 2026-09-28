using System.Linq;
using Tailed.Cameras;
using Tailed.Map;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Tailed.EditorTools
{
    /// <summary>
    /// Builds Assets/Tailed/Scenes/Town.unity from code so the scene is reproducible.
    /// Run: tools/bridge.sh execute Tailed.EditorTools.TownSceneSetup.Create
    /// </summary>
    public static class TownSceneSetup
    {
        const string ScenePath = "Assets/Tailed/Scenes/Town.unity";
        const string ToonPath = "Assets/Tailed/Materials/Toon.mat";
        const string DebugPath = "Assets/Tailed/Materials/LaneDebug.mat";
        const string SkyPath = "Assets/Tailed/Materials/Sky.mat";

        [MenuItem("Tailed/Rebuild Town Scene")]
        public static string Create()
        {
            PlayerSettings.runInBackground = true; // keep simulating when the window loses focus (multi-instance tests)
            EnsureLayer(6, "Ground"); EnsureLayer(7, "Buildings"); EnsureLayer(8, "Vehicles");
            EnsureLayer(9, "PlayerCar"); EnsureLayer(10, "Mirrors");
            EnsureAlwaysIncluded("Tailed/Toon", "Tailed/ToonVehicle", "Tailed/ToonGlass", "Tailed/Plate", "Tailed/Mirror",
                                 "Tailed/SignText", "Tailed/UnlitVertexColor", "Tailed/GradientSky", "Tailed/Additive", "Tailed/FontBlit", "Tailed/NightGlow", "Tailed/PlateUI");
            var toon = EnsureMaterial(ToonPath, "Tailed/Toon");
            var debug = EnsureMaterial(DebugPath, "Tailed/UnlitVertexColor");
            var sky = EnsureMaterial(SkyPath, "Tailed/GradientSky");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Sun: warm, fairly high, soft shadows.
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.95f, 0.86f);
            sun.intensity = 1.35f;
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(48f, -35f, 0f);
            RenderSettings.sun = sun;

            // Bright, soft ambient + sky-coloured haze for depth.
            RenderSettings.skybox = sky;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.66f, 0.76f, 0.92f);
            RenderSettings.ambientEquatorColor = new Color(0.62f, 0.66f, 0.66f);
            RenderSettings.ambientGroundColor = new Color(0.38f, 0.36f, 0.33f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.00022f;
            RenderSettings.fogColor = new Color(0.78f, 0.87f, 0.96f); // matches the sky horizon

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.2f;
            cam.farClipPlane = 3500f;
            var camData = camGo.AddComponent<UniversalAdditionalCameraData>();
            camData.renderPostProcessing = true;
            camData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            camData.antialiasingQuality = AntialiasingQuality.High;
            camGo.AddComponent<FlyCamera>();
            camGo.AddComponent<DevViews>();
            camGo.AddComponent<Tailed.Cockpit.OpticsCamera>();
            camGo.AddComponent<Tailed.Cockpit.CameraDirector>();
            camGo.transform.SetPositionAndRotation(new Vector3(-240f, 880f, -240f), Quaternion.Euler(45f, 45f, 0f));

            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>("Assets/Settings/SampleSceneProfile.asset");
            if (profile != null)
            {
                var volume = new GameObject("Global Volume").AddComponent<Volume>();
                volume.isGlobal = true;
                volume.sharedProfile = profile;
            }

            var town = new GameObject("Town").AddComponent<TownBuilder>();
            town.ToonMaterial = toon;
            town.DebugMaterial = debug;
            new GameObject("Traffic").AddComponent<Tailed.Traffic.TrafficRunner>();
            var game = new GameObject("Game");
            game.AddComponent<Tailed.Game.GameBootstrap>();
            game.AddComponent<Tailed.UI.GameUI>();

            // Overview shots need shadows much further out than the template's 50 m.
            // Smooth edges: 4× MSAA everywhere (plus SMAA on the main camera below).
            foreach (var asset in AllUrpAssets()) { asset.shadowDistance = 400f; asset.msaaSampleCount = 4; EditorUtility.SetDirty(asset); }

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            return $"saved {ScenePath}";
        }

        static void EnsureLayer(int index, string name)
        {
            var tm = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var el = tm.FindProperty("layers").GetArrayElementAtIndex(index);
            if (el.stringValue == name) return;
            el.stringValue = name;
            tm.ApplyModifiedProperties();
        }

        static void EnsureAlwaysIncluded(params string[] shaders)
        {
            var gs = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0]);
            var arr = gs.FindProperty("m_AlwaysIncludedShaders");
            foreach (var name in shaders)
            {
                var sh = Shader.Find(name) ?? throw new System.InvalidOperationException($"shader {name} not found");
                bool present = false;
                for (int i = 0; i < arr.arraySize; i++) if (arr.GetArrayElementAtIndex(i).objectReferenceValue == sh) present = true;
                if (present) continue;
                arr.InsertArrayElementAtIndex(arr.arraySize);
                arr.GetArrayElementAtIndex(arr.arraySize - 1).objectReferenceValue = sh;
            }
            gs.ApplyModifiedProperties();
        }

        static Material EnsureMaterial(string path, string shaderName)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat != null) return mat;
            var shader = Shader.Find(shaderName) ?? throw new System.InvalidOperationException($"shader {shaderName} not found");
            mat = new Material(shader) { enableInstancing = true };
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        static UniversalRenderPipelineAsset[] AllUrpAssets() =>
            AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset")
                .Select(g => AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(a => a != null).ToArray();
    }
}
