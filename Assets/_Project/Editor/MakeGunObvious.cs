#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace ArenaFps.Editor
{
    /// <summary>
    /// Selects the placeholder viewmodel and ensures its material is gunmetal.
    /// MATERIAL ASSIGNMENT ONLY — never mutates mesh, rig, scale, or player scripts.
    /// </summary>
    public static class MakeGunObvious
    {
        const string GunMatPath = "Assets/_Project/Art/Materials/Mat_Viewmodel_Gunmetal.mat";

        [MenuItem("Arena FPS/Make Placeholder Gun Obvious")]
        public static void Run()
        {
            var gun = GameObject.Find("PlaceholderAR");
            if (gun == null)
            {
                var player = GameObject.Find("Player");
                if (player != null)
                    gun = FindChild(player.transform, "PlaceholderAR")?.gameObject;
            }

            if (gun == null)
            {
                EditorUtility.DisplayDialog(
                    "Gun not found",
                    "Select the Arena scene, ensure Player is in the Hierarchy, then try again.\n\nPath should be:\nPlayer → CameraPivot → WeaponRoot → PlaceholderAR",
                    "OK");
                return;
            }

            // Material-only: do NOT touch localScale / mesh / hierarchy.
            var renderer = gun.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(GunMatPath);
                if (mat == null)
                {
                    mat = new Material(Shader.Find("Universal Render Pipeline/Lit"))
                    {
                        name = "Mat_Viewmodel_Gunmetal"
                    };
                    mat.SetColor("_BaseColor", new Color(0.22f, 0.23f, 0.25f, 1f));
                    if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0.85f);
                    if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.38f);
                    if (!AssetDatabase.IsValidFolder("Assets/_Project/Art/Materials"))
                        AssetDatabase.CreateFolder("Assets/_Project/Art", "Materials");
                    AssetDatabase.CreateAsset(mat, GunMatPath);
                }
                renderer.sharedMaterial = mat;
            }

            Selection.activeGameObject = gun;
            SceneView.lastActiveSceneView?.FrameSelected();
            EditorUtility.DisplayDialog(
                "Found it",
                "Placeholder gun is selected and framed (gunmetal material).\n\nTo play FPS-style:\n1. Click the Game tab (next to Scene)\n2. Press the Play button at the top\n3. Click in the Game view to lock the mouse",
                "OK");
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
