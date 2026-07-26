#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

/// <summary>
/// One-shot bootstrap: URP assets, Forward+, Arena scene, M1 Pro quality defaults.
/// Run via menu or: Unity -batchmode -executeMethod ArenaFps.Editor.BootstrapUrpProject.Run
/// </summary>
namespace ArenaFps.Editor
{
    public static class BootstrapUrpProject
    {
        const string SettingsDir = "Assets/_Project/Settings/URP";
        const string ScenePath = "Assets/_Project/Scenes/Arena.unity";
        const string PipelineAssetPath = SettingsDir + "/URP_PC_Asset.asset";
        const string RendererPath = SettingsDir + "/URP_PC_Renderer.asset";
        const string GlobalSettingsPath = SettingsDir + "/URP_GlobalSettings.asset";

        [MenuItem("Arena FPS/Bootstrap URP Project")]
        public static void Run()
        {
            Directory.CreateDirectory(SettingsDir.Replace("Assets", Application.dataPath));

            var renderer = CreateRenderer();
            var pipeline = CreatePipeline(renderer);
            AssignPipeline(pipeline);
            ConfigurePlayerSettings();
            CreateArenaScene();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("[ArenaFps] Bootstrap complete: URP Forward+, Arena scene, M1 Pro defaults.");
            if (Application.isBatchMode)
                EditorApplication.Exit(0);
        }

        static UniversalRendererData CreateRenderer()
        {
            var existing = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (existing != null)
                return existing;

            var renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
            AssetDatabase.CreateAsset(renderer, RendererPath);

            // Forward+ is the Metal/tile-friendly path on Apple Silicon.
            var so = new SerializedObject(renderer);
            var renderingMode = so.FindProperty("m_RenderingMode");
            if (renderingMode != null)
            {
                // 0 = Forward, 2 = ForwardPlus (URP 17+)
                renderingMode.intValue = 2;
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            EditorUtility.SetDirty(renderer);
            return renderer;
        }

        static UniversalRenderPipelineAsset CreatePipeline(UniversalRendererData renderer)
        {
            var existing = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelineAssetPath);
            if (existing != null)
                return existing;

            var pipeline = UniversalRenderPipelineAsset.Create(renderer);
            AssetDatabase.CreateAsset(pipeline, PipelineAssetPath);

            // URP 17 exposes several knobs as read-only properties — set via SerializedObject.
            var so = new SerializedObject(pipeline);
            SetFloat(so, "m_RenderScale", 0.67f);
            SetBool(so, "m_SupportsHDR", true);
            SetInt(so, "m_MSAA", 1); // TAA/STP path, not MSAA
            SetFloat(so, "m_ShadowDistance", 60f);
            SetInt(so, "m_MainLightShadowmapResolution", 2048);
            SetInt(so, "m_AdditionalLightsRenderingMode", 1); // PerPixel
            SetBool(so, "m_MainLightShadowsSupported", true);
            SetBool(so, "m_AdditionalLightShadowsSupported", false); // one shadow caster
            SetBool(so, "m_UseSRPBatcher", true);
            // GPU Resident Drawer: 1 = InstancedDrawing
            SetInt(so, "m_GPUResidentDrawerMode", 1);
            so.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(pipeline);
            return pipeline;
        }

        static void AssignPipeline(UniversalRenderPipelineAsset pipeline)
        {
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;

            // Ensure Graphics Settings asset persists.
            EditorUtility.SetDirty(GraphicsSettings.GetGraphicsSettings());
        }

        static void ConfigurePlayerSettings()
        {
            PlayerSettings.companyName = "ArenaFps";
            PlayerSettings.productName = "Urban Arena";
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.gpuSkinning = true;

            QualitySettings.vSyncCount = 0;
            QualitySettings.maxQueuedFrames = 1;
        }

        static void CreateArenaScene()
        {
            if (File.Exists(ScenePath))
            {
                Debug.Log("[ArenaFps] Arena scene already exists — skipping create.");
                return;
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Lighting
            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.96f, 0.90f);
            light.intensity = 1.15f;
            light.shadows = LightShadows.Soft;
            lightGo.transform.rotation = Quaternion.Euler(42f, -35f, 0f);

            // Ground plane for greybox
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.position = Vector3.zero;
            ground.transform.localScale = new Vector3(6f, 1f, 6f); // 60x60m

            // Simple cover blocks
            CreateBox("Cover_A", new Vector3(-6f, 1f, 4f), new Vector3(3f, 2f, 1f));
            CreateBox("Cover_B", new Vector3(5f, 1.25f, -3f), new Vector3(1.5f, 2.5f, 4f));
            CreateBox("Cover_C", new Vector3(0f, 1f, 10f), new Vector3(8f, 2f, 1f));
            CreateBox("Wall_West", new Vector3(-20f, 2.5f, 0f), new Vector3(1f, 5f, 40f));
            CreateBox("Wall_East", new Vector3(20f, 2.5f, 0f), new Vector3(1f, 5f, 40f));
            CreateBox("Wall_North", new Vector3(0f, 2.5f, 20f), new Vector3(40f, 5f, 1f));
            CreateBox("Wall_South", new Vector3(0f, 2.5f, -20f), new Vector3(40f, 5f, 1f));

            // Player spawn marker
            var spawn = new GameObject("PlayerSpawn");
            spawn.transform.position = new Vector3(0f, 1.7f, -8f);
            spawn.transform.rotation = Quaternion.identity;

            // Camera placeholder (replaced by player prefab later)
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 250f;
            cam.fieldOfView = 75f;
            camGo.AddComponent<AudioListener>();
            camGo.transform.position = spawn.transform.position;
            camGo.transform.rotation = Quaternion.identity;

            // Volume for post later
            var volumeGo = new GameObject("Global Volume");
            var volume = volumeGo.AddComponent<Volume>();
            volume.isGlobal = true;

            EditorSceneManager.SaveScene(scene, ScenePath);

            // Set as first build scene
            var scenes = new EditorBuildSettingsScene[]
            {
                new EditorBuildSettingsScene(ScenePath, true)
            };
            EditorBuildSettings.scenes = scenes;
        }

        static void CreateBox(string name, Vector3 position, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.position = position;
            go.transform.localScale = scale;
        }

        static void SetFloat(SerializedObject so, string name, float value)
        {
            var p = so.FindProperty(name);
            if (p != null) p.floatValue = value;
        }

        static void SetBool(SerializedObject so, string name, bool value)
        {
            var p = so.FindProperty(name);
            if (p != null) p.boolValue = value;
        }

        static void SetInt(SerializedObject so, string name, int value)
        {
            var p = so.FindProperty(name);
            if (p != null) p.intValue = value;
        }
    }
}
#endif
