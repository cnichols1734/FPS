#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ArenaFps.Editor
{
    /// <summary>
    /// Kill pale flat ground quads (CRITIQUE_01 #1 / GROUND_QUAD_SOURCES.md).
    /// Root cause: colliderless Road_/Conn_/*_Stripe slabs with bright OD_Asphalt (0.58)
    /// and pale OD_Concrete kerbs (0.74–0.78). Idempotent under GQ_GroundQuad.
    /// Does not touch player / prefabs / weapons.
    /// Menu: Arena FPS / AAA Ground Quad Pass
    /// </summary>
    public static class AaaGroundQuadPass
    {
        const string ScenePath = "Assets/_Project/Scenes/Arena.unity";
        const string RootName = "GQ_GroundQuad";
        const string MatDir = "Assets/_Project/Art/Materials/GroundQuad";
        const string OdMatDir = "Assets/_Project/Art/Materials/OverflowDressing";
        const string ReportPath = "_research/GROUND_QUAD_SOURCES.md";
        const string MetricPath = "_research/pale_fix_metrics.txt";

        // Dark asphalt / worn dirt — COD Overflow street, not sand cards.
        static readonly Color AsphaltDark = new(0.22f, 0.20f, 0.18f, 1f);
        static readonly Color DirtWorn = new(0.34f, 0.28f, 0.20f, 1f);
        static readonly Color KerbWorn = new(0.36f, 0.33f, 0.28f, 1f);
        static readonly Color EdgeBlend = new(0.10f, 0.09f, 0.07f, 0.75f);

        static Transform _root;
        static Transform _map;
        static int _stripesRemoved;
        static int _matsDarkened;
        static int _kerbsRetinted;
        static int _blends;
        static int _sidewalksRaised;

        [MenuItem("Arena FPS/AAA Ground Quad Pass")]
        public static void Run()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.isPlaying = false;
                Debug.LogWarning("[GQ] Exit play mode and re-run.");
                return;
            }

            OpenArena();
            _map = GameObject.Find("ThreeLaneMap")?.transform;
            if (_map == null)
            {
                Debug.LogError("[GQ] ThreeLaneMap missing.");
                return;
            }

            EnsureDir(MatDir);
            EnsureDir("_research");

            // BEFORE metrics
            var before = MeasurePale("BEFORE");

            ClearPrevious();
            _stripesRemoved = _matsDarkened = _kerbsRetinted = _blends = _sidewalksRaised = 0;

            _root = new GameObject(RootName).transform;
            _root.SetParent(_map, false);

            try
            {
                Stage1_DeleteStripes();
                Stage2_DarkenGroundMaterials();
                Stage3_RetintKerbs();
                Stage4_RaiseHiddenSidewalks();
                Stage5_EdgeBlendDecals();
                VerifySpawnClear();

                SetStaticRecursive(_root.gameObject);
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
                EditorSceneManager.SaveOpenScenes();
                AssetDatabase.SaveAssets();

                var after = MeasurePale("AFTER");
                WriteMetrics(before, after);

                Debug.Log(
                    $"[GQ] DONE stripesRemoved={_stripesRemoved} matsDarkened={_matsDarkened} " +
                    $"kerbsRetinted={_kerbsRetinted} blends={_blends} sidewalksRaised={_sidewalksRaised} " +
                    $"paleBefore={before.total} paleAfter={after.total}");
            }
            catch (Exception ex)
            {
                Debug.LogError("[GQ] FATAL: " + ex);
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
                EditorSceneManager.SaveOpenScenes();
                throw;
            }
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
                    if (t.name == RootName || t.name.StartsWith("GQ_"))
                        doomed.Add(t.gameObject);
                }
            }
            var orphan = GameObject.Find(RootName);
            if (orphan != null && !doomed.Contains(orphan)) doomed.Add(orphan);
            foreach (var go in doomed) Object.DestroyImmediate(go);
        }

        // ─── Stage 1: kill paint-line cards that inherited asphalt ───────────
        static void Stage1_DeleteStripes()
        {
            var doomed = new List<GameObject>();
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include))
            {
                if (t == null) continue;
                // Paint stripes retextured as asphalt cards
                if (t.name.EndsWith("_Stripe") && (t.name.StartsWith("Road_") || t.name.StartsWith("Conn_")))
                    doomed.Add(t.gameObject);
                // Legacy pale blend patches (flat quads on asphalt)
                else if (t.name.StartsWith("OP_GroundPatch") || t.name.StartsWith("OD_GroundPatch")
                         || t.name.StartsWith("PH_GroundBreak"))
                    doomed.Add(t.gameObject);
            }
            foreach (var go in doomed)
            {
                Object.DestroyImmediate(go);
                _stripesRemoved++;
            }
            Debug.Log($"[GQ] Removed {_stripesRemoved} stripe/patch sand cards.");
        }

        // ─── Stage 2: darken asphalt / dirt on Ground + Road + Conn + Beach ──
        static void Stage2_DarkenGroundMaterials()
        {
            // Shared assets first
            DarkenAsset($"{OdMatDir}/OD_Asphalt.mat", AsphaltDark);
            DarkenAsset($"{OdMatDir}/OD_DirtGround.mat", DirtWorn);
            DarkenAsset($"{OdMatDir}/OD_Dirt.mat", DirtWorn);
            DarkenAsset($"{OdMatDir}/OP_PackedDust.mat", DirtWorn);
            DarkenAsset($"{OdMatDir}/OP_Gravel.mat", new Color(0.30f, 0.27f, 0.22f));

            if (_map == null) return;
            foreach (Transform t in _map)
            {
                if (t == null) continue;
                bool isGround = t.name == "Ground" || t.name == "Beach_Dirt"
                    || t.name.StartsWith("Road_") || t.name.StartsWith("Conn_");
                if (!isGround) continue;
                if (t.name.EndsWith("_Stripe")) continue; // already deleted

                var r = t.GetComponent<Renderer>();
                if (r == null || r.sharedMaterial == null) continue;

                bool dirtish = t.name == "Beach_Dirt"
                    || t.name.Contains("Beach") || t.name.Contains("Dirt") || t.name.Contains("Vault")
                    || r.sharedMaterial.name.Contains("Dirt") || r.sharedMaterial.name.Contains("Packed")
                    || r.sharedMaterial.name.Contains("Gravel");

                var src = r.sharedMaterial;
                var mat = new Material(src);
                mat.name = (dirtish ? "GQ_Dirt_" : "GQ_Asphalt_") + t.name;
                Color target = dirtish ? DirtWorn : AsphaltDark;
                if (mat.HasProperty("_BaseColor"))
                {
                    // Preserve slight per-chunk jitter from PH but clamp into dark band
                    var c = mat.GetColor("_BaseColor");
                    float j = 1f + ((Mathf.Abs(t.name.GetHashCode()) % 5) - 2) * 0.025f;
                    mat.SetColor("_BaseColor", new Color(
                        Mathf.Clamp(target.r * j, 0.14f, 0.40f),
                        Mathf.Clamp(target.g * j * 0.98f, 0.12f, 0.36f),
                        Mathf.Clamp(target.b * j * 0.95f, 0.10f, 0.32f),
                        1f));
                }
                // Break tiling 5–8 m
                float tile = dirtish ? 6.5f : 11f;
                tile += (Mathf.Abs(t.name.GetHashCode()) % 7) * 0.35f;
                var scale = new Vector2(tile, tile * (0.88f + (Mathf.Abs(t.name.GetHashCode()) % 3) * 0.05f));
                mat.mainTextureScale = scale;
                if (mat.HasProperty("_BaseMap")) mat.SetTextureScale("_BaseMap", scale);
                if (mat.HasProperty("_BumpMap")) mat.SetTextureScale("_BumpMap", scale);
                if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", dirtish ? 0.12f : 0.18f);

                // Persist under MatDir for idempotent re-run (overwrite by name)
                string assetPath = $"{MatDir}/{mat.name}.mat";
                var existing = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
                if (existing != null)
                {
                    EditorUtility.CopySerialized(mat, existing);
                    Object.DestroyImmediate(mat);
                    mat = existing;
                }
                else
                {
                    AssetDatabase.CreateAsset(mat, assetPath);
                }

                r.sharedMaterial = mat;
                // Sink road slabs flush-ish so they don't float as cards (keep slight z-order)
                if (t.name.StartsWith("Road_") || t.name.StartsWith("Conn_"))
                {
                    var p = t.position;
                    p.y = 0.015f;
                    t.position = p;
                }
                _matsDarkened++;
            }
            Debug.Log($"[GQ] Darkened {_matsDarkened} ground/road/conn renderers.");
        }

        static void DarkenAsset(string path, Color col)
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) return;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", col);
            EditorUtility.SetDirty(m);
        }

        // ─── Stage 3: pale OP_Kerb concrete → worn kerb ──────────────────────
        static void Stage3_RetintKerbs()
        {
            var kerbMat = UpsertMat("GQ_KerbWorn", KerbWorn,
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Concrete031/Concrete031_2K-JPG_Color.jpg",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Concrete031/Concrete031_2K-JPG_NormalGL.jpg");

            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include))
            {
                if (t == null) continue;
                string n = t.name;
                if (!(n.StartsWith("OP_Kerb") || n.StartsWith("OP_ForceKerb") || n.StartsWith("PH_Kerb")))
                    continue;
                var r = t.GetComponent<Renderer>();
                if (r == null) continue;
                r.sharedMaterial = kerbMat;
                r.enabled = true;
                _kerbsRetinted++;
            }
            Debug.Log($"[GQ] Retinted {_kerbsRetinted} kerbs to worn concrete.");
        }

        // ─── Stage 4: hidden Sidewalk_ → raised worn walk + kerb edge ────────
        static void Stage4_RaiseHiddenSidewalks()
        {
            var walkMat = UpsertMat("GQ_RaisedWalk", new Color(0.32f, 0.30f, 0.26f),
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Concrete031/Concrete031_2K-JPG_Color.jpg",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Concrete031/Concrete031_2K-JPG_NormalGL.jpg");
            var kerbMat = UpsertMat("GQ_KerbWorn", KerbWorn,
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Concrete031/Concrete031_2K-JPG_Color.jpg",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Concrete031/Concrete031_2K-JPG_NormalGL.jpg");

            if (_map == null) return;
            foreach (Transform t in _map)
            {
                if (!t.name.StartsWith("Sidewalk_")) continue;
                var r = t.GetComponent<Renderer>();
                if (r == null) continue;
                // Keep flat slab hidden; ensure raised replacement exists under GQ_
                r.enabled = false;
                // No invisible colliders — strip collider from hidden slab
                var oldCol = t.GetComponent<Collider>();
                if (oldCol != null) Object.DestroyImmediate(oldCol);
                var b = r.bounds;

                var raised = GameObject.CreatePrimitive(PrimitiveType.Cube);
                raised.name = $"GQ_RaisedWalk_{_sidewalksRaised}";
                raised.transform.SetParent(_root, true);
                raised.transform.position = new Vector3(b.center.x, 0.12f, b.center.z);
                raised.transform.localScale = new Vector3(
                    Mathf.Max(b.size.x, 1.2f), 0.24f, Mathf.Max(b.size.z, 1.2f));
                Object.DestroyImmediate(raised.GetComponent<Collider>()); // decorative trim height but walkable via ground
                raised.GetComponent<Renderer>().sharedMaterial = walkMat;
                SetStaticRecursive(raised);

                // Road-edge kerb strip
                float edgeX = b.center.x >= 0f ? b.min.x : b.max.x;
                var kerb = GameObject.CreatePrimitive(PrimitiveType.Cube);
                kerb.name = $"GQ_SideKerb_{_sidewalksRaised}";
                kerb.transform.SetParent(_root, true);
                kerb.transform.position = new Vector3(edgeX, 0.16f, b.center.z);
                kerb.transform.localScale = new Vector3(0.42f, 0.32f, Mathf.Clamp(b.size.z, 2f, 12f));
                Object.DestroyImmediate(kerb.GetComponent<Collider>());
                kerb.GetComponent<Renderer>().sharedMaterial = kerbMat;
                SetStaticRecursive(kerb);

                _sidewalksRaised++;
            }
            Debug.Log($"[GQ] Raised {_sidewalksRaised} sidewalk replacements with kerbs.");
        }

        // ─── Stage 5: dark dirt edge blends so road/ground seams don't card ──
        static void Stage5_EdgeBlendDecals()
        {
            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.name = "GQ_EdgeBlend";
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", EdgeBlend);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.08f);
            string path = $"{MatDir}/GQ_EdgeBlend.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
            {
                EditorUtility.CopySerialized(mat, existing);
                Object.DestroyImmediate(mat);
                mat = existing;
            }
            else AssetDatabase.CreateAsset(mat, path);

            // Place blends along main / market / west lanes every ~8 m
            void Place(float x, float z, float s)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                go.name = $"GQ_EdgeBlend_{_blends}";
                go.transform.SetParent(_root, true);
                go.transform.position = new Vector3(x, 0.025f, z);
                go.transform.rotation = Quaternion.Euler(90f, UnityEngine.Random.Range(0f, 360f), 0f);
                go.transform.localScale = new Vector3(s, s * UnityEngine.Random.Range(0.45f, 0.85f), 1f);
                Object.DestroyImmediate(go.GetComponent<Collider>());
                go.GetComponent<Renderer>().sharedMaterial = mat;
                SetStaticRecursive(go);
                _blends++;
            }

            for (float z = -70f; z <= 70f; z += 8f)
            {
                Place(UnityEngine.Random.Range(-5.5f, 5.5f), z, UnityEngine.Random.Range(2.2f, 4.0f));
                Place(UnityEngine.Random.Range(24f, 38f), z, UnityEngine.Random.Range(2.0f, 3.6f));
                Place(UnityEngine.Random.Range(-48f, -30f), z, UnityEngine.Random.Range(2.0f, 3.6f));
            }
            // Critique hotspots
            foreach (var c in new[] {
                new Vector3(-12.2f, 0f, -45f), new Vector3(50f, 0f, 2f),
                new Vector3(-1.7f, 0f, -57f), new Vector3(58f, 0f, -35f),
                new Vector3(0f, 0f, -9f)
            })
            {
                for (int i = 0; i < 4; i++)
                    Place(c.x + UnityEngine.Random.Range(-5f, 5f), c.z + UnityEngine.Random.Range(-5f, 5f),
                        UnityEngine.Random.Range(2.5f, 4.5f));
            }
            Debug.Log($"[GQ] Placed {_blends} dark edge-blend decals.");
        }

        static void VerifySpawnClear()
        {
            var spawn = new Vector3(0f, 0.05f, -63f);
            var hits = Physics.OverlapSphere(spawn, 0.55f, ~0, QueryTriggerInteraction.Ignore);
            int bad = 0;
            foreach (var h in hits)
            {
                if (h == null) continue;
                string n = h.gameObject.name;
                // Ground / road underfoot OK
                if (n == "Ground" || n.StartsWith("Road_") || n.StartsWith("Conn_") || n == "Beach_Dirt")
                    continue;
                // Ignore our decorative no-collider pieces (shouldn't appear)
                bad++;
                Debug.LogWarning($"[GQ] Spawn overlap: {n}");
            }
            if (bad > 0)
                Debug.LogWarning($"[GQ] Spawn has {bad} non-ground overlaps — inspect before play.");
            else
                Debug.Log("[GQ] Spawn (0,0.05,-63) clear of non-ground colliders.");
        }

        // ─── Pale metric (fixed thresholds, post ON) ─────────────────────────
        struct PaleMetric
        {
            public int total;
            public int el10, el14, el01;
            public string detail;
        }

        static PaleMetric MeasurePale(string label)
        {
            var shots = new (string name, Vector3 pos, float yaw, float pitch)[]
            {
                ("EL_10", new Vector3(-13.58f, 1.70f, -44.34f), 11.3f, 2.4f),
                ("EL_14", new Vector3(49.92f, 1.70f, 2.33f), 138.5f, 4.8f),
                ("EL_01", new Vector3(-1.68f, 1.70f, -57.80f), 43.6f, 1.5f),
            };
            const int W = 1600, H = 900;
            const float LumMin = 0.18f, LumMax = 0.70f, SatMax = 0.42f;

            var go = new GameObject("__GQ_PaleCam");
            var cam = go.AddComponent<Camera>();
            cam.allowHDR = true;
            cam.fieldOfView = 72f;
            cam.nearClipPlane = 0.08f;
            cam.farClipPlane = 280f;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.enabled = false;
            var ud = cam.GetUniversalAdditionalCameraData();
            if (ud != null)
            {
                ud.renderPostProcessing = true;
                ud.antialiasing = AntialiasingMode.None;
            }

            var m = new PaleMetric();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== {label} post={ud != null && ud.renderPostProcessing} ===");
            Directory.CreateDirectory(Path.GetFullPath("_research/pale_detect"));

            foreach (var (name, pos, yaw, pitch) in shots)
            {
                cam.transform.position = pos;
                cam.transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
                var tex = Grab(cam, W, H);
                File.WriteAllBytes(Path.GetFullPath($"_research/pale_detect/{name}_{label}.png"), tex.EncodeToPNG());
                var px = tex.GetPixels32();
                int yMax = (int)(H * 0.55f);
                int pale = 0;
                for (int y = 0; y < yMax; y += 2)
                for (int x = 0; x < W; x += 2)
                {
                    var c = px[y * W + x];
                    float lr = c.r / 255f, lg = c.g / 255f, lb = c.b / 255f;
                    float lum = 0.2126f * lr + 0.7152f * lg + 0.0722f * lb;
                    float maxc = Mathf.Max(lr, Mathf.Max(lg, lb));
                    float minc = Mathf.Min(lr, Mathf.Min(lg, lb));
                    float sat = maxc > 1e-5f ? (maxc - minc) / maxc : 0f;
                    bool warm = (lr + lg) > (2f * lb + 0.01f) || (lr > lb && lg >= lb * 0.85f);
                    if (lum >= LumMin && lum <= LumMax && sat <= SatMax && warm) pale++;
                }
                sb.AppendLine($"{name} paleFixed={pale}");
                if (name == "EL_10") m.el10 = pale;
                if (name == "EL_14") m.el14 = pale;
                if (name == "EL_01") m.el01 = pale;
                m.total += pale;
                Object.DestroyImmediate(tex);
            }
            Object.DestroyImmediate(go);
            m.detail = sb.ToString();
            File.AppendAllText(Path.GetFullPath(MetricPath), m.detail + "\n");
            return m;
        }

        static void WriteMetrics(PaleMetric before, PaleMetric after)
        {
            var md = File.Exists(Path.GetFullPath(ReportPath))
                ? File.ReadAllText(Path.GetFullPath(ReportPath))
                : "# GROUND_QUAD_SOURCES\n";
            var appendix = new System.Text.StringBuilder();
            appendix.AppendLine();
            appendix.AppendLine("---");
            appendix.AppendLine();
            appendix.AppendLine("## Fix verification (AaaGroundQuadPass)");
            appendix.AppendLine();
            appendix.AppendLine("Fixed-threshold pale detector, post-processing ON, EL_10/14/01.");
            appendix.AppendLine();
            appendix.AppendLine("| View | Before | After | Δ |");
            appendix.AppendLine("|---|---:|---:|---:|");
            appendix.AppendLine($"| EL_10 | {before.el10} | {after.el10} | {after.el10 - before.el10} |");
            appendix.AppendLine($"| EL_14 | {before.el14} | {after.el14} | {after.el14 - before.el14} |");
            appendix.AppendLine($"| EL_01 | {before.el01} | {after.el01} | {after.el01 - before.el01} |");
            appendix.AppendLine($"| **Total** | **{before.total}** | **{after.total}** | **{after.total - before.total}** |");
            appendix.AppendLine();
            appendix.AppendLine($"Actions: stripesRemoved={_stripesRemoved}, matsDarkened={_matsDarkened}, kerbsRetinted={_kerbsRetinted}, sidewalksRaised={_sidewalksRaised}, edgeBlends={_blends}.");
            appendix.AppendLine($"Frames: `_research/pale_detect/EL_*_BEFORE.png` / `*_AFTER.png`.");
            // Replace prior verification section if present
            int idx = md.IndexOf("## Fix verification", StringComparison.Ordinal);
            if (idx >= 0) md = md.Substring(0, idx).TrimEnd() + "\n";
            File.WriteAllText(Path.GetFullPath(ReportPath), md + appendix);
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

        static Material UpsertMat(string name, Color baseColor, string albedoPath, string normalPath)
        {
            string assetPath = $"{MatDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                mat.name = name;
                AssetDatabase.CreateAsset(mat, assetPath);
            }
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", baseColor);
            var albedo = AssetDatabase.LoadAssetAtPath<Texture2D>(albedoPath);
            var normal = AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath);
            if (albedo != null)
            {
                mat.mainTexture = albedo;
                if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", albedo);
            }
            if (normal != null && mat.HasProperty("_BumpMap"))
            {
                mat.SetTexture("_BumpMap", normal);
                mat.EnableKeyword("_NORMALMAP");
            }
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.18f);
            mat.mainTextureScale = new Vector2(3.2f, 3.2f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static void SetStaticRecursive(GameObject go)
        {
            go.isStatic = true;
            foreach (Transform c in go.transform) SetStaticRecursive(c.gameObject);
        }
    }
}
#endif
