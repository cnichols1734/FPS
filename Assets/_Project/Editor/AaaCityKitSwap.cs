#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaFps.Editor
{
    /// <summary>
    /// Replaces mid-lane primitive building masses with Kenney CC0 City Kit FBX instances
    /// (commercial + industrial + light suburban). Additive under CK_*; hides PB/FD mid visuals.
    /// Menu: Arena FPS / AAA City Kit Swap
    /// </summary>
    public static class AaaCityKitSwap
    {
        const string ScenePath = "Assets/_Project/Scenes/Arena.unity";
        const string CommercialDir = "Assets/_Project/Art/Models/Environment/City/Kenney_Commercial/Models/FBX format";
        const string IndustrialDir = "Assets/_Project/Art/Models/Environment/City/Kenney_Industrial/Models/FBX format";
        const string SuburbanDir = "Assets/_Project/Art/Models/Environment/City/Kenney_Suburban/Models/FBX format";
        const string RootName = "CK_CityKitRoot";

        // Import units are ~1m cubes; mid-lane masses are 5–7m — fit-to-bounds handles scale.
        const float DefaultUniformFallback = 6.2f;
        const float DetailScale = 5.5f;

        static readonly string[] MidPrefixes =
        {
            "PB_Building_Mid_SW_Cafe",
            "PB_Building_Mid_SE_Pawn",
            "PB_Building_Mid_NW_Clinic",
            "PB_Building_Mid_NE_Pharmacy",
        };

        static readonly string[] FdMidPrefixes =
        {
            "FD_Cafe", "FD_Pawn", "FD_Clinic", "FD_Pharmacy",
        };

        [MenuItem("Arena FPS/AAA City Kit Swap")]
        public static void Run()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.isPlaying = false;
                Debug.LogWarning("[AAA CityKit] Exiting play mode; run again in edit mode.");
                return;
            }

            var scene = EditorSceneManager.GetActiveScene().path.EndsWith("Arena.unity")
                ? EditorSceneManager.GetActiveScene()
                : EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            var map = GameObject.Find("ThreeLaneMap");
            if (map == null)
            {
                Debug.LogError("[AAA CityKit] ThreeLaneMap missing; aborting.");
                return;
            }

            ClearPrevious(map.transform);
            HidePrimitiveMidVisuals(map.transform);

            var root = new GameObject(RootName);
            root.isStatic = true;
            root.transform.SetParent(map.transform, false);
            GameObjectUtility.SetStaticEditorFlags(root, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic | StaticEditorFlags.OccluderStatic);

            int buildings = 0;
            int details = 0;

            buildings += PlaceMidLaneCommercial(root.transform);
            buildings += PlaceMidLaneFillers(root.transform);
            buildings += PlaceIndustrialClutter(root.transform);
            buildings += PlaceSuburbanSpice(root.transform);
            details += PlaceStreetDetails(root.transform);
            PlaceBackgroundSkyscrapers(root.transform, ref buildings);

            ReframeCaptureCameras();
            DisableAaaCameras();

            try { SpawnArenaCombat.Run(); }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[AAA CityKit] SpawnArenaCombat skipped: {ex.Message}");
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log($"[AAA CityKit] Swap complete: buildings={buildings} details={details}. Mid PB/FD visuals hidden, Arena saved.");
        }

        static void ClearPrevious(Transform map)
        {
            var doomed = new List<GameObject>();
            foreach (Transform child in map)
            {
                if (child.name == RootName || child.name.StartsWith("CK_"))
                    doomed.Add(child.gameObject);
            }

            // Also clear any orphaned CK_ under map descendants from partial runs.
            foreach (var t in map.GetComponentsInChildren<Transform>(true))
            {
                if (t != map && t.name.StartsWith("CK_") && t.parent == map)
                    doomed.Add(t.gameObject);
            }

            foreach (var go in doomed)
            {
                if (go != null)
                    Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// Hide renderers on mid primitive masses + FD mid facade art.
        /// CRITICAL: if a renderer is hidden, its collider is destroyed in the same operation.
        /// No invisible walls — Kenney/PolyHaven replacements must provide visible collision.
        /// </summary>
        static void HidePrimitiveMidVisuals(Transform map)
        {
            foreach (var t in map.GetComponentsInChildren<Transform>(true))
            {
                bool midPb = false;
                foreach (var p in MidPrefixes)
                {
                    if (t.name.StartsWith(p))
                    {
                        midPb = true;
                        break;
                    }
                }

                bool fdMid = false;
                foreach (var p in FdMidPrefixes)
                {
                    if (t.name.StartsWith(p))
                    {
                        fdMid = true;
                        break;
                    }
                }

                if (!midPb && !fdMid)
                    continue;

                foreach (var r in t.GetComponents<Renderer>())
                    r.enabled = false;

                // Never leave colliders on hidden geometry.
                foreach (var c in t.GetComponents<Collider>())
                    Object.DestroyImmediate(c);
            }
        }

        static int PlaceMidLaneCommercial(Transform root)
        {
            // World footprints match AaaEnvironmentPass mid masses. For yaw ±90, fit size is
            // (worldZ, worldY, worldX) because Kenney +Z front → storefront faces mid after yaw.
            var slots = new[]
            {
                new Slot("CK_Mid_SW_Cafe", "building-c.fbx", new Vector3(-5.8f, 0f, -14.5f), new Vector3(8f, 5.4f, 6.1f), 90f, CommercialDir),
                new Slot("CK_Mid_SE_Pawn", "building-f.fbx", new Vector3(6f, 0f, -12.5f), new Vector3(7f, 5.8f, 5.7f), -90f, CommercialDir),
                new Slot("CK_Mid_NW_Clinic", "building-i.fbx", new Vector3(-6.2f, 0f, 13f), new Vector3(8.5f, 5.6f, 6f), 90f, CommercialDir),
                new Slot("CK_Mid_NE_Pharmacy", "building-k.fbx", new Vector3(6.2f, 0f, 14f), new Vector3(8f, 6.4f, 5.8f), -90f, CommercialDir),
            };

            int n = 0;
            foreach (var s in slots)
            {
                if (SpawnFitted(root, s))
                    n++;
            }

            return n;
        }

        static int PlaceMidLaneFillers(Transform root)
        {
            // Extra mid-street masses so eye-level isn't just four boxes with gaps.
            var slots = new[]
            {
                new Slot("CK_Mid_SW_Fill", "building-a.fbx", new Vector3(-5.9f, 0f, -6.2f), new Vector3(6.5f, 6.8f, 5.4f), 90f, CommercialDir),
                new Slot("CK_Mid_SE_Fill", "building-d.fbx", new Vector3(5.9f, 0f, -4.8f), new Vector3(6.2f, 7.2f, 5.2f), -90f, CommercialDir),
                new Slot("CK_Mid_NW_Fill", "building-g.fbx", new Vector3(-5.9f, 0f, 5.5f), new Vector3(6.8f, 6.2f, 5.5f), 90f, CommercialDir),
                new Slot("CK_Mid_NE_Fill", "building-m.fbx", new Vector3(5.9f, 0f, 6.2f), new Vector3(6.4f, 7.5f, 5.3f), -90f, CommercialDir),
                new Slot("CK_Mid_SW_Deep", "building-b.fbx", new Vector3(-6.0f, 0f, -22.5f), new Vector3(7.2f, 6.0f, 5.8f), 90f, CommercialDir),
                new Slot("CK_Mid_SE_Deep", "building-h.fbx", new Vector3(6.0f, 0f, -21.0f), new Vector3(6.8f, 5.6f, 5.6f), -90f, CommercialDir),
                new Slot("CK_Mid_NW_Deep", "building-j.fbx", new Vector3(-6.0f, 0f, 21.5f), new Vector3(7.0f, 6.5f, 5.8f), 90f, CommercialDir),
                new Slot("CK_Mid_NE_Deep", "building-n.fbx", new Vector3(6.0f, 0f, 22.0f), new Vector3(6.5f, 7.0f, 5.5f), -90f, CommercialDir),
            };

            int n = 0;
            foreach (var s in slots)
            {
                if (SpawnFitted(root, s))
                    n++;
            }

            return n;
        }

        static int PlaceIndustrialClutter(Transform root)
        {
            // Side-alley / backlot industrial — keep clear of mid road ( |x| < 3 ) and cover props.
            var slots = new[]
            {
                new Slot("CK_Ind_West_A", "building-a.fbx", new Vector3(-11.5f, 0f, -10f), new Vector3(4.5f, 5.5f, 5.5f), 0f, IndustrialDir),
                new Slot("CK_Ind_West_B", "building-e.fbx", new Vector3(-12.2f, 0f, 8f), new Vector3(5.0f, 6.0f, 6.0f), 180f, IndustrialDir),
                new Slot("CK_Ind_East_A", "building-c.fbx", new Vector3(11.8f, 0f, -8f), new Vector3(4.8f, 5.8f, 5.2f), 0f, IndustrialDir),
                new Slot("CK_Ind_East_B", "building-g.fbx", new Vector3(12.0f, 0f, 10f), new Vector3(5.0f, 6.2f, 5.8f), 180f, IndustrialDir),
                new Slot("CK_Ind_West_C", "building-k.fbx", new Vector3(-18.5f, 0f, -1f), new Vector3(8.0f, 7.0f, 6.0f), 90f, IndustrialDir),
                new Slot("CK_Ind_East_C", "building-m.fbx", new Vector3(18.5f, 0f, 2f), new Vector3(8.0f, 7.5f, 6.0f), -90f, IndustrialDir),
            };

            int n = 0;
            foreach (var s in slots)
            {
                if (SpawnFitted(root, s))
                    n++;
            }

            // Chimneys as vertical silhouette on industrial roofs (no mesh collider).
            SpawnDetail(root, "CK_Detail_Chimney_W", Path.Combine(IndustrialDir, "chimney-medium.fbx"),
                new Vector3(-11.5f, 5.6f, -10f), Quaternion.identity, DetailScale * 0.45f, false);
            SpawnDetail(root, "CK_Detail_Chimney_E", Path.Combine(IndustrialDir, "chimney-small.fbx"),
                new Vector3(11.8f, 5.9f, -8f), Quaternion.identity, DetailScale * 0.4f, false);

            return n;
        }

        static int PlaceSuburbanSpice(Transform root)
        {
            // Sparse variety near spawn flanks — not mid-lane.
            var slots = new[]
            {
                new Slot("CK_Sub_BlueFlank", "building-type-c.fbx", new Vector3(-10.5f, 0f, -34f), new Vector3(5.5f, 4.5f, 6.0f), 0f, SuburbanDir),
                new Slot("CK_Sub_RedFlank", "building-type-f.fbx", new Vector3(10.5f, 0f, 34f), new Vector3(5.5f, 4.5f, 6.0f), 180f, SuburbanDir),
            };

            int n = 0;
            foreach (var s in slots)
            {
                if (SpawnFitted(root, s))
                    n++;
            }

            return n;
        }

        static void PlaceBackgroundSkyscrapers(Transform root, ref int buildings)
        {
            var slots = new[]
            {
                new Slot("CK_Sky_NW", "building-skyscraper-a.fbx", new Vector3(-22f, 0f, 28f), new Vector3(8f, 22f, 8f), 25f, CommercialDir),
                new Slot("CK_Sky_NE", "building-skyscraper-c.fbx", new Vector3(22f, 0f, 30f), new Vector3(7.5f, 26f, 7.5f), -20f, CommercialDir),
                new Slot("CK_Sky_SW", "building-skyscraper-b.fbx", new Vector3(-24f, 0f, -30f), new Vector3(8f, 20f, 8f), 15f, CommercialDir),
                new Slot("CK_Sky_SE", "building-skyscraper-e.fbx", new Vector3(24f, 0f, -28f), new Vector3(7f, 24f, 7f), -30f, CommercialDir),
            };

            foreach (var s in slots)
            {
                if (SpawnFitted(root, s))
                    buildings++;
            }
        }

        static int PlaceStreetDetails(Transform root)
        {
            int n = 0;
            // Awnings / overhangs along mid storefronts (east face of west row, west face of east row).
            n += SpawnDetail(root, "CK_Awning_Cafe", Path.Combine(CommercialDir, "detail-awning-wide.fbx"),
                new Vector3(-2.55f, 3.1f, -14.5f), Quaternion.Euler(0f, 90f, 0f), DetailScale * 0.55f, false) ? 1 : 0;
            n += SpawnDetail(root, "CK_Awning_Pawn", Path.Combine(CommercialDir, "detail-awning.fbx"),
                new Vector3(2.55f, 3.2f, -12.5f), Quaternion.Euler(0f, -90f, 0f), DetailScale * 0.5f, false) ? 1 : 0;
            n += SpawnDetail(root, "CK_Overhang_Clinic", Path.Combine(CommercialDir, "detail-overhang-wide.fbx"),
                new Vector3(-2.55f, 3.0f, 13f), Quaternion.Euler(0f, 90f, 0f), DetailScale * 0.55f, false) ? 1 : 0;
            n += SpawnDetail(root, "CK_Overhang_Pharmacy", Path.Combine(CommercialDir, "detail-overhang.fbx"),
                new Vector3(2.55f, 3.15f, 14f), Quaternion.Euler(0f, -90f, 0f), DetailScale * 0.5f, false) ? 1 : 0;

            // Parasols at mid plaza / sidewalk — keep out of cover volumes.
            n += SpawnDetail(root, "CK_Parasol_A", Path.Combine(CommercialDir, "detail-parasol-a.fbx"),
                new Vector3(-2.2f, 0f, -8.5f), Quaternion.Euler(0f, 20f, 0f), DetailScale * 0.35f, false) ? 1 : 0;
            n += SpawnDetail(root, "CK_Parasol_B", Path.Combine(CommercialDir, "detail-parasol-b.fbx"),
                new Vector3(2.4f, 0f, 8.8f), Quaternion.Euler(0f, -35f, 0f), DetailScale * 0.35f, false) ? 1 : 0;
            n += SpawnDetail(root, "CK_Parasol_C", Path.Combine(CommercialDir, "detail-parasol-a.fbx"),
                new Vector3(-1.8f, 0f, 16.5f), Quaternion.Euler(0f, 55f, 0f), DetailScale * 0.32f, false) ? 1 : 0;

            return n;
        }

        static bool SpawnFitted(Transform root, Slot slot)
        {
            string path = Path.Combine(slot.dir, slot.file).Replace('\\', '/');
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                Debug.LogWarning($"[AAA CityKit] Missing model: {path}");
                return false;
            }

            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (go == null)
                go = Object.Instantiate(prefab);

            go.name = slot.name;
            go.transform.SetParent(root, false);
            // Fit in identity orientation first — world-bounds fit after yaw maps axes wrong
            // and shoved Kenney masses into the mid lane (Cafe hit at ~1m eye distance).
            go.transform.SetPositionAndRotation(slot.pos, Quaternion.identity);
            FitToTargetSize(go, slot.size);
            go.transform.rotation = Quaternion.Euler(0f, slot.yaw, 0f);
            SnapToFootprint(go, slot.pos, slot.size);
            EnsureMeshColliders(go);
            SetStaticRecursive(go);
            return true;
        }

        static bool SpawnDetail(Transform root, string name, string path, Vector3 pos, Quaternion rot, float uniformScale, bool collider)
        {
            path = path.Replace('\\', '/');
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                Debug.LogWarning($"[AAA CityKit] Missing detail: {path}");
                return false;
            }

            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (go == null)
                go = Object.Instantiate(prefab);

            go.name = name;
            go.transform.SetParent(root, false);
            go.transform.SetPositionAndRotation(pos, rot);
            go.transform.localScale = Vector3.one * uniformScale;
            if (collider)
                EnsureMeshColliders(go);
            SetStaticRecursive(go);
            return true;
        }

        static void FitToTargetSize(GameObject go, Vector3 targetSize)
        {
            // Caller must leave rotation at identity so axis mapping matches targetSize xyz.
            go.transform.localScale = Vector3.one;
            var renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                go.transform.localScale = Vector3.one * DefaultUniformFallback;
                return;
            }

            var bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);

            Vector3 size = bounds.size;
            float sx = size.x > 0.01f ? targetSize.x / size.x : DefaultUniformFallback;
            float sy = size.y > 0.01f ? targetSize.y / size.y : DefaultUniformFallback;
            float sz = size.z > 0.01f ? targetSize.z / size.z : DefaultUniformFallback;

            // Prefer near-uniform so Kenney proportions survive; clamp stretch.
            float avg = (sx + sy + sz) / 3f;
            sx = Mathf.Clamp(sx, avg * 0.75f, avg * 1.35f);
            sy = Mathf.Clamp(sy, avg * 0.75f, avg * 1.35f);
            sz = Mathf.Clamp(sz, avg * 0.75f, avg * 1.35f);

            go.transform.localScale = new Vector3(sx, sy, sz);
        }

        static void SnapToFootprint(GameObject go, Vector3 footprintCenterBottom, Vector3 targetSize)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                go.transform.position = footprintCenterBottom;
                return;
            }

            var bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);

            Vector3 targetCenter = footprintCenterBottom + Vector3.up * (targetSize.y * 0.5f);
            go.transform.position += targetCenter - bounds.center;

            bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);
            if (Mathf.Abs(bounds.min.y) > 0.02f)
                go.transform.position += new Vector3(0f, -bounds.min.y, 0f);

            // Keep mid-lane clear: if mass crosses |x|<2.4, nudge outward.
            bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);
            const float laneHalf = 2.4f;
            if (bounds.max.x > -laneHalf && bounds.min.x < laneHalf)
            {
                if (footprintCenterBottom.x < 0f && bounds.max.x > -laneHalf)
                    go.transform.position += new Vector3(-laneHalf - bounds.max.x - 0.05f, 0f, 0f);
                else if (footprintCenterBottom.x > 0f && bounds.min.x < laneHalf)
                    go.transform.position += new Vector3(laneHalf - bounds.min.x + 0.05f, 0f, 0f);
            }
        }

        static void EnsureMeshColliders(GameObject go)
        {
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh == null)
                    continue;
                var mc = mf.GetComponent<MeshCollider>();
                if (mc == null)
                    mc = mf.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
                mc.convex = false;
            }
        }

        static void SetStaticRecursive(GameObject go)
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

        static void ReframeCaptureCameras()
        {
            // Eye-level: mid lane looking north — Kenney storefronts fill the frame.
            // Pull back so both Kenney storefront rows fill the frame without clipping Cafe.
            SetCam("AAA_EyeLevel_Camera", new Vector3(0f, 1.7f, -19.5f), new Vector3(0f, 2.6f, -6f), 64f);
            SetCam("AAA_MidLane_Camera", new Vector3(0.3f, 2.2f, -17f), new Vector3(0f, 3.0f, 6f), 56f);
            SetCam("AAA_Aerial_Camera", new Vector3(0f, 52f, -6f), new Vector3(0f, 0f, 4f), 48f);
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
            cam.farClipPlane = 250f;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.allowHDR = true;
            cam.enabled = false;
        }

        readonly struct Slot
        {
            public readonly string name;
            public readonly string file;
            public readonly Vector3 pos;
            public readonly Vector3 size;
            public readonly float yaw;
            public readonly string dir;

            public Slot(string name, string file, Vector3 pos, Vector3 size, float yaw, string dir)
            {
                this.name = name;
                this.file = file;
                this.pos = pos;
                this.size = size;
                this.yaw = yaw;
                this.dir = dir;
            }
        }
    }
}
#endif
