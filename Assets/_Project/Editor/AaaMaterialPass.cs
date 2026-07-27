#if UNITY_EDITOR
using System.IO;
using ArenaFps.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaFps.Editor
{
    /// <summary>
    /// Swaps map materials onto the generated photoreal albedo set and stamps wall posters.
    /// </summary>
    public static class AaaMaterialPass
    {
        const string Gen = "Assets/_Project/Art/Textures/Generated";
        const string MatFolder = "Assets/_Project/Art/Materials/Map";

        [MenuItem("Arena FPS/AAA Material Pass (Generated Textures)")]
        public static void Run()
        {
            Directory.CreateDirectory(MatFolder.Replace("Assets/", Application.dataPath + "/"));

            var asphalt = UpsertMat("Mat_Asphalt", $"{Gen}/Asphalt_Color.png", new Color(0.55f, 0.55f, 0.56f), 0.92f, 0f, 22f);
            var brick = UpsertMat("Mat_Brick", $"{Gen}/BrickWall_Color.png", new Color(0.85f, 0.8f, 0.78f), 0.88f, 0f, 3.2f);
            var concrete = UpsertMat("Mat_Concrete", $"{Gen}/Concrete_Color.png", new Color(0.8f, 0.8f, 0.8f), 0.86f, 0f, 4.5f);
            var metal = UpsertMat("Mat_Metal", $"{Gen}/Metal_Color.png", new Color(0.75f, 0.75f, 0.75f), 0.35f, 0.85f, 2.2f);
            var plaster = UpsertMat("Mat_Plaster", $"{Gen}/Plaster_Color.png", new Color(0.9f, 0.88f, 0.84f), 0.9f, 0f, 3.5f);
            var wood = AssetDatabase.LoadAssetAtPath<Material>($"{MatFolder}/Mat_Wood.mat");
            if (wood == null)
                wood = UpsertMat("Mat_Wood", "Assets/_Project/Resources/Textures/Wood/Wood095_Color.jpg", new Color(0.7f, 0.55f, 0.4f), 0.8f, 0.05f, 2f);

            var map = GameObject.Find("ThreeLaneMap");
            if (map != null)
            {
                foreach (var tag in map.GetComponentsInChildren<MapMaterialTag>(true))
                {
                    var mat = tag.materialKey switch
                    {
                        "Mat_Asphalt" => asphalt,
                        "Mat_Brick" => brick,
                        "Mat_Concrete" => concrete,
                        "Mat_Metal" => metal,
                        "Mat_Plaster" => plaster,
                        "Mat_Wood" => wood,
                        _ => concrete,
                    };
                    var r = tag.GetComponent<MeshRenderer>();
                    if (r != null && mat != null)
                        r.sharedMaterial = mat;
                }
            }

            StampPosters();
            ApplySkyAndLight();

            var scene = SceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("[AAA Materials] Generated texture pass applied.");
        }

        static Material UpsertMat(string name, string texPath, Color tint, float roughness, float metallic, float tiling)
        {
            var path = $"{MatFolder}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                mat = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(mat, path);
            }

            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", tint);
            else mat.color = tint;
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 1f - roughness);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metallic);

            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            if (tex != null)
            {
                var importer = AssetImporter.GetAtPath(texPath) as TextureImporter;
                if (importer != null)
                {
                    bool dirty = false;
                    if (!importer.isReadable) { importer.isReadable = false; dirty = true; }
                    if (importer.wrapMode != TextureWrapMode.Repeat)
                    {
                        importer.wrapMode = TextureWrapMode.Repeat;
                        dirty = true;
                    }
                    if (importer.maxTextureSize < 2048)
                    {
                        importer.maxTextureSize = 2048;
                        dirty = true;
                    }
                    if (dirty)
                    {
                        importer.SaveAndReimport();
                        tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
                    }
                }

                mat.SetTexture("_BaseMap", tex);
                mat.mainTextureScale = new Vector2(tiling, tiling);
            }

            EditorUtility.SetDirty(mat);
            return mat;
        }

        static void StampPosters()
        {
            var posterTex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{Gen}/Poster_Military_01.png");
            if (posterTex == null) return;

            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = AssetDatabase.LoadAssetAtPath<Material>($"{MatFolder}/Mat_Poster.mat");
            if (mat == null)
            {
                mat = new Material(shader) { name = "Mat_Poster" };
                AssetDatabase.CreateAsset(mat, $"{MatFolder}/Mat_Poster.mat");
            }
            mat.SetTexture("_BaseMap", posterTex);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.15f);
            EditorUtility.SetDirty(mat);

            var parent = GameObject.Find("ThreeLaneMap")?.transform;
            if (parent == null) return;

            PlacePoster(parent, "Poster_W_1", new Vector3(-12.9f, 2.8f, -18f), new Vector3(0.05f, 2.2f, 1.5f), mat);
            PlacePoster(parent, "Poster_E_1", new Vector3(12.9f, 2.8f, 16f), new Vector3(0.05f, 2.2f, 1.5f), mat);
            PlacePoster(parent, "Poster_Mid", new Vector3(-0.2f, 3.2f, -3.4f), new Vector3(1.4f, 2f, 0.05f), mat);
        }

        static void PlacePoster(Transform parent, string name, Vector3 pos, Vector3 scale, Material mat)
        {
            var existing = parent.Find(name);
            GameObject go;
            if (existing != null)
                go = existing.gameObject;
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = name;
                go.transform.SetParent(parent, true);
                Object.DestroyImmediate(go.GetComponent<Collider>());
            }

            go.transform.position = pos;
            go.transform.localScale = scale;
            var r = go.GetComponent<MeshRenderer>();
            if (r != null) r.sharedMaterial = mat;
        }

        static void ApplySkyAndLight()
        {
            var light = Object.FindAnyObjectByType<Light>();
            if (light != null && light.type == LightType.Directional)
            {
                light.color = new Color(1f, 0.91f, 0.78f);
                light.intensity = 1.55f;
                light.shadows = LightShadows.Soft;
                light.transform.rotation = Quaternion.Euler(48f, -28f, 0f);
                light.shadowStrength = 0.85f;
            }

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.0075f;
            RenderSettings.fogColor = new Color(0.48f, 0.52f, 0.56f);
        }
    }
}
#endif
