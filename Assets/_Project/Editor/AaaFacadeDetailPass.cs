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
    /// Focused eye-level COD blind-test pass: deep mid-lane facade architecture,
    /// blended ground/wall decals, authored barrel prop, softer lighting.
    /// Additive under FD_* — does not touch Match/TDM/HUD.
    /// </summary>
    public static class AaaFacadeDetailPass
    {
        const string Gen = "Assets/_Project/Art/Textures/Generated";
        const string MatDir = "Assets/_Project/Art/Materials/Map";
        const string BarrelPath = "Assets/_Project/Art/Models/Environment/Props/Barrel_01/Barrel_01_1k.fbx";

        static readonly Dictionary<string, Material> Mats = new();

        [MenuItem("Arena FPS/AAA Facade Detail Pass")]
        public static void Run()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.isPlaying = false;
                Debug.LogWarning("[AAA Facade] Exiting play mode; run again in edit mode.");
                return;
            }

            var map = GameObject.Find("ThreeLaneMap");
            if (map == null)
            {
                Debug.LogError("[AAA Facade] ThreeLaneMap missing; aborting.");
                return;
            }

            EnsureFolders();
            ClearPrevious(map.transform);
            BuildDecalTextures();
            BuildMaterials();
            BuildMidLaneFacades(map.transform);
            BuildWestEastSideFacades(map.transform);
            BuildGroundAndWallDecals(map.transform);
            PlaceAuthoredBarrels(map.transform);
            BoostEyeReadableDepth(map.transform);
            SoftenLighting();
            ReframeAndDisableCameras();

            try { SpawnArenaCombat.Run(); }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[AAA Facade] SpawnArenaCombat skipped: {ex.Message}");
            }

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
            AssetDatabase.SaveAssets();
            Debug.Log("[AAA Facade] Detail pass complete: mid facades, decals, barrels, lighting, cameras disabled.");
        }

        static void EnsureFolders()
        {
            if (!Directory.Exists(Path.GetFullPath(Gen)))
                Directory.CreateDirectory(Path.GetFullPath(Gen));
            if (!Directory.Exists(Path.GetFullPath(MatDir)))
                Directory.CreateDirectory(Path.GetFullPath(MatDir));
        }

        static void ClearPrevious(Transform map)
        {
            var doomed = new List<GameObject>();
            foreach (Transform child in map)
            {
                if (child.name.StartsWith("FD_"))
                    doomed.Add(child.gameObject);
            }
            foreach (var go in doomed)
                Object.DestroyImmediate(go);
        }

        // ── textures ──────────────────────────────────────────────────────────

        static void BuildDecalTextures()
        {
            WriteOilStain("FD_OilStain.png", 256);
            WriteCrackDecal("FD_CrackDecal.png", 256);
            WriteGraffitiAlpha("FD_GraffitiAlpha.png", 256);
            WritePosterWeathered("FD_PosterWeathered.png", 256);
            EnsureNormal("FD_Brick_Normal.png", 41, 0.32f, TexKind.Brick);
            EnsureNormal("FD_Concrete_Normal.png", 27, 0.20f, TexKind.Concrete);
            EnsureNormal("FD_Asphalt_Normal.png", 13, 0.26f, TexKind.Asphalt);
        }

        enum TexKind { Brick, Concrete, Asphalt }

        static void WriteOilStain(string file, int size)
        {
            var path = $"{Gen}/{file}";
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size * 2f - 1f;
                float v = (y + 0.5f) / size * 2f - 1f;
                float r = Mathf.Sqrt(u * u * 0.7f + v * v * 1.4f);
                float n = Mathf.PerlinNoise(x * 0.07f, y * 0.07f);
                float a = Mathf.Clamp01(1.15f - r * 1.5f - n * 0.35f);
                a *= a;
                float g = 0.02f + n * 0.04f;
                tex.SetPixel(x, y, new Color(g * 0.7f, g * 0.65f, g * 0.5f, Mathf.Clamp01(a * 1.15f)));
            }
            SaveTex(tex, path, true);
        }

        static void WriteCrackDecal(string file, int size)
        {
            var path = $"{Gen}/{file}";
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float edge = Mathf.Min(x, y, size - 1 - x, size - 1 - y) / (size * 0.5f);
                float fade = Mathf.SmoothStep(0f, 1f, edge);
                // Branching crack via distance to a wavy polyline.
                float cx = size * 0.5f + Mathf.Sin(y * 0.08f) * 18f + Mathf.Sin(y * 0.021f) * 30f;
                float d = Mathf.Abs(x - cx);
                float branch = Mathf.Abs(x - (cx + Mathf.Sin(y * 0.13f + 2f) * 40f));
                float crack = Mathf.Min(d, branch);
                float a = Mathf.Clamp01(1f - crack / 2.2f) * fade * 0.92f;
                float shade = 0.08f + Mathf.PerlinNoise(x * 0.2f, y * 0.2f) * 0.06f;
                tex.SetPixel(x, y, new Color(shade, shade * 0.95f, shade * 0.9f, a));
            }
            SaveTex(tex, path, true);
        }

        static void WriteGraffitiAlpha(string file, int size)
        {
            var path = $"{Gen}/{file}";
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = x / (float)size;
                float v = y / (float)size;
                float margin = Mathf.Min(u, v, 1f - u, 1f - v);
                float edgeFade = Mathf.SmoothStep(0f, 0.08f, margin);
                // Stencil letter-ish blobs + spray noise.
                float tag = TagMask(u, v);
                float spray = Mathf.PerlinNoise(x * 0.11f + 3f, y * 0.11f) * 0.35f;
                float a = Mathf.Clamp01(tag + spray - 0.18f) * edgeFade * 0.9f;
                Color c = TagColor(u, v);
                c.a = a;
                tex.SetPixel(x, y, c);
            }
            SaveTex(tex, path, true);
        }

        static float TagMask(float u, float v)
        {
            // Rough "X / bar / circle" shapes so it reads as street art, not a white card.
            float d1 = Mathf.Abs((u - 0.35f) - (v - 0.3f));
            float d2 = Mathf.Abs((u - 0.35f) + (v - 0.3f) - 0.5f);
            float xMark = Mathf.Clamp01(1f - Mathf.Min(d1, d2) * 18f);
            float bar = Mathf.Clamp01(1f - Mathf.Abs(v - 0.62f) * 22f) * Mathf.Clamp01(1f - Mathf.Abs(u - 0.55f) * 4f);
            float circ = Mathf.Clamp01(1f - Mathf.Abs(Mathf.Sqrt((u - 0.72f) * (u - 0.72f) + (v - 0.38f) * (v - 0.38f)) - 0.12f) * 30f);
            return Mathf.Max(xMark, Mathf.Max(bar, circ));
        }

        static Color TagColor(float u, float v)
        {
            if (v > 0.55f) return new Color(0.95f, 0.82f, 0.12f);
            if (u > 0.6f) return new Color(0.15f, 0.75f, 0.95f);
            return new Color(0.92f, 0.18f, 0.35f);
        }

        static void WritePosterWeathered(string file, int size)
        {
            var path = $"{Gen}/{file}";
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = x / (float)size;
                float v = y / (float)size;
                // Printed poster look without needing a readable source texture.
                Color c = Color.Lerp(new Color(0.42f, 0.18f, 0.14f), new Color(0.72f, 0.55f, 0.28f),
                    Mathf.PerlinNoise(u * 4f, v * 3f));
                if (v > 0.55f && v < 0.72f && u > 0.15f && u < 0.85f)
                    c = new Color(0.88f, 0.82f, 0.62f); // banner strip
                if (Mathf.Abs(u - 0.5f) < 0.18f && Mathf.Abs(v - 0.38f) < 0.14f)
                    c = new Color(0.15f, 0.16f, 0.18f); // silhouette block
                float margin = Mathf.Min(u, v, 1f - u, 1f - v);
                float tear = Mathf.PerlinNoise(x * 0.05f, y * 0.09f);
                float a = Mathf.SmoothStep(0f, 0.06f, margin);
                if (tear > 0.72f && (u < 0.12f || v > 0.88f)) a *= 0.15f;
                a *= 0.78f + tear * 0.15f;
                c *= new Color(0.85f, 0.78f, 0.68f);
                c.a = a;
                tex.SetPixel(x, y, c);
            }
            SaveTex(tex, path, true);
        }

        static void EnsureNormal(string file, int seed, float strength, TexKind kind)
        {
            var path = $"{Gen}/{file}";
            if (AssetDatabase.LoadAssetAtPath<Texture2D>(path) != null) return;
            var tex = new Texture2D(256, 256, TextureFormat.RGBA32, false, true);
            for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
            {
                float h = Height(x, y, seed, kind);
                float hx = Height(x + 1, y, seed, kind) - h;
                float hy = Height(x, y + 1, seed, kind) - h;
                var n = new Vector3(-hx * strength * 8f, -hy * strength * 8f, 1f).normalized;
                tex.SetPixel(x, y, new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f));
            }
            SaveTex(tex, path, false, true);
        }

        static float Height(int x, int y, int seed, TexKind kind)
        {
            float n = Mathf.PerlinNoise((x + seed) * 0.05f, (y + seed * 3) * 0.05f);
            if (kind == TexKind.Brick)
            {
                int row = y / 16;
                int col = (x + (row % 2) * 16) / 32;
                float mortar = ((x + (row % 2) * 16) % 32 < 2 || y % 16 < 2) ? 0.15f : 0.7f;
                return mortar + n * 0.2f;
            }
            if (kind == TexKind.Asphalt)
                return n * 0.6f + Mathf.PerlinNoise(x * 0.2f, y * 0.2f) * 0.4f;
            return n;
        }

        static void SaveTex(Texture2D tex, string path, bool alpha, bool normal = false)
        {
            tex.Apply();
            File.WriteAllBytes(Path.GetFullPath(path), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null) return;
            imp.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            imp.alphaIsTransparency = alpha;
            imp.wrapMode = TextureWrapMode.Clamp;
            imp.sRGBTexture = !normal;
            imp.mipmapEnabled = true;
            imp.maxTextureSize = 1024;
            imp.SaveAndReimport();
        }

        // ── materials ─────────────────────────────────────────────────────────

        static void BuildMaterials()
        {
            Mats.Clear();
            Mats["brick"] = Lit("FD_BrickDeep", $"{Gen}/BrickWall_Color.png", $"{Gen}/FD_Brick_Normal.png",
                new Color(0.68f, 0.48f, 0.40f), 0f, 0.18f, 2.8f, false);
            Mats["concrete"] = Lit("FD_ConcreteDeep", $"{Gen}/Concrete_Color.png", $"{Gen}/FD_Concrete_Normal.png",
                new Color(0.70f, 0.69f, 0.64f), 0f, 0.22f, 3.5f, false);
            Mats["plaster"] = Lit("FD_PlasterDeep", $"{Gen}/Plaster_Color.png", $"{Gen}/FD_Concrete_Normal.png",
                new Color(0.78f, 0.72f, 0.60f), 0f, 0.20f, 2.5f, false);
            Mats["trim"] = Solid("FD_DarkTrim", new Color(0.12f, 0.11f, 0.10f), 0.15f, 0.25f);
            Mats["metal"] = Lit("FD_MetalDeep", $"{Gen}/Metal_Color.png", $"{Gen}/P2_Metal_Normal.png",
                new Color(0.45f, 0.46f, 0.44f), 0.75f, 0.35f, 2f, false);
            Mats["glass"] = Glass("FD_GlassTint", new Color(0.18f, 0.28f, 0.32f, 0.72f));
            Mats["awning"] = Solid("FD_AwningCloth", new Color(0.55f, 0.12f, 0.10f), 0f, 0.18f);
            Mats["awningBlue"] = Solid("FD_AwningBlue", new Color(0.12f, 0.22f, 0.55f), 0f, 0.18f);
            Mats["oil"] = Decal("FD_OilDecal", $"{Gen}/FD_OilStain.png", new Color(1f, 1f, 1f, 1f), 0.55f);
            Mats["crack"] = Decal("FD_CrackDecalMat", $"{Gen}/FD_CrackDecal.png", new Color(1f, 1f, 1f, 1f), 0.12f);
            Mats["graffiti"] = Decal("FD_GraffitiDecal", $"{Gen}/FD_GraffitiAlpha.png", Color.white, 0.15f);
            Mats["poster"] = Decal("FD_PosterDecal", $"{Gen}/FD_PosterWeathered.png", Color.white, 0.18f);
            Mats["wood"] = Solid("FD_WoodDoor", new Color(0.32f, 0.20f, 0.12f), 0f, 0.22f);
            Mats["warm"] = Solid("FD_WarmGlow", new Color(1f, 0.72f, 0.38f), 0f, 0.55f);
        }

        static Material Lit(string name, string albedo, string normal, Color tint, float metallic, float smoothness, float tiling, bool transparent)
        {
            var mat = Solid(name, tint, metallic, smoothness);
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(albedo);
            if (tex != null)
            {
                mat.SetTexture("_BaseMap", tex);
                mat.mainTextureScale = new Vector2(tiling, tiling);
            }
            var nrm = AssetDatabase.LoadAssetAtPath<Texture2D>(normal);
            if (nrm != null && mat.HasProperty("_BumpMap"))
            {
                mat.SetTexture("_BumpMap", nrm);
                mat.EnableKeyword("_NORMALMAP");
                mat.SetFloat("_BumpScale", 1.15f);
            }
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
            SetOpaque(mat);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material Glass(string name, Color color)
        {
            // Slightly brighter tinted glass so windows read as glass, not black cards.
            var mat = Solid(name, color, 0.02f, 0.92f);
            SetTransparent(mat, color);
            if (mat.HasProperty("_EmissionColor"))
            {
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", new Color(0.08f, 0.10f, 0.12f));
            }
            return mat;
        }

        static Material Decal(string name, string texPath, Color tint, float smoothness)
        {
            var path = $"{MatDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard")) { name = name };
                AssetDatabase.CreateAsset(mat, path);
            }
            SetTransparent(mat, tint);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            if (tex != null)
            {
                mat.SetTexture("_BaseMap", tex);
                mat.mainTextureScale = Vector2.one;
            }
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static void SetOpaque(Material mat)
        {
            if (!mat.HasProperty("_Surface")) return;
            mat.SetFloat("_Surface", 0f);
            mat.SetFloat("_Blend", 0f);
            mat.SetOverrideTag("RenderType", "Opaque");
            mat.SetInt("_SrcBlend", (int)BlendMode.One);
            mat.SetInt("_DstBlend", (int)BlendMode.Zero);
            mat.SetInt("_ZWrite", 1);
            mat.renderQueue = (int)RenderQueue.Geometry;
            mat.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
        }

        static void SetTransparent(Material mat, Color color)
        {
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            else mat.color = color;
            if (!mat.HasProperty("_Surface"))
            {
                // Standard fallback
                mat.SetFloat("_Mode", 3f);
                mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.EnableKeyword("_ALPHABLEND_ON");
                mat.renderQueue = (int)RenderQueue.Transparent;
                return;
            }
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.renderQueue = (int)RenderQueue.Transparent;
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
        }

        // ── mid-lane facades (eye cam frustum) ────────────────────────────────

        static void BuildMidLaneFacades(Transform map)
        {
            // Cafe SW: center (-5.8,0,-14.5) size 6.1x5.4x8 — south face near eye cam.
            DetailedBuildingFace(map, "FD_Cafe",
                center: new Vector3(-5.8f, 0f, -14.5f), size: new Vector3(6.1f, 5.4f, 8f),
                south: true, north: true, east: true, west: false,
                body: Mats["plaster"], accent: Mats["awning"]);

            // Pawn SE: (6,0,-12.5) 5.7x5.8x7
            DetailedBuildingFace(map, "FD_Pawn",
                center: new Vector3(6f, 0f, -12.5f), size: new Vector3(5.7f, 5.8f, 7f),
                south: true, north: true, east: false, west: true,
                body: Mats["brick"], accent: Mats["awningBlue"]);

            // Clinic NW: (-6.2,0,13) 6x5.6x8.5 — mid-distance north
            DetailedBuildingFace(map, "FD_Clinic",
                center: new Vector3(-6.2f, 0f, 13f), size: new Vector3(6f, 5.6f, 8.5f),
                south: true, north: false, east: true, west: false,
                body: Mats["brick"], accent: Mats["awning"]);

            // Pharmacy NE: (6.2,0,14) 5.8x6.4x8
            DetailedBuildingFace(map, "FD_Pharmacy",
                center: new Vector3(6.2f, 0f, 14f), size: new Vector3(5.8f, 6.4f, 8f),
                south: true, north: false, east: false, west: true,
                body: Mats["plaster"], accent: Mats["awningBlue"]);

            // Stacked volume / setbacks so silhouettes stop reading as single boxes.
            Box(map, "FD_Cafe_Setback", new Vector3(-5.8f, 4.85f, -14.5f), new Vector3(5.2f, 1.1f, 6.6f), Mats["concrete"], false);
            Box(map, "FD_Cafe_MechPenthouse", new Vector3(-4.6f, 5.7f, -13.2f), new Vector3(2.4f, 0.9f, 2.0f), Mats["metal"], false);
            Box(map, "FD_Pawn_Setback", new Vector3(6f, 5.2f, -12.5f), new Vector3(4.8f, 1.0f, 5.6f), Mats["concrete"], false);
            Box(map, "FD_Pawn_BillboardArm", new Vector3(6f, 4.0f, -16.2f), new Vector3(3.2f, 0.18f, 0.22f), Mats["metal"], false);
            Box(map, "FD_Pawn_Billboard", new Vector3(6f, 4.55f, -16.35f), new Vector3(2.8f, 1.1f, 0.10f), Mats["poster"], false);
            Box(map, "FD_Clinic_Setback", new Vector3(-6.2f, 5.1f, 13f), new Vector3(5.0f, 1.0f, 7.0f), Mats["concrete"], false);
            Box(map, "FD_Pharmacy_Setback", new Vector3(6.2f, 5.7f, 14f), new Vector3(4.9f, 1.15f, 6.5f), Mats["concrete"], false);

            // Mid-lane street furniture / mid-distance density.
            Box(map, "FD_Mid_Newsstand", new Vector3(-1.2f, 0.7f, -6.5f), new Vector3(1.8f, 1.4f, 1.1f), Mats["metal"], true);
            Box(map, "FD_Mid_NewsstandRoof", new Vector3(-1.2f, 1.55f, -6.5f), new Vector3(2.1f, 0.12f, 1.4f), Mats["awning"], false);
            Box(map, "FD_Mid_PhoneBooth", new Vector3(2.4f, 1.1f, -4.2f), new Vector3(0.85f, 2.2f, 0.85f), Mats["metal"], true);
            Box(map, "FD_Mid_PhoneGlass", new Vector3(2.4f, 1.2f, -4.2f), new Vector3(0.72f, 1.4f, 0.72f), Mats["glass"], false);
        }

        static void DetailedBuildingFace(Transform map, string prefix, Vector3 center, Vector3 size,
            bool south, bool north, bool east, bool west, Material body, Material accent)
        {
            float hx = size.x * 0.5f;
            float hz = size.z * 0.5f;
            float hy = size.y;

            if (south) FaceSouth(map, prefix + "_S", center.x, center.z - hz, hx * 2f, hy, body, accent);
            if (north) FaceNorth(map, prefix + "_N", center.x, center.z + hz, hx * 2f, hy, body, accent);
            if (east) FaceEast(map, prefix + "_E", center.x + hx, center.z, hz * 2f, hy, body, accent);
            if (west) FaceWest(map, prefix + "_W", center.x - hx, center.z, hz * 2f, hy, body, accent);
        }

        static void FaceSouth(Transform map, string p, float x, float zFace, float width, float height, Material body, Material accent)
        {
            // Base ledge / watertable + cornice + belt course.
            Box(map, p + "_Watertable", new Vector3(x, 0.55f, zFace - 0.12f), new Vector3(width + 0.35f, 0.35f, 0.28f), Mats["concrete"], false);
            Box(map, p + "_Belt", new Vector3(x, 2.55f, zFace - 0.10f), new Vector3(width + 0.2f, 0.18f, 0.22f), Mats["trim"], false);
            Box(map, p + "_Cornice", new Vector3(x, height - 0.25f, zFace - 0.16f), new Vector3(width + 0.45f, 0.32f, 0.38f), Mats["concrete"], false);
            Box(map, p + "_CorniceLip", new Vector3(x, height - 0.05f, zFace - 0.22f), new Vector3(width + 0.55f, 0.12f, 0.18f), Mats["trim"], false);

            // Recessed storefront door.
            Box(map, p + "_DoorRecess", new Vector3(x, 1.15f, zFace - 0.08f), new Vector3(1.55f, 2.25f, 0.20f), Mats["trim"], false);
            Box(map, p + "_Door", new Vector3(x, 1.15f, zFace - 0.02f), new Vector3(1.25f, 2.05f, 0.08f), Mats["wood"], false);
            Box(map, p + "_DoorGlass", new Vector3(x, 1.45f, zFace + 0.01f), new Vector3(0.85f, 1.05f, 0.04f), Mats["glass"], false);
            Box(map, p + "_Awning", new Vector3(x, 2.45f, zFace - 0.55f), new Vector3(2.6f, 0.12f, 1.0f), accent, false);

            // Window bays with true frame + glass + sill + lintel.
            int cols = Mathf.Clamp(Mathf.FloorToInt(width / 2.2f), 2, 4);
            for (int c = 0; c < cols; c++)
            {
                if (c == cols / 2) continue; // leave door bay clear
                float along = -width * 0.5f + (c + 0.5f) * (width / cols);
                WindowBay(map, p + "_Win0_" + c, new Vector3(x + along, 3.55f, zFace - 0.06f), Vector3.back, 1.15f, 1.25f);
                if (height > 5.2f)
                    WindowBay(map, p + "_Win1_" + c, new Vector3(x + along, 4.85f, zFace - 0.06f), Vector3.back, 1.05f, 1.1f);
            }

            // Pilasters break facade into bays.
            for (int i = 0; i <= cols; i++)
            {
                float along = -width * 0.5f + i * (width / cols);
                Box(map, p + "_Pilaster_" + i, new Vector3(x + along, height * 0.5f, zFace - 0.08f),
                    new Vector3(0.22f, height - 0.4f, 0.20f), Mats["concrete"], false);
            }
        }

        static void FaceNorth(Transform map, string p, float x, float zFace, float width, float height, Material body, Material accent)
        {
            Box(map, p + "_Watertable", new Vector3(x, 0.55f, zFace + 0.12f), new Vector3(width + 0.35f, 0.35f, 0.28f), Mats["concrete"], false);
            Box(map, p + "_Belt", new Vector3(x, 2.55f, zFace + 0.10f), new Vector3(width + 0.2f, 0.18f, 0.22f), Mats["trim"], false);
            Box(map, p + "_Cornice", new Vector3(x, height - 0.25f, zFace + 0.16f), new Vector3(width + 0.45f, 0.32f, 0.38f), Mats["concrete"], false);
            Box(map, p + "_DoorRecess", new Vector3(x, 1.15f, zFace + 0.08f), new Vector3(1.55f, 2.25f, 0.20f), Mats["trim"], false);
            Box(map, p + "_Door", new Vector3(x, 1.15f, zFace + 0.02f), new Vector3(1.25f, 2.05f, 0.08f), Mats["wood"], false);
            Box(map, p + "_Awning", new Vector3(x, 2.45f, zFace + 0.55f), new Vector3(2.6f, 0.12f, 1.0f), accent, false);

            int cols = Mathf.Clamp(Mathf.FloorToInt(width / 2.2f), 2, 4);
            for (int c = 0; c < cols; c++)
            {
                if (c == cols / 2) continue;
                float along = -width * 0.5f + (c + 0.5f) * (width / cols);
                WindowBay(map, p + "_Win0_" + c, new Vector3(x + along, 3.55f, zFace + 0.06f), Vector3.forward, 1.15f, 1.25f);
            }
        }

        static void FaceEast(Transform map, string p, float xFace, float z, float depth, float height, Material body, Material accent)
        {
            Box(map, p + "_Watertable", new Vector3(xFace + 0.12f, 0.55f, z), new Vector3(0.28f, 0.35f, depth + 0.35f), Mats["concrete"], false);
            Box(map, p + "_Belt", new Vector3(xFace + 0.10f, 2.55f, z), new Vector3(0.22f, 0.18f, depth + 0.2f), Mats["trim"], false);
            Box(map, p + "_Cornice", new Vector3(xFace + 0.16f, height - 0.25f, z), new Vector3(0.38f, 0.32f, depth + 0.45f), Mats["concrete"], false);
            Box(map, p + "_DoorRecess", new Vector3(xFace + 0.08f, 1.15f, z), new Vector3(0.20f, 2.25f, 1.55f), Mats["trim"], false);
            Box(map, p + "_Door", new Vector3(xFace + 0.02f, 1.15f, z), new Vector3(0.08f, 2.05f, 1.25f), Mats["wood"], false);
            Box(map, p + "_Awning", new Vector3(xFace + 0.55f, 2.45f, z), new Vector3(1.0f, 0.12f, 2.4f), accent, false);

            int cols = Mathf.Clamp(Mathf.FloorToInt(depth / 2.4f), 2, 4);
            for (int c = 0; c < cols; c++)
            {
                if (c == cols / 2) continue;
                float along = -depth * 0.5f + (c + 0.5f) * (depth / cols);
                WindowBay(map, p + "_Win0_" + c, new Vector3(xFace + 0.06f, 3.55f, z + along), Vector3.right, 1.15f, 1.25f);
                if (height > 5.2f)
                    WindowBay(map, p + "_Win1_" + c, new Vector3(xFace + 0.06f, 4.85f, z + along), Vector3.right, 1.05f, 1.1f);
            }
        }

        static void FaceWest(Transform map, string p, float xFace, float z, float depth, float height, Material body, Material accent)
        {
            Box(map, p + "_Watertable", new Vector3(xFace - 0.12f, 0.55f, z), new Vector3(0.28f, 0.35f, depth + 0.35f), Mats["concrete"], false);
            Box(map, p + "_Belt", new Vector3(xFace - 0.10f, 2.55f, z), new Vector3(0.22f, 0.18f, depth + 0.2f), Mats["trim"], false);
            Box(map, p + "_Cornice", new Vector3(xFace - 0.16f, height - 0.25f, z), new Vector3(0.38f, 0.32f, depth + 0.45f), Mats["concrete"], false);
            Box(map, p + "_DoorRecess", new Vector3(xFace - 0.08f, 1.15f, z), new Vector3(0.20f, 2.25f, 1.55f), Mats["trim"], false);
            Box(map, p + "_Door", new Vector3(xFace - 0.02f, 1.15f, z), new Vector3(0.08f, 2.05f, 1.25f), Mats["wood"], false);
            Box(map, p + "_Awning", new Vector3(xFace - 0.55f, 2.45f, z), new Vector3(1.0f, 0.12f, 2.4f), accent, false);

            int cols = Mathf.Clamp(Mathf.FloorToInt(depth / 2.4f), 2, 4);
            for (int c = 0; c < cols; c++)
            {
                if (c == cols / 2) continue;
                float along = -depth * 0.5f + (c + 0.5f) * (depth / cols);
                WindowBay(map, p + "_Win0_" + c, new Vector3(xFace - 0.06f, 3.55f, z + along), Vector3.left, 1.15f, 1.25f);
                if (height > 5.2f)
                    WindowBay(map, p + "_Win1_" + c, new Vector3(xFace - 0.06f, 4.85f, z + along), Vector3.left, 1.05f, 1.1f);
            }
        }

        static void WindowBay(Transform map, string name, Vector3 pos, Vector3 outward, float w, float h)
        {
            bool xz = Mathf.Abs(outward.x) > 0.5f;
            // Deep return + dark interior void so openings read as architecture, not stickers.
            Vector3 voidSize = xz ? new Vector3(0.45f, h + 0.15f, w + 0.15f) : new Vector3(w + 0.15f, h + 0.15f, 0.45f);
            Vector3 frame = xz ? new Vector3(0.22f, h + 0.35f, w + 0.38f) : new Vector3(w + 0.38f, h + 0.35f, 0.22f);
            Vector3 glass = xz ? new Vector3(0.05f, h * 0.88f, w * 0.88f) : new Vector3(w * 0.88f, h * 0.88f, 0.05f);
            Vector3 sill = xz ? new Vector3(0.38f, 0.12f, w + 0.40f) : new Vector3(w + 0.40f, 0.12f, 0.38f);
            Vector3 lintel = xz ? new Vector3(0.36f, 0.18f, w + 0.42f) : new Vector3(w + 0.42f, 0.18f, 0.36f);
            Vector3 mullion = xz ? new Vector3(0.05f, h * 0.88f, 0.07f) : new Vector3(0.07f, h * 0.88f, 0.05f);

            Box(map, name + "_Void", pos - outward * 0.22f, voidSize, Mats["trim"], false);
            Box(map, name + "_Frame", pos + outward * 0.02f, frame, Mats["concrete"], false);
            Box(map, name + "_Glass", pos + outward * 0.10f, glass, Mats["glass"], false);
            Box(map, name + "_Sill", pos + Vector3.down * (h * 0.5f + 0.10f) + outward * 0.14f, sill, Mats["concrete"], false);
            Box(map, name + "_Lintel", pos + Vector3.up * (h * 0.5f + 0.14f) + outward * 0.12f, lintel, Mats["concrete"], false);
            Box(map, name + "_MullionV", pos + outward * 0.11f, mullion, Mats["trim"], false);
            Vector3 mullH = xz ? new Vector3(0.05f, 0.07f, w * 0.88f) : new Vector3(w * 0.88f, 0.07f, 0.05f);
            Box(map, name + "_MullionH", pos + outward * 0.11f, mullH, Mats["trim"], false);
        }

        // ── side street walls still in eye frustum ────────────────────────────

        static void BuildWestEastSideFacades(Transform map)
        {
            // Print shop east face (~x=-11.6) and Market west face (~x=9.4) — mid-distance flanks.
            for (int i = 0; i < 5; i++)
            {
                float z = -10f + i * 3.2f;
                WindowBay(map, $"FD_PrintShop_Win_{i}", new Vector3(-11.55f, 3.2f + (i % 2) * 1.7f, z), Vector3.right, 1.3f, 1.35f);
                Box(map, $"FD_PrintShop_Ledge_{i}", new Vector3(-11.45f, 2.35f + (i % 2) * 1.7f, z), new Vector3(0.28f, 0.12f, 1.5f), Mats["concrete"], false);
            }
            Box(map, "FD_PrintShop_Cornice", new Vector3(-11.4f, 7.9f, -5f), new Vector3(0.4f, 0.35f, 14f), Mats["concrete"], false);
            Box(map, "FD_PrintShop_Belt", new Vector3(-11.48f, 1.4f, -5f), new Vector3(0.28f, 0.22f, 14f), Mats["trim"], false);
            Box(map, "FD_PrintShop_DoorRecess", new Vector3(-11.55f, 1.15f, -2f), new Vector3(0.25f, 2.2f, 1.6f), Mats["trim"], false);
            Box(map, "FD_PrintShop_Door", new Vector3(-11.48f, 1.15f, -2f), new Vector3(0.1f, 2.05f, 1.3f), Mats["wood"], false);

            for (int i = 0; i < 5; i++)
            {
                float z = -14f + i * 3.4f;
                WindowBay(map, $"FD_Market_Win_{i}", new Vector3(9.45f, 3.0f + (i % 2) * 1.65f, z), Vector3.left, 1.25f, 1.3f);
            }
            Box(map, "FD_Market_Cornice", new Vector3(9.55f, 5.9f, -23f), new Vector3(0.4f, 0.32f, 12f), Mats["concrete"], false);
            Box(map, "FD_Market_Awning", new Vector3(10.2f, 2.5f, -18f), new Vector3(1.2f, 0.14f, 5.5f), Mats["awning"], false);
        }

        // ── decals ────────────────────────────────────────────────────────────

        static void BuildGroundAndWallDecals(Transform map)
        {
            // Ground oil / wet stains along the eye-cam approach (alpha-blended, slightly above asphalt).
            GroundDecal(map, "FD_Oil_Approach_A", new Vector3(-4.5f, 0.04f, -15.5f), new Vector3(3.8f, 0.02f, 2.2f), Mats["oil"], 12f);
            GroundDecal(map, "FD_Oil_Approach_B", new Vector3(-1.2f, 0.04f, -10.5f), new Vector3(2.6f, 0.02f, 3.4f), Mats["oil"], -8f);
            GroundDecal(map, "FD_Oil_Mid", new Vector3(1.5f, 0.04f, -3.5f), new Vector3(3.2f, 0.02f, 2.0f), Mats["oil"], 25f);
            GroundDecal(map, "FD_Crack_A", new Vector3(-3.0f, 0.035f, -12.8f), new Vector3(4.5f, 0.018f, 1.8f), Mats["crack"], 5f);
            GroundDecal(map, "FD_Crack_B", new Vector3(0.5f, 0.035f, -7.0f), new Vector3(3.2f, 0.018f, 2.4f), Mats["crack"], -18f);
            GroundDecal(map, "FD_Crack_C", new Vector3(2.8f, 0.035f, 1.0f), new Vector3(2.8f, 0.018f, 3.0f), Mats["crack"], 40f);

            for (int i = 0; i < 6; i++)
            {
                float z = -16f + i * 3.5f;
                GroundDecal(map, $"FD_Tire_{i}", new Vector3(-0.3f + (i % 2) * 0.8f, 0.032f, z),
                    new Vector3(0.35f, 0.015f, 1.8f), Mats["oil"], i * 3f);
            }

            // Wall graffiti / weathered posters — thin, alpha, tinted (not white cards).
            WallDecal(map, "FD_Graffiti_CafeE", new Vector3(-2.65f, 2.3f, -12.5f), new Vector3(0.04f, 1.8f, 2.6f), Mats["graffiti"]);
            WallDecal(map, "FD_Poster_CafeS", new Vector3(-7.4f, 2.4f, -18.55f), new Vector3(1.4f, 1.9f, 0.04f), Mats["poster"]);
            WallDecal(map, "FD_Poster_PawnW", new Vector3(3.1f, 2.5f, -11.0f), new Vector3(0.04f, 1.7f, 1.3f), Mats["poster"]);
            WallDecal(map, "FD_Graffiti_PrintE", new Vector3(-11.35f, 2.6f, -7.5f), new Vector3(0.04f, 2.0f, 2.8f), Mats["graffiti"]);
            WallDecal(map, "FD_Poster_ClinicS", new Vector3(-6.2f, 2.3f, 8.7f), new Vector3(2.4f, 1.6f, 0.04f), Mats["poster"]);
            WallDecal(map, "FD_Graffiti_PawnS", new Vector3(7.5f, 2.2f, -16.05f), new Vector3(1.6f, 1.5f, 0.04f), Mats["graffiti"]);
        }

        static void GroundDecal(Transform map, string name, Vector3 pos, Vector3 scale, Material mat, float yaw)
        {
            var go = Box(map, name, pos, scale, mat, false, yaw);
            // Flatten Z-fight: slight random micro-offset already in pos.y
            var r = go.GetComponent<MeshRenderer>();
            if (r != null) r.shadowCastingMode = ShadowCastingMode.Off;
        }

        static void WallDecal(Transform map, string name, Vector3 pos, Vector3 scale, Material mat)
        {
            var go = Box(map, name, pos, scale, mat, false);
            var r = go.GetComponent<MeshRenderer>();
            if (r != null) r.shadowCastingMode = ShadowCastingMode.Off;
        }

        // ── authored props ────────────────────────────────────────────────────

        static void PlaceAuthoredBarrels(Transform map)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(BarrelPath);
            if (prefab == null)
            {
                Debug.LogWarning("[AAA Facade] Barrel_01 missing; using procedural cylinders.");
                for (int i = 0; i < 4; i++)
                    Cylinder(map, $"FD_BarrelFallback_{i}", new Vector3(-3.5f + i * 0.7f, 0.55f, -14.8f), 0.55f, 1.05f, Mats["metal"]);
                return;
            }

            // Albedo/normal for barrel if textures exist.
            EnsureBarrelMaterial();

            var spots = new[]
            {
                new Vector3(-2.4f, 0f, -16.8f),
                new Vector3(-1.7f, 0f, -16.2f),
                new Vector3(2.6f, 0f, -14.5f),
                new Vector3(1.9f, 0f, -9.2f),
                new Vector3(-0.8f, 0f, -7.5f),
            };
            for (int i = 0; i < spots.Length; i++)
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, map);
                go.name = $"FD_Barrel_{i}";
                go.transform.position = spots[i];
                go.transform.rotation = Quaternion.Euler(0f, i * 37f, 0f);
                go.transform.localScale = Vector3.one * 1.15f;
                go.isStatic = true;
                ApplyBarrelMats(go);
                if (go.GetComponent<Collider>() == null)
                {
                    var c = go.AddComponent<CapsuleCollider>();
                    c.height = 1.1f;
                    c.radius = 0.35f;
                    c.center = new Vector3(0f, 0.55f, 0f);
                }
            }
        }

        static void EnsureBarrelMaterial()
        {
            var path = $"{MatDir}/FD_BarrelPolyHaven.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard")) { name = "FD_BarrelPolyHaven" };
                AssetDatabase.CreateAsset(mat, path);
            }
            var albedo = AssetDatabase.LoadAssetAtPath<Texture2D>(
                "Assets/_Project/Art/Models/Environment/Props/Barrel_01/textures/Barrel_01_explosive_diff_1k.png");
            var normal = AssetDatabase.LoadAssetAtPath<Texture2D>(
                "Assets/_Project/Art/Models/Environment/Props/Barrel_01/textures/Barrel_01_explosive_nor_gl_1k.png");
            var metal = AssetDatabase.LoadAssetAtPath<Texture2D>(
                "Assets/_Project/Art/Models/Environment/Props/Barrel_01/textures/Barrel_01_explosive_metallic_1k.png");
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            if (albedo != null)
            {
                mat.SetTexture("_BaseMap", albedo);
                mat.mainTexture = albedo;
            }
            if (normal != null && mat.HasProperty("_BumpMap"))
            {
                var imp = AssetImporter.GetAtPath(
                    "Assets/_Project/Art/Models/Environment/Props/Barrel_01/textures/Barrel_01_explosive_nor_gl_1k.png") as TextureImporter;
                if (imp != null && imp.textureType != TextureImporterType.NormalMap)
                {
                    imp.textureType = TextureImporterType.NormalMap;
                    imp.SaveAndReimport();
                }
                mat.SetTexture("_BumpMap", normal);
                mat.EnableKeyword("_NORMALMAP");
            }
            // Avoid metallic map blacking-out barrels when channel packing differs.
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0.65f);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.40f);
            if (metal != null) { /* keep for future packed workflow */ }
            EditorUtility.SetDirty(mat);
            Mats["barrel"] = mat;
        }

        static void ApplyBarrelMats(GameObject go)
        {
            if (!Mats.TryGetValue("barrel", out var mat) || mat == null) return;
            foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true))
                r.sharedMaterial = mat;
        }

        // ── lighting / cameras ────────────────────────────────────────────────

        static void SoftenLighting()
        {
            foreach (var l in Object.FindObjectsByType<Light>())
            {
                if (l.type != LightType.Directional) continue;
                l.intensity = 1.15f;
                l.shadowStrength = 0.52f;
                l.color = new Color(0.96f, 0.97f, 1f);
                l.shadows = LightShadows.Soft;
            }
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.50f, 0.56f, 0.66f);
            RenderSettings.ambientEquatorColor = new Color(0.38f, 0.35f, 0.30f);
            RenderSettings.ambientGroundColor = new Color(0.20f, 0.18f, 0.15f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.0045f;
            RenderSettings.fogColor = new Color(0.55f, 0.58f, 0.62f);

            // Warm practical near eye approach.
            EnsurePoint("FD_Practical_Cafe", new Vector3(-4.5f, 3.0f, -16.5f), new Color(1f, 0.7f, 0.4f), 2.4f, 9f);
            EnsurePoint("FD_Practical_Mid", new Vector3(0.5f, 3.2f, -5f), new Color(1f, 0.75f, 0.45f), 2.0f, 10f);
        }

        static void EnsurePoint(string name, Vector3 pos, Color color, float intensity, float range)
        {
            var go = GameObject.Find(name);
            if (go == null)
            {
                var map = GameObject.Find("ThreeLaneMap");
                go = new GameObject(name);
                go.transform.SetParent(map != null ? map.transform : null, true);
                go.AddComponent<Light>();
            }
            go.transform.position = pos;
            var l = go.GetComponent<Light>();
            l.type = LightType.Point;
            l.color = color;
            l.intensity = intensity;
            l.range = range;
            l.shadows = LightShadows.None;
        }

        /// <summary>
        /// Extra depth that reads at eye-level: chunky frames, lit glass, sidewalk grime ribbons.
        /// Requested eye pos (-6,1.7,-18) is inside Mid_SW_Cafe — park just outside in the lane.
        /// </summary>
        static void BoostEyeReadableDepth(Transform map)
        {
            // Chunky storefront surrounds on cafe east (faces the lane) + pawn west.
            Box(map, "FD_Cafe_StorefrontFrame", new Vector3(-2.65f, 1.7f, -14.5f), new Vector3(0.35f, 3.2f, 5.8f), Mats["concrete"], false);
            Box(map, "FD_Cafe_StorefrontGlassA", new Vector3(-2.45f, 2.4f, -16.2f), new Vector3(0.08f, 1.6f, 1.5f), Mats["glass"], false);
            Box(map, "FD_Cafe_StorefrontGlassB", new Vector3(-2.45f, 2.4f, -13.0f), new Vector3(0.08f, 1.6f, 1.5f), Mats["glass"], false);
            Box(map, "FD_Cafe_SignBand", new Vector3(-2.40f, 3.55f, -14.5f), new Vector3(0.18f, 0.55f, 5.2f), Mats["awning"], false);

            Box(map, "FD_Pawn_StorefrontFrame", new Vector3(3.05f, 1.75f, -12.5f), new Vector3(0.35f, 3.3f, 5.2f), Mats["concrete"], false);
            Box(map, "FD_Pawn_StorefrontGlassA", new Vector3(2.85f, 2.45f, -14.0f), new Vector3(0.08f, 1.65f, 1.4f), Mats["glass"], false);
            Box(map, "FD_Pawn_StorefrontGlassB", new Vector3(2.85f, 2.45f, -11.2f), new Vector3(0.08f, 1.65f, 1.4f), Mats["glass"], false);
            Box(map, "FD_Pawn_SignBand", new Vector3(2.80f, 3.65f, -12.5f), new Vector3(0.18f, 0.55f, 4.6f), Mats["awningBlue"], false);

            // Lit interior glow behind glass so windows don't read as flat black cards.
            EnsurePoint("FD_WinGlow_Cafe", new Vector3(-3.6f, 2.5f, -14.5f), new Color(1f, 0.78f, 0.45f), 1.6f, 6f);
            EnsurePoint("FD_WinGlow_Pawn", new Vector3(4.0f, 2.5f, -12.5f), new Color(0.7f, 0.85f, 1f), 1.4f, 6f);

            // Heavy sidewalk grime ribbons visible at feet.
            GroundDecal(map, "FD_Oil_StreetCenter", new Vector3(-0.2f, 0.045f, -14f), new Vector3(2.2f, 0.02f, 8.5f), Mats["oil"], 3f);
            GroundDecal(map, "FD_Crack_StreetCenter", new Vector3(0.6f, 0.04f, -11f), new Vector3(1.8f, 0.018f, 7f), Mats["crack"], -6f);
            GroundDecal(map, "FD_Oil_CafeCurb", new Vector3(-2.0f, 0.045f, -16.5f), new Vector3(2.5f, 0.02f, 3.2f), Mats["oil"], 18f);

            // Mid-distance vertical pipes + AC to break blank brick slabs.
            Cylinder(map, "FD_Pipe_CafeE", new Vector3(-2.55f, 3.2f, -11.8f), 0.18f, 5.5f, Mats["metal"]);
            Cylinder(map, "FD_Pipe_PawnW", new Vector3(2.95f, 3.4f, -9.8f), 0.16f, 5.8f, Mats["metal"]);
            Box(map, "FD_AC_Cafe", new Vector3(-2.35f, 4.4f, -12.6f), new Vector3(0.7f, 0.55f, 1.1f), Mats["metal"], false);
            Box(map, "FD_AC_Pawn", new Vector3(2.75f, 4.6f, -10.6f), new Vector3(0.7f, 0.55f, 1.1f), Mats["metal"], false);
        }

        static void ReframeAndDisableCameras()
        {
            // (-6,1.7,-18) is inside Cafe mass; hold the same look target from the open lane.
            SetCam("AAA_EyeLevel_Camera", new Vector3(-1.2f, 1.7f, -18.2f), new Vector3(2f, 1.5f, 6f), 68f);
            SetCam("AAA_MidLane_Camera", new Vector3(0f, 1.85f, -16.5f), new Vector3(0.5f, 1.6f, 5f), 62f);
            SetCam("AAA_Aerial_Camera", new Vector3(0f, 48f, -8f), new Vector3(0f, 0f, 0f), 50f);
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
            cam.farClipPlane = 200f;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.allowHDR = true;
            cam.enabled = false;
        }

        // ── primitives ────────────────────────────────────────────────────────

        static GameObject Box(Transform parent, string name, Vector3 pos, Vector3 scale, Material mat, bool collider)
            => Box(parent, name, pos, scale, mat, collider, 0f);

        static GameObject Box(Transform parent, string name, Vector3 pos, Vector3 scale, Material mat, bool collider, float yaw)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, true);
            go.transform.position = pos;
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            go.transform.localScale = scale;
            go.isStatic = true;
            var r = go.GetComponent<MeshRenderer>();
            if (r != null) r.sharedMaterial = mat;
            // Decorative trim above head height never gets collision (parapets, ledges, awnings, etc.).
            bool highDecor = pos.y > 2.2f || name.Contains("Parapet") || name.Contains("Ledge")
                             || name.Contains("Awning") || name.Contains("Sign") || name.Contains("Pillar");
            if (!collider || highDecor)
            {
                var c = go.GetComponent<Collider>();
                if (c != null) Object.DestroyImmediate(c);
            }
            return go;
        }

        static GameObject Cylinder(Transform parent, string name, Vector3 pos, float diameter, float height, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            go.transform.SetParent(parent, true);
            go.transform.position = pos;
            go.transform.localScale = new Vector3(diameter, height * 0.5f, diameter);
            go.isStatic = true;
            var r = go.GetComponent<MeshRenderer>();
            if (r != null) r.sharedMaterial = mat;
            // Facade cylinders are decorative (vents, pipes) — no collider.
            var col = go.GetComponent<Collider>();
            if (col != null) Object.DestroyImmediate(col);
            return go;
        }
    }
}
#endif
