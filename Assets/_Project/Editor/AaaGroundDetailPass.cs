#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ArenaFps.Editor
{
    /// <summary>
    /// Retexture Arena ground/road with imported 4K Zzz PBR sets, vary asphalt per segment,
    /// retexture kerbs, scatter debris/crack overlays evenly across the whole playable map.
    /// Idempotent under GD_GroundDetail. Does not touch player / weapons / grading.
    /// Menu: Arena FPS / AAA Ground Detail Pass
    /// </summary>
    public static class AaaGroundDetailPass
    {
        const string ScenePath = "Assets/_Project/Scenes/Arena.unity";
        const string RootName = "GD_GroundDetail";
        const string MatDir = "Assets/_Project/Art/Materials/GroundDetail";
        const string ZzzMat = "Assets/_Project/Art/Materials/Zzz";
        const string ReportPath = "_research/zzz_ground_detail_pass.txt";

        // Shared material asset keys → Zzz source mats
        const string SrcDamagedA = "Zzz_damaged_asphalt_vizcebf_4k";
        const string SrcDamagedB = "Zzz_damaged_asphalt_vizhdcz_4k";
        const string SrcRough = "Zzz_rough_asphalt_vlpqdf1_4k";
        const string SrcWet = "Zzz_wet_destroyed_asphalt_si1odala_4k";
        const string SrcConcrete = "Zzz_concrete_pavement_wlrvaf3_4k";
        const string SrcDirt = "Zzz_military_trenches_ground_patch_rock_s_04_yd0lfcq_mid";
        const string SrcDebris = "Zzz_road_debris_sgvlofg_4k";
        const string SrcCracks = "Zzz_asphalt_cracks";

        static Transform _root;
        static Transform _map;
        static readonly Dictionary<string, Material> _instanceMats = new();
        static readonly StringBuilder _assignLog = new();
        static int _roads, _kerbs, _overlays, _walks;

        [MenuItem("Arena FPS/AAA Ground Detail Pass")]
        public static void Run()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.isPlaying = false;
                Debug.LogWarning("[GD] Exit play mode and re-run.");
                return;
            }

            OpenArena();
            _map = GameObject.Find("ThreeLaneMap")?.transform;
            if (_map == null)
            {
                Debug.LogError("[GD] ThreeLaneMap missing.");
                return;
            }

            // Require import pass materials
            if (AssetDatabase.LoadAssetAtPath<Material>($"{ZzzMat}/{SrcDamagedA}.mat") == null)
            {
                Debug.LogError("[GD] Zzz materials missing — run Arena FPS / AAA Zzz Import Pass first.");
                return;
            }

            EnsureDir(MatDir);
            EnsureDir("_research");
            _instanceMats.Clear();
            _assignLog.Clear();
            _roads = _kerbs = _overlays = _walks = 0;

            ClearPrevious();
            _root = new GameObject(RootName).transform;
            _root.SetParent(_map, false);

            try
            {
                RetextureRoadsAndGround();
                RetextureKerbs();
                RetextureRaisedWalks();
                ScatterOverlaysEvenly();
                VerifyNormals();
                VerifySpawnClear();
                CountInvisibleColliders();

                SetStaticRecursive(_root.gameObject);
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
                EditorSceneManager.SaveOpenScenes();
                AssetDatabase.SaveAssets();

                var report = new StringBuilder();
                report.AppendLine("=== AaaGroundDetailPass " + DateTime.Now.ToString("o") + " ===");
                report.AppendLine($"roads={_roads} kerbs={_kerbs} walks={_walks} overlays={_overlays}");
                report.AppendLine("--- assignments ---");
                report.Append(_assignLog);
                File.WriteAllText(ReportPath, report.ToString());

                Debug.Log($"[GD] DONE roads={_roads} kerbs={_kerbs} walks={_walks} overlays={_overlays}. See {ReportPath}");
            }
            catch (Exception ex)
            {
                Debug.LogError("[GD] FATAL: " + ex);
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
                EditorSceneManager.SaveOpenScenes();
                throw;
            }
        }

        static void OpenArena()
        {
            if (SceneManager.GetActiveScene().path != ScenePath)
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        static void EnsureDir(string d)
        {
            var full = Path.IsPathRooted(d) ? d : Path.GetFullPath(d);
            if (!Directory.Exists(full)) Directory.CreateDirectory(full);
            if (d.StartsWith("Assets/") && !AssetDatabase.IsValidFolder(d))
            {
                string[] parts = d.Split('/');
                string cur = parts[0];
                for (int i = 1; i < parts.Length; i++)
                {
                    string next = cur + "/" + parts[i];
                    if (!AssetDatabase.IsValidFolder(next))
                        AssetDatabase.CreateFolder(cur, parts[i]);
                    cur = next;
                }
            }
        }

        static void ClearPrevious()
        {
            var doomed = new List<GameObject>();
            if (_map != null)
            {
                foreach (Transform t in _map)
                {
                    if (t.name == RootName || t.name.StartsWith("GD_"))
                        doomed.Add(t.gameObject);
                }
            }
            var orphan = GameObject.Find(RootName);
            if (orphan != null && !doomed.Contains(orphan)) doomed.Add(orphan);
            // Also purge any GD_ overlays under map children
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include))
            {
                if (t != null && t.name.StartsWith("GD_") && !doomed.Contains(t.gameObject))
                {
                    // Only destroy roots we own (not nested under doomed)
                    if (t.parent == null || t.parent == _map || t.name == RootName)
                        doomed.Add(t.gameObject);
                }
            }
            foreach (var go in doomed)
                if (go != null) Object.DestroyImmediate(go);
        }

        // ── Segment assignment table ─────────────────────────────────────────
        struct SegAssign
        {
            public string srcMat;
            public float tiling;
            public Color tint;
            public float bump;
            public float smoothnessMul;
        }

        static SegAssign AssignFor(string objectName, string currentMatName)
        {
            // Deterministic per-name jitter so seams don't line up
            int h = Mathf.Abs(objectName.GetHashCode());
            float tileJitter = 0.85f + (h % 9) * 0.08f; // 0.85–1.49
            float tintJ = 1f + ((h % 5) - 2) * 0.03f;

            bool dirt = objectName.Contains("Dirt") || currentMatName.Contains("Dirt")
                        || objectName == "Beach_Dirt"
                        || objectName is "Road_A4" or "Road_B1" or "Road_B5" or "Road_C2"
                        || objectName.StartsWith("Conn_X2") || objectName.StartsWith("Conn_X7")
                        || objectName.StartsWith("Conn_X9") || objectName.StartsWith("Conn_X10")
                        || objectName.StartsWith("Conn_X15") || objectName.StartsWith("Conn_X17");

            if (dirt)
            {
                return new SegAssign
                {
                    srcMat = SrcDirt,
                    tiling = 7.5f * tileJitter,
                    // Let the 4K/2K albedo carry value — mild warm pull only.
                    tint = new Color(0.92f * tintJ, 0.86f * tintJ, 0.74f * tintJ, 1f),
                    bump = 1.35f,
                    smoothnessMul = 0.85f
                };
            }

            // Wet / puddle — sparse low connectors near beach / hubs
            if (objectName is "Conn_X2_BeachCut" or "Conn_X1_BlueHub"
                || objectName == "Conn_X3_BoatMain")
            {
                // X2 is dirt above; X1/X3 get wet sparingly
                if (objectName != "Conn_X2_BeachCut")
                {
                    return new SegAssign
                    {
                        srcMat = SrcWet,
                        tiling = 9f * tileJitter,
                        tint = new Color(0.88f * tintJ, 0.87f * tintJ, 0.85f * tintJ, 1f),
                        bump = 1.4f,
                        smoothnessMul = 1.35f
                    };
                }
            }

            // Plaza / sidewalk-ish hubs → concrete pavement
            if (objectName.Contains("Plaza") || objectName == "Conn_X13_Plaza"
                || objectName == "Conn_X14_RedHub")
            {
                return new SegAssign
                {
                    srcMat = SrcConcrete,
                    tiling = 10f * tileJitter,
                    tint = new Color(0.90f * tintJ, 0.87f * tintJ, 0.80f * tintJ, 1f),
                    bump = 1.25f,
                    smoothnessMul = 0.9f
                };
            }

            // Main lanes — alternate damaged A/B
            string src;
            float baseTile;
            Color tint;
            switch (objectName)
            {
                case "Road_A1":
                case "Road_B2":
                case "Road_C3":
                case "Conn_X4_Construction":
                case "Conn_X11_Baskets":
                    src = SrcDamagedA; baseTile = 12f;
                    tint = new Color(0.82f, 0.80f, 0.76f); break;
                case "Road_A2":
                case "Road_B3":
                case "Road_C4":
                case "Conn_X6_BankThrough":
                case "Conn_X16_DeliSpices":
                    src = SrcDamagedB; baseTile = 11f;
                    tint = new Color(0.80f, 0.78f, 0.74f); break;
                case "Road_A3":
                case "Road_B4":
                case "Road_C5":
                case "Road_C1":
                case "Conn_X1_BlueHub":
                    src = SrcRough; baseTile = 13f;
                    tint = new Color(0.78f, 0.76f, 0.72f); break;
                case "Ground":
                    // Large 118×154m plane: ~2.0m/repeat so grit reads at 1.7m eye height.
                    // Prior 14× tiling collapsed to a featureless sheet at walking distance.
                    src = SrcRough; baseTile = 58f;
                    tint = new Color(0.58f, 0.54f, 0.48f); break;
                default:
                    // Remaining asphalt connectors — hash pick among 3
                    int pick = h % 3;
                    src = pick == 0 ? SrcDamagedA : pick == 1 ? SrcDamagedB : SrcRough;
                    baseTile = 10f + (h % 5);
                    tint = new Color(0.88f, 0.85f, 0.81f); break;
            }

            // One low wet patch on B-lane south of mid for puddle variety
            if (objectName == "Road_B2" && (h % 2 == 0))
            {
                // keep damaged A — wet reserved for connectors
            }

            return new SegAssign
            {
                srcMat = src,
                tiling = baseTile * tileJitter,
                tint = new Color(
                    Mathf.Clamp01(tint.r * tintJ),
                    Mathf.Clamp01(tint.g * tintJ * 0.98f),
                    Mathf.Clamp01(tint.b * tintJ * 0.95f), 1f),
                bump = 1.05f + (h % 4) * 0.05f,
                smoothnessMul = src == SrcWet ? 1.3f : 1f
            };
        }

        static void RetextureRoadsAndGround()
        {
            if (_map == null) return;
            foreach (Transform t in _map.GetComponentsInChildren<Transform>(true))
            {
                if (t == null) continue;
                string n = t.name;
                bool isGroundish = n == "Ground" || n == "Beach_Dirt"
                    || n.StartsWith("Road_") || n.StartsWith("Conn_");
                if (!isGroundish) continue;
                if (n.EndsWith("_Stripe")) continue;
                if (n.StartsWith("GD_") || n.StartsWith("GQ_Edge") || n.StartsWith("GQ_Raised")) continue;

                var r = t.GetComponent<Renderer>();
                if (r == null || r.sharedMaterial == null) continue;

                var assign = AssignFor(n, r.sharedMaterial.name);
                // Override Beach_Dirt / dirt roads already handled in AssignFor

                // Wet sparingly: only Conn_X1 and a couple of low connectors
                if (n is "Conn_X3_BoatMain")
                {
                    assign.srcMat = SrcWet;
                    assign.tiling = 8.5f;
                    assign.tint = new Color(0.88f, 0.86f, 0.84f);
                    assign.smoothnessMul = 1.4f;
                    assign.bump = 1.4f;
                }

                var mat = InstanceMat($"GD_{assign.srcMat}_{n}", assign);
                r.sharedMaterial = mat;
                _roads++;
                _assignLog.AppendLine($"{n} -> {assign.srcMat} tile={assign.tiling:F2} tint={assign.tint}");
            }
            Debug.Log($"[GD] Retextured {_roads} road/ground renderers.");
        }

        static Material InstanceMat(string name, SegAssign a)
        {
            if (_instanceMats.TryGetValue(name, out var cached) && cached != null)
                return cached;

            var src = AssetDatabase.LoadAssetAtPath<Material>($"{ZzzMat}/{a.srcMat}.mat");
            if (src == null)
            {
                Debug.LogError("[GD] Missing source mat " + a.srcMat);
                return null;
            }

            string assetPath = $"{MatDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (mat == null)
            {
                mat = new Material(src) { name = name };
                AssetDatabase.CreateAsset(mat, assetPath);
            }
            else
            {
                EditorUtility.CopySerialized(src, mat);
                mat.name = name;
            }

            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", a.tint);

            var scale = new Vector2(a.tiling, a.tiling * (0.88f + (Mathf.Abs(name.GetHashCode()) % 4) * 0.04f));
            mat.mainTextureScale = scale;
            if (mat.HasProperty("_BaseMap")) mat.SetTextureScale("_BaseMap", scale);
            if (mat.HasProperty("_BumpMap"))
            {
                mat.SetTextureScale("_BumpMap", scale);
                mat.EnableKeyword("_NORMALMAP");
                if (mat.HasProperty("_BumpScale")) mat.SetFloat("_BumpScale", a.bump);
            }
            if (mat.HasProperty("_MetallicGlossMap"))
                mat.SetTextureScale("_MetallicGlossMap", scale);
            if (mat.HasProperty("_OcclusionMap"))
                mat.SetTextureScale("_OcclusionMap", scale);

            if (mat.HasProperty("_Smoothness"))
            {
                // Source mats set Smoothness=1 to multiply map; scale overall feel
                float baseSm = mat.GetFloat("_Smoothness");
                mat.SetFloat("_Smoothness", Mathf.Clamp01(baseSm * a.smoothnessMul));
            }

            // Hard requirement: normal map present
            if (mat.HasProperty("_BumpMap") && mat.GetTexture("_BumpMap") == null)
                Debug.LogWarning($"[GD] Material {name} has NO normal map after assign.");

            EditorUtility.SetDirty(mat);
            _instanceMats[name] = mat;
            return mat;
        }

        static void RetextureKerbs()
        {
            var assign = new SegAssign
            {
                srcMat = SrcConcrete,
                tiling = 3.2f,
                tint = new Color(0.78f, 0.72f, 0.62f, 1f),
                bump = 1.45f,
                smoothnessMul = 0.8f
            };
            var kerbMat = InstanceMat("GD_Kerb_ConcretePavement", assign);

            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include))
            {
                if (t == null) continue;
                string n = t.name;
                bool isKerb = n.StartsWith("OP_Kerb") || n.StartsWith("OP_ForceKerb")
                              || n.StartsWith("PH_Kerb") || n.StartsWith("GQ_SideKerb")
                              || n.Contains("KerbWorn") || (n.Contains("Kerb") && t.GetComponent<Renderer>() != null);
                if (!isKerb) continue;
                // Skip wall-dirt false positives
                if (n.Contains("WallDirt")) continue;

                var r = t.GetComponent<Renderer>();
                if (r == null) continue;
                r.sharedMaterial = kerbMat;
                r.enabled = true;
                _kerbs++;
            }
            _assignLog.AppendLine($"KERBS x{_kerbs} -> {SrcConcrete}");
            Debug.Log($"[GD] Kerbs retextured: {_kerbs}");
        }

        static void RetextureRaisedWalks()
        {
            var assign = new SegAssign
            {
                srcMat = SrcConcrete,
                tiling = 4.5f,
                tint = new Color(0.88f, 0.84f, 0.76f, 1f),
                bump = 1.3f,
                smoothnessMul = 0.85f
            };
            var walkMat = InstanceMat("GD_RaisedWalk_Concrete", assign);

            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include))
            {
                if (t == null) continue;
                if (!(t.name.StartsWith("GQ_RaisedWalk") || t.name.StartsWith("Sidewalk_"))) continue;
                var r = t.GetComponent<Renderer>();
                if (r == null || !r.enabled) continue;
                r.sharedMaterial = walkMat;
                _walks++;
            }
            _assignLog.AppendLine($"WALKS x{_walks} -> {SrcConcrete}");
        }

        // ── Even overlay scatter (debris + cracks), no colliders ─────────────
        static void ScatterOverlaysEvenly()
        {
            var debris = AssetDatabase.LoadAssetAtPath<Material>($"{ZzzMat}/{SrcDebris}.mat");
            var cracks = AssetDatabase.LoadAssetAtPath<Material>($"{ZzzMat}/{SrcCracks}.mat");
            if (debris == null && cracks == null)
            {
                Debug.LogWarning("[GD] No overlay materials — skip scatter.");
                return;
            }

            // Playable extents covering all three lanes + connectors
            float xMin = -52f, xMax = 58f, zMin = -72f, zMax = 72f;
            float cell = 7.5f; // denser even grid across whole map
            var rng = new System.Random(0x6D44A11); // fixed seed → idempotent layout

            var debrisMat = debris != null ? CloneOverlay("GD_DebrisOverlay", debris) : null;
            var crackMat = cracks != null ? CloneOverlay("GD_CrackOverlay", cracks) : null;

            for (float z = zMin; z <= zMax; z += cell)
            {
                for (float x = xMin; x <= xMax; x += cell)
                {
                    // Jitter inside cell so it doesn't look like a grid
                    float jx = x + (float)(rng.NextDouble() * 5.0 - 2.5);
                    float jz = z + (float)(rng.NextDouble() * 5.0 - 2.5);

                    // Keep spawn clear
                    if (Vector2.Distance(new Vector2(jx, jz), new Vector2(0f, -63f)) < 3.5f)
                        continue;

                    // Raycast down to find ground Y / confirm ground surface
                    Vector3 origin = new Vector3(jx, 8f, jz);
                    if (!Physics.Raycast(origin, Vector3.down, out var hit, 20f))
                        continue;
                    string hn = hit.collider.name;
                    bool groundish = hn == "Ground" || hn.StartsWith("Road_") || hn.StartsWith("Conn_")
                                     || hn.Contains("Dirt") || hn.Contains("Sidewalk")
                                     || hn.Contains("Kerb") || hn.StartsWith("GQ_Raised");
                    if (!groundish) continue;
                    // Skip steep hits
                    if (Vector3.Angle(hit.normal, Vector3.up) > 25f) continue;

                    int roll = rng.Next(100);
                    Material use = null;
                    float size;
                    if (roll < 50 && debrisMat != null)
                    {
                        use = debrisMat;
                        size = 2.0f + (float)rng.NextDouble() * 3.0f;
                    }
                    else if (crackMat != null)
                    {
                        use = crackMat;
                        size = 2.8f + (float)rng.NextDouble() * 4.0f;
                    }
                    else continue;

                    PlaceOverlayQuad(hit.point + hit.normal * 0.022f, hit.normal,
                        size, (float)(rng.NextDouble() * 360.0), use);
                }
            }

            // Extra density along main lane centerlines so roads aren't empty mid-cell
            PlaceLaneExtras(rng, debrisMat, crackMat, 0f);     // mid
            PlaceLaneExtras(rng, debrisMat, crackMat, -38f);   // west
            PlaceLaneExtras(rng, debrisMat, crackMat, 36f);    // east

            Debug.Log($"[GD] Overlay quads placed: {_overlays}");
            _assignLog.AppendLine($"OVERLAYS x{_overlays} (debris+cracks, even grid)");
        }

        static void PlaceLaneExtras(System.Random rng, Material debris, Material cracks, float laneX)
        {
            for (float z = -70f; z <= 70f; z += 7f)
            {
                float x = laneX + (float)(rng.NextDouble() * 4.0 - 2.0);
                float zz = z + (float)(rng.NextDouble() * 3.0 - 1.5);
                if (Vector2.Distance(new Vector2(x, zz), new Vector2(0f, -63f)) < 3.5f) continue;
                if (!Physics.Raycast(new Vector3(x, 8f, zz), Vector3.down, out var hit, 20f)) continue;
                string hn = hit.collider.name;
                if (!(hn.StartsWith("Road_") || hn.StartsWith("Conn_") || hn == "Ground")) continue;

                var use = (rng.Next(100) < 60 && debris != null) ? debris : cracks;
                if (use == null) continue;
                float size = 1.4f + (float)rng.NextDouble() * 2.2f;
                PlaceOverlayQuad(hit.point + hit.normal * 0.02f, hit.normal,
                    size, (float)(rng.NextDouble() * 360.0), use);
            }
        }

        static Material CloneOverlay(string name, Material src)
        {
            string path = $"{MatDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(src) { name = name };
                AssetDatabase.CreateAsset(mat, path);
            }
            else
            {
                EditorUtility.CopySerialized(src, mat);
                mat.name = name;
            }
            // Ensure cutout / no metallic shine
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", new Color(0.92f, 0.88f, 0.82f, 1f));
            if (mat.HasProperty("_AlphaClip")) mat.SetFloat("_AlphaClip", 1f);
            if (mat.HasProperty("_Cutoff")) mat.SetFloat("_Cutoff", 0.18f);
            if (mat.HasProperty("_BumpScale")) mat.SetFloat("_BumpScale", 1.2f);
            mat.EnableKeyword("_ALPHATEST_ON");
            mat.SetOverrideTag("RenderType", "TransparentCutout");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
            // Overlay shouldn't tile — clamp
            if (mat.HasProperty("_BaseMap")) mat.SetTextureScale("_BaseMap", Vector2.one);
            if (mat.HasProperty("_BumpMap"))
            {
                mat.SetTextureScale("_BumpMap", Vector2.one);
                mat.EnableKeyword("_NORMALMAP");
            }
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static void PlaceOverlayQuad(Vector3 pos, Vector3 normal, float size, float yaw, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = $"GD_Overlay_{_overlays}";
            go.transform.SetParent(_root, true);
            go.transform.position = pos;
            // Align quad to ground (Quad faces +Z by default; rotate to lie on XZ)
            go.transform.rotation = Quaternion.LookRotation(-normal, Vector3.forward)
                                    * Quaternion.Euler(0f, 0f, yaw);
            // Simpler: pitch 90 + yaw
            go.transform.rotation = Quaternion.Euler(90f, yaw, 0f);
            go.transform.localScale = new Vector3(size, size * (0.55f + (size * 0.02f)), 1f);

            // CRITICAL: no collider on decorative overlays
            var col = go.GetComponent<Collider>();
            if (col != null) Object.DestroyImmediate(col);

            var r = go.GetComponent<Renderer>();
            if (r != null) r.sharedMaterial = mat;
            SetStaticRecursive(go);
            _overlays++;
        }

        static void VerifyNormals()
        {
            int missing = 0;
            foreach (Transform t in _map.GetComponentsInChildren<Transform>(true))
            {
                if (t == null) continue;
                string n = t.name;
                bool isGroundish = n == "Ground" || n == "Beach_Dirt"
                    || n.StartsWith("Road_") || n.StartsWith("Conn_")
                    || n.Contains("Kerb") || n.StartsWith("GQ_RaisedWalk");
                if (!isGroundish) continue;
                var r = t.GetComponent<Renderer>();
                if (r == null || r.sharedMaterial == null) continue;
                var m = r.sharedMaterial;
                if (!m.HasProperty("_BumpMap") || m.GetTexture("_BumpMap") == null)
                {
                    missing++;
                    _assignLog.AppendLine("MISSING NORMAL: " + n + " mat=" + m.name);
                }
            }
            _assignLog.AppendLine($"missingNormals={missing}");
            if (missing > 0)
                Debug.LogWarning($"[GD] {missing} ground renderers still lack normals.");
            else
                Debug.Log("[GD] All ground/road/kerb materials have normals.");
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
                if (n == "Ground" || n.StartsWith("Road_") || n.StartsWith("Conn_") || n == "Beach_Dirt")
                    continue;
                if (n.StartsWith("GD_")) { bad++; Debug.LogError("[GD] Spawn blocked by " + n); continue; }
                bad++;
                Debug.LogWarning("[GD] Spawn overlap: " + n);
            }
            _assignLog.AppendLine($"spawnOverlaps={bad}");
        }

        static void CountInvisibleColliders()
        {
            int invis = 0;
            foreach (var col in Object.FindObjectsByType<Collider>(FindObjectsInactive.Include))
            {
                if (col == null || col.isTrigger) continue;
                var r = col.GetComponent<Renderer>();
                if (r == null) r = col.GetComponentInChildren<Renderer>();
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy)
                {
                    // Ground plane might be fine; count only if truly no visible mesh
                    if (r == null || !r.enabled)
                    {
                        // Allow disabled sidewalks that already had colliders stripped by GQ pass
                        if (col.GetComponent<MeshFilter>() == null && col is MeshCollider)
                        { invis++; _assignLog.AppendLine("INVIS_COL: " + col.name); }
                        else if (r != null && !r.enabled)
                        { invis++; _assignLog.AppendLine("INVIS_COL(disabledR): " + col.name); }
                    }
                }
            }
            _assignLog.AppendLine($"invisibleCollidersApprox={invis}");
        }

        static void SetStaticRecursive(GameObject go)
        {
            go.isStatic = true;
            foreach (Transform c in go.transform)
                SetStaticRecursive(c.gameObject);
        }
    }
}
#endif
