#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ArenaFps.Editor
{
    /// <summary>
    /// Critique CRITIQUE_01 photograph fixes: #2 lighting/fog, #10 sky, #1 ground tiling.
    /// Idempotent under PH_Photograph. Overrides OP sky/fog/grade settings; does not touch player/prefabs.
    /// Menu: Arena FPS / AAA Photograph Pass
    /// </summary>
    public static class AaaPhotographPass
    {
        const string ScenePath = "Assets/_Project/Scenes/Arena.unity";
        const string RootName = "PH_Photograph";
        const string MatDir = "Assets/_Project/Art/Materials/Photograph";
        const string OdMatDir = "Assets/_Project/Art/Materials/OverflowDressing";
        const string SkyboxPath = "Assets/_Project/Settings/Lighting/Arena_Overflow_Overcast_Skybox.mat";
        const string VolumeProfilePath = "Assets/_Project/Settings/Lighting/Arena_AAA_GlobalVolume.asset";
        const string CritiqueOut = "_research/critique/ours_v2";
        const string HdriDir = "Assets/_Project/Art/Textures/HDRI";

        // Fog was 0.0042; cut ~70% → 0.00125 (dusty daylight, mid-distance contrast restored).
        const float FogDensity = 0.00125f;
            // Soft shadows were 0.30 — unreadable. Target strong ground contact.
        const float ShadowStrength = 0.90f;
        const float SunIntensity = 2.20f;
        // Readable key: ~48° elevation, yaw −38° (≈322°) — long NW-SE shadows on street.
        static readonly Vector3 SunEuler = new(48f, -38f, 0f);

        static readonly System.Random Rng = new(20260727);
        static Transform _root;
        static Transform _map;
        static int _decals;
        static int _kerbs;
        static int _skirts;
        static int _patchesRemoved;

        [MenuItem("Arena FPS/AAA Photograph Pass")]
        public static void Run()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.isPlaying = false;
                Debug.LogWarning("[PH] Exit play mode and re-run.");
                return;
            }

            OpenArena();
            _map = GameObject.Find("ThreeLaneMap")?.transform;
            if (_map == null)
            {
                Debug.LogError("[PH] ThreeLaneMap missing.");
                return;
            }

            _decals = _kerbs = _skirts = _patchesRemoved = 0;
            EnsureDir(MatDir);
            EnsureDir(CritiqueOut);

            ClearPrevious();
            RemoveBadGroundPatches();

            _root = new GameObject(RootName).transform;
            _root.SetParent(_map, false);

            try
            {
                Stage0_LightingFogGrade();
                Stage1_SkyAbPick();
                Stage2_GroundBreakup();
                Stage3_KerbsAndDirtSkirts();
                TuneSsao();

                SetStatic(_root.gameObject);
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
                EditorSceneManager.SaveOpenScenes();
                AssetDatabase.SaveAssets();
                DynamicGI.UpdateEnvironment();

                var metrics = CaptureCritiqueViews();
                AuditInvisibleColliders();

                Debug.Log(
                    $"[PH] DONE fog={FogDensity} sunI={SunIntensity} shadowStr={ShadowStrength} " +
                    $"decals={_decals} kerbs={_kerbs} skirts={_skirts} patchesRemoved={_patchesRemoved} " +
                    $"sunlit/shadow lumRatio={metrics.shadowRatio:F2} skyMax={metrics.skyMax:F3} pureWhite%={metrics.pureWhitePct:F2}");
            }
            catch (Exception ex)
            {
                Debug.LogError("[PH] FATAL: " + ex);
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
                EditorSceneManager.SaveOpenScenes();
                throw;
            }
        }

        [MenuItem("Arena FPS/AAA Photograph Pass/Capture Critique Views Only")]
        public static void RunCaptureOnly()
        {
            OpenArena();
            EnsureDir(CritiqueOut);
            var m = CaptureCritiqueViews();
            AuditInvisibleColliders();
            Debug.Log($"[PH] capture-only ratio={m.shadowRatio:F2} skyMax={m.skyMax:F3} white%={m.pureWhitePct:F2}");
        }

        [MenuItem("Arena FPS/AAA Photograph Pass/Lighting Sky Retune + Capture")]
        public static void RunLightingSkyRetune()
        {
            OpenArena();
            _map = GameObject.Find("ThreeLaneMap")?.transform;
            EnsureDir(CritiqueOut);
            Stage0_LightingFogGrade();
            Stage1_SkyAbPick();
            TuneSsao();
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
            AssetDatabase.SaveAssets();
            DynamicGI.UpdateEnvironment();
            var m = CaptureCritiqueViews();
            AuditInvisibleColliders();
            Debug.Log($"[PH] retune DONE ratio={m.shadowRatio:F2} skyMax={m.skyMax:F3} white%={m.pureWhitePct:F2}");
        }

        static void OpenArena()
        {
            var active = SceneManager.GetActiveScene();
            if (active.path != ScenePath)
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        static void EnsureDir(string d)
        {
            var full = Path.IsPathRooted(d) ? d : Path.GetFullPath(d);
            if (!Directory.Exists(full)) Directory.CreateDirectory(full);
            if (d.StartsWith("Assets/")) AssetDatabase.Refresh();
        }

        static void ClearPrevious()
        {
            var doomed = new List<GameObject>();
            if (_map != null)
            {
                foreach (Transform t in _map)
                {
                    if (t.name == RootName || t.name.StartsWith("PH_"))
                        doomed.Add(t.gameObject);
                }
            }
            var orphan = GameObject.Find(RootName);
            if (orphan != null && !doomed.Contains(orphan)) doomed.Add(orphan);
            foreach (var go in doomed) Object.DestroyImmediate(go);
        }

        /// <summary>
        /// Pale sand cards: OP_GroundPatch + Road_/Conn_ *_Stripe (paint lines retextured as asphalt).
        /// </summary>
        static void RemoveBadGroundPatches()
        {
            var all = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include);
            var doomed = new List<GameObject>();
            foreach (var t in all)
            {
                if (t == null) continue;
                if (t.name.StartsWith("OP_GroundPatch") || t.name.StartsWith("OD_GroundPatch"))
                    doomed.Add(t.gameObject);
                else if (t.name.EndsWith("_Stripe") && (t.name.StartsWith("Road_") || t.name.StartsWith("Conn_")))
                    doomed.Add(t.gameObject);
            }
            foreach (var go in doomed)
            {
                Object.DestroyImmediate(go);
                _patchesRemoved++;
            }
            Debug.Log($"[PH] Removed {_patchesRemoved} pale ground-patch/stripe quads.");
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Fix #2 — lighting, fog, photographic grade, contact AO
        // ═══════════════════════════════════════════════════════════════════════

        static void Stage0_LightingFogGrade()
        {
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = new Color(0.74f, 0.68f, 0.55f); // warm dusty, slightly darker
            RenderSettings.fogDensity = FogDensity;

            // Lower ambient so key light + shadows survive. Keep warm dust bounce.
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.52f, 0.50f, 0.44f);
            RenderSettings.ambientEquatorColor = new Color(0.44f, 0.38f, 0.30f);
            RenderSettings.ambientGroundColor = new Color(0.20f, 0.16f, 0.12f);
            RenderSettings.ambientIntensity = 0.55f;
            RenderSettings.reflectionIntensity = 0.40f;

            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsInactive.Include))
            {
                if (l.type != LightType.Directional) continue;
                l.enabled = true;
                l.intensity = SunIntensity;
                l.color = new Color(1f, 0.94f, 0.82f);
                l.bounceIntensity = 0.55f;
                l.shadows = LightShadows.Soft;
                l.shadowStrength = ShadowStrength;
                l.shadowBias = 0.03f;
                l.shadowNormalBias = 0.22f;
                l.transform.rotation = Quaternion.Euler(SunEuler);
                EditorUtility.SetDirty(l);
                EditorUtility.SetDirty(l.gameObject);
            }

            // Shadow distance / quality so street shadows reach ground
            QualitySettings.shadowDistance = 110f;
            QualitySettings.shadowCascades = 4;
            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset
                           ?? QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
            if (pipeline != null)
            {
                var so = new SerializedObject(pipeline);
                SetFloat(so, "m_ShadowDistance", 110f);
                SetInt(so, "m_ShadowCascadeCount", 4);
                SetInt(so, "m_MainLightShadowmapResolution", 4096);
                SetBool(so, "m_SoftShadowsSupported", true);
                SetBool(so, "m_MainLightShadowsSupported", true);
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(pipeline);
            }

            // Re-load profile from disk so we never overwrite a concurrent fix with a stale in-memory copy.
            AssetDatabase.ImportAsset(VolumeProfilePath, ImportAssetOptions.ForceSynchronousImport);
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(VolumeProfilePath);
            var volume = Object.FindObjectsByType<Volume>(FindObjectsInactive.Include)
                .FirstOrDefault(v => v.isGlobal);
            if (profile == null && volume != null) profile = volume.sharedProfile;
            if (profile == null)
            {
                Debug.LogWarning("[PH] Volume profile missing; grade skipped.");
                return;
            }

            if (volume != null)
            {
                volume.sharedProfile = profile;
                volume.weight = 1f;
            }

            // Photographic curve. CRITICAL (URP): LiftGammaGain / ShadowsMidtonesHighlights
            // RGB channels are MULTIPLIERS centred on 1.0 — never write ~0 (that blacks the frame).
            // Only the w component is a luminance offset centred on 0.
            var color = GetOrAdd<ColorAdjustments>(profile);
            // Was 0.05 → mean ~0.24 underexposed orange. Target dusty daylight ~0.40.
            Set(color.postExposure, 0.55f);

            var tone = GetOrAdd<Tonemapping>(profile);
            Set(tone.mode, TonemappingMode.ACES);

            GetOrAdd<WhiteBalance>(profile);
            GetOrAdd<LiftGammaGain>(profile);
            GetOrAdd<ShadowsMidtonesHighlights>(profile);
            AaaUrpGradeUtil.ApplyCanonicalDustyGrade(profile, "AaaPhotographPass");

            var bloom = GetOrAdd<Bloom>(profile);
            Set(bloom.threshold, 1.15f);
            Set(bloom.intensity, 0.12f);
            Set(bloom.scatter, 0.50f);
            Set(bloom.tint, new Color(1f, 0.93f, 0.82f));

            var vignette = GetOrAdd<Vignette>(profile);
            Set(vignette.intensity, 0.14f);
            Set(vignette.smoothness, 0.55f);
            Set(vignette.color, new Color(0.08f, 0.05f, 0.03f));

            var grain = GetOrAdd<FilmGrain>(profile);
            Set(grain.type, FilmGrainLookup.Medium1);
            Set(grain.intensity, 0.08f);
            Set(grain.response, 0.65f);

            EditorUtility.SetDirty(profile);
            if (volume != null) EditorUtility.SetDirty(volume);

            Debug.Log($"[PH] Stage0 fog {0.0042f}→{FogDensity}, sun I {1.35f}→{SunIntensity} shadowStr {0.30f}→{ShadowStrength}, ambI→0.55, grade rgb∈[0.5,1.5]");
        }

        static void TuneSsao()
        {
            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset
                           ?? QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
            if (pipeline == null) return;

            var rendererData = GetRendererData(pipeline);
            if (rendererData == null)
            {
                Debug.LogWarning("[PH] URP renderer data not found; SSAO skipped.");
                return;
            }

            var ssaoType = Type.GetType("UnityEngine.Rendering.Universal.ScreenSpaceAmbientOcclusion, Unity.RenderPipelines.Universal.Runtime");
            if (ssaoType == null)
            {
                Debug.LogWarning("[PH] SSAO type missing.");
                return;
            }

            var rendererSo = new SerializedObject(rendererData);
            var features = rendererSo.FindProperty("m_RendererFeatures");
            if (features == null || !features.isArray) return;

            ScriptableObject feature = null;
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
                feature.name = "PH_ScreenSpaceAmbientOcclusion";
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

            var fso = new SerializedObject(feature);
            SetByName(fso, "m_Active", true);
            SetFloatByContains(fso, "intensity", 1.35f);
            SetFloatByContains(fso, "radius", 0.65f);
            SetBoolByContains(fso, "downsample", false);
            SetBoolByContains(fso, "afteropaque", true);
            fso.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(feature);
            EditorUtility.SetDirty(rendererData);
            Debug.Log("[PH] SSAO intensity≈1.05 radius≈0.55 (contact darkening for prop bases / wall seams).");
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Fix #10 — sky: A/B cloudy HDRIs, pick structured plate, match sun
        // ═══════════════════════════════════════════════════════════════════════

        static void Stage1_SkyAbPick()
        {
            // Street-level A/B (player FOV). Prior zenith-up camera flattered kloofendal via fog wash.
            var candidates = new (string name, string path, float exposure, float rotation)[]
            {
                ("overcast_soil_puresky", $"{HdriDir}/overcast_soil_puresky_8k.hdr", 0.55f, 200f),
                ("mud_road_puresky", $"{HdriDir}/mud_road_puresky_8k.hdr", 0.55f, 200f),
                ("cannon", $"{HdriDir}/cannon_8k.hdr", 0.50f, 200f),
                ("kloofendal_partly_cloudy", $"{HdriDir}/kloofendal_48d_partly_cloudy_puresky_4k.hdr", 0.30f, 118f),
                ("overcast_industrial", $"{HdriDir}/overcast_industrial_courtyard_4k.hdr", 0.45f, 90f),
            };

            var sky = AssetDatabase.LoadAssetAtPath<Material>(SkyboxPath);
            if (sky == null)
            {
                sky = new Material(Shader.Find("Skybox/Panoramic")) { name = "Arena_Overflow_Overcast_Skybox" };
                AssetDatabase.CreateAsset(sky, SkyboxPath);
            }

            string bestName = "overcast_soil_puresky";
            string bestPath = $"{HdriDir}/overcast_soil_puresky_8k.hdr";
            float bestExp = 0.55f;
            float bestRot = 200f;
            float bestScore = -1f;
            var abDir = Path.GetFullPath("_research/critique/ours_v2/sky_ab");
            if (!Directory.Exists(abDir)) Directory.CreateDirectory(abDir);

            var temp = new GameObject("PH_SkyABCam");
            var cam = temp.AddComponent<Camera>();
            cam.allowHDR = true;
            cam.fieldOfView = 72f;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 200f;
            var ud = cam.GetUniversalAdditionalCameraData();
            if (ud != null) ud.renderPostProcessing = true;
            // Eye-level street FOV — cloud structure must read here, not only at zenith
            cam.transform.position = new Vector3(0.57f, 1.70f, -9.30f);
            cam.transform.rotation = Quaternion.Euler(2f, 10.1f, 0f);

            var log = new System.Text.StringBuilder();
            log.AppendLine("SkyAB street-level FOV=72 post=ON");

            foreach (var c in candidates)
            {
                var hdri = AssetDatabase.LoadAssetAtPath<Texture>(c.path);
                if (hdri == null)
                {
                    Debug.LogWarning($"[PH] HDRI missing: {c.path}");
                    continue;
                }

                // Align sky rotation so HDRI warm side agrees with directional light (~yaw -38°)
                float rot = c.rotation;
                ApplySky(sky, hdri, c.exposure, rot);
                // Quick exposure nudge toward maxL 0.85–0.95
                float exp = FineTuneSkyExposureReturn(sky, cam, c.exposure);
                var tex = Grab(cam, 1600, 900);
                File.WriteAllBytes(Path.Combine(abDir, $"AB_{c.name}.png"), tex.EncodeToPNG());

                float maxL = 0f, sum = 0f, sumSq = 0f;
                int n = 0, pureWhite = 0;
                int h = tex.height, w = tex.width;
                // Upper 35% of frame = sky band at street FOV
                for (int y = (int)(h * 0.65f); y < h; y += 3)
                {
                    for (int x = 0; x < w; x += 4)
                    {
                        var px = tex.GetPixel(x, y);
                        float lum = 0.2126f * px.r + 0.7152f * px.g + 0.0722f * px.b;
                        maxL = Mathf.Max(maxL, lum);
                        sum += lum;
                        sumSq += lum * lum;
                        n++;
                        if (px.r >= 0.998f && px.g >= 0.998f && px.b >= 0.998f) pureWhite++;
                    }
                }
                float mean = n > 0 ? sum / n : 0f;
                float variance = n > 0 ? Mathf.Max(0f, sumSq / n - mean * mean) : 0f;
                float std = Mathf.Sqrt(variance);
                float whitePct = n > 0 ? 100f * pureWhite / n : 100f;
                // Structure at eye level; require max in 0.85–0.95 band; zero white clip
                float bandPenalty = 0f;
                if (maxL < 0.85f) bandPenalty += (0.85f - maxL) * 8f;
                if (maxL > 0.95f) bandPenalty += (maxL - 0.95f) * 12f;
                float score = std * 8f - bandPenalty - whitePct * 2f;
                // Prefer layered overcast soil (Overflow match) over blue postcard plates
                if (c.name.Contains("overcast_soil")) score += 0.20f;
                if (c.name.Contains("mud_road")) score += 0.10f;
                if (c.name.Contains("kloofendal")) score -= 0.05f; // prior false win via zenith fog
                log.AppendLine($"{c.name}: std={std:F3} max={maxL:F3} mean={mean:F3} white%={whitePct:F2} exp={exp:F3} score={score:F3}");
                Debug.Log($"[PH] SkyAB {c.name}: std={std:F3} max={maxL:F3} white%={whitePct:F2} score={score:F3}");
                if (score > bestScore)
                {
                    bestScore = score;
                    bestName = c.name;
                    bestPath = c.path;
                    bestExp = exp;
                    bestRot = rot;
                }
                Object.DestroyImmediate(tex);
            }

            Object.DestroyImmediate(temp);
            File.WriteAllText(Path.Combine(abDir, "sky_ab_log.txt"), log.ToString());

            var bestHdri = AssetDatabase.LoadAssetAtPath<Texture>(bestPath);
            ApplySky(sky, bestHdri, bestExp, bestRot);

            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsInactive.Include))
            {
                if (l.type != LightType.Directional) continue;
                l.transform.rotation = Quaternion.Euler(SunEuler.x, SunEuler.y, 0f);
            }

            RenderSettings.skybox = sky;
            EditorUtility.SetDirty(sky);
            DynamicGI.UpdateEnvironment();
            float appliedExp = sky.HasProperty("_Exposure") ? sky.GetFloat("_Exposure") : bestExp;
            Debug.Log($"[PH] Stage1 sky winner={bestName} exp≈{appliedExp:F2} rot={bestRot:F0} score={bestScore:F3}");
        }

        static void ApplySky(Material sky, Texture hdri, float exposure, float rotation)
        {
            if (hdri != null && sky.HasProperty("_MainTex"))
                sky.SetTexture("_MainTex", hdri);
            if (sky.HasProperty("_Exposure")) sky.SetFloat("_Exposure", exposure);
            if (sky.HasProperty("_Rotation")) sky.SetFloat("_Rotation", rotation);
            if (sky.HasProperty("_Tint"))
                sky.SetColor("_Tint", new Color(0.98f, 0.94f, 0.88f));
            if (sky.HasProperty("_ImageType")) sky.SetFloat("_ImageType", 0f);
            if (sky.HasProperty("_Mapping")) sky.SetFloat("_Mapping", 1f);
            RenderSettings.skybox = sky;
            EditorUtility.SetDirty(sky);
        }

        static void FineTuneSkyExposure(Material sky, float startExp)
        {
            var temp = new GameObject("PH_SkyTuneCam");
            var cam = temp.AddComponent<Camera>();
            cam.allowHDR = true;
            cam.fieldOfView = 72f;
            cam.transform.position = new Vector3(0.57f, 1.70f, -9.30f);
            cam.transform.rotation = Quaternion.Euler(2f, 10.1f, 0f);
            var ud = cam.GetUniversalAdditionalCameraData();
            if (ud != null) ud.renderPostProcessing = true;
            float exp = FineTuneSkyExposureReturn(sky, cam, startExp);
            Object.DestroyImmediate(temp);
            Debug.Log($"[PH] Sky exposure fine-tuned → {exp:F3}");
        }

        static float FineTuneSkyExposureReturn(Material sky, Camera cam, float startExp)
        {
            float exp = startExp;
            for (int iter = 0; iter < 6; iter++)
            {
                if (sky.HasProperty("_Exposure")) sky.SetFloat("_Exposure", exp);
                var tex = Grab(cam, 800, 450);
                float maxL = 0f;
                int white = 0, n = 0;
                for (int y = (int)(tex.height * 0.65f); y < tex.height; y += 3)
                for (int x = 0; x < tex.width; x += 4)
                {
                    var px = tex.GetPixel(x, y);
                    float lum = 0.2126f * px.r + 0.7152f * px.g + 0.0722f * px.b;
                    maxL = Mathf.Max(maxL, lum);
                    if (px.r >= 0.998f && px.g >= 0.998f && px.b >= 0.998f) white++;
                    n++;
                }
                Object.DestroyImmediate(tex);
                if (maxL > 0.95f || (n > 0 && white * 100f / n > 0.0f))
                    exp *= 0.92f;
                else if (maxL < 0.85f)
                    exp *= 1.06f;
                else
                    break;
                exp = Mathf.Clamp(exp, 0.20f, 1.20f);
            }
            if (sky.HasProperty("_Exposure")) sky.SetFloat("_Exposure", exp);
            EditorUtility.SetDirty(sky);
            return exp;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Fix #1 — ground tiling breakup, lane decals, UV variation
        // ═══════════════════════════════════════════════════════════════════════

        static void Stage2_GroundBreakup()
        {
            // Vary UV scale on asphalt / dirt so tiling doesn't repeat every 5–8 m
            VaryGroundUVs();

            // Opaque dark patches — transparent URP decals were reading as pale sand quads
            var oil = LoadOrMakeOpaque("PH_Oil", new Color(0.10f, 0.09f, 0.08f),
                "Assets/_Project/Art/Textures/Generated/FD_OilStain.png");
            var dirt = LoadOrMakeOpaque("PH_Dirt", new Color(0.32f, 0.26f, 0.16f), null);
            var leak = LoadOrMakeOpaque("PH_Leak", new Color(0.24f, 0.20f, 0.14f), null);
            var puddle = LoadOrMakeOpaque("PH_Puddle", new Color(0.14f, 0.15f, 0.13f), null);
            var crack = LoadOrMakeOpaque("PH_Crack", new Color(0.12f, 0.11f, 0.09f), null);
            var darkAsphalt = LoadOrMakeOpaque("PH_DarkPatch", new Color(0.18f, 0.17f, 0.15f), null);

            var mats = new[] { dirt, oil, leak, puddle, crack, darkAsphalt };

            // 3–4 decals per 10 m → step ≈ 2.8 m along each lane + barren edges
            void SprinkleLane(float xCenter, float xJitter, float z0, float z1, float step)
            {
                for (float z = z0; z <= z1; z += step)
                {
                    // 3–4 per 10 m: place a small cluster
                    int cluster = 3 + (Rng.Next(0, 2));
                    for (int i = 0; i < cluster; i++)
                    {
                        float jx = (float)(Rng.NextDouble() - 0.5) * xJitter;
                        float jz = (float)(Rng.NextDouble() - 0.5) * step * 0.7f;
                        float s = 1.6f + (float)Rng.NextDouble() * 3.2f;
                        var mat = mats[(_decals + i) % mats.Length];
                        SpawnGroundDecal(new Vector3(xCenter + jx, 0.022f + (i % 3) * 0.002f, z + jz), s, mat);
                    }
                }
            }

            // Main x∈[-6,6], market [22,40], west [-50,-28]
            SprinkleLane(0f, 5.5f, -70f, 70f, 2.8f);
            SprinkleLane(31f, 8f, -70f, 70f, 2.9f);
            SprinkleLane(-39f, 9f, -70f, 70f, 2.9f);

            // Critic barren edges: (50,2), (58,-35), z<-50 south
            void EdgeBurst(Vector3 center, int count, float radius)
            {
                for (int i = 0; i < count; i++)
                {
                    float ang = (float)Rng.NextDouble() * Mathf.PI * 2f;
                    float r = (float)Rng.NextDouble() * radius;
                    var p = center + new Vector3(Mathf.Cos(ang) * r, 0.024f, Mathf.Sin(ang) * r);
                    SpawnGroundDecal(p, 2f + (float)Rng.NextDouble() * 3.5f, mats[i % mats.Length]);
                }
            }
            EdgeBurst(new Vector3(50f, 0f, 2f), 18, 9f);
            EdgeBurst(new Vector3(58f, 0f, -35f), 22, 11f);
            EdgeBurst(new Vector3(-1.7f, 0f, -57f), 20, 10f);
            EdgeBurst(new Vector3(0f, 0f, -65f), 14, 8f);
            EdgeBurst(new Vector3(44f, 0f, 10f), 12, 7f);

            // Smaller textured asphalt/dirt breakup patches (NOT pale sand quads)
            var asphaltDark = UpsertPbr("PH_AsphaltDark",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Asphalt031/Asphalt031_2K-JPG_Color.jpg",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Asphalt031/Asphalt031_2K-JPG_NormalGL.jpg",
                new Color(0.42f, 0.40f, 0.36f), 0.18f, 3.5f);
            var dirtTex = UpsertPbr("PH_DirtPatch",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Ground054/Ground054_2K-JPG_Color.jpg",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Ground054/Ground054_2K-JPG_NormalGL.jpg",
                new Color(0.48f, 0.40f, 0.28f), 0.14f, 2.5f);

            var patchCenters = new[]
            {
                new Vector3(1f, 0.018f, -55f), new Vector3(-2f, 0.018f, -40f), new Vector3(3f, 0.018f, -20f),
                new Vector3(-1f, 0.018f, 5f), new Vector3(2f, 0.018f, 30f), new Vector3(0f, 0.018f, 50f),
                new Vector3(28f, 0.018f, -40f), new Vector3(34f, 0.018f, -10f), new Vector3(30f, 0.018f, 20f),
                new Vector3(-36f, 0.018f, -30f), new Vector3(-40f, 0.018f, 0f), new Vector3(-32f, 0.018f, 25f),
                new Vector3(52f, 0.018f, 0f), new Vector3(56f, 0.018f, -32f), new Vector3(48f, 0.018f, 8f),
            };
            foreach (var p in patchCenters)
            {
                float s = 4.5f + (float)Rng.NextDouble() * 3.5f;
                var mat = (Rng.Next(0, 2) == 0) ? asphaltDark : dirtTex;
                var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                go.name = $"PH_GroundBreak_{_decals}";
                go.transform.SetParent(_root, true);
                go.transform.position = p;
                go.transform.rotation = Quaternion.Euler(90f, Rng.Next(0, 360), 0f);
                go.transform.localScale = new Vector3(s, s * (0.55f + (float)Rng.NextDouble() * 0.5f), 1f);
                Object.DestroyImmediate(go.GetComponent<Collider>());
                var r = go.GetComponent<Renderer>();
                r.sharedMaterial = mat;
                // Slightly transparent dark overlay feel via darker tint already on mat
                SetStatic(go);
                _decals++;
            }

            Debug.Log($"[PH] Stage2 ground decals/patches={_decals}");
        }

        static void VaryGroundUVs()
        {
            if (_map == null) return;
            int varied = 0;
            foreach (Transform t in _map)
            {
                if (t.name.EndsWith("_Stripe")) continue; // never UV-vary paint lines as asphalt
                bool isGround = t.name == "Ground" || t.name == "Beach_Dirt"
                    || t.name.StartsWith("Road_") || t.name.StartsWith("Conn_")
                    || t.name.StartsWith("Sidewalk_");
                if (!isGround) continue;
                var r = t.GetComponent<Renderer>();
                if (r == null) continue;
                var src = r.sharedMaterial;
                if (src == null) continue;

                // Instance material with unique tiling so 5–8 m repeat breaks
                int h = Mathf.Abs(t.name.GetHashCode());
                float tile = 7f + (h % 9);
                if (t.name.StartsWith("Sidewalk_")) tile = 3.2f + (h % 5) * 0.4f;
                if (t.name == "Beach_Dirt") tile = 6f + (h % 4);

                var mat = new Material(src);
                mat.name = src.name + "_PH_" + varied;
                if (mat.HasProperty("_BaseMap"))
                    mat.SetTextureScale("_BaseMap", new Vector2(tile, tile * (0.85f + (varied % 3) * 0.12f)));
                mat.mainTextureScale = new Vector2(tile, tile * 0.92f);
                if (mat.HasProperty("_BumpMap"))
                    mat.SetTextureScale("_BumpMap", mat.mainTextureScale);
                // Slight per-chunk tint jitter
                if (mat.HasProperty("_BaseColor"))
                {
                    var c = mat.GetColor("_BaseColor");
                    float j = 1f + ((varied % 5) - 2) * 0.03f;
                    mat.SetColor("_BaseColor", new Color(c.r * j, c.g * j * 0.99f, c.b * j * 0.97f, c.a));
                }
                r.sharedMaterial = mat;
                varied++;
            }
            Debug.Log($"[PH] UV-varied {varied} ground/sidewalk renderers.");
        }

        static void Stage3_KerbsAndDirtSkirts()
        {
            var kerbMat = AssetDatabase.LoadAssetAtPath<Material>($"{OdMatDir}/OD_Concrete.mat")
                          ?? UpsertPbr("PH_KerbConcrete",
                              "Assets/_Project/Art/Textures/Incoming/AmbientCG/Concrete031/Concrete031_2K-JPG_Color.jpg",
                              "Assets/_Project/Art/Textures/Incoming/AmbientCG/Concrete031/Concrete031_2K-JPG_NormalGL.jpg",
                              new Color(0.55f, 0.52f, 0.46f), 0.22f, 2.5f);
            var wearMat = LoadOrMakeOpaque("PH_KerbWear", new Color(0.28f, 0.24f, 0.18f), null);
            var raisedWalkMat = UpsertPbr("PH_RaisedWalk",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Concrete031/Concrete031_2K-JPG_Color.jpg",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Concrete031/Concrete031_2K-JPG_NormalGL.jpg",
                new Color(0.48f, 0.45f, 0.40f), 0.20f, 3.2f);

            // Real kerbs with height along sidewalks + lane edges at barren zones
            void PlaceKerb(Vector3 pos, float len, float yaw)
            {
                var kerb = GameObject.CreatePrimitive(PrimitiveType.Cube);
                kerb.name = $"PH_Kerb_{_kerbs}";
                kerb.transform.SetParent(_root, true);
                kerb.transform.position = pos + Vector3.up * 0.16f;
                kerb.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                kerb.transform.localScale = new Vector3(len, 0.32f, 0.48f);
                Object.DestroyImmediate(kerb.GetComponent<Collider>()); // decorative, walkable
                kerb.GetComponent<Renderer>().sharedMaterial = kerbMat;
                SetStatic(kerb);
                _kerbs++;

                // Worn edge strip (darker, slightly inset)
                var wear = GameObject.CreatePrimitive(PrimitiveType.Cube);
                wear.name = $"PH_KerbWear_{_kerbs}";
                wear.transform.SetParent(_root, true);
                wear.transform.position = pos + Vector3.up * 0.02f + Quaternion.Euler(0, yaw, 0) * Vector3.forward * 0.32f;
                wear.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                wear.transform.localScale = new Vector3(len * 0.95f, 0.06f, 0.40f);
                Object.DestroyImmediate(wear.GetComponent<Collider>());
                wear.GetComponent<Renderer>().sharedMaterial = wearMat;
                SetStatic(wear);
            }

            // Convert pale flat Sidewalk_ quads into raised dark concrete + kerb (kills sand-slab tell)
            if (_map != null)
            {
                foreach (Transform t in _map)
                {
                    if (!t.name.StartsWith("Sidewalk_")) continue;
                    var r = t.GetComponent<Renderer>();
                    if (r == null) continue;
                    r.enabled = true; // idempotent: prior run may have hidden these
                    var b = r.bounds;

                    // Hide the flat pale slab renderer; replace with raised slab of real height
                    r.enabled = false;
                    var raised = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    raised.name = $"PH_RaisedWalk_{_kerbs}";
                    raised.transform.SetParent(_root, true);
                    raised.transform.position = new Vector3(b.center.x, 0.12f, b.center.z);
                    raised.transform.localScale = new Vector3(
                        Mathf.Max(b.size.x, 1.2f),
                        0.24f,
                        Mathf.Max(b.size.z, 1.2f));
                    Object.DestroyImmediate(raised.GetComponent<Collider>());
                    raised.GetComponent<Renderer>().sharedMaterial = raisedWalkMat;
                    SetStatic(raised);

                    float laneX = 0f;
                    if (Mathf.Abs(b.center.x - 30f) < Mathf.Abs(b.center.x)) laneX = 30f;
                    if (Mathf.Abs(b.center.x + 34f) < Mathf.Abs(b.center.x - laneX)) laneX = -34f;
                    float edgeX = b.center.x < laneX ? b.max.x : b.min.x;
                    float len = Mathf.Clamp(b.size.z, 3f, 12f);
                    PlaceKerb(new Vector3(edgeX, 0f, b.center.z), len, 0f);
                }
            }

            // Explicit kerbs at barren critique spots + main lane shoulders
            var kerbSpecs = new (Vector3 p, float len, float yaw)[]
            {
                (new Vector3(6.2f, 0f, -55f), 10f, 0f),
                (new Vector3(-6.2f, 0f, -55f), 10f, 0f),
                (new Vector3(6.2f, 0f, -40f), 10f, 0f),
                (new Vector3(-6.2f, 0f, -40f), 10f, 0f),
                (new Vector3(6.2f, 0f, -10f), 10f, 0f),
                (new Vector3(-6.2f, 0f, 10f), 10f, 0f),
                (new Vector3(6.2f, 0f, 40f), 10f, 0f),
                (new Vector3(22f, 0f, -35f), 8f, 0f),
                (new Vector3(40f, 0f, -35f), 8f, 0f),
                (new Vector3(22f, 0f, 5f), 8f, 0f),
                (new Vector3(40f, 0f, 5f), 8f, 0f),
                (new Vector3(-28f, 0f, -20f), 8f, 0f),
                (new Vector3(-50f, 0f, -20f), 8f, 0f),
                (new Vector3(-28f, 0f, 10f), 8f, 0f),
                (new Vector3(48f, 0f, 2f), 9f, 90f),
                (new Vector3(56f, 0f, -34f), 10f, 90f),
                (new Vector3(50f, 0f, -20f), 8f, 0f),
            };
            foreach (var (p, len, yaw) in kerbSpecs)
                PlaceKerb(p, len, yaw);

            // Dirt skirts: dark band where ground meets wall
            var skirtMat = LoadOrMakeOpaque("PH_DirtSkirt", new Color(0.16f, 0.12f, 0.08f), null);
            if (_map != null)
            {
                foreach (Transform bldg in _map)
                {
                    if (!bldg.name.StartsWith("Bldg_") || bldg.name.Contains("Fountain")) continue;
                    var mass = bldg.Find(bldg.name + "_Mass");
                    var r = mass != null ? mass.GetComponent<Renderer>() : bldg.GetComponentInChildren<Renderer>();
                    if (r == null) continue;
                    var b = r.bounds;

                    var faces = new (Vector3 center, Vector3 normal, float width)[]
                    {
                        (new Vector3(b.center.x, 0, b.max.z), Vector3.forward, b.size.x),
                        (new Vector3(b.center.x, 0, b.min.z), Vector3.back, b.size.x),
                        (new Vector3(b.max.x, 0, b.center.z), Vector3.right, b.size.z),
                        (new Vector3(b.min.x, 0, b.center.z), Vector3.left, b.size.z),
                    };

                    foreach (var (center, normal, width) in faces)
                    {
                        float distLane = Mathf.Min(
                            Mathf.Abs(center.x),
                            Mathf.Abs(center.x - 30f),
                            Mathf.Abs(center.x + 34f),
                            Mathf.Abs(center.x - 50f));
                        // Include east edge / south approach (critic barren zones)
                        bool nearEdge = center.x > 44f || center.z < -48f || Mathf.Abs(center.x + 58f) < 20f;
                        if (distLane > 22f && !nearEdge) continue;

                        float skirtLen = Mathf.Clamp(width * 0.92f, 2f, 16f);
                        // Horizontal dirt band on ground against wall
                        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                        go.name = $"PH_DirtSkirt_{_skirts}";
                        go.transform.SetParent(_root, true);
                        go.transform.position = center + normal * 0.65f + Vector3.up * 0.02f;
                        go.transform.rotation = Quaternion.Euler(90f, Quaternion.LookRotation(normal).eulerAngles.y, 0f);
                        go.transform.localScale = new Vector3(skirtLen, 1.4f, 1f);
                        Object.DestroyImmediate(go.GetComponent<Collider>());
                        go.GetComponent<Renderer>().sharedMaterial = skirtMat;
                        SetStatic(go);
                        _skirts++;

                        // Vertical rising-damp band on wall
                        var wall = GameObject.CreatePrimitive(PrimitiveType.Quad);
                        wall.name = $"PH_WallDamp_{_skirts}";
                        wall.transform.SetParent(_root, true);
                        wall.transform.position = center + normal * 0.03f + Vector3.up * 0.70f;
                        wall.transform.rotation = Quaternion.LookRotation(-normal);
                        wall.transform.localScale = new Vector3(skirtLen * 0.95f, 1.35f, 1f);
                        Object.DestroyImmediate(wall.GetComponent<Collider>());
                        wall.GetComponent<Renderer>().sharedMaterial = skirtMat;
                        SetStatic(wall);
                        _skirts++;
                    }
                }
            }

            Debug.Log($"[PH] Stage3 kerbs={_kerbs} dirtSkirts={_skirts}");
        }

        static void SpawnGroundDecal(Vector3 pos, float size, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = $"PH_Decal_{_decals}";
            go.transform.SetParent(_root, true);
            go.transform.position = pos;
            go.transform.rotation = Quaternion.Euler(90f, Rng.Next(0, 360), 0f);
            go.transform.localScale = new Vector3(size, size * (0.6f + (float)Rng.NextDouble() * 0.5f), 1f);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.GetComponent<Renderer>().sharedMaterial = mat;
            SetStatic(go);
            _decals++;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Capture critique viewpoints + metrics
        // ═══════════════════════════════════════════════════════════════════════

        struct Metrics
        {
            public float shadowRatio;
            public float skyMax;
            public float pureWhitePct;
        }

        static Metrics CaptureCritiqueViews()
        {
            var shots = new (string name, Vector3 pos, float yaw, float pitch)[]
            {
                ("EL_01_L01_Main_S", new Vector3(-1.68f, 1.70f, -57.80f), 43.6f, 1.5f),
                ("EL_02_L02_Main_Mid", new Vector3(0.57f, 1.70f, -9.30f), 10.1f, 0.2f),
                ("EL_10_S10_Spawn_ish", new Vector3(-13.58f, 1.70f, -44.34f), 11.3f, 2.4f),
                ("EL_14_R14_x50_z2_y138", new Vector3(49.92f, 1.70f, 2.33f), 138.5f, 4.8f),
                ("EL_18_R18_x58_z-35_y247", new Vector3(57.90f, 1.70f, -34.87f), 247.2f, 4.3f),
                // extras for lighting / under-vehicle
                ("EL_05_L05_Market_Mid", new Vector3(21.39f, 3.25f, -7.58f), 171.5f, 2.4f),
                ("EL_15_R15_x-32_z1_y293", new Vector3(-31.90f, 1.70f, 0.72f), 293.3f, -0.8f),
            };

            var outDir = Path.GetFullPath(CritiqueOut);
            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

            var temp = new GameObject("PH_CritiqueCam");
            var cam = temp.AddComponent<Camera>();
            cam.allowHDR = true;
            cam.fieldOfView = 72f;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 200f;
            var data = cam.GetUniversalAdditionalCameraData();
            if (data != null)
            {
                // MUST enable post — CopyFrom / new Camera does NOT inherit URP post flag.
                // Verifying with post OFF is how the black-screen grade bug shipped.
                data.renderPostProcessing = true;
                data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            }

            float globalSkyMax = 0f;
            int globalWhite = 0, globalSkyN = 0;
            float bestRatio = 0f;
            var log = new System.Text.StringBuilder();
            log.AppendLine($"PhotographPassCapture seed=20260727 fog={FogDensity} sunI={SunIntensity} shadowStr={ShadowStrength}");

            foreach (var (name, pos, yaw, pitch) in shots)
            {
                cam.transform.position = pos;
                cam.transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
                var tex = Grab(cam, 1920, 1080);
                var path = Path.Combine(outDir, name + ".png");
                File.WriteAllBytes(path, tex.EncodeToPNG());

                // Sky upper band
                float localMax = 0f;
                int white = 0, sn = 0;
                int h = tex.height, w = tex.width;
                for (int y = (int)(h * 0.70f); y < h; y += 6)
                for (int x = 0; x < w; x += 8)
                {
                    var c = tex.GetPixel(x, y);
                    float lum = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
                    localMax = Mathf.Max(localMax, lum);
                    if (lum >= 0.995f) white++;
                    sn++;
                }
                globalSkyMax = Mathf.Max(globalSkyMax, localMax);
                globalWhite += white;
                globalSkyN += sn;

                // Shadow vs sunlit ground: sample lower third left/right vs centre; also dark pockets
                float sunlit = SamplePercentile(tex, 0, (int)(h * 0.32f), w / 5, w * 4 / 5, 0.75f);
                float shadow = SamplePercentile(tex, 0, (int)(h * 0.32f), w / 5, w * 4 / 5, 0.15f);
                float ratio = shadow > 0.01f ? sunlit / shadow : 0f;
                bestRatio = Mathf.Max(bestRatio, ratio);

                log.AppendLine($"{name}.png\tpos=({pos.x:F2}, {pos.y:F2}, {pos.z:F2})\tyaw={yaw:F1}\tpitch={pitch:F1}\tskyMax={localMax:F3}\tsunlit={sunlit:F3}\tshadow={shadow:F3}\tratio={ratio:F2}");
                Debug.Log($"[PH] {name}: skyMax={localMax:F3} sunlit={sunlit:F3} shadow={shadow:F3} ratio={ratio:F2}");
                Object.DestroyImmediate(tex);
            }

            // Dedicated under-vehicle vs open ground sample from EL_10 / EL_05
            float underVeh = SampleUnderVehicleContact(cam);
            log.AppendLine($"underVehicleContactLum={underVeh:F3}");

            File.WriteAllText(Path.Combine(outDir, "capture_log.txt"), log.ToString());
            Object.DestroyImmediate(temp);
            AssetDatabase.Refresh();

            float whitePct = globalSkyN > 0 ? 100f * globalWhite / globalSkyN : 0f;
            return new Metrics
            {
                shadowRatio = bestRatio,
                skyMax = globalSkyMax,
                pureWhitePct = whitePct
            };
        }

        static float SampleUnderVehicleContact(Camera cam)
        {
            // Aim at known vehicle pockets from critique EL_10 / EL_05
            var probes = new (Vector3 pos, float yaw, float pitch)[]
            {
                (new Vector3(-13.58f, 1.70f, -44.34f), 11.3f, 8f), // look slightly down toward truck
                (new Vector3(21.39f, 2.0f, -7.58f), 171.5f, 12f),
            };
            float darkest = 1f;
            float openBright = 0f;
            foreach (var (pos, yaw, pitch) in probes)
            {
                cam.transform.position = pos;
                cam.transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
                var tex = Grab(cam, 1280, 720);
                float shadow = SamplePercentile(tex, 0, (int)(tex.height * 0.40f), tex.width / 4, tex.width * 3 / 4, 0.08f);
                float open = SamplePercentile(tex, 0, (int)(tex.height * 0.40f), tex.width / 4, tex.width * 3 / 4, 0.80f);
                darkest = Mathf.Min(darkest, shadow);
                openBright = Mathf.Max(openBright, open);
                Object.DestroyImmediate(tex);
            }
            float ratio = darkest > 0.01f ? openBright / darkest : 0f;
            Debug.Log($"[PH] under-vehicle lum={darkest:F3} openGround={openBright:F3} ratio={ratio:F2}");
            return darkest;
        }

        static float SamplePercentile(Texture2D tex, int y0, int y1, int x0, int x1, float percentile)
        {
            var vals = new List<float>(4096);
            for (int y = y0; y < y1; y += 5)
            for (int x = x0; x < x1; x += 6)
            {
                var c = tex.GetPixel(x, y);
                vals.Add(0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b);
            }
            if (vals.Count == 0) return 0f;
            vals.Sort();
            int idx = Mathf.Clamp(Mathf.RoundToInt((vals.Count - 1) * percentile), 0, vals.Count - 1);
            return vals[idx];
        }

        static Texture2D Grab(Camera cam, int w, int h)
        {
            // HDR camera → blit to LDR before ReadPixels (direct ARGB32 read was returning black under ACES)
            var rtHdr = new RenderTexture(w, h, 24, RenderTextureFormat.DefaultHDR);
            var rtLdr = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
            var prev = cam.targetTexture;
            cam.targetTexture = rtHdr;
            cam.Render();
            Graphics.Blit(rtHdr, rtLdr);
            RenderTexture.active = rtLdr;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            cam.targetTexture = prev;
            RenderTexture.active = null;
            Object.DestroyImmediate(rtHdr);
            Object.DestroyImmediate(rtLdr);
            return tex;
        }

        static void AuditInvisibleColliders()
        {
            var all = Object.FindObjectsByType<Collider>(FindObjectsInactive.Include);
            int invis = 0;
            foreach (var c in all)
            {
                if (c is CharacterController) continue;
                // Strip any PH_ invisible collider that slipped in
                if (c.name.StartsWith("PH_") || (c.transform.parent != null && c.transform.root.name == RootName))
                {
                    bool anyVis = c.GetComponentsInChildren<Renderer>().Any(x => x != null && x.enabled);
                    if (!anyVis)
                    {
                        Object.DestroyImmediate(c);
                        continue;
                    }
                }
                var r = c.GetComponent<Renderer>();
                if (r == null) r = c.GetComponentInChildren<Renderer>();
                if (r == null || !r.enabled) invis++;
            }
            all = Object.FindObjectsByType<Collider>(FindObjectsInactive.Include);
            invis = 0;
            foreach (var c in all)
            {
                if (c is CharacterController) continue;
                var r = c.GetComponent<Renderer>();
                if (r == null) r = c.GetComponentInChildren<Renderer>();
                if (r == null || !r.enabled) invis++;
            }
            Debug.Log($"[PH] colliders={all.Length} invisible={invis}");
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Helpers
        // ═══════════════════════════════════════════════════════════════════════

        static Material LoadOrMakeOpaque(string name, Color c, string albedoPath)
        {
            var path = $"{MatDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
                mat.name = name;
                AssetDatabase.CreateAsset(mat, path);
            }
            if (!string.IsNullOrEmpty(albedoPath))
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(albedoPath);
                if (tex != null)
                {
                    mat.SetTexture("_BaseMap", tex);
                    mat.mainTexture = tex;
                }
            }
            mat.SetColor("_BaseColor", c);
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_Smoothness", name.Contains("Puddle") ? 0.55f : 0.16f);
            // Force opaque — prior transparent setup rendered as pale sand cards
            if (mat.HasProperty("_Surface"))
            {
                mat.SetFloat("_Surface", 0f);
                mat.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.SetOverrideTag("RenderType", "Opaque");
                mat.renderQueue = 2000;
            }
            if (mat.HasProperty("_ZWrite")) mat.SetInt("_ZWrite", 1);
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material UpsertPbr(string name, string albedo, string normal, Color tint, float smoothness, float tiling)
        {
            var path = $"{MatDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
                mat.name = name;
                AssetDatabase.CreateAsset(mat, path);
            }
            var alb = AssetDatabase.LoadAssetAtPath<Texture2D>(albedo);
            var nrm = AssetDatabase.LoadAssetAtPath<Texture2D>(normal);
            if (alb != null) { mat.SetTexture("_BaseMap", alb); mat.mainTexture = alb; }
            mat.SetColor("_BaseColor", tint);
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_Smoothness", smoothness);
            mat.SetTextureScale("_BaseMap", new Vector2(tiling, tiling));
            mat.mainTextureScale = new Vector2(tiling, tiling);
            if (nrm != null)
            {
                mat.SetTexture("_BumpMap", nrm);
                mat.EnableKeyword("_NORMALMAP");
                mat.SetFloat("_BumpScale", 0.85f);
                mat.SetTextureScale("_BumpMap", new Vector2(tiling, tiling));
            }
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
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
                if (AssetDatabase.Contains(profile) && !AssetDatabase.Contains(component))
                    AssetDatabase.AddObjectToAsset(component, profile);
            }
            component.active = true;
            return component;
        }

        static void Set<T>(VolumeParameter<T> p, T v)
        {
            p.overrideState = true;
            p.value = v;
        }

        static UniversalRendererData GetRendererData(UniversalRenderPipelineAsset pipeline)
        {
            var so = new SerializedObject(pipeline);
            var list = so.FindProperty("m_RendererDataList");
            if (list != null && list.isArray && list.arraySize > 0)
            {
                var renderer = list.GetArrayElementAtIndex(0).objectReferenceValue as UniversalRendererData;
                if (renderer != null) return renderer;
            }
            return AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/_Project/Settings/URP/URP_PC_Renderer.asset");
        }

        static void SetStatic(GameObject go)
        {
            go.isStatic = true;
            foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
                t.gameObject.isStatic = true;
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
                if (it.propertyType == SerializedPropertyType.Float &&
                    it.propertyPath.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0)
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
                if (it.propertyType == SerializedPropertyType.Boolean &&
                    it.propertyPath.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0)
                    it.boolValue = value;
            }
        }
    }
}
#endif
