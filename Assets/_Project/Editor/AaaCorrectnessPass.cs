#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ArenaFps.Editor
{
    /// <summary>
    /// Correctness pass for floating frustum fillers, invisible colliders, gunmetal viewmodel,
    /// and eye-level ground tiling. Idempotent under COR_* prefixes.
    /// Menu: Arena FPS / AAA Correctness Pass
    /// </summary>
    public static class AaaCorrectnessPass
    {
        const string ScenePath = "Assets/_Project/Scenes/Arena.unity";
        const string ReportPath = "_research/CORRECTNESS_REPORT.md";
        const string LogPath = "_research/correctness_pass.txt";
        const string BreakRootName = "COR_GroundBreakup";
        const string GunMatPath = "Assets/_Project/Art/Materials/Mat_Viewmodel_Gunmetal.mat";
        const string GroundMatPath = "Assets/_Project/Art/Materials/GroundDetail/GD_Zzz_rough_asphalt_vlpqdf1_4k_Ground.mat";
        const string DetailAlbedoPath = "Assets/_Project/Art/Imported/Zzz/ground/damaged_asphalt_vizhdcz_4k/Damaged_Asphalt_vizhdcz_4K_BaseColor.jpg";
        const string DetailNormalPath = "Assets/_Project/Art/Imported/Zzz/ground/damaged_asphalt_vizhdcz_4k/Damaged_Asphalt_vizhdcz_4K_Normal.jpg";
        const string MetalNormalPath = "Assets/_Project/Art/Textures/Generated/P2_Metal_Normal.png";
        const float LateralReach = 0.65f;
        const float RoofSeatMax = 0.55f;

        static readonly StringBuilder Log = new();
        static int _deleted, _reseated, _leftAlone;
        static int _invisBefore, _invisAfter;
        static long _trisBefore, _trisAfter;
        static int _overlaysPlaced;

        [MenuItem("Arena FPS/AAA Correctness Pass")]
        public static void Run()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.isPlaying = false;
                Debug.LogWarning("[COR] Exit play mode and re-run.");
                return;
            }

            Log.Clear();
            _deleted = _reseated = _leftAlone = 0;
            _overlaysPlaced = 0;
            OpenArena();

            _trisBefore = CountTris();
            _invisBefore = CountInvisibleColliders(true);

            FixInvisibleCollidersAtSource();
            FixFloatingProps();
            FixGunMaterialOnly();
            FixGroundTilingAndBreakup();

            _invisAfter = CountInvisibleColliders(true);
            _trisAfter = CountTris();

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
            AssetDatabase.SaveAssets();

            WriteLog();
            Debug.Log($"[COR] DONE deleted={_deleted} reseated={_reseated} leftAlone={_leftAlone} invis={_invisBefore}->{_invisAfter} tris={_trisBefore}->{_trisAfter}");
        }

        [MenuItem("Arena FPS/AAA Correctness Pass/Floaters Only")]
        public static void RunFloatersOnly()
        {
            OpenArena();
            Log.Clear();
            FixFloatingProps();
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
            WriteLog();
        }

        [MenuItem("Arena FPS/AAA Correctness Pass/Ground Only")]
        public static void RunGroundOnly()
        {
            OpenArena();
            Log.Clear();
            FixGroundTilingAndBreakup();
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
            AssetDatabase.SaveAssets();
            WriteLog();
        }

        static void OpenArena()
        {
            var active = SceneManager.GetActiveScene();
            if (active.path != ScenePath)
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        // ── Defect 2: invisible colliders ────────────────────────────────────

        static void FixInvisibleCollidersAtSource()
        {
            Log.AppendLine("=== DEFECT 2: Invisible colliders ===");
            // Persisted runtime FX singleton from a dirty Play-mode save.
            // Source: ImpactFx.Instance creates __ImpactFx with 24 pooled casing primitives.
            var fx = GameObject.Find("__ImpactFx");
            if (fx != null)
            {
                int casings = 0;
                foreach (var t in fx.GetComponentsInChildren<Transform>(true))
                    if (t.name.StartsWith("Casing_")) casings++;
                Log.AppendLine($"Destroyed persisted __ImpactFx (casings={casings}). Source fix: ImpactFx HideFlags.DontSave + collider off while pooled.");
                Object.DestroyImmediate(fx);
            }
            else
            {
                Log.AppendLine("__ImpactFx not present in scene (already clean).");
            }

            // Strip any remaining collider that has no MeshRenderer/MeshFilter on self/children
            // and is not a CharacterController / Player.
            int stripped = 0;
            var doomed = new List<Collider>();
            foreach (var c in Object.FindObjectsByType<Collider>(FindObjectsInactive.Include))
            {
                if (c == null || c is CharacterController) continue;
                if (c.transform.root != null && c.transform.root.name == "Player") continue;
                if (HasMeshRenderer(c.transform)) continue;
                // Decorative high trim: never keep collider-only
                doomed.Add(c);
            }
            foreach (var c in doomed)
            {
                if (c == null) continue;
                Log.AppendLine("  strip " + PathOf(c.transform));
                Object.DestroyImmediate(c);
                stripped++;
            }
            Log.AppendLine($"strippedExtra={stripped}");
        }

        static bool HasMeshRenderer(Transform t)
        {
            foreach (var r in t.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                // Mesh present counts even if GO currently inactive (pooled FX)
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) return true;
                if (r is SkinnedMeshRenderer smr && smr.sharedMesh != null) return true;
            }
            return false;
        }

        // ── Defect 1: floating props ─────────────────────────────────────────

        static void FixFloatingProps()
        {
            Log.AppendLine("=== DEFECT 1: Floating props ===");
            var targets = new List<GameObject>();
            foreach (var r in Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude))
            {
                if (r == null || !r.gameObject.scene.IsValid()) continue;
                string n = r.name;
                bool force = n.StartsWith("OP_Force");
                bool zzz = n.StartsWith("ZZZ_Facade_") || n.StartsWith("ZZZ_Roof_");
                if (!force && !zzz) continue;
                // Skip ground-level Force rubble/dirt/kerb — not mid-air defects
                if (n.StartsWith("OP_ForceRubble") || n.StartsWith("OP_ForceDirt") || n.StartsWith("OP_ForceKerb"))
                {
                    _leftAlone++;
                    continue;
                }
                targets.Add(r.gameObject);
            }

            // All OP_ForceCable / ForceSign / ForceAC are frustum fillers — delete on sight.
            // Legitimate wall mounts come from Stage4 / ZZZ placement with snap.
            var handled = new HashSet<GameObject>();
            foreach (var go in targets.ToList())
            {
                if (go == null) continue;
                string n = go.name;
                if (n.StartsWith("OP_ForceCable") || n.StartsWith("OP_ForceSign") || n.StartsWith("OP_ForceAC"))
                {
                    handled.Add(go);
                    Log.AppendLine($"  DELETE {PathOf(go.transform)} (force frustum filler)");
                    Object.DestroyImmediate(go);
                    _deleted++;
                }
            }

            foreach (var go in targets)
            {
                if (go == null) continue;
                if (handled.Contains(go)) continue;

                if (IsMounted(go))
                {
                    _leftAlone++;
                    Log.AppendLine($"  KEEP {PathOf(go.transform)} (mounted)");
                    continue;
                }

                // Try re-seat facade ebox against nearest wall
                bool canReseat = go.name.Contains("_ebox") || go.name.Contains("Facade");
                if (canReseat && TryReseatToWall(go))
                {
                    _reseated++;
                    Log.AppendLine($"  RESEAT {PathOf(go.transform)}");
                    continue;
                }

                // Roof props with large gap and no roof under them → try drop onto roof, else delete
                if (go.name.StartsWith("ZZZ_Roof_") && TryReseatToRoof(go))
                {
                    _reseated++;
                    Log.AppendLine($"  RESEAT_ROOF {PathOf(go.transform)}");
                    continue;
                }

                Log.AppendLine($"  DELETE {PathOf(go.transform)} (unsupported)");
                Object.DestroyImmediate(go);
                _deleted++;
            }

            // Second sweep: any remaining elevated dressing boxes (signs/yagi bits/etc.)
            // with neither roof seat nor lateral support.
            foreach (var r in Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude))
            {
                if (r == null || !r.enabled || !r.gameObject.scene.IsValid()) continue;
                if (r.transform.root != null && r.transform.root.name == "Player") continue;
                string n = r.name;
                if (n.Contains("Mass") || n.Contains("Wall_") || n.StartsWith("Win") || n.StartsWith("Door")
                    || n.StartsWith("Balcony") || n.StartsWith("Quoin") || n.Contains("Trim")
                    || n.Contains("Awning") || n.Contains("Glass"))
                    continue;
                var b = r.bounds;
                if (b.min.y < 3.2f) continue;
                float dim = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
                if (dim > 4.5f || dim < 0.08f) continue;
                bool dressing = n.StartsWith("OP_") || n.StartsWith("OD_") || n.StartsWith("ZZZ_")
                                || n.StartsWith("PH_") || n.StartsWith("Cat_") || n.StartsWith("Prop_");
                if (!dressing) continue;
                if (IsMounted(r.gameObject)) { _leftAlone++; continue; }
                Log.AppendLine($"  DELETE {PathOf(r.transform)} (elevated unsupported)");
                Object.DestroyImmediate(r.gameObject);
                _deleted++;
            }

            Log.AppendLine($"deleted={_deleted} reseated={_reseated} leftAlone={_leftAlone}");
        }

        static bool IsMounted(GameObject go)
        {
            var r = go.GetComponent<MeshRenderer>();
            if (r == null) return false;
            var b = r.bounds;

            // Roof / ledge seat: something solid within RoofSeatMax below that is NOT a road/ground plane
            if (HasRoofSeat(b, go)) return true;

            // Lateral wall / pole within LateralReach
            if (HasLateralSupport(b, go)) return true;

            return false;
        }

        static bool HasRoofSeat(Bounds b, GameObject self)
        {
            var origins = new Vector3[]
            {
                new Vector3(b.center.x, b.min.y + 0.02f, b.center.z),
                new Vector3(b.min.x + 0.05f, b.min.y + 0.02f, b.center.z),
                new Vector3(b.max.x - 0.05f, b.min.y + 0.02f, b.center.z),
                new Vector3(b.center.x, b.min.y + 0.02f, b.min.z + 0.05f),
                new Vector3(b.center.x, b.min.y + 0.02f, b.max.z - 0.05f),
            };
            foreach (var o in origins)
            {
                if (!Physics.Raycast(o, Vector3.down, out var h, RoofSeatMax + 0.15f, ~0, QueryTriggerInteraction.Ignore))
                    continue;
                if (h.collider.transform == self.transform || h.collider.transform.IsChildOf(self.transform))
                    continue;
                if (IsGroundLike(h.collider.gameObject.name)) continue;
                // Accept building / wall / roof / ledge
                if (h.distance <= RoofSeatMax) return true;
            }
            return false;
        }

        static bool HasLateralSupport(Bounds b, GameObject self)
        {
            var dirs = new Vector3[]
            {
                Vector3.forward, Vector3.back, Vector3.left, Vector3.right,
                (Vector3.forward + Vector3.right).normalized,
                (Vector3.forward + Vector3.left).normalized,
                (Vector3.back + Vector3.right).normalized,
                (Vector3.back + Vector3.left).normalized,
            };
            var origins = new Vector3[]
            {
                b.center,
                new Vector3(b.center.x, Mathf.Lerp(b.min.y, b.max.y, 0.3f), b.center.z),
                new Vector3(b.center.x, Mathf.Lerp(b.min.y, b.max.y, 0.7f), b.center.z),
            };
            foreach (var o in origins)
            {
                foreach (var d in dirs)
                {
                    foreach (var hit in Physics.RaycastAll(o, d, LateralReach, ~0, QueryTriggerInteraction.Ignore))
                    {
                        if (hit.collider == null) continue;
                        if (hit.collider.transform == self.transform || hit.collider.transform.IsChildOf(self.transform))
                            continue;
                        if (self.transform.IsChildOf(hit.collider.transform)) continue;
                        if (hit.normal.y > 0.65f) continue; // floor
                        if (IsGroundLike(hit.collider.gameObject.name)) continue;
                        return true;
                    }
                }
            }
            return false;
        }

        static bool CableNearStructure(List<GameObject> segs)
        {
            if (segs.Count == 0) return false;
            Bounds b = segs[0].GetComponent<MeshRenderer>().bounds;
            foreach (var g in segs)
            {
                var r = g.GetComponent<MeshRenderer>();
                if (r != null) b.Encapsulate(r.bounds);
            }
            // Probe ends
            var ends = new Vector3[]
            {
                new Vector3(b.min.x, b.center.y, b.min.z),
                new Vector3(b.max.x, b.center.y, b.max.z),
                new Vector3(b.min.x, b.center.y, b.max.z),
                new Vector3(b.max.x, b.center.y, b.min.z),
            };
            foreach (var e in ends)
            {
                foreach (var d in new[] { Vector3.forward, Vector3.back, Vector3.left, Vector3.right })
                {
                    if (Physics.Raycast(e, d, out var h, 2.5f, ~0, QueryTriggerInteraction.Ignore))
                    {
                        if (!IsGroundLike(h.collider.gameObject.name) && h.normal.y < 0.65f)
                            return true;
                    }
                }
            }
            return false;
        }

        static bool TryReseatToWall(GameObject go)
        {
            var r = go.GetComponent<MeshRenderer>();
            if (r == null) return false;
            var b = r.bounds;
            float bestDist = float.MaxValue;
            RaycastHit best = default;
            bool found = false;
            var dirs = new Vector3[]
            {
                Vector3.forward, Vector3.back, Vector3.left, Vector3.right,
                (Vector3.forward + Vector3.right).normalized,
                (Vector3.forward + Vector3.left).normalized,
                (Vector3.back + Vector3.right).normalized,
                (Vector3.back + Vector3.left).normalized,
            };
            // Search from several heights
            for (float t = 0.2f; t <= 0.8f; t += 0.3f)
            {
                var origin = new Vector3(b.center.x, Mathf.Lerp(b.min.y, b.max.y, t), b.center.z);
                foreach (var d in dirs)
                {
                    if (!Physics.Raycast(origin, d, out var h, 8f, ~0, QueryTriggerInteraction.Ignore))
                        continue;
                    if (IsGroundLike(h.collider.gameObject.name)) continue;
                    if (h.normal.y > 0.55f) continue;
                    if (h.distance < bestDist)
                    {
                        bestDist = h.distance;
                        best = h;
                        found = true;
                    }
                }
            }
            if (!found || bestDist > 7.5f) return false;

            // Place flush: move so the near face sits against the wall
            float thickness = Mathf.Min(b.size.x, b.size.z);
            if (thickness < 0.01f) thickness = 0.08f;
            Vector3 newCenter = best.point + best.normal * (thickness * 0.5f + 0.02f);
            // Keep current Y (mount height) unless wildly off
            newCenter.y = go.transform.position.y;
            // Face outward
            go.transform.position = newCenter;
            if (best.normal.sqrMagnitude > 0.01f)
                go.transform.rotation = Quaternion.LookRotation(-best.normal);

            // Re-verify mount
            return IsMounted(go);
        }

        static bool TryReseatToRoof(GameObject go)
        {
            var r = go.GetComponent<MeshRenderer>();
            if (r == null) return false;
            var b = r.bounds;
            // Cast down from above current position looking for non-ground roof
            var from = new Vector3(b.center.x, b.center.y + 12f, b.center.z);
            var hits = Physics.RaycastAll(from, Vector3.down, 40f, ~0, QueryTriggerInteraction.Ignore)
                .OrderBy(h => h.distance).ToArray();
            foreach (var h in hits)
            {
                if (h.collider.transform == go.transform || h.collider.transform.IsChildOf(go.transform))
                    continue;
                if (IsGroundLike(h.collider.gameObject.name)) continue;
                if (h.point.y < 2.5f) continue; // not a roof
                float lift = h.point.y - b.min.y + 0.03f;
                go.transform.position += Vector3.up * lift;
                return IsMounted(go) || HasRoofSeat(go.GetComponent<MeshRenderer>().bounds, go);
            }
            return false;
        }

        static bool IsGroundLike(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            return n == "Ground" || n == "Beach_Dirt"
                   || n.StartsWith("Road_") || n.StartsWith("Conn_")
                   || n.StartsWith("GQ_") || n.StartsWith("GD_Overlay")
                   || n.StartsWith("COR_Patch");
        }

        // ── Defect 3: gunmetal ───────────────────────────────────────────────

        static void FixGunMaterialOnly()
        {
            Log.AppendLine("=== DEFECT 3: Gun material ===");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(GunMatPath);
            if (mat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit");
                mat = new Material(shader) { name = "Mat_Viewmodel_Gunmetal" };
                AssetDatabase.CreateAsset(mat, GunMatPath);
            }

            // Believable gunmetal — dark desaturated metal, low-mid smoothness
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", new Color(0.22f, 0.23f, 0.25f, 1f));
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0.85f);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.38f);

            var nrm = AssetDatabase.LoadAssetAtPath<Texture2D>(MetalNormalPath);
            // Prefer Zzz metal tank normal if present
            var zzzNrm = AssetDatabase.LoadAssetAtPath<Texture2D>(
                "Assets/_Project/Art/Imported/Zzz/props/metal_water_tank_wdklears_low/Metal_Water_Tank_wdklears_Low_Normal.jpg");
            if (zzzNrm == null)
            {
                // search
                foreach (var g in AssetDatabase.FindAssets("t:Texture2D Metal_Water_Tank"))
                {
                    var p = AssetDatabase.GUIDToAssetPath(g);
                    if (p.Contains("Normal")) { zzzNrm = AssetDatabase.LoadAssetAtPath<Texture2D>(p); break; }
                }
            }
            var useNrm = zzzNrm != null ? zzzNrm : nrm;
            if (useNrm != null && mat.HasProperty("_BumpMap"))
            {
                mat.SetTexture("_BumpMap", useNrm);
                mat.SetTextureScale("_BumpMap", new Vector2(2.5f, 2.5f));
                if (mat.HasProperty("_BumpScale")) mat.SetFloat("_BumpScale", 0.85f);
                mat.EnableKeyword("_NORMALMAP");
            }
            if (mat.HasProperty("_BaseMap") && mat.GetTexture("_BaseMap") != null)
                mat.SetTextureScale("_BaseMap", new Vector2(2.5f, 2.5f));

            EditorUtility.SetDirty(mat);

            var gun = GameObject.Find("PlaceholderAR");
            if (gun == null)
            {
                var player = GameObject.Find("Player");
                if (player != null) gun = FindChild(player.transform, "PlaceholderAR")?.gameObject;
            }
            if (gun != null)
            {
                var rend = gun.GetComponent<MeshRenderer>();
                if (rend != null)
                {
                    // Material assignment only — do not touch mesh/rig/transform/scale
                    rend.sharedMaterial = mat;
                    Log.AppendLine($"Assigned {mat.name} to {PathOf(gun.transform)} (no transform/mesh changes)");
                }
            }
            else Log.AppendLine("PlaceholderAR not found");

            // Neutralize orange mat asset if it exists so a stale reference can't come back
            var orange = AssetDatabase.LoadAssetAtPath<Material>("Assets/_Project/Art/Materials/PlaceholderGun_Orange.mat");
            if (orange != null)
            {
                if (orange.HasProperty("_BaseColor"))
                    orange.SetColor("_BaseColor", new Color(0.22f, 0.23f, 0.25f, 1f));
                orange.name = "PlaceholderGun_Gunmetal";
                EditorUtility.SetDirty(orange);
                AssetDatabase.RenameAsset("Assets/_Project/Art/Materials/PlaceholderGun_Orange.mat",
                    "PlaceholderGun_Gunmetal.mat");
                Log.AppendLine("Neutralized PlaceholderGun_Orange.mat → gunmetal");
            }
        }

        // ── Defect 4: ground tiling ──────────────────────────────────────────

        static void FixGroundTilingAndBreakup()
        {
            Log.AppendLine("=== DEFECT 4: Ground tiling ===");
            var ground = GameObject.Find("Ground");
            if (ground == null)
            {
                Log.AppendLine("Ground missing");
                return;
            }

            var mat = AssetDatabase.LoadAssetAtPath<Material>(GroundMatPath);
            var rend = ground.GetComponent<Renderer>();
            if (mat == null && rend != null) mat = rend.sharedMaterial;
            if (mat == null)
            {
                Log.AppendLine("Ground material missing");
                return;
            }

            // 118m plane: tile ~58 → ~2.0m/repeat — grit reads at 1.7m eye height
            // Slight Z stretch mismatch avoids obvious square tiling
            var scale = new Vector2(58f, 53f);
            mat.mainTextureScale = scale;
            if (mat.HasProperty("_BaseMap")) mat.SetTextureScale("_BaseMap", scale);
            if (mat.HasProperty("_BumpMap"))
            {
                mat.SetTextureScale("_BumpMap", scale);
                if (mat.HasProperty("_BumpScale")) mat.SetFloat("_BumpScale", 1.55f);
                mat.EnableKeyword("_NORMALMAP");
            }
            if (mat.HasProperty("_MetallicGlossMap")) mat.SetTextureScale("_MetallicGlossMap", scale);
            if (mat.HasProperty("_OcclusionMap")) mat.SetTextureScale("_OcclusionMap", scale);

            // Mid-frequency detail layer (different scale → breaks uniform sheet)
            var detailAlb = AssetDatabase.LoadAssetAtPath<Texture2D>(DetailAlbedoPath);
            if (detailAlb == null)
            {
                foreach (var g in AssetDatabase.FindAssets("Damaged_Asphalt_vizhdcz_4K_BaseColor t:Texture2D"))
                {
                    detailAlb = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath(g));
                    if (detailAlb != null) break;
                }
            }
            var detailNrm = AssetDatabase.LoadAssetAtPath<Texture2D>(DetailNormalPath);
            if (detailNrm == null)
            {
                foreach (var g in AssetDatabase.FindAssets("Damaged_Asphalt_vizhdcz_4K_Normal t:Texture2D"))
                {
                    detailNrm = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath(g));
                    if (detailNrm != null) break;
                }
            }

            var detailScale = new Vector2(78f, 71f); // ~1.5m mid-freq
            if (detailAlb != null && mat.HasProperty("_DetailAlbedoMap"))
            {
                mat.SetTexture("_DetailAlbedoMap", detailAlb);
                mat.SetTextureScale("_DetailAlbedoMap", detailScale);
                if (mat.HasProperty("_DetailAlbedoMapScale"))
                    mat.SetFloat("_DetailAlbedoMapScale", 0.55f);
                mat.EnableKeyword("_DETAIL_MULX2");
            }
            if (detailNrm != null && mat.HasProperty("_DetailNormalMap"))
            {
                mat.SetTexture("_DetailNormalMap", detailNrm);
                mat.SetTextureScale("_DetailNormalMap", detailScale);
                if (mat.HasProperty("_DetailNormalMapScale"))
                    mat.SetFloat("_DetailNormalMapScale", 0.7f);
                mat.EnableKeyword("_DETAIL_MULX2");
            }

            // Slightly deepen tint so grit contrast survives the dusty grade
            if (mat.HasProperty("_BaseColor"))
            {
                var c = mat.GetColor("_BaseColor");
                mat.SetColor("_BaseColor", new Color(
                    Mathf.Clamp01(c.r * 0.92f),
                    Mathf.Clamp01(c.g * 0.92f),
                    Mathf.Clamp01(c.b * 0.94f), 1f));
            }

            EditorUtility.SetDirty(mat);
            if (rend != null) rend.sharedMaterial = mat;

            Log.AppendLine($"Ground tile {scale} detailTile {detailScale} detailAlb={(detailAlb!=null)} detailNrm={(detailNrm!=null)}");

            PlaceGroundBreakupPatches();
        }

        static void PlaceGroundBreakupPatches()
        {
            var map = GameObject.Find("ThreeLaneMap")?.transform;
            if (map == null) return;

            // Idempotent clear
            var existing = map.Find(BreakRootName);
            if (existing != null) Object.DestroyImmediate(existing.gameObject);

            var root = new GameObject(BreakRootName).transform;
            root.SetParent(map, false);

            var cracks = AssetDatabase.LoadAssetAtPath<Material>(
                "Assets/_Project/Art/Materials/GroundDetail/GD_CrackOverlay.mat");
            var debris = AssetDatabase.LoadAssetAtPath<Material>(
                "Assets/_Project/Art/Materials/GroundDetail/GD_DebrisOverlay.mat");
            if (cracks == null)
                cracks = AssetDatabase.LoadAssetAtPath<Material>(
                    "Assets/_Project/Art/Materials/Zzz/Zzz_asphalt_cracks.mat");
            if (debris == null)
                debris = AssetDatabase.LoadAssetAtPath<Material>(
                    "Assets/_Project/Art/Materials/Zzz/Zzz_road_debris_sgvlofg_4k.mat");

            if (cracks == null && debris == null)
            {
                Log.AppendLine("No overlay mats for breakup patches");
                return;
            }

            // Sparse patches across whole Ground footprint — material breakup, not geometry spam
            var rng = new System.Random(0xC0CEC7);
            float xMin = -55f, xMax = 55f, zMin = -74f, zMax = 74f;
            float cell = 9.5f;
            for (float z = zMin; z <= zMax; z += cell)
            {
                for (float x = xMin; x <= xMax; x += cell)
                {
                    float jx = x + (float)(rng.NextDouble() * 6 - 3);
                    float jz = z + (float)(rng.NextDouble() * 6 - 3);
                    if (Vector2.Distance(new Vector2(jx, jz), new Vector2(0f, -63f)) < 4f)
                        continue;
                    if (!Physics.Raycast(new Vector3(jx, 10f, jz), Vector3.down, out var hit, 25f))
                        continue;
                    // Prefer the master Ground plane so breakup is even under roads too when Ground shows
                    string hn = hit.collider.name;
                    if (!(hn == "Ground" || hn.StartsWith("Road_") || hn.StartsWith("Conn_") || hn == "Beach_Dirt"))
                        continue;
                    if (Vector3.Angle(hit.normal, Vector3.up) > 20f) continue;

                    var use = (rng.Next(100) < 55 && debris != null) ? debris : cracks;
                    if (use == null) use = cracks ?? debris;
                    float size = 2.2f + (float)rng.NextDouble() * 3.8f;
                    var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    quad.name = $"COR_Patch_{_overlaysPlaced:000}";
                    quad.transform.SetParent(root, true);
                    Object.DestroyImmediate(quad.GetComponent<Collider>());
                    quad.transform.position = hit.point + hit.normal * 0.018f;
                    quad.transform.rotation = Quaternion.LookRotation(-hit.normal)
                        * Quaternion.Euler(0, 0, (float)(rng.NextDouble() * 360));
                    // Quad faces -Z by default after LookRotation(-up); flatten onto ground
                    quad.transform.rotation = Quaternion.FromToRotation(Vector3.forward, -hit.normal)
                        * Quaternion.Euler(0, 0, (float)(rng.NextDouble() * 360));
                    // Simpler: align up with normal
                    quad.transform.position = hit.point + Vector3.up * 0.02f;
                    quad.transform.rotation = Quaternion.Euler(90f, (float)(rng.NextDouble() * 360), 0f);
                    quad.transform.localScale = new Vector3(size, size, 1f);
                    var qr = quad.GetComponent<MeshRenderer>();
                    qr.sharedMaterial = use;
                    qr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    GameObjectUtility.SetStaticEditorFlags(quad,
                        StaticEditorFlags.BatchingStatic | StaticEditorFlags.ContributeGI);
                    _overlaysPlaced++;
                }
            }

            Log.AppendLine($"breakupPatches={_overlaysPlaced} (+{_overlaysPlaced * 2} tris)");
        }

        // ── Metrics ──────────────────────────────────────────────────────────

        public static int CountInvisibleColliders(bool includeInactive)
        {
            int invis = 0;
            var mode = includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude;
            foreach (var c in Object.FindObjectsByType<Collider>(mode))
            {
                if (c == null || c is CharacterController) continue;
                if (c.transform.root != null && c.transform.root.name == "Player") continue;
                // Project rule: collider must have a mesh renderer with a mesh (active or pooled)
                if (!HasMeshRenderer(c.transform))
                    invis++;
                else
                {
                    // Also flag enabled colliders whose only renderers are disabled (not just inactive GO)
                    bool anyEnabledRend = false;
                    foreach (var r in c.GetComponentsInChildren<Renderer>(true))
                    {
                        if (r != null && r.enabled) { anyEnabledRend = true; break; }
                    }
                    if (!anyEnabledRend) invis++;
                }
            }
            return invis;
        }

        static long CountTris()
        {
            long n = 0;
            foreach (var mf in Object.FindObjectsByType<MeshFilter>(FindObjectsInactive.Exclude))
            {
                if (mf == null || mf.sharedMesh == null || !mf.gameObject.scene.IsValid()) continue;
                n += mf.sharedMesh.triangles.Length / 3;
            }
            return n;
        }

        static void WriteLog()
        {
            Log.AppendLine($"trisBefore={_trisBefore} trisAfter={_trisAfter} delta={_trisAfter - _trisBefore}");
            Log.AppendLine($"invisBefore={_invisBefore} invisAfter={_invisAfter}");
            var dir = Path.GetDirectoryName(Path.GetFullPath(LogPath));
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(Path.GetFullPath(LogPath), Log.ToString());
        }

        static string PathOf(Transform t)
        {
            var s = t.name;
            var p = t.parent;
            while (p != null) { s = p.name + "/" + s; p = p.parent; }
            return s;
        }

        static Transform FindChild(Transform root, string name)
        {
            if (root.name == name) return root;
            foreach (Transform c in root)
            {
                var f = FindChild(c, name);
                if (f != null) return f;
            }
            return null;
        }
    }
}
#endif
