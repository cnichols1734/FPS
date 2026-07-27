#if UNITY_EDITOR
using System.Collections.Generic;
using ArenaFps.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaFps.Editor
{
    /// <summary>
    /// Rebuilds Arena into a Black Ops 2–style 3-lane urban TDM map (Raid / Standoff DNA).
    /// Blue spawn south, Red spawn north. West / Mid / East lanes with mid contested yard.
    /// </summary>
    public static class BuildThreeLaneMap
    {
        const string ScenePath = "Assets/_Project/Scenes/Arena.unity";

        static readonly HashSet<string> PreserveExact = new()
        {
            "Directional Light",
            "Global Volume",
            "Player",
            "PlayerSpawn",
        };

        [MenuItem("Arena FPS/Build Three-Lane TDM Map")]
        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            ClearGreybox();
            var root = new GameObject("ThreeLaneMap");
            Undo.RegisterCreatedObjectUndo(root, "Build Three-Lane Map");

            BuildShell(root.transform);
            BuildLaneDividers(root.transform);
            BuildMidYard(root.transform);
            BuildSpawnBases(root.transform);
            BuildCoverScatter(root.transform);
            BuildDressing(root.transform);
            DecorateFacades(root.transform);
            PlaceSpawns();
            ApplyEditTimeMaterials(root.transform);
            BakeNavMesh();
            FrameSceneView();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[ArenaFps] Three-lane TDM map built, dressed, navmesh baked.");
            if (Application.isBatchMode)
                EditorApplication.Exit(0);
        }

        static void ClearGreybox()
        {
            var doomed = new List<GameObject>();
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (PreserveExact.Contains(root.name))
                    continue;
                // Wipe prior map builds + old greybox pieces.
                if (root.name is "ThreeLaneMap" or "Ground" or "Cover_A" or "Cover_B" or "Cover_C"
                    or "Wall_West" or "Wall_East" or "Wall_North" or "Wall_South"
                    or "__RuntimeNavMesh" or "__CombatBootstrap" or "__ArenaDresser" or "__Match")
                {
                    doomed.Add(root);
                    continue;
                }

                if (root.name.StartsWith("Wall_") || root.name.StartsWith("Cover_")
                    || root.name.StartsWith("Building_") || root.name.StartsWith("Prop_"))
                    doomed.Add(root);
            }

            foreach (var go in doomed)
                Object.DestroyImmediate(go);
        }

        static void BuildShell(Transform parent)
        {
            // Playable footprint ~56m wide × 72m long — COD mid-size 6v6 feel.
            // Unity Plane is 10×10; keep Y at 0 so NavMesh + CharacterController agree.
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.SetParent(parent, true);
            ground.transform.position = Vector3.zero;
            ground.transform.localScale = new Vector3(5.6f, 1f, 7.2f);
            ground.isStatic = true;
            var groundTag = ground.AddComponent<MapMaterialTag>();
            groundTag.materialKey = "Mat_Asphalt";
            Undo.RegisterCreatedObjectUndo(ground, "Map Ground");

            // Outer perimeter walls (taller so you can't peek the void).
            Box(parent, "Wall_West", new Vector3(-28.5f, 4f, 0f), new Vector3(1f, 8f, 72f), "Mat_Brick");
            Box(parent, "Wall_East", new Vector3(28.5f, 4f, 0f), new Vector3(1f, 8f, 72f), "Mat_Brick");
            Box(parent, "Wall_South", new Vector3(0f, 4f, -36.5f), new Vector3(58f, 8f, 1f), "Mat_Brick");
            Box(parent, "Wall_North", new Vector3(0f, 4f, 36.5f), new Vector3(58f, 8f, 1f), "Mat_Brick");
        }

        static void BuildLaneDividers(Transform parent)
        {
            // WEST LANE buildings — long sightline corridor on the left.
            Building(parent, "Building_W_South", new Vector3(-18f, 0f, -22f), new Vector3(10f, 6f, 14f));
            Building(parent, "Building_W_Mid", new Vector3(-20f, 0f, 2f), new Vector3(8f, 7f, 12f));
            Building(parent, "Building_W_North", new Vector3(-17f, 0f, 24f), new Vector3(12f, 5.5f, 10f));

            // EAST LANE buildings — mirrored massing, slightly different heights for silhouette.
            Building(parent, "Building_E_South", new Vector3(18f, 0f, -20f), new Vector3(11f, 5.5f, 12f));
            Building(parent, "Building_E_Mid", new Vector3(19.5f, 0f, 0f), new Vector3(9f, 8f, 14f));
            Building(parent, "Building_E_North", new Vector3(17f, 0f, 22f), new Vector3(10f, 6f, 12f));

            // MID lane pinch points — keep channels ~6–8m wide so lanes read instantly.
            Building(parent, "Building_Mid_SW", new Vector3(-8.5f, 0f, -10f), new Vector3(4f, 4f, 8f));
            Building(parent, "Building_Mid_SE", new Vector3(8.5f, 0f, -8f), new Vector3(4f, 4.5f, 6f));
            Building(parent, "Building_Mid_NW", new Vector3(-8f, 0f, 12f), new Vector3(4.5f, 4f, 7f));
            Building(parent, "Building_Mid_NE", new Vector3(8.5f, 0f, 10f), new Vector3(4f, 5f, 8f));

            // Second-story overlooks over mid (classic BO2 power position).
            Box(parent, "Overlook_West", new Vector3(-10f, 4.5f, 1f), new Vector3(6f, 1f, 8f), "Mat_Concrete");
            Box(parent, "Overlook_East", new Vector3(10f, 4.5f, -1f), new Vector3(6f, 1f, 8f), "Mat_Concrete");
            // Stair blocks up to overlooks
            Box(parent, "Stairs_West", new Vector3(-10f, 1.5f, -5f), new Vector3(3f, 3f, 3f), "Mat_Concrete");
            Box(parent, "Stairs_East", new Vector3(10f, 1.5f, 5f), new Vector3(3f, 3f, 3f), "Mat_Concrete");
        }

        static void BuildMidYard(Transform parent)
        {
            // Contested mid — open enough for crossfires, cluttered enough for gunfights.
            Box(parent, "Mid_FountainBase", new Vector3(0f, 0.35f, 0f), new Vector3(6f, 0.7f, 6f), "Mat_Concrete");
            Box(parent, "Mid_Bus", new Vector3(0.5f, 1.4f, 4.5f), new Vector3(3.2f, 2.8f, 8f), "Mat_Metal");
            Box(parent, "Mid_Kiosk", new Vector3(-2f, 1.2f, -4f), new Vector3(3f, 2.4f, 3f), "Mat_Plaster");

            // Low mid barriers
            Box(parent, "Mid_Barrier_A", new Vector3(-3.5f, 0.6f, 2f), new Vector3(2.5f, 1.2f, 0.5f), "Mat_Metal");
            Box(parent, "Mid_Barrier_B", new Vector3(3.5f, 0.6f, -2.5f), new Vector3(2.5f, 1.2f, 0.5f), "Mat_Metal");
        }

        static void BuildSpawnBases(Transform parent)
        {
            // Blue (south) spawn courtyard
            Building(parent, "Spawn_Blue_Main", new Vector3(0f, 0f, -30f), new Vector3(16f, 5f, 6f));
            Box(parent, "Spawn_Blue_WingL", new Vector3(-10f, 2f, -28f), new Vector3(6f, 4f, 4f), "Mat_Plaster");
            Box(parent, "Spawn_Blue_WingR", new Vector3(10f, 2f, -28f), new Vector3(6f, 4f, 4f), "Mat_Plaster");
            Box(parent, "Spawn_Blue_Sandbags", new Vector3(0f, 0.55f, -24f), new Vector3(10f, 1.1f, 1.2f), "Mat_Wood");

            // Red (north) spawn courtyard
            Building(parent, "Spawn_Red_Main", new Vector3(0f, 0f, 30f), new Vector3(16f, 5f, 6f));
            Box(parent, "Spawn_Red_WingL", new Vector3(-10f, 2f, 28f), new Vector3(6f, 4f, 4f), "Mat_Plaster");
            Box(parent, "Spawn_Red_WingR", new Vector3(10f, 2f, 28f), new Vector3(6f, 4f, 4f), "Mat_Plaster");
            Box(parent, "Spawn_Red_Sandbags", new Vector3(0f, 0.55f, 24f), new Vector3(10f, 1.1f, 1.2f), "Mat_Wood");
        }

        static void BuildCoverScatter(Transform parent)
        {
            // Hard cover — tagged Cover_* so CombatBootstrap / dresser can find breakables.
            Cover(parent, "Cover_A", new Vector3(-14f, 1f, -12f), new Vector3(3.2f, 2f, 1.2f), "Mat_Wood");
            Cover(parent, "Cover_B", new Vector3(14f, 1.25f, -10f), new Vector3(1.6f, 2.5f, 4.2f), "Mat_Metal");
            Cover(parent, "Cover_C", new Vector3(0f, 1f, 8f), new Vector3(7f, 2f, 1.1f), "Mat_Concrete");
            Cover(parent, "Cover_D", new Vector3(-12f, 1f, 8f), new Vector3(3f, 2f, 1.2f), "Mat_Wood");
            Cover(parent, "Cover_E", new Vector3(12f, 1f, 10f), new Vector3(3f, 2f, 1.2f), "Mat_Wood");
            Cover(parent, "Cover_F", new Vector3(-15f, 1.1f, 0f), new Vector3(1.4f, 2.2f, 3.5f), "Mat_Metal");
            Cover(parent, "Cover_G", new Vector3(15f, 1.1f, 2f), new Vector3(1.4f, 2.2f, 3.5f), "Mat_Metal");
            Cover(parent, "Cover_H", new Vector3(-4f, 0.9f, -16f), new Vector3(4f, 1.8f, 1f), "Mat_Concrete");
            Cover(parent, "Cover_I", new Vector3(4f, 0.9f, 16f), new Vector3(4f, 1.8f, 1f), "Mat_Concrete");
            Cover(parent, "Cover_J", new Vector3(-8f, 1f, 20f), new Vector3(2.5f, 2f, 1.2f), "Mat_Wood");
            Cover(parent, "Cover_K", new Vector3(8f, 1f, -20f), new Vector3(2.5f, 2f, 1.2f), "Mat_Wood");
            Cover(parent, "Cover_L", new Vector3(0f, 1.2f, -6f), new Vector3(1.5f, 2.4f, 3f), "Mat_Metal");
        }

        static void BuildDressing(Transform parent)
        {
            // Visual density — crates, barrels, pipes, dumpsters. Not all collidable walk blockers.
            Prop(parent, "Prop_CrateStack_A", new Vector3(-22f, 0.9f, -8f), new Vector3(2.2f, 1.8f, 2.2f), "Mat_Wood");
            Prop(parent, "Prop_CrateStack_B", new Vector3(22f, 0.9f, 8f), new Vector3(2.2f, 1.8f, 2.2f), "Mat_Wood");
            Prop(parent, "Prop_Dumpster_A", new Vector3(-11f, 0.85f, -18f), new Vector3(2.4f, 1.7f, 1.4f), "Mat_Metal");
            Prop(parent, "Prop_Dumpster_B", new Vector3(11f, 0.85f, 18f), new Vector3(2.4f, 1.7f, 1.4f), "Mat_Metal");
            Prop(parent, "Prop_Barrel_A", new Vector3(-5f, 0.6f, 5f), new Vector3(0.9f, 1.2f, 0.9f), "Mat_Metal");
            Prop(parent, "Prop_Barrel_B", new Vector3(5.5f, 0.6f, -5f), new Vector3(0.9f, 1.2f, 0.9f), "Mat_Metal");
            Prop(parent, "Prop_Barrel_C", new Vector3(-16f, 0.6f, 16f), new Vector3(0.9f, 1.2f, 0.9f), "Mat_Metal");
            Prop(parent, "Prop_Pipe_A", new Vector3(-24f, 1.5f, 5f), new Vector3(0.5f, 3f, 0.5f), "Mat_Metal");
            Prop(parent, "Prop_Pipe_B", new Vector3(24f, 1.5f, -5f), new Vector3(0.5f, 3f, 0.5f), "Mat_Metal");
            Prop(parent, "Prop_Scaffold_A", new Vector3(-19f, 2f, -4f), new Vector3(4f, 4f, 1f), "Mat_Metal");
            Prop(parent, "Prop_Scaffold_B", new Vector3(19f, 2f, 4f), new Vector3(4f, 4f, 1f), "Mat_Metal");
            Prop(parent, "Prop_Billboard_Mid", new Vector3(0f, 5.5f, 0f), new Vector3(8f, 3f, 0.4f), "Mat_Metal");

            // Lane street stripes (flat markers)
            Box(parent, "LaneMarker_West", new Vector3(-14f, 0.12f, 0f), new Vector3(0.4f, 0.02f, 50f), "Mat_Asphalt");
            Box(parent, "LaneMarker_East", new Vector3(14f, 0.12f, 0f), new Vector3(0.4f, 0.02f, 50f), "Mat_Asphalt");

            // Team spawn banners — readable from mid.
            Box(parent, "Banner_Blue", new Vector3(0f, 6.5f, -30f), new Vector3(8f, 1.2f, 0.3f), "Mat_Plaster");
            Box(parent, "Banner_Red", new Vector3(0f, 6.5f, 30f), new Vector3(8f, 1.2f, 0.3f), "Mat_Plaster");
        }

        static void DecorateFacades(Transform parent)
        {
            // Cheap window / vent language so brick boxes stop reading as Minecraft.
            void Windows(string name, Vector3 center, Vector3 size, Vector3 facing)
            {
                var go = Box(parent, name, center + facing * 0.02f, size, "Mat_Metal");
                // Non-blocking so they never choke NavMesh / bullets oddly.
                var col = go.GetComponent<Collider>();
                if (col != null)
                    Object.DestroyImmediate(col);
            }

            // West lane faces toward mid (east-facing).
            Windows("Win_W_S_1", new Vector3(-13f, 3.2f, -20f), new Vector3(0.1f, 1.4f, 1.6f), Vector3.right);
            Windows("Win_W_S_2", new Vector3(-13f, 3.2f, -24f), new Vector3(0.1f, 1.4f, 1.6f), Vector3.right);
            Windows("Win_W_M_1", new Vector3(-16f, 3.5f, 0f), new Vector3(0.1f, 1.6f, 1.8f), Vector3.right);
            Windows("Win_W_M_2", new Vector3(-16f, 5.5f, 4f), new Vector3(0.1f, 1.2f, 1.4f), Vector3.right);
            Windows("Win_W_N_1", new Vector3(-11f, 3f, 22f), new Vector3(0.1f, 1.4f, 1.6f), Vector3.right);

            // East lane faces toward mid (west-facing).
            Windows("Win_E_S_1", new Vector3(13f, 3f, -18f), new Vector3(0.1f, 1.4f, 1.6f), Vector3.left);
            Windows("Win_E_S_2", new Vector3(13f, 3f, -22f), new Vector3(0.1f, 1.4f, 1.6f), Vector3.left);
            Windows("Win_E_M_1", new Vector3(15f, 4f, -2f), new Vector3(0.1f, 1.6f, 1.8f), Vector3.left);
            Windows("Win_E_M_2", new Vector3(15f, 6f, 2f), new Vector3(0.1f, 1.2f, 1.4f), Vector3.left);
            Windows("Win_E_N_1", new Vector3(12f, 3.2f, 20f), new Vector3(0.1f, 1.4f, 1.6f), Vector3.left);

            // Mid building vents
            Windows("Vent_Mid_SW", new Vector3(-6.4f, 2.8f, -10f), new Vector3(0.1f, 1.1f, 1.2f), Vector3.right);
            Windows("Vent_Mid_SE", new Vector3(6.4f, 2.8f, -8f), new Vector3(0.1f, 1.1f, 1.2f), Vector3.left);
        }

        static void PlaceSpawns()
        {
            EnsureSpawn("PlayerSpawn", new Vector3(0f, 1.7f, -26f));
            EnsureSpawn("Spawn_Blue_1", new Vector3(-6f, 0.1f, -26f));
            EnsureSpawn("Spawn_Blue_2", new Vector3(6f, 0.1f, -26f));
            EnsureSpawn("Spawn_Blue_3", new Vector3(-12f, 0.1f, -22f));
            EnsureSpawn("Spawn_Blue_4", new Vector3(12f, 0.1f, -22f));
            EnsureSpawn("Spawn_Blue_5", new Vector3(0f, 0.1f, -22f));

            EnsureSpawn("Spawn_Red_1", new Vector3(0f, 0.1f, 26f));
            EnsureSpawn("Spawn_Red_2", new Vector3(-6f, 0.1f, 26f));
            EnsureSpawn("Spawn_Red_3", new Vector3(6f, 0.1f, 26f));
            EnsureSpawn("Spawn_Red_4", new Vector3(-12f, 0.1f, 22f));
            EnsureSpawn("Spawn_Red_5", new Vector3(12f, 0.1f, 22f));

            var player = GameObject.Find("Player");
            var spawn = GameObject.Find("PlayerSpawn");
            if (player != null && spawn != null)
            {
                player.transform.position = spawn.transform.position;
                player.transform.rotation = Quaternion.identity;
            }
        }

        static void EnsureSpawn(string name, Vector3 pos)
        {
            var go = GameObject.Find(name);
            if (go == null)
            {
                go = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(go, "Spawn " + name);
            }
            go.transform.position = pos;
        }

        static void Building(Transform parent, string name, Vector3 centerBottom, Vector3 size)
        {
            // centerBottom is footprint center at ground; lift so base sits on y=0.
            var center = centerBottom + Vector3.up * (size.y * 0.5f);
            Box(parent, name, center, size, "Mat_Brick");
            // Roof lip for silhouette
            Box(parent, name + "_Roof", center + Vector3.up * (size.y * 0.5f + 0.15f),
                new Vector3(size.x + 0.4f, 0.3f, size.z + 0.4f), "Mat_Concrete");
        }

        static void Cover(Transform parent, string name, Vector3 center, Vector3 size, string mat)
        {
            var go = Box(parent, name, center, size, mat);
            go.tag = "Untagged";
        }

        static void Prop(Transform parent, string name, Vector3 center, Vector3 size, string mat)
            => Box(parent, name, center, size, mat);

        static GameObject Box(Transform parent, string name, Vector3 center, Vector3 size, string matTag)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, true);
            go.transform.position = center;
            go.transform.localScale = size;
            go.isStatic = true;

            var marker = go.AddComponent<MapMaterialTag>();
            marker.materialKey = matTag;

            Undo.RegisterCreatedObjectUndo(go, "Map " + name);
            return go;
        }

        static void ApplyEditTimeMaterials(Transform root)
        {
            var mats = new Dictionary<string, Material>
            {
                ["Mat_Asphalt"] = MakeMat("Mat_Asphalt", new Color(0.28f, 0.28f, 0.29f), 0.9f, 0f, "Assets/_Project/Resources/Textures/Asphalt/Asphalt033_Color.jpg", 28f),
                ["Mat_Brick"] = MakeMat("Mat_Brick", new Color(0.45f, 0.3f, 0.24f), 0.88f, 0f, "Assets/_Project/Resources/Textures/Brick/brick_4_diff_2k.jpg", 3.5f),
                ["Mat_Concrete"] = MakeMat("Mat_Concrete", new Color(0.5f, 0.49f, 0.47f), 0.85f, 0f, "Assets/_Project/Resources/Textures/Concrete/Concrete048_2K_Color.jpg", 5f),
                ["Mat_Metal"] = MakeMat("Mat_Metal", new Color(0.4f, 0.42f, 0.45f), 0.35f, 0.9f, "Assets/_Project/Resources/Textures/Metal/CorrugatedSteel009_Color.jpg", 2.5f),
                ["Mat_Wood"] = MakeMat("Mat_Wood", new Color(0.4f, 0.28f, 0.16f), 0.8f, 0.05f, "Assets/_Project/Resources/Textures/Wood/Wood095_Color.jpg", 2f),
                ["Mat_Plaster"] = MakeMat("Mat_Plaster", new Color(0.74f, 0.72f, 0.68f), 0.9f, 0f, "Assets/_Project/Resources/Textures/Plaster/Plaster001_Color.jpg", 3f),
            };

            foreach (var tag in root.GetComponentsInChildren<MapMaterialTag>())
            {
                if (!mats.TryGetValue(tag.materialKey, out var mat))
                    mat = mats["Mat_Concrete"];
                var r = tag.GetComponent<MeshRenderer>();
                if (r != null)
                    r.sharedMaterial = mat;
            }
        }

        static Material MakeMat(string name, Color color, float roughness, float metallic, string texPath, float tiling)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = new Material(shader) { name = name };
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            else mat.color = color;
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 1f - roughness);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metallic);

            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            if (tex != null)
            {
                mat.SetTexture("_BaseMap", tex);
                mat.mainTextureScale = new Vector2(tiling, tiling);
            }

            // Persist so scene references survive reload.
            const string folder = "Assets/_Project/Art/Materials/Map";
            if (!AssetDatabase.IsValidFolder(folder))
            {
                if (!AssetDatabase.IsValidFolder("Assets/_Project/Art/Materials"))
                    AssetDatabase.CreateFolder("Assets/_Project/Art", "Materials");
                AssetDatabase.CreateFolder("Assets/_Project/Art/Materials", "Map");
            }
            var path = $"{folder}/{name}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
            {
                EditorUtility.CopySerialized(mat, existing);
                Object.DestroyImmediate(mat);
                return existing;
            }

            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        static void BakeNavMesh()
        {
            var surfaceType = System.Type.GetType("Unity.AI.Navigation.NavMeshSurface, Unity.AI.Navigation");
            if (surfaceType == null)
            {
                Debug.LogWarning("[ArenaFps] NavMeshSurface missing — skip bake.");
                return;
            }

            var existing = Object.FindFirstObjectByType(surfaceType) as Component;
            if (existing != null)
                Object.DestroyImmediate(existing.gameObject);

            var go = new GameObject("__NavMeshSurface");
            var surface = go.AddComponent(surfaceType);

            var useGeometry = surfaceType.GetProperty("useGeometry");
            if (useGeometry != null && useGeometry.CanWrite)
            {
                var geometryType = System.Type.GetType("UnityEngine.AI.NavMeshCollectGeometry, UnityEngine");
                if (geometryType != null)
                    useGeometry.SetValue(surface, System.Enum.Parse(geometryType, "PhysicsColliders"));
            }

            var layerMask = surfaceType.GetProperty("layerMask");
            if (layerMask != null && layerMask.CanWrite)
                layerMask.SetValue(surface, (LayerMask)~0);

            surfaceType.GetMethod("BuildNavMesh")?.Invoke(surface, null);
            Debug.Log("[ArenaFps] NavMesh baked from physics colliders.");
        }

        static void FrameSceneView()
        {
            var view = SceneView.lastActiveSceneView;
            if (view == null) return;
            view.orthographic = false;
            view.pivot = new Vector3(0f, 6f, 0f);
            view.rotation = Quaternion.Euler(55f, 35f, 0f);
            view.size = 45f;
            view.Repaint();
        }
    }
}
#endif
