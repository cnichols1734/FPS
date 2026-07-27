#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
    /// Job 2+3: street-level sky A/B + dusty-daylight exposure rebalance.
    /// Idempotent. Does not touch player / prefabs / weapons.
    /// Menu: Arena FPS / AAA Sky Exposure Pass
    /// </summary>
    public static class AaaSkyExposurePass
    {
        const string ScenePath = "Assets/_Project/Scenes/Arena.unity";
        const string SkyboxPath = "Assets/_Project/Settings/Lighting/Arena_Overflow_Overcast_Skybox.mat";
        const string VolumeProfilePath = "Assets/_Project/Settings/Lighting/Arena_AAA_GlobalVolume.asset";
        const string HdriDir = "Assets/_Project/Art/Textures/HDRI";
        const string AbDir = "_research/critique/ours_v2/sky_ab";
        const string ReportPath = "_research/SKY_EXPOSURE_REPORT.md";
        static readonly Vector3 SunEuler = new(48f, -38f, 0f);

        [MenuItem("Arena FPS/AAA Sky Exposure Pass")]
        public static void Run()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.isPlaying = false;
                Debug.LogWarning("[SX] Exit play mode and re-run.");
                return;
            }

            var active = SceneManager.GetActiveScene();
            if (active.path != ScenePath)
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            Directory.CreateDirectory(Path.GetFullPath(AbDir));
            Directory.CreateDirectory(Path.GetFullPath("_research"));

            AssetDatabase.Refresh();

            var sb = new StringBuilder();
            sb.AppendLine("# SKY_EXPOSURE_REPORT");
            sb.AppendLine();
            sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("Street-level FOV=72°, post-processing ON. Not zenith-up.");
            sb.AppendLine();

            var (winner, winExp, winRot, abLog) = RunSkyAb();
            sb.AppendLine("## Sky A/B");
            sb.AppendLine();
            sb.AppendLine(abLog);
            sb.AppendLine();
            sb.AppendLine($"**Winner:** `{winner}` exp={winExp:F3} rot={winRot:F0}");
            sb.AppendLine();

            var before = MeasureStreet("BEFORE_EXPOSURE");
            sb.AppendLine("## Exposure before");
            sb.AppendLine(before.detail);
            sb.AppendLine();

            RebalanceExposure(before.meanLum);
            var after = MeasureStreet("AFTER_EXPOSURE");
            sb.AppendLine("## Exposure after");
            sb.AppendLine(after.detail);
            sb.AppendLine();
            sb.AppendLine($"**Final:** meanLuminance={after.meanLum:F4} nearBlack%={after.nearBlackPct:F2} clippedHi%={after.clipPct:F2} skyMax={after.skyMax:F3} pureWhite%={after.whitePct:F2}");
            sb.AppendLine();

            // Spawn / collider audit
            var spawn = new Vector3(0f, 0.05f, -63f);
            var hits = Physics.OverlapSphere(spawn, 0.55f, ~0, QueryTriggerInteraction.Ignore);
            int invis = 0;
            foreach (var col in Object.FindObjectsByType<Collider>(FindObjectsInactive.Exclude))
            {
                if (col == null || !col.enabled || col.isTrigger) continue;
                var r = col.GetComponent<Renderer>() ?? col.GetComponentInChildren<Renderer>();
                if (r == null || !r.enabled) invis++;
            }
            var player = GameObject.Find("Player");
            sb.AppendLine("## Constraints");
            sb.AppendLine($"- Spawn overlaps: {hits.Length} ({string.Join(", ", hits.Select(h => h.name))})");
            sb.AppendLine($"- Player pos: {(player != null ? player.transform.position.ToString("F3") : "NULL")}");
            sb.AppendLine($"- Invisible colliders: {invis}");
            sb.AppendLine($"- Sky winner: {winner}");

            File.WriteAllText(Path.GetFullPath(ReportPath), sb.ToString());
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
            AssetDatabase.SaveAssets();
            Debug.Log($"[SX] DONE winner={winner} mean={after.meanLum:F4} black%={after.nearBlackPct:F2}");
        }

        static (string name, float exp, float rot, string log) RunSkyAb()
        {
            var candidates = new (string name, string path, float exposure, float rotation)[]
            {
                ("overcast_soil_puresky", $"{HdriDir}/overcast_soil_puresky_8k.hdr", 0.55f, 200f),
                ("mud_road_puresky", $"{HdriDir}/mud_road_puresky_8k.hdr", 0.55f, 200f),
                ("cannon", $"{HdriDir}/cannon_8k.hdr", 0.50f, 200f),
                ("kloofendal_partly_cloudy", $"{HdriDir}/kloofendal_48d_partly_cloudy_puresky_4k.hdr", 0.30f, 118f),
                ("cloud_layers", $"{HdriDir}/cloud_layers_8k.hdr", 0.45f, 180f),
                ("overcast_industrial", $"{HdriDir}/overcast_industrial_courtyard_4k.hdr", 0.45f, 90f),
            };

            var sky = AssetDatabase.LoadAssetAtPath<Material>(SkyboxPath);
            if (sky == null)
            {
                sky = new Material(Shader.Find("Skybox/Panoramic")) { name = "Arena_Overflow_Overcast_Skybox" };
                AssetDatabase.CreateAsset(sky, SkyboxPath);
            }

            var go = new GameObject("__SX_SkyCam");
            var cam = go.AddComponent<Camera>();
            cam.allowHDR = true;
            cam.fieldOfView = 72f;
            cam.nearClipPlane = 0.08f;
            cam.farClipPlane = 280f;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.enabled = false;
            var ud = cam.GetUniversalAdditionalCameraData();
            if (ud != null) { ud.renderPostProcessing = true; ud.antialiasing = AntialiasingMode.None; }
            cam.transform.position = new Vector3(0.57f, 1.70f, -9.30f);
            cam.transform.rotation = Quaternion.Euler(2f, 10.1f, 0f);

            string bestName = null;
            string bestPath = null;
            float bestExp = 0.55f, bestRot = 200f, bestScore = -999f;
            var log = new StringBuilder();
            log.AppendLine("| HDRI | std | maxL | mean | white% | exp | score |");
            log.AppendLine("|---|---:|---:|---:|---:|---:|---:|");

            foreach (var c in candidates)
            {
                var hdri = AssetDatabase.LoadAssetAtPath<Texture>(c.path);
                if (hdri == null)
                {
                    log.AppendLine($"| `{c.name}` | MISSING | | | | | |");
                    continue;
                }

                ApplySky(sky, hdri, c.exposure, c.rotation);
                float exp = TuneSkyExp(sky, cam, c.exposure);
                var tex = Grab(cam, 1600, 900);
                File.WriteAllBytes(Path.GetFullPath($"{AbDir}/AB_{c.name}.png"), tex.EncodeToPNG());

                float maxL = 0f, sum = 0f, sumSq = 0f;
                int n = 0, white = 0;
                int h = tex.height, w = tex.width;
                for (int y = (int)(h * 0.65f); y < h; y += 2)
                for (int x = 0; x < w; x += 3)
                {
                    var px = tex.GetPixel(x, y);
                    float lum = 0.2126f * px.r + 0.7152f * px.g + 0.0722f * px.b;
                    maxL = Mathf.Max(maxL, lum);
                    sum += lum; sumSq += lum * lum; n++;
                    if (px.r >= 0.998f && px.g >= 0.998f && px.b >= 0.998f) white++;
                }
                float mean = sum / Mathf.Max(1, n);
                float std = Mathf.Sqrt(Mathf.Max(0f, sumSq / Mathf.Max(1, n) - mean * mean));
                float whitePct = 100f * white / Mathf.Max(1, n);
                float bandPen = 0f;
                if (maxL < 0.85f) bandPen += (0.85f - maxL) * 8f;
                if (maxL > 0.95f) bandPen += (maxL - 0.95f) * 12f;
                float score = std * 8f - bandPen - whitePct * 2f;
                if (c.name.Contains("overcast_soil")) score += 0.20f;
                if (c.name.Contains("mud_road")) score += 0.10f;
                if (c.name.Contains("kloofendal")) score -= 0.05f;

                log.AppendLine($"| `{c.name}` | {std:F3} | {maxL:F3} | {mean:F3} | {whitePct:F2} | {exp:F3} | {score:F3} |");
                if (score > bestScore)
                {
                    bestScore = score;
                    bestName = c.name;
                    bestPath = c.path;
                    bestExp = exp;
                    bestRot = c.rotation;
                }
                Object.DestroyImmediate(tex);
            }

            Object.DestroyImmediate(go);

            if (bestPath != null)
            {
                var hdri = AssetDatabase.LoadAssetAtPath<Texture>(bestPath);
                ApplySky(sky, hdri, bestExp, bestRot);
            }

            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsInactive.Include))
            {
                if (l.type != LightType.Directional) continue;
                l.transform.rotation = Quaternion.Euler(SunEuler.x, SunEuler.y, 0f);
                // Keep warm dusty key; don't overdrive
                if (l.intensity < 1.2f) l.intensity = 1.55f;
                l.shadowStrength = Mathf.Max(l.shadowStrength, 0.75f);
                EditorUtility.SetDirty(l);
            }

            RenderSettings.skybox = sky;
            EditorUtility.SetDirty(sky);
            DynamicGI.UpdateEnvironment();
            File.WriteAllText(Path.GetFullPath($"{AbDir}/sky_ab_log.txt"), log.ToString());
            return (bestName ?? "none", bestExp, bestRot, log.ToString());
        }

        static void RebalanceExposure(float currentMean)
        {
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(VolumeProfilePath);
            var volume = Object.FindObjectsByType<Volume>(FindObjectsInactive.Include)
                .FirstOrDefault(v => v != null && v.isGlobal);
            if (profile == null && volume != null) profile = volume.sharedProfile;
            if (profile == null)
            {
                Debug.LogError("[SX] No volume profile.");
                return;
            }

            // Target mean 0.40. Approximate stops: ΔEV ≈ log2(target/current)
            float target = 0.40f;
            float deltaEv = 0f;
            if (currentMean > 0.01f)
                deltaEv = Mathf.Clamp(Mathf.Log(target / currentMean, 2f), -1.5f, 2.5f);

            if (!profile.TryGet(out ColorAdjustments color))
                color = profile.Add<ColorAdjustments>(true);
            float post = color.postExposure.value + deltaEv;
            // Keep warm dusty — don't go milky/cold
            post = Mathf.Clamp(post, -0.5f, 1.8f);
            color.postExposure.Override(post);

            // Everything except exposure comes from the single canonical grade.
            AaaUrpGradeUtil.ApplyCanonicalDustyGrade(profile, "AaaSkyExposurePass");

            // Soften fog wash so street isn't crushed orange mush
            if (RenderSettings.fogDensity > 0.0025f)
                RenderSettings.fogDensity = 0.0020f;
            RenderSettings.fogColor = new Color(0.70f, 0.64f, 0.52f);
            RenderSettings.ambientIntensity = Mathf.Clamp(RenderSettings.ambientIntensity, 0.55f, 0.85f);
            if (RenderSettings.ambientIntensity < 0.65f)
                RenderSettings.ambientIntensity = 0.70f;

            EditorUtility.SetDirty(profile);
            if (volume != null) EditorUtility.SetDirty(volume);
            DynamicGI.UpdateEnvironment();
            Debug.Log($"[SX] Exposure rebalance postEV→{post:F2} (delta={deltaEv:F2} from mean={currentMean:F3})");
        }

        struct StreetMetric
        {
            public float meanLum, nearBlackPct, clipPct, skyMax, whitePct;
            public string detail;
        }

        static StreetMetric MeasureStreet(string label)
        {
            var shots = new (string name, Vector3 pos, float yaw, float pitch)[]
            {
                ("EL_10", new Vector3(-13.58f, 1.70f, -44.34f), 11.3f, 2.4f),
                ("EL_02", new Vector3(0.57f, 1.70f, -9.30f), 10.1f, 0.2f),
                ("EL_14", new Vector3(49.92f, 1.70f, 2.33f), 138.5f, 4.8f),
            };
            var go = new GameObject("__SX_MeasCam");
            var cam = go.AddComponent<Camera>();
            cam.allowHDR = true;
            cam.fieldOfView = 72f;
            cam.nearClipPlane = 0.08f;
            cam.farClipPlane = 280f;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.enabled = false;
            var ud = cam.GetUniversalAdditionalCameraData();
            if (ud != null) { ud.renderPostProcessing = true; ud.antialiasing = AntialiasingMode.None; }

            float sumAll = 0f; int nAll = 0, nb = 0, clip = 0;
            float skyMax = 0f; int white = 0, skyN = 0;
            var sb = new StringBuilder();
            Directory.CreateDirectory(Path.GetFullPath("_research/pale_detect"));

            foreach (var (name, pos, yaw, pitch) in shots)
            {
                cam.transform.position = pos;
                cam.transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
                var tex = Grab(cam, 1280, 720);
                File.WriteAllBytes(Path.GetFullPath($"_research/pale_detect/{name}_{label}.png"), tex.EncodeToPNG());
                var px = tex.GetPixels32();
                float sum = 0f; int n = 0, localNb = 0;
                for (int i = 0; i < px.Length; i++)
                {
                    var c = px[i];
                    float lr = c.r / 255f, lg = c.g / 255f, lb = c.b / 255f;
                    float lum = 0.2126f * lr + 0.7152f * lg + 0.0722f * lb;
                    sum += lum; n++;
                    if (lum < 0.02f) { nb++; localNb++; }
                    if (lum > 0.98f) clip++;
                    int y = i / tex.width;
                    if (y > tex.height * 0.65f)
                    {
                        skyMax = Mathf.Max(skyMax, lum);
                        skyN++;
                        if (c.r >= 254 && c.g >= 254 && c.b >= 254) white++;
                    }
                }
                float mean = sum / Mathf.Max(1, n);
                sumAll += sum; nAll += n;
                sb.AppendLine($"{label}/{name} mean={mean:F4} nearBlack%={100f * localNb / n:F2}");
                Object.DestroyImmediate(tex);
            }
            Object.DestroyImmediate(go);

            var m = new StreetMetric
            {
                meanLum = sumAll / Mathf.Max(1, nAll),
                nearBlackPct = 100f * nb / Mathf.Max(1, nAll),
                clipPct = 100f * clip / Mathf.Max(1, nAll),
                skyMax = skyMax,
                whitePct = skyN > 0 ? 100f * white / skyN : 0f,
                detail = sb.ToString() + $"TOTAL mean={sumAll / Mathf.Max(1, nAll):F4} nearBlack%={100f * nb / Mathf.Max(1, nAll):F2} clip%={100f * clip / Mathf.Max(1, nAll):F2} skyMax={skyMax:F3} white%={(skyN > 0 ? 100f * white / skyN : 0f):F2}\n"
            };
            return m;
        }

        static void ApplySky(Material sky, Texture hdri, float exposure, float rotation)
        {
            if (hdri != null && sky.HasProperty("_MainTex")) sky.SetTexture("_MainTex", hdri);
            if (sky.HasProperty("_Exposure")) sky.SetFloat("_Exposure", exposure);
            if (sky.HasProperty("_Rotation")) sky.SetFloat("_Rotation", rotation);
            if (sky.HasProperty("_Tint")) sky.SetColor("_Tint", new Color(0.98f, 0.94f, 0.88f));
            if (sky.HasProperty("_ImageType")) sky.SetFloat("_ImageType", 0f);
            if (sky.HasProperty("_Mapping")) sky.SetFloat("_Mapping", 1f);
            RenderSettings.skybox = sky;
            EditorUtility.SetDirty(sky);
            DynamicGI.UpdateEnvironment();
        }

        static float TuneSkyExp(Material sky, Camera cam, float start)
        {
            float exp = start;
            for (int i = 0; i < 6; i++)
            {
                if (sky.HasProperty("_Exposure")) sky.SetFloat("_Exposure", exp);
                var tex = Grab(cam, 800, 450);
                float maxL = 0f; int white = 0, n = 0;
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
                if (maxL > 0.95f || white > 0) exp *= 0.92f;
                else if (maxL < 0.85f) exp *= 1.06f;
                else break;
                exp = Mathf.Clamp(exp, 0.18f, 1.25f);
            }
            if (sky.HasProperty("_Exposure")) sky.SetFloat("_Exposure", exp);
            EditorUtility.SetDirty(sky);
            return exp;
        }

        static Texture2D Grab(Camera cam, int w, int h)
        {
            var rtHdr = new RenderTexture(w, h, 24, RenderTextureFormat.DefaultHDR);
            var rtLdr = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
            cam.targetTexture = rtHdr;
            cam.Render();
            Graphics.Blit(rtHdr, rtLdr);
            RenderTexture.active = rtLdr;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            cam.targetTexture = null;
            RenderTexture.active = null;
            Object.DestroyImmediate(rtHdr);
            Object.DestroyImmediate(rtLdr);
            return tex;
        }
    }
}
#endif
