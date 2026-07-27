#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace ArenaFps.Editor
{
    /// <summary>
    /// Repeatable cinematic lighting/post pass for Arena.
    /// Run via menu or: Unity -batchmode -executeMethod ArenaFps.Editor.AaaLightingPass.Run
    /// </summary>
    public static class AaaLightingPass
    {
        const string ScenePath = "Assets/_Project/Scenes/Arena.unity";
        const string LightingDir = "Assets/_Project/Settings/Lighting";
        const string VolumeProfilePath = LightingDir + "/Arena_AAA_GlobalVolume.asset";
        const string SkyboxMaterialPath = LightingDir + "/Arena_AbandonedConstruction_Skybox.mat";
        const string PreferredHdriPath = "Assets/_Project/Resources/HDRI/abandoned_construction_4k.hdr";
        const string FallbackHdriPath = "Assets/_Project/Art/Textures/HDRI/abandoned_construction_4k.hdr";
        const string LightingRigName = "AAA_Lighting_Rig";

        [MenuItem("Arena FPS/AAA Lighting Pass")]
        public static void Run()
        {
            OpenArenaSafely();
            EnsureFolder(LightingDir);

            ConfigureRenderPipeline();
            ConfigureSkyFogAndAmbient();
            ConfigureDirectionalLight();
            ConfigureGlobalVolume();
            ConfigureAccentLights();
            ConfigureCameras();

            var scene = SceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            DynamicGI.UpdateEnvironment();

            Debug.Log("[ArenaFps] AAA lighting pass applied: URP Forward+, cinematic volume, HDRI sky, fog/ambient, warm key, and practical accent lights.");
            if (Application.isBatchMode)
                EditorApplication.Exit(0);
        }

        static void OpenArenaSafely()
        {
            var active = SceneManager.GetActiveScene();
            if (active.path == ScenePath)
                return;

            if (active.IsValid() && active.isDirty)
                throw new InvalidOperationException($"Refusing to unload dirty scene '{active.name}'. Save it before running AAA Lighting Pass.");

            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        static void ConfigureRenderPipeline()
        {
            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (pipeline == null)
                pipeline = QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
            if (pipeline == null)
                throw new InvalidOperationException("AAA Lighting Pass requires the URP pipeline asset to be assigned.");

            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
            QualitySettings.shadowResolution = (global::UnityEngine.ShadowResolution)3;
            QualitySettings.shadowDistance = 90f;
            QualitySettings.shadowCascades = 4;
            QualitySettings.pixelLightCount = 8;
            QualitySettings.softParticles = true;

            var so = new SerializedObject(pipeline);
            SetBool(so, "m_SupportsHDR", true);
            SetFloat(so, "m_RenderScale", 0.9f);
            SetBool(so, "m_RequireDepthTexture", true);
            SetBool(so, "m_RequireOpaqueTexture", true);
            SetInt(so, "m_MainLightShadowmapResolution", 4096);
            SetInt(so, "m_AdditionalLightsShadowmapResolution", 2048);
            SetInt(so, "m_AdditionalLightsShadowResolutionTierLow", 512);
            SetInt(so, "m_AdditionalLightsShadowResolutionTierMedium", 1024);
            SetInt(so, "m_AdditionalLightsShadowResolutionTierHigh", 2048);
            SetBool(so, "m_MainLightShadowsSupported", true);
            SetBool(so, "m_AdditionalLightShadowsSupported", true);
            SetInt(so, "m_AdditionalLightsRenderingMode", 1); // Per-pixel additional lights.
            SetInt(so, "m_AdditionalLightsPerObjectLimit", 8);
            SetBool(so, "m_SoftShadowsSupported", true);
            SetInt(so, "m_SoftShadowQuality", 2); // High.
            SetFloat(so, "m_ShadowDistance", 90f);
            SetInt(so, "m_ShadowCascadeCount", 4);
            SetFloat(so, "m_ShadowDepthBias", 0.7f);
            SetFloat(so, "m_ShadowNormalBias", 0.45f);
            SetBool(so, "m_UseSRPBatcher", true);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(pipeline);

            var rendererData = GetRendererData(pipeline);
            if (rendererData != null)
            {
                var rendererSo = new SerializedObject(rendererData);
                SetInt(rendererSo, "m_RenderingMode", 2); // Forward+ in URP 17+.
                SetInt(rendererSo, "m_DepthPrimingMode", 1); // Auto where supported.
                SetInt(rendererSo, "m_IntermediateTextureMode", 1); // Auto.
                rendererSo.ApplyModifiedPropertiesWithoutUndo();
                EnsureSsaoRendererFeature(rendererData);
                EditorUtility.SetDirty(rendererData);
            }

            EditorUtility.SetDirty(GraphicsSettings.GetGraphicsSettings());
        }

        static void ConfigureSkyFogAndAmbient()
        {
            var skybox = EnsureSkyboxMaterial();
            RenderSettings.skybox = skybox;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = new Color(0.39f, 0.44f, 0.50f, 1f);
            RenderSettings.fogDensity = 0.0105f;
            RenderSettings.fogStartDistance = 8f;
            RenderSettings.fogEndDistance = 115f;

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.51f, 0.56f, 0.64f, 1f);
            RenderSettings.ambientEquatorColor = new Color(0.28f, 0.31f, 0.36f, 1f);
            RenderSettings.ambientGroundColor = new Color(0.12f, 0.11f, 0.10f, 1f);
            RenderSettings.ambientIntensity = 0.70f;
            RenderSettings.reflectionIntensity = 0.50f;
            RenderSettings.reflectionBounces = 1;
            RenderSettings.defaultReflectionMode = UnityEngine.Rendering.DefaultReflectionMode.Skybox;
        }

        static Material EnsureSkyboxMaterial()
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(SkyboxMaterialPath);
            if (material == null)
            {
                var shader = Shader.Find("Skybox/Panoramic");
                if (shader == null)
                    throw new InvalidOperationException("Could not find Skybox/Panoramic shader.");
                material = new Material(shader) { name = "Arena_AbandonedConstruction_Skybox" };
                AssetDatabase.CreateAsset(material, SkyboxMaterialPath);
            }

            var hdri = AssetDatabase.LoadAssetAtPath<Texture>(PreferredHdriPath);
            if (hdri == null)
                hdri = AssetDatabase.LoadAssetAtPath<Texture>(FallbackHdriPath);
            if (hdri != null && material.HasProperty("_MainTex"))
                material.SetTexture("_MainTex", hdri);

            SetMaterialFloat(material, "_Exposure", 0.60f);
            SetMaterialFloat(material, "_Rotation", 215f);
            SetMaterialFloat(material, "_ImageType", 0f); // 360-degree equirectangular.
            SetMaterialFloat(material, "_Mapping", 1f); // Latitude-longitude where available.
            EditorUtility.SetDirty(material);
            return material;
        }

        static void ConfigureDirectionalLight()
        {
            var key = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include)
                .FirstOrDefault(l => l.type == LightType.Directional);
            if (key == null)
            {
                var go = new GameObject("Directional Light");
                key = go.AddComponent<Light>();
                key.type = LightType.Directional;
            }

            key.name = "Directional Light";
            key.enabled = true;
            key.type = LightType.Directional;
            key.renderMode = LightRenderMode.ForcePixel;
            key.color = new Color(1.0f, 0.82f, 0.62f, 1f);
            key.intensity = 1.95f;
            key.bounceIntensity = 0.75f;
            key.shadows = LightShadows.Soft;
            key.shadowStrength = 0.92f;
            key.shadowBias = 0.025f;
            key.shadowNormalBias = 0.18f;
            key.shadowNearPlane = 0.1f;
            key.transform.rotation = Quaternion.Euler(39f, 318f, 0f);
            EditorUtility.SetDirty(key);
            EditorUtility.SetDirty(key.gameObject);
        }

        static void ConfigureGlobalVolume()
        {
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(VolumeProfilePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                profile.name = "Arena_AAA_GlobalVolume";
                AssetDatabase.CreateAsset(profile, VolumeProfilePath);
            }

            ConfigureVolumeProfile(profile);

            var volume = UnityEngine.Object.FindObjectsByType<Volume>(FindObjectsInactive.Include)
                .FirstOrDefault(v => v.isGlobal) ?? new GameObject("Global Volume").AddComponent<Volume>();
            volume.gameObject.name = "Global Volume";
            volume.isGlobal = true;
            volume.priority = 100f;
            volume.weight = 1f;
            volume.sharedProfile = profile;

            EditorUtility.SetDirty(profile);
            EditorUtility.SetDirty(volume);
            EditorUtility.SetDirty(volume.gameObject);
        }

        static void ConfigureVolumeProfile(VolumeProfile profile)
        {
            profile.components.RemoveAll(component => component == null);

            var bloom = GetOrAdd<Bloom>(profile);
            SetParam(bloom.threshold, 1.38f);
            SetParam(bloom.intensity, 0.18f);
            SetParam(bloom.scatter, 0.44f);
            SetParam(bloom.tint, new Color(1f, 0.88f, 0.74f, 1f));
            SetParam(bloom.highQualityFiltering, true);
            SetParam(bloom.dirtIntensity, 0.08f);

            var color = GetOrAdd<ColorAdjustments>(profile);
            SetParam(color.postExposure, 0.03f);
            SetParam(color.contrast, 18f);
            SetParam(color.colorFilter, new Color(0.93f, 0.96f, 1f, 1f));
            SetParam(color.hueShift, -2f);
            SetParam(color.saturation, -4f);

            var tone = GetOrAdd<Tonemapping>(profile);
            SetParam(tone.mode, TonemappingMode.ACES);

            var vignette = GetOrAdd<Vignette>(profile);
            SetParam(vignette.color, new Color(0.025f, 0.028f, 0.035f, 1f));
            SetParam(vignette.center, new Vector2(0.5f, 0.5f));
            SetParam(vignette.intensity, 0.15f);
            SetParam(vignette.smoothness, 0.68f);
            SetParam(vignette.rounded, false);

            var dof = GetOrAdd<DepthOfField>(profile);
            SetParam(dof.mode, DepthOfFieldMode.Gaussian);
            SetParam(dof.gaussianStart, 48f);
            SetParam(dof.gaussianEnd, 145f);
            SetParam(dof.gaussianMaxRadius, 0.16f);
            SetParam(dof.highQualitySampling, true);

            var grain = GetOrAdd<FilmGrain>(profile);
            SetParam(grain.type, FilmGrainLookup.Medium1);
            SetParam(grain.intensity, 0.11f);
            SetParam(grain.response, 0.72f);

            var liftGammaGain = GetOrAdd<LiftGammaGain>(profile);
            SetParam(liftGammaGain.lift, new Vector4(-0.018f, -0.014f, 0.006f, 0f));
            SetParam(liftGammaGain.gamma, new Vector4(0.012f, 0.008f, -0.01f, 0f));
            SetParam(liftGammaGain.gain, new Vector4(0.025f, 0.014f, -0.005f, 0f));

            foreach (var component in profile.components)
                EditorUtility.SetDirty(component);
        }

        static void ConfigureAccentLights()
        {
            var existing = GameObject.Find(LightingRigName);
            if (existing != null)
                UnityEngine.Object.DestroyImmediate(existing);

            var rig = new GameObject(LightingRigName);
            CreatePoint(rig.transform, "AAA_Practical_Mid_Courtyard", new Vector3(0f, 4.1f, 1f), 8.5f, 22f, new Color(1f, 0.66f, 0.34f), true);
            CreatePoint(rig.transform, "AAA_Practical_Mid_West", new Vector3(-14f, 3.2f, -2f), 7.5f, 16f, new Color(1f, 0.58f, 0.29f), false);
            CreatePoint(rig.transform, "AAA_Practical_Mid_East", new Vector3(14f, 3.2f, 3f), 7.5f, 16f, new Color(1f, 0.58f, 0.29f), false);
            CreatePoint(rig.transform, "AAA_Practical_Blue_Spawn_Left", new Vector3(-9f, 3.4f, -27f), 6.5f, 15f, new Color(1f, 0.68f, 0.36f), false);
            CreatePoint(rig.transform, "AAA_Practical_Blue_Spawn_Right", new Vector3(9f, 3.4f, -27f), 6.5f, 15f, new Color(1f, 0.68f, 0.36f), false);
            CreatePoint(rig.transform, "AAA_Practical_Red_Spawn_Left", new Vector3(-9f, 3.4f, 27f), 6.5f, 15f, new Color(1f, 0.62f, 0.33f), false);
            CreatePoint(rig.transform, "AAA_Practical_Red_Spawn_Right", new Vector3(9f, 3.4f, 27f), 6.5f, 15f, new Color(1f, 0.62f, 0.33f), false);
            CreatePoint(rig.transform, "AAA_Cool_Overcast_Fill", new Vector3(0f, 7.5f, -6f), 3.2f, 42f, new Color(0.55f, 0.66f, 0.86f), false);

            EditorUtility.SetDirty(rig);
        }

        static void CreatePoint(Transform parent, string name, Vector3 position, float intensity, float range, Color color, bool castsShadows)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = position;
            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.renderMode = LightRenderMode.ForcePixel;
            light.color = color;
            light.intensity = intensity;
            light.range = range;
            light.bounceIntensity = 0.45f;
            light.shadows = castsShadows ? LightShadows.Soft : LightShadows.None;
            light.shadowStrength = castsShadows ? 0.45f : 0f;
            light.shadowBias = 0.04f;
            light.shadowNormalBias = 0.35f;
        }

        static void ConfigureCameras()
        {
            foreach (var camera in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include))
            {
                camera.allowHDR = true;
                camera.allowMSAA = false;
                camera.nearClipPlane = Mathf.Min(camera.nearClipPlane, 0.05f);
                camera.farClipPlane = Mathf.Max(camera.farClipPlane, 250f);

                var data = camera.GetUniversalAdditionalCameraData();
                data.renderPostProcessing = true;
                data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                data.antialiasingQuality = AntialiasingQuality.High;
                data.stopNaN = true;
                data.dithering = true;
                EditorUtility.SetDirty(camera);
                EditorUtility.SetDirty(data);
            }
        }

        static T GetOrAdd<T>(VolumeProfile profile) where T : VolumeComponent
        {
            var component = profile.components.OfType<T>().FirstOrDefault();
            if (component == null)
            {
                component = ScriptableObject.CreateInstance<T>();
                component.name = typeof(T).Name;
                component.hideFlags = HideFlags.HideInInspector | HideFlags.HideInHierarchy;
                profile.components.Add(component);
            }

            if (AssetDatabase.Contains(profile) && !AssetDatabase.Contains(component))
                AssetDatabase.AddObjectToAsset(component, profile);

            component.active = true;
            return component;
        }

        static void SetParam<T>(VolumeParameter<T> parameter, T value)
        {
            parameter.overrideState = true;
            parameter.value = value;
        }

        static UniversalRendererData GetRendererData(UniversalRenderPipelineAsset pipeline)
        {
            var so = new SerializedObject(pipeline);
            var list = so.FindProperty("m_RendererDataList");
            if (list != null && list.isArray && list.arraySize > 0)
            {
                var renderer = list.GetArrayElementAtIndex(0).objectReferenceValue as UniversalRendererData;
                if (renderer != null)
                    return renderer;
            }

            var rendererPath = "Assets/_Project/Settings/URP/URP_PC_Renderer.asset";
            return AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
        }

        static void EnsureSsaoRendererFeature(UniversalRendererData rendererData)
        {
            var ssaoType = Type.GetType("UnityEngine.Rendering.Universal.ScreenSpaceAmbientOcclusion, Unity.RenderPipelines.Universal.Runtime");
            if (ssaoType == null)
            {
                Debug.LogWarning("[ArenaFps] SSAO renderer feature type not found; using volume grade/fog for AO feel.");
                return;
            }

            ScriptableObject feature = null;
            var rendererSo = new SerializedObject(rendererData);
            var features = rendererSo.FindProperty("m_RendererFeatures");
            if (features == null || !features.isArray)
                return;

            for (int i = 0; i < features.arraySize; i++)
            {
                var current = features.GetArrayElementAtIndex(i).objectReferenceValue as ScriptableObject;
                if (current != null && current.GetType() == ssaoType)
                {
                    feature = current;
                    break;
                }
            }

            if (feature == null)
            {
                feature = ScriptableObject.CreateInstance(ssaoType);
                feature.name = "AAA_ScreenSpaceAmbientOcclusion";
                AssetDatabase.AddObjectToAsset(feature, rendererData);
                features.arraySize++;
                features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = feature;

                var featureMap = rendererSo.FindProperty("m_RendererFeatureMap");
                if (featureMap != null && featureMap.isArray)
                {
                    featureMap.arraySize++;
                    long localId = unchecked((long)Unsupported.GetLocalIdentifierInFileForPersistentObject(feature));
                    featureMap.GetArrayElementAtIndex(featureMap.arraySize - 1).longValue = localId;
                }
            }

            rendererSo.ApplyModifiedPropertiesWithoutUndo();
            ConfigureSsaoFeature(feature);
            EditorUtility.SetDirty(feature);
            EditorUtility.SetDirty(rendererData);
        }

        static void ConfigureSsaoFeature(ScriptableObject feature)
        {
            var so = new SerializedObject(feature);
            SetByName(so, "m_Active", true);
            SetFloatByContains(so, "intensity", 0.58f);
            SetFloatByContains(so, "radius", 0.42f);
            SetBoolByContains(so, "downsample", false);
            SetBoolByContains(so, "afteropaque", true);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void SetByName(SerializedObject so, string property, bool value)
        {
            var p = so.FindProperty(property);
            if (p != null && p.propertyType == SerializedPropertyType.Boolean)
                p.boolValue = value;
        }

        static void SetFloatByContains(SerializedObject so, string contains, float value)
        {
            var it = so.GetIterator();
            bool enterChildren = true;
            while (it.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (it.propertyType == SerializedPropertyType.Float && it.propertyPath.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0)
                    it.floatValue = value;
            }
        }

        static void SetBoolByContains(SerializedObject so, string contains, bool value)
        {
            var it = so.GetIterator();
            bool enterChildren = true;
            while (it.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (it.propertyType == SerializedPropertyType.Boolean && it.propertyPath.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0)
                    it.boolValue = value;
            }
        }

        static void EnsureFolder(string assetFolder)
        {
            if (AssetDatabase.IsValidFolder(assetFolder))
                return;

            var fullPath = assetFolder.Replace("Assets", Application.dataPath);
            Directory.CreateDirectory(fullPath);
            AssetDatabase.Refresh();
        }

        static void SetMaterialFloat(Material material, string property, float value)
        {
            if (material.HasProperty(property))
                material.SetFloat(property, value);
        }

        static void SetFloat(SerializedObject so, string name, float value)
        {
            var p = so.FindProperty(name);
            if (p != null)
                p.floatValue = value;
        }

        static void SetBool(SerializedObject so, string name, bool value)
        {
            var p = so.FindProperty(name);
            if (p != null)
                p.boolValue = value;
        }

        static void SetInt(SerializedObject so, string name, int value)
        {
            var p = so.FindProperty(name);
            if (p != null)
                p.intValue = value;
        }
    }
}
#endif
