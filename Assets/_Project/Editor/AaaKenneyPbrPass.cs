#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace ArenaFps.Editor
{
    /// <summary>
    /// AAA Kenney PBR Pass — rematerializes CK_/Kenney buildings with ambientCG detail PBR,
    /// kills the brown brick sky enclosure, densifies mid-lane street props, softens lighting.
    /// Additive under KP_*; does not touch Match/TDM/HUD systems.
    /// Menu: Arena FPS / AAA Kenney PBR Pass
    /// </summary>
    public static class AaaKenneyPbrPass
    {
        const string ScenePath = "Assets/_Project/Scenes/Arena.unity";
        const string MatDir = "Assets/_Project/Art/Materials/Map";
        const string GenDir = "Assets/_Project/Art/Textures/Generated";
        const string BarrelPath = "Assets/_Project/Art/Models/Environment/Props/Barrel_01/Barrel_01_1k.fbx";
        const string SkyboxMatPath = "Assets/_Project/Settings/Lighting/Arena_AbandonedConstruction_Skybox.mat";
        const string HdriPreferred = "Assets/_Project/Resources/HDRI/abandoned_construction_4k.hdr";
        const string HdriFallback = "Assets/_Project/Art/Textures/HDRI/abandoned_construction_4k.hdr";
        const string HdriBakery = "Assets/_Project/Resources/HDRI/abandoned_bakery_4k.hdr";
        const string RootName = "KP_KenneyPbrRoot";

        // ambientCG / project textures
        const string ConcreteColor = "Assets/_Project/Art/Textures/Concrete/Concrete034_1K-JPG_Color.jpg";
        const string ConcreteNormal = "Assets/_Project/Art/Textures/Concrete/Concrete034_1K-JPG_NormalGL.jpg";
        const string ConcreteRough = "Assets/_Project/Art/Textures/Concrete/Concrete034_1K-JPG_Roughness.jpg";
        const string Concrete048 = "Assets/_Project/Art/Textures/Concrete/Concrete048_1K-JPG_Color.jpg";
        const string PlasterColor = "Assets/_Project/Resources/Textures/Plaster/Plaster001_Color.jpg";
        const string PlasterNormal = "Assets/_Project/Resources/Textures/Plaster/Plaster001_NormalGL.jpg";
        const string PlasterRough = "Assets/_Project/Resources/Textures/Plaster/Plaster001_Roughness.jpg";
        const string MetalColor = "Assets/_Project/Resources/Textures/Metal/Metal063_Color.jpg";
        const string MetalNormal = "Assets/_Project/Resources/Textures/Metal/Metal063_NormalGL.jpg";
        const string MetalRough = "Assets/_Project/Resources/Textures/Metal/Metal063_Roughness.jpg";
        const string AsphaltColor = "Assets/_Project/Resources/Textures/Asphalt/Asphalt033_Color.jpg";
        const string AsphaltNormal = "Assets/_Project/Resources/Textures/Asphalt/Asphalt033_NormalGL.jpg";
        const string AsphaltRough = "Assets/_Project/Resources/Textures/Asphalt/Asphalt033_Roughness.jpg";
        const string BrickColor = "Assets/_Project/Art/Textures/Brick/brick_4_diff_2k.jpg";
        const string KenneyColormap = "Assets/_Project/Art/Models/Environment/City/Kenney_Commercial/Models/FBX format/Textures/colormap.png";

        static readonly Dictionary<string, Material> Mats = new();

        [MenuItem("Arena FPS/AAA Kenney PBR Pass")]
        public static void Run()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.isPlaying = false;
                Debug.LogWarning("[AAA KenneyPBR] Exiting play mode; run again in edit mode.");
                return;
            }

            var scene = EditorSceneManager.GetActiveScene().path.EndsWith("Arena.unity")
                ? EditorSceneManager.GetActiveScene()
                : EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            var map = GameObject.Find("ThreeLaneMap");
            if (map == null)
            {
                Debug.LogError("[AAA KenneyPBR] ThreeLaneMap missing; aborting.");
                return;
            }

            EnsureFolders();
            ClearPrevious(map.transform);

            int remat = RematerializeKenneyBuildings(map.transform);
            SoftenEnclosureAndSky(map.transform);
            OpenSkyBehindMidLane(map.transform);
            int props = DensifyMidStreet(map.transform);
            SoftenLighting();
            ReframeCaptureCameras();
            DisableAaaCameras();

            try { SpawnArenaCombat.Run(); }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[AAA KenneyPBR] SpawnArenaCombat skipped: {ex.Message}");
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            DynamicGI.UpdateEnvironment();

            Debug.Log($"[AAA KenneyPBR] Done: remat={remat} props={props}. Sky open, mid densified, cams off, Arena saved.");
        }

        static void EnsureFolders()
        {
            if (!Directory.Exists(Path.GetFullPath(MatDir)))
                Directory.CreateDirectory(Path.GetFullPath(MatDir));
            if (!Directory.Exists(Path.GetFullPath(GenDir)))
                Directory.CreateDirectory(Path.GetFullPath(GenDir));
        }

        static void ClearPrevious(Transform map)
        {
            var doomed = new List<GameObject>();
            foreach (Transform child in map)
            {
                if (child.name == RootName || child.name.StartsWith("KP_"))
                    doomed.Add(child.gameObject);
            }

            foreach (var t in map.GetComponentsInChildren<Transform>(true))
            {
                if (t != map && t.name.StartsWith("KP_") && t.parent == map)
                    doomed.Add(t.gameObject);
            }

            foreach (var go in doomed)
            {
                if (go != null)
                    Object.DestroyImmediate(go);
            }
        }

        // ── Materials ─────────────────────────────────────────────────────────

        static void BuildMaterials()
        {
            Mats.Clear();

            // Bake grit into atlas (preserves Kenney UV0) — stronger than detail maps alone.
            string weatheredCommercial = BakeWeatheredAtlas(
                "KP_Colormap_Commercial_Weathered.png", KenneyColormap, ConcreteColor, desat: 0.35f, grit: 0.42f, warmPull: -0.04f);
            string weatheredIndustrial = BakeWeatheredAtlas(
                "KP_Colormap_Industrial_Weathered.png",
                FindFirstTexture(
                    "Assets/_Project/Art/Models/Environment/City/Kenney_Industrial/Models/FBX format/Textures/colormap.png",
                    KenneyColormap),
                MetalColor, desat: 0.40f, grit: 0.50f, warmPull: -0.06f);
            string weatheredSuburban = BakeWeatheredAtlas(
                "KP_Colormap_Suburban_Weathered.png",
                FindFirstTexture(
                    "Assets/_Project/Art/Models/Environment/City/Kenney_Suburban/Models/FBX format/Textures/colormap.png",
                    "Assets/_Project/Art/Models/Environment/City/Kenney_Suburban/Models/Textures/variation-b.png",
                    KenneyColormap),
                PlasterColor, desat: 0.28f, grit: 0.38f, warmPull: -0.02f);

            Mats["ck_commercial"] = UpsertKenneyAtlasMat(
                "KP_KenneyCommercial_PBR",
                weatheredCommercial,
                ConcreteColor, ConcreteNormal,
                new Color(0.90f, 0.89f, 0.86f, 1f),
                metallic: 0.02f, smoothness: 0.14f,
                detailTiling: 16f, detailAlbedoScale: 0.55f, detailNormalScale: 0.70f);

            Mats["ck_industrial"] = UpsertKenneyAtlasMat(
                "KP_KenneyIndustrial_PBR",
                weatheredIndustrial,
                MetalColor, MetalNormal,
                new Color(0.80f, 0.80f, 0.78f, 1f),
                metallic: 0.15f, smoothness: 0.18f,
                detailTiling: 12f, detailAlbedoScale: 0.45f, detailNormalScale: 0.75f);

            Mats["ck_suburban"] = UpsertKenneyAtlasMat(
                "KP_KenneySuburban_PBR",
                weatheredSuburban,
                PlasterColor, PlasterNormal,
                new Color(0.93f, 0.91f, 0.88f, 1f),
                metallic: 0.0f, smoothness: 0.13f,
                detailTiling: 14f, detailAlbedoScale: 0.48f, detailNormalScale: 0.55f);

            // Cool glass/concrete for distant towers if left visible.
            Mats["ck_sky"] = UpsertFullPbr("KP_KenneySkyscraper_Cool", Concrete048, ConcreteNormal, ConcreteRough,
                new Color(0.55f, 0.58f, 0.62f), 0.08f, 0.35f, 6f);

            Mats["glass"] = UpsertGlass("KP_GlassDark", new Color(0.04f, 0.07f, 0.09f, 0.55f), 0.05f, 0.72f);
            Mats["concrete"] = UpsertFullPbr("KP_Concrete034", ConcreteColor, ConcreteNormal, ConcreteRough,
                new Color(0.78f, 0.76f, 0.72f), 0f, 0.22f, 3.5f);
            Mats["plaster"] = UpsertFullPbr("KP_Plaster001", PlasterColor, PlasterNormal, PlasterRough,
                new Color(0.86f, 0.82f, 0.76f), 0f, 0.20f, 3.2f);
            Mats["metal"] = UpsertFullPbr("KP_Metal063", MetalColor, MetalNormal, MetalRough,
                new Color(0.55f, 0.56f, 0.54f), 0.75f, 0.35f, 2.4f);
            Mats["asphalt"] = UpsertFullPbr("KP_Asphalt033", AsphaltColor, AsphaltNormal, AsphaltRough,
                new Color(0.42f, 0.42f, 0.43f), 0f, 0.18f, 8f);
            Mats["brick"] = UpsertFullPbr("KP_Brick4", BrickColor, null, null,
                new Color(0.72f, 0.58f, 0.48f), 0f, 0.18f, 2.8f);
            Mats["sand"] = Solid("KP_Sandbag", new Color(0.58f, 0.49f, 0.34f), 0f, 0.14f);
            Mats["trash"] = Solid("KP_Trash", new Color(0.04f, 0.038f, 0.035f), 0f, 0.12f);
            Mats["warm"] = Solid("KP_WarmPractical", new Color(1f, 0.72f, 0.38f), 0f, 0.55f);
            Mats["dark"] = Solid("KP_DarkTrim", new Color(0.06f, 0.055f, 0.05f), 0.1f, 0.25f);

            // Prefer existing FD oil/crack decals when present.
            Mats["oil"] = AssetDatabase.LoadAssetAtPath<Material>($"{MatDir}/FD_OilDecal.mat")
                          ?? Solid("KP_OilDecal", new Color(0.02f, 0.018f, 0.015f, 0.85f), 0f, 0.55f);
            Mats["crack"] = AssetDatabase.LoadAssetAtPath<Material>($"{MatDir}/FD_CrackDecalMat.mat")
                            ?? Solid("KP_CrackDecal", new Color(0.15f, 0.14f, 0.13f, 0.75f), 0f, 0.12f);
            Mats["dirt"] = Solid("KP_FacadeDirt", new Color(0.22f, 0.18f, 0.14f, 0.55f), 0f, 0.10f);
        }

        static string FindFirstTexture(params string[] paths)
        {
            foreach (var p in paths)
            {
                if (!string.IsNullOrEmpty(p) && AssetDatabase.LoadAssetAtPath<Texture2D>(p) != null)
                    return p;
            }
            return paths[paths.Length - 1];
        }

        static Material UpsertKenneyAtlasMat(
            string name, string atlasPath, string detailAlbedoPath, string detailNormalPath,
            Color tint, float metallic, float smoothness,
            float detailTiling, float detailAlbedoScale, float detailNormalScale)
        {
            var path = $"{MatDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                mat = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(mat, path);
            }

            // Keep Kenney UV0 atlas intact on base map — do NOT retile atlas.
            var atlas = AssetDatabase.LoadAssetAtPath<Texture2D>(atlasPath);
            if (atlas != null)
            {
                mat.SetTexture("_BaseMap", atlas);
                mat.mainTextureScale = Vector2.one;
                mat.mainTextureOffset = Vector2.zero;
            }

            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", tint);
            else mat.color = tint;
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metallic);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
            if (mat.HasProperty("_BumpScale")) mat.SetFloat("_BumpScale", 0.35f);

            // Detail grit overlay (tiling) — preserves atlas color layout.
            var detailAlb = AssetDatabase.LoadAssetAtPath<Texture2D>(detailAlbedoPath);
            var detailNrm = AssetDatabase.LoadAssetAtPath<Texture2D>(detailNormalPath);
            if (detailAlb != null && mat.HasProperty("_DetailAlbedoMap"))
            {
                EnsureRepeat(detailAlbedoPath);
                mat.SetTexture("_DetailAlbedoMap", detailAlb);
                if (mat.HasProperty("_DetailAlbedoMapScale"))
                    mat.SetFloat("_DetailAlbedoMapScale", detailAlbedoScale);
                mat.EnableKeyword("_DETAIL_MULX2");
                // URP uses _DetailAlbedoMap_ST for tiling when available via SetTextureScale.
                mat.SetTextureScale("_DetailAlbedoMap", new Vector2(detailTiling, detailTiling));
            }

            if (detailNrm != null && mat.HasProperty("_DetailNormalMap"))
            {
                EnsureNormalImport(detailNormalPath);
                mat.SetTexture("_DetailNormalMap", detailNrm);
                if (mat.HasProperty("_DetailNormalMapScale"))
                    mat.SetFloat("_DetailNormalMapScale", detailNormalScale);
                mat.SetTextureScale("_DetailNormalMap", new Vector2(detailTiling, detailTiling));
                mat.EnableKeyword("_DETAIL_MULX2");
            }

            // Mild occlusion feel via ambient occlusion scalar if present.
            if (mat.HasProperty("_OcclusionStrength"))
                mat.SetFloat("_OcclusionStrength", 0.55f);

            // Micro-normal from concrete even though UVs don't match atlas — kills plastic flat.
            if (!string.IsNullOrEmpty(detailNormalPath) && mat.HasProperty("_BumpMap"))
            {
                var nrm = AssetDatabase.LoadAssetAtPath<Texture2D>(detailNormalPath);
                if (nrm != null)
                {
                    EnsureNormalImport(detailNormalPath);
                    mat.SetTexture("_BumpMap", nrm);
                    mat.EnableKeyword("_NORMALMAP");
                    if (mat.HasProperty("_BumpScale")) mat.SetFloat("_BumpScale", 0.55f);
                    mat.SetTextureScale("_BumpMap", new Vector2(detailTiling * 0.65f, detailTiling * 0.65f));
                }
            }

            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>
        /// Bake desaturated + grit-multiplied atlas so Kenney UV0 stays valid while killing plastic colormap.
        /// </summary>
        static string BakeWeatheredAtlas(string outName, string atlasPath, string gritPath, float desat, float grit, float warmPull)
        {
            var outPath = $"{GenDir}/{outName}";
            if (AssetDatabase.LoadAssetAtPath<Texture2D>(outPath) != null)
                return outPath;

            EnsureReadable(atlasPath);
            EnsureReadable(gritPath);
            var atlas = AssetDatabase.LoadAssetAtPath<Texture2D>(atlasPath);
            var gritTex = AssetDatabase.LoadAssetAtPath<Texture2D>(gritPath);
            if (atlas == null)
                return atlasPath;

            int w = atlas.width;
            int h = atlas.height;
            var src = atlas.GetPixels32();
            Color32[] gritPx = null;
            int gw = 1, gh = 1;
            if (gritTex != null && gritTex.isReadable)
            {
                gritPx = gritTex.GetPixels32();
                gw = gritTex.width;
                gh = gritTex.height;
            }

            var dst = new Color32[src.Length];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                var c = src[i];
                float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f;
                float lum = r * 0.299f + g * 0.587f + b * 0.114f;
                r = Mathf.Lerp(r, lum, desat);
                g = Mathf.Lerp(g, lum, desat);
                b = Mathf.Lerp(b, lum, desat);
                // Pull out of warm plastic / toy brick.
                r = Mathf.Clamp01(r + warmPull);
                b = Mathf.Clamp01(b - warmPull * 0.5f);

                if (gritPx != null)
                {
                    int gx = (x * gw / w) % gw;
                    int gy = (y * gh / h) % gh;
                    var gp = gritPx[gy * gw + gx];
                    float gm = (gp.r + gp.g + gp.b) / (3f * 255f);
                    gm = Mathf.Lerp(1f, gm * 1.15f, grit);
                    r *= gm; g *= gm; b *= gm;
                }

                // Slight edge darken on bright atlas cells (fake AO).
                if (lum > 0.55f)
                {
                    float ao = 1f - (lum - 0.55f) * 0.25f;
                    r *= ao; g *= ao; b *= ao;
                }

                dst[i] = new Color32(
                    (byte)Mathf.Clamp(Mathf.RoundToInt(r * 255f), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt(g * 255f), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt(b * 255f), 0, 255),
                    c.a);
            }

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true);
            tex.SetPixels32(dst);
            tex.Apply(true);
            File.WriteAllBytes(Path.GetFullPath(outPath), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(outPath);
            var imp = AssetImporter.GetAtPath(outPath) as TextureImporter;
            if (imp != null)
            {
                imp.wrapMode = TextureWrapMode.Clamp; // atlas must not tile
                imp.sRGBTexture = true;
                imp.maxTextureSize = 2048;
                imp.SaveAndReimport();
            }
            return outPath;
        }

        static Material UpsertFullPbr(string name, string colorPath, string normalPath, string roughPath,
            Color tint, float metallic, float smoothness, float tiling)
        {
            var path = $"{MatDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                mat = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(mat, path);
            }

            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", tint);
            else mat.color = tint;
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metallic);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);

            var color = AssetDatabase.LoadAssetAtPath<Texture2D>(colorPath);
            if (color != null)
            {
                EnsureRepeat(colorPath);
                mat.SetTexture("_BaseMap", color);
                mat.mainTextureScale = new Vector2(tiling, tiling);
            }

            if (!string.IsNullOrEmpty(normalPath))
            {
                var nrm = AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath);
                if (nrm != null && mat.HasProperty("_BumpMap"))
                {
                    EnsureNormalImport(normalPath);
                    mat.SetTexture("_BumpMap", nrm);
                    mat.EnableKeyword("_NORMALMAP");
                    if (mat.HasProperty("_BumpScale")) mat.SetFloat("_BumpScale", 1f);
                }
            }

            // Pack roughness→smoothness into metallic gloss alpha when roughness map exists.
            if (!string.IsNullOrEmpty(roughPath) && mat.HasProperty("_MetallicGlossMap"))
            {
                var packed = EnsureRoughnessPacked(name + "_Mask", roughPath, metallic);
                if (packed != null)
                {
                    mat.SetTexture("_MetallicGlossMap", packed);
                    mat.EnableKeyword("_METALLICSPECGLOSSMAP");
                    if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 1f);
                }
            }

            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Texture2D EnsureRoughnessPacked(string name, string roughPath, float metallic)
        {
            var outPath = $"{GenDir}/{name}.png";
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
            if (existing != null)
                return existing;

            var rough = AssetDatabase.LoadAssetAtPath<Texture2D>(roughPath);
            if (rough == null)
                return null;

            EnsureReadable(roughPath);
            rough = AssetDatabase.LoadAssetAtPath<Texture2D>(roughPath);
            if (rough == null || !rough.isReadable)
                return null;

            int w = rough.width;
            int h = rough.height;
            var src = rough.GetPixels32();
            var dst = new Color32[src.Length];
            byte met = (byte)Mathf.Clamp(Mathf.RoundToInt(metallic * 255f), 0, 255);
            for (int i = 0; i < src.Length; i++)
            {
                // ambientCG roughness: invert → smoothness in alpha; metallic in R.
                byte sm = (byte)(255 - src[i].r);
                dst[i] = new Color32(met, met, met, sm);
            }

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true);
            tex.SetPixels32(dst);
            tex.Apply(true);
            File.WriteAllBytes(Path.GetFullPath(outPath), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(outPath);
            EnsureRepeat(outPath);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
        }

        static Material UpsertGlass(string name, Color color, float metallic, float smoothness)
        {
            var path = $"{MatDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                mat = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(mat, path);
            }

            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            else mat.color = color;
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metallic);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
            if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f); // Transparent
            if (mat.HasProperty("_Blend")) mat.SetFloat("_Blend", 0f);
            if (mat.HasProperty("_SrcBlend")) mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (mat.HasProperty("_DstBlend")) mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 0f);
            mat.renderQueue = (int)RenderQueue.Transparent;
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.SetOverrideTag("RenderType", "Transparent");
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material Solid(string name, Color color, float metallic, float smoothness)
        {
            var path = $"{MatDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard")) { name = name };
                AssetDatabase.CreateAsset(mat, path);
            }
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            else mat.color = color;
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metallic);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static void EnsureRepeat(string texPath)
        {
            var importer = AssetImporter.GetAtPath(texPath) as TextureImporter;
            if (importer == null) return;
            bool dirty = false;
            if (importer.wrapMode != TextureWrapMode.Repeat) { importer.wrapMode = TextureWrapMode.Repeat; dirty = true; }
            if (importer.maxTextureSize < 2048) { importer.maxTextureSize = 2048; dirty = true; }
            if (dirty) importer.SaveAndReimport();
        }

        static void EnsureNormalImport(string texPath)
        {
            var importer = AssetImporter.GetAtPath(texPath) as TextureImporter;
            if (importer == null) return;
            bool dirty = false;
            if (importer.textureType != TextureImporterType.NormalMap)
            {
                importer.textureType = TextureImporterType.NormalMap;
                dirty = true;
            }
            if (importer.wrapMode != TextureWrapMode.Repeat) { importer.wrapMode = TextureWrapMode.Repeat; dirty = true; }
            if (dirty) importer.SaveAndReimport();
        }

        static void EnsureReadable(string texPath)
        {
            var importer = AssetImporter.GetAtPath(texPath) as TextureImporter;
            if (importer == null) return;
            if (!importer.isReadable)
            {
                importer.isReadable = true;
                importer.SaveAndReimport();
            }
        }

        // ── Rematerialize Kenney ──────────────────────────────────────────────

        static int RematerializeKenneyBuildings(Transform map)
        {
            BuildMaterials();

            int count = 0;
            foreach (var t in map.GetComponentsInChildren<Transform>(true))
            {
                if (!IsKenneyTarget(t))
                    continue;

                Material body = PickBodyMat(t.name);
                foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                {
                    if (IsGlassName(r.gameObject.name))
                    {
                        AssignAll(r, Mats["glass"]);
                        count++;
                        continue;
                    }

                    // Multi-submesh: swap glass-like slots if material name hints window.
                    var shared = r.sharedMaterials;
                    bool touched = false;
                    for (int i = 0; i < shared.Length; i++)
                    {
                        var m = shared[i];
                        string mn = m != null ? m.name.ToLowerInvariant() : "";
                        if (mn.Contains("glass") || mn.Contains("window") || mn.Contains("pane"))
                        {
                            shared[i] = Mats["glass"];
                            touched = true;
                        }
                        else
                        {
                            shared[i] = body;
                            touched = true;
                        }
                    }

                    if (touched)
                    {
                        r.sharedMaterials = shared;
                        count++;
                    }
                }

                // Facade wear decals + glass overlays on mid commercial storefronts.
                if (t.name.StartsWith("CK_Mid_"))
                    StampFacadeOverlays(map, t);
            }

            // Ground asphalt upgrade if Ground exists.
            var ground = FindNamed(map, "Ground");
            if (ground != null)
            {
                var gr = ground.GetComponent<Renderer>();
                if (gr != null && Mats["asphalt"] != null)
                {
                    gr.sharedMaterial = Mats["asphalt"];
                    count++;
                }
            }

            return count;
        }

        static bool IsKenneyTarget(Transform t)
        {
            if (t.name.StartsWith("CK_")) return true;
            if (t.name.Contains("CityKit")) return true;
            // Prefab instances under CK root whose names are raw FBX names.
            var p = t;
            while (p != null)
            {
                if (p.name == "CK_CityKitRoot" || p.name.StartsWith("CK_"))
                    return t.name.StartsWith("CK_") || t.parent != null && t.parent.name.StartsWith("CK_");
                p = p.parent;
            }
            return false;
        }

        static Material PickBodyMat(string name)
        {
            if (name.Contains("Sky") || name.Contains("skyscraper"))
                return Mats["ck_sky"];
            if (name.StartsWith("CK_Ind_") || name.Contains("Chimney"))
                return Mats["ck_industrial"];
            if (name.StartsWith("CK_Sub_"))
                return Mats["ck_suburban"];
            if (name.StartsWith("CK_Awning") || name.StartsWith("CK_Overhang") || name.StartsWith("CK_Parasol") || name.StartsWith("CK_Detail"))
                return Mats["ck_commercial"];
            return Mats["ck_commercial"];
        }

        static bool IsGlassName(string n)
        {
            n = n.ToLowerInvariant();
            return n.Contains("window") || n.Contains("glass") || n.Contains("pane") || n.Contains("glazing");
        }

        static void AssignAll(Renderer r, Material mat)
        {
            var arr = r.sharedMaterials;
            for (int i = 0; i < arr.Length; i++)
                arr[i] = mat;
            r.sharedMaterials = arr;
        }

        static void StampFacadeOverlays(Transform map, Transform building)
        {
            var renderers = building.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return;

            var bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);

            bool westRow = building.position.x < 0f;
            float faceX = westRow ? bounds.max.x + 0.04f : bounds.min.x - 0.04f;
            Vector3 outward = westRow ? Vector3.right : Vector3.left;

            // Dark glass storefront bank — additive, does not break Kenney UVs.
            float z0 = bounds.center.z;
            float halfW = Mathf.Min(bounds.size.z * 0.38f, 2.4f);
            for (int i = 0; i < 3; i++)
            {
                float along = (i - 1) * (halfW * 0.85f);
                float h = 1.15f + (i == 1 ? 0.25f : 0f);
                float y = 2.55f + (i == 1 ? 0.1f : 0f);
                var size = westRow
                    ? new Vector3(0.04f, h, halfW * 0.55f)
                    : new Vector3(0.04f, h, halfW * 0.55f);
                Box(map, $"KP_Glass_{building.name}_{i}",
                    new Vector3(faceX, y, z0 + along), size, Mats["glass"], false, 0f);

                // Dirt band under windows.
                Box(map, $"KP_Dirt_{building.name}_{i}",
                    new Vector3(faceX + outward.x * 0.01f, 1.35f, z0 + along),
                    westRow ? new Vector3(0.03f, 0.55f, halfW * 0.5f) : new Vector3(0.03f, 0.55f, halfW * 0.5f),
                    Mats["dirt"], false, 0f);
            }

            // Upper grit stripe.
            Box(map, $"KP_Wear_{building.name}",
                new Vector3(faceX, bounds.max.y - 0.45f, z0),
                westRow ? new Vector3(0.03f, 0.35f, bounds.size.z * 0.7f) : new Vector3(0.03f, 0.35f, bounds.size.z * 0.7f),
                Mats["dirt"], false, 0f);
        }

        // ── Enclosure / sky ───────────────────────────────────────────────────

        static void SoftenEnclosureAndSky(Transform map)
        {
            // Lower / open perimeter so HDRI reads instead of indoor warehouse brick.
            SoftenWall(FindNamed(map, "Wall_West"), yScale: 3.2f, yCenter: 1.6f);
            SoftenWall(FindNamed(map, "Wall_East"), yScale: 3.2f, yCenter: 1.6f);
            SoftenWall(FindNamed(map, "Wall_South"), yScale: 2.6f, yCenter: 1.3f);
            SoftenWall(FindNamed(map, "Wall_North"), yScale: 2.6f, yCenter: 1.3f);

            // Hide / disable caps + decorative panels that fill the upper sky.
            foreach (var t in map.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name;
                if (n.EndsWith("_Cap") || n.Contains("Wall_South_Panel") || n.Contains("Wall_North_Panel")
                    || n.Contains("Wall_West_Panel") || n.Contains("Wall_East_Panel"))
                {
                    foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                        r.enabled = false;
                    foreach (var c in t.GetComponentsInChildren<Collider>(true))
                        c.enabled = false;
                }
            }

            // Rematerialize remaining walls to less toy brick — plaster/concrete, not red brick.
            foreach (var name in new[] { "Wall_West", "Wall_East", "Wall_South", "Wall_North" })
            {
                var w = FindNamed(map, name);
                if (w == null) continue;
                var r = w.GetComponent<Renderer>();
                if (r != null)
                    r.sharedMaterial = name.Contains("West") || name.Contains("East")
                        ? Mats["concrete"]
                        : Mats["plaster"];
            }

            // Mid PB masses hidden by CityKitSwap — keep renderers off AND strip colliders (no invisible walls).
            foreach (var t in map.GetComponentsInChildren<Transform>(true))
            {
                if (!t.name.StartsWith("PB_Building_Mid_") && !t.name.StartsWith("Bldg_")) continue;
                bool midish = t.name.StartsWith("PB_Building_Mid_")
                              || t.name.Contains("Bank") || t.name.Contains("Shoes")
                              || t.name.Contains("Baskets") || t.name.Contains("TopBottom")
                              || t.name.Contains("Deli") || t.name.Contains("Spices");
                if (!midish) continue;
                // Only re-hide if already hidden by a prior pass (renderer off) — strip orphan colliders.
                foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                {
                    if (!r.enabled)
                    {
                        foreach (var c in r.GetComponents<Collider>())
                            Object.DestroyImmediate(c);
                    }
                }
            }

            ApplyOutdoorSky();
        }

        /// <summary>
        /// Kenney skyscrapers + tall brick side masses fill eye frustum with brown — hide/remat them so HDRI reads.
        /// Keep colliders where present; only kill visuals that create the warehouse enclosure.
        /// </summary>
        static void OpenSkyBehindMidLane(Transform map)
        {
            foreach (var t in map.GetComponentsInChildren<Transform>(true))
            {
                bool skyTower = t.name.StartsWith("CK_Sky_");
                bool tallBrickMass =
                    t.name.Contains("PB_Building_East_Mid_Offices") ||
                    t.name.Contains("PB_Building_West_North_Hotel") ||
                    t.name.Contains("P2_EastMidFacade") ||
                    t.name.Contains("P2_WestNorthFacade") ||
                    t.name.Contains("P2_WestSouthFacade") ||
                    t.name.Contains("P2_EastSouthFacade");

                if (!skyTower && !tallBrickMass)
                    continue;

                if (skyTower)
                {
                    // Prefer open sky: disable renderers on background towers AND strip colliders.
                    foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                        r.enabled = false;
                    foreach (var c in t.GetComponentsInChildren<Collider>(true))
                        Object.DestroyImmediate(c);
                    continue;
                }

                // Side brick masses: remat to cool concrete / plaster and slightly lower if very tall.
                foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                {
                    if (!r.enabled) continue;
                    if (r.bounds.max.y > 8f && Mats.ContainsKey("concrete"))
                        AssignAll(r, Mats["concrete"]);
                    else if (Mats.ContainsKey("plaster"))
                        AssignAll(r, Mats["plaster"]);
                }
            }

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.55f, 0.62f, 0.72f);
            RenderSettings.ambientEquatorColor = new Color(0.38f, 0.36f, 0.34f);
            RenderSettings.ambientGroundColor = new Color(0.16f, 0.15f, 0.14f);
            RenderSettings.ambientIntensity = 1.0f;
            DynamicGI.UpdateEnvironment();
        }

        static void SoftenWall(Transform wall, float yScale, float yCenter)
        {
            if (wall == null) return;
            var p = wall.position;
            var s = wall.localScale;
            wall.position = new Vector3(p.x, yCenter, p.z);
            wall.localScale = new Vector3(s.x, yScale, s.z);
            // Keep colliders in sync for SpawnArenaCombat / nav.
            var box = wall.GetComponent<BoxCollider>();
            if (box != null)
            {
                box.center = Vector3.zero;
                box.size = Vector3.one;
            }
        }

        /// <summary>
        /// abandoned_construction HDRI reads as brown brick warehouse in mid-lane FOV.
        /// Prefer cool procedural outdoor sky.
        /// </summary>
        static void ApplyOutdoorSky()
        {
            const string procPath = "Assets/_Project/Settings/Lighting/Arena_OutdoorProcedural_Skybox.mat";
            var sky = AssetDatabase.LoadAssetAtPath<Material>(procPath);
            if (sky == null)
            {
                var shader = Shader.Find("Skybox/Procedural") ?? Shader.Find("Skybox/Panoramic");
                if (shader == null) return;
                sky = new Material(shader) { name = "Arena_OutdoorProcedural_Skybox" };
                AssetDatabase.CreateAsset(sky, procPath);
            }

            if (sky.shader != null && sky.shader.name.Contains("Procedural"))
            {
                if (sky.HasProperty("_SunSize")) sky.SetFloat("_SunSize", 0.04f);
                if (sky.HasProperty("_SunSizeConvergence")) sky.SetFloat("_SunSizeConvergence", 5f);
                if (sky.HasProperty("_AtmosphereThickness")) sky.SetFloat("_AtmosphereThickness", 0.85f);
                if (sky.HasProperty("_SkyTint")) sky.SetColor("_SkyTint", new Color(0.55f, 0.62f, 0.72f));
                if (sky.HasProperty("_GroundColor")) sky.SetColor("_GroundColor", new Color(0.22f, 0.21f, 0.20f));
                if (sky.HasProperty("_Exposure")) sky.SetFloat("_Exposure", 1.15f);
            }
            else
            {
                var hdri = AssetDatabase.LoadAssetAtPath<Texture>(HdriBakery)
                           ?? AssetDatabase.LoadAssetAtPath<Texture>(HdriPreferred)
                           ?? AssetDatabase.LoadAssetAtPath<Texture>(HdriFallback);
                if (hdri != null && sky.HasProperty("_MainTex"))
                    sky.SetTexture("_MainTex", hdri);
                if (sky.HasProperty("_Exposure")) sky.SetFloat("_Exposure", 0.55f);
                if (sky.HasProperty("_Rotation")) sky.SetFloat("_Rotation", 90f);
                if (sky.HasProperty("_Tint")) sky.SetColor("_Tint", new Color(0.65f, 0.72f, 0.85f));
            }

            EditorUtility.SetDirty(sky);
            RenderSettings.skybox = sky;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
            RenderSettings.reflectionIntensity = 0.45f;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.52f, 0.60f, 0.72f);
            RenderSettings.ambientEquatorColor = new Color(0.36f, 0.34f, 0.32f);
            RenderSettings.ambientGroundColor = new Color(0.14f, 0.13f, 0.12f);
            RenderSettings.ambientIntensity = 1.0f;

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.0085f;
            RenderSettings.fogColor = new Color(0.62f, 0.66f, 0.72f, 1f);
        }

        // ── Street densify ────────────────────────────────────────────────────

        static int DensifyMidStreet(Transform map)
        {
            var root = new GameObject(RootName);
            root.isStatic = true;
            root.transform.SetParent(map, false);
            var rt = root.transform;
            int n = 0;

            // Keep mid lane width clear: |x| < 1.6 for walkable. Props at |x| ≈ 1.7–2.3 sidewalk.
            n += PlaceBarrel(rt, "KP_Barrel_A", new Vector3(-2.05f, 0f, -16.5f), 12f);
            n += PlaceBarrel(rt, "KP_Barrel_B", new Vector3(2.15f, 0f, -11.2f), -25f);
            n += PlaceBarrel(rt, "KP_Barrel_C", new Vector3(-2.1f, 0f, -3.5f), 40f);
            n += PlaceBarrel(rt, "KP_Barrel_D", new Vector3(2.05f, 0f, 5.8f), -10f);
            n += PlaceBarrel(rt, "KP_Barrel_E", new Vector3(-2.2f, 0f, 12.4f), 70f);
            n += PlaceBarrel(rt, "KP_Barrel_F", new Vector3(2.1f, 0f, 18.0f), -55f);

            n += SandbagPile(rt, "KP_Sandbags_S", new Vector3(-2.0f, 0f, -20.5f), 8f);
            n += SandbagPile(rt, "KP_Sandbags_N", new Vector3(2.05f, 0f, 9.5f), -12f);
            n += SandbagPile(rt, "KP_Sandbags_Mid", new Vector3(-1.95f, 0f, 2.2f), 5f);

            n += TrashPile(rt, "KP_Trash_W", new Vector3(-2.25f, 0f, -8.8f));
            n += TrashPile(rt, "KP_Trash_E", new Vector3(2.2f, 0f, 14.5f));

            // Oil / crack decals along mid asphalt — thin, no blockers.
            n += Decal(rt, "KP_Oil_A", new Vector3(0.4f, 0.04f, -15f), new Vector3(1.8f, 0.02f, 1.1f), Mats["oil"], 18f);
            n += Decal(rt, "KP_Oil_B", new Vector3(-0.3f, 0.04f, -6.5f), new Vector3(2.2f, 0.02f, 0.9f), Mats["oil"], -12f);
            n += Decal(rt, "KP_Oil_C", new Vector3(0.2f, 0.04f, 4.0f), new Vector3(1.6f, 0.02f, 1.3f), Mats["oil"], 35f);
            n += Decal(rt, "KP_Crack_A", new Vector3(-0.6f, 0.035f, -12f), new Vector3(0.9f, 0.018f, 2.4f), Mats["crack"], 8f);
            n += Decal(rt, "KP_Crack_B", new Vector3(0.5f, 0.035f, 1.5f), new Vector3(1.1f, 0.018f, 2.8f), Mats["crack"], -22f);
            n += Decal(rt, "KP_Crack_C", new Vector3(-0.2f, 0.035f, 11f), new Vector3(0.8f, 0.018f, 2.2f), Mats["crack"], 40f);

            // Warm practicals — keep soft; previous 2.4 intensity blew out Kenney plaster.
            WarmLight(rt, "KP_Practical_S", new Vector3(-1.8f, 3.6f, -14f), new Color(1f, 0.72f, 0.42f), 1.15f, 9f);
            WarmLight(rt, "KP_Practical_M", new Vector3(1.9f, 3.4f, -2f), new Color(1f, 0.75f, 0.48f), 1.05f, 8.5f);
            WarmLight(rt, "KP_Practical_N", new Vector3(-1.7f, 3.7f, 13f), new Color(1f, 0.70f, 0.40f), 1.10f, 9f);

            // More sidewalk clutter visible from eye cam (lane half-width stays ~1.5m clear).
            n += PlaceBarrel(rt, "KP_Barrel_G", new Vector3(1.85f, 0f, -17.8f), 30f);
            n += PlaceBarrel(rt, "KP_Barrel_H", new Vector3(-1.9f, 0f, -13.2f), -40f);
            n += SandbagPile(rt, "KP_Sandbags_Eye", new Vector3(1.9f, 0f, -15.8f), -6f);

            return n;
        }

        static int PlaceBarrel(Transform parent, string name, Vector3 pos, float yaw)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(BarrelPath);
            if (prefab != null)
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                if (go == null) go = Object.Instantiate(prefab);
                go.name = name;
                go.transform.SetParent(parent, false);
                go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
                go.transform.localScale = Vector3.one;

                // PolyHaven Barrel_01 imports ~cm-scale — fit to ~0.95m drum height.
                FitUniformHeight(go, 0.95f);
                // Snap base to ground.
                var rs = go.GetComponentsInChildren<Renderer>();
                if (rs.Length > 0)
                {
                    var b = rs[0].bounds;
                    for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
                    go.transform.position += new Vector3(0f, -b.min.y, 0f);
                    go.transform.position = new Vector3(pos.x, go.transform.position.y, pos.z);
                }

                var barrelMat = AssetDatabase.LoadAssetAtPath<Material>($"{MatDir}/FD_BarrelPolyHaven.mat") ?? Mats["metal"];
                foreach (var r in go.GetComponentsInChildren<Renderer>())
                    r.sharedMaterial = barrelMat;
                // Non-blocking prop: remove mesh colliders so lane width stays clear for nav.
                foreach (var c in go.GetComponentsInChildren<Collider>())
                    Object.DestroyImmediate(c);
                SetStatic(go);
                return 1;
            }

            // Fallback primitive drum.
            var drum = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            drum.name = name;
            drum.transform.SetParent(parent, false);
            drum.transform.SetPositionAndRotation(pos + Vector3.up * 0.48f, Quaternion.Euler(0f, yaw, 0f));
            drum.transform.localScale = new Vector3(0.58f, 0.48f, 0.58f);
            var mr = drum.GetComponent<MeshRenderer>();
            if (mr != null) mr.sharedMaterial = Mats["metal"];
            var col = drum.GetComponent<Collider>();
            if (col != null) Object.DestroyImmediate(col);
            SetStatic(drum);
            return 1;
        }

        static void FitUniformHeight(GameObject go, float targetHeight)
        {
            go.transform.localScale = Vector3.one;
            var rs = go.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return;
            var b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            float h = b.size.y;
            if (h < 1e-4f) h = 0.01f;
            float s = targetHeight / h;
            go.transform.localScale = Vector3.one * s;
        }

        static int SandbagPile(Transform parent, string name, Vector3 pos, float yaw)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, false);
            root.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
            int n = 0;
            for (int row = 0; row < 2; row++)
            for (int i = 0; i < 4; i++)
            {
                Box(root.transform, $"Bag_{row}_{i}",
                    new Vector3((i - 1.5f) * 0.55f + row * 0.12f, 0.18f + row * 0.22f, 0f),
                    new Vector3(0.52f, 0.22f, 0.40f), Mats["sand"], true, 0f);
                n++;
            }
            SetStatic(root);
            return n;
        }

        static int TrashPile(Transform parent, string name, Vector3 pos)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, false);
            root.transform.position = pos;
            int n = 0;
            for (int i = 0; i < 6; i++)
            {
                float x = ((i * 37) % 100) / 100f * 1.2f - 0.6f;
                float z = ((i * 53) % 100) / 100f * 1.0f - 0.5f;
                Box(root.transform, $"Bag_{i}", new Vector3(x, 0.2f, z),
                    new Vector3(0.42f, 0.38f, 0.42f), Mats["trash"], true, i * 17f);
                n++;
            }
            SetStatic(root);
            return n;
        }

        static int Decal(Transform parent, string name, Vector3 pos, Vector3 scale, Material mat, float yaw)
        {
            Box(parent, name, pos, scale, mat, false, yaw);
            return 1;
        }

        static void WarmLight(Transform parent, string name, Vector3 pos, Color color, float intensity, float range)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = color;
            l.intensity = intensity;
            l.range = range;
            l.shadows = LightShadows.None;
            l.renderMode = LightRenderMode.ForcePixel;
        }

        // ── Lighting ──────────────────────────────────────────────────────────

        static void SoftenLighting()
        {
            var lights = Object.FindObjectsByType<Light>(FindObjectsInactive.Include);
            Light sun = null;
            foreach (var l in lights)
            {
                if (l.type == LightType.Directional)
                {
                    sun = l;
                    break;
                }
            }

            if (sun != null)
            {
                sun.color = new Color(1f, 0.88f, 0.72f, 1f);
                sun.intensity = 1.35f;
                sun.shadows = LightShadows.Soft;
                sun.shadowStrength = 0.50f;
                sun.bounceIntensity = 0.85f;
                sun.transform.rotation = Quaternion.Euler(42f, -35f, 0f);
                EditorUtility.SetDirty(sun);
            }

            // Soften global volume fog if present (don't nuke the profile).
            var volumes = Object.FindObjectsByType<UnityEngine.Rendering.Volume>(FindObjectsInactive.Include);
            foreach (var v in volumes)
            {
                if (!v.isGlobal || v.sharedProfile == null) continue;
                // Leave bloom/tonemap; ambient already set via RenderSettings.
                EditorUtility.SetDirty(v);
            }
        }

        // ── Cameras ───────────────────────────────────────────────────────────

        static void ReframeCaptureCameras()
        {
            SetCam("AAA_EyeLevel_Camera", new Vector3(0f, 1.68f, -18.8f), new Vector3(0f, 2.4f, -4.5f), 62f);
            SetCam("AAA_MidLane_Camera", new Vector3(0.2f, 2.15f, -16.5f), new Vector3(0f, 2.8f, 7f), 54f);
            SetCam("AAA_Aerial_Camera", new Vector3(0f, 48f, -4f), new Vector3(0f, 0f, 4f), 48f);
        }

        static void DisableAaaCameras()
        {
            foreach (var name in new[] { "AAA_EyeLevel_Camera", "AAA_MidLane_Camera", "AAA_Aerial_Camera" })
            {
                var go = GameObject.Find(name);
                if (go == null) continue;
                var cam = go.GetComponent<Camera>();
                if (cam != null) cam.enabled = false;
            }
        }

        static void SetCam(string name, Vector3 pos, Vector3 lookAt, float fov)
        {
            var go = GameObject.Find(name);
            if (go == null)
            {
                var rig = GameObject.Find("__AaaCaptureRig") ?? new GameObject("__AaaCaptureRig");
                go = new GameObject(name);
                go.transform.SetParent(rig.transform, true);
                go.AddComponent<Camera>();
            }

            go.transform.position = pos;
            go.transform.rotation = Quaternion.LookRotation((lookAt - pos).normalized, Vector3.up);
            var cam = go.GetComponent<Camera>();
            if (cam == null) cam = go.AddComponent<Camera>();
            cam.fieldOfView = fov;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 280f;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.allowHDR = true;
            cam.enabled = false;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        static Transform FindNamed(Transform map, string name)
        {
            foreach (var t in map.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == name)
                    return t;
            }
            return null;
        }

        static GameObject Box(Transform parent, string name, Vector3 localPos, Vector3 scale, Material mat, bool collider, float yaw)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            go.transform.localScale = scale;
            go.isStatic = true;
            var r = go.GetComponent<MeshRenderer>();
            if (r != null && mat != null) r.sharedMaterial = mat;
            if (!collider)
            {
                var c = go.GetComponent<Collider>();
                if (c != null) Object.DestroyImmediate(c);
            }
            return go;
        }

        static void SetStatic(GameObject go)
        {
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.isStatic = true;
                GameObjectUtility.SetStaticEditorFlags(t.gameObject,
                    StaticEditorFlags.BatchingStatic |
                    StaticEditorFlags.OccludeeStatic |
                    StaticEditorFlags.OccluderStatic);
            }
        }
    }
}
#endif
