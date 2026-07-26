#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace ArenaFps.Editor
{
    public static class MakeGunObvious
    {
        [MenuItem("Arena FPS/Make Placeholder Gun Obvious")]
        public static void Run()
        {
            var gun = GameObject.Find("PlaceholderAR");
            if (gun == null)
            {
                // Prefab instance path
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

            gun.transform.localScale = new Vector3(0.12f, 0.12f, 0.55f);

            var renderer = gun.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                mat.color = new Color(1f, 0.35f, 0.05f); // bright orange
                mat.name = "PlaceholderGun_Orange";
                renderer.sharedMaterial = mat;
                if (!AssetDatabase.IsValidFolder("Assets/_Project/Art/Materials"))
                    AssetDatabase.CreateFolder("Assets/_Project/Art", "Materials");
                AssetDatabase.CreateAsset(mat, "Assets/_Project/Art/Materials/PlaceholderGun_Orange.mat");
            }

            Selection.activeGameObject = gun;
            SceneView.lastActiveSceneView?.FrameSelected();
            EditorUtility.DisplayDialog(
                "Found it",
                "Placeholder gun is selected and framed (bright orange).\n\nTo play FPS-style:\n1. Click the Game tab (next to Scene)\n2. Press the Play button at the top\n3. Click in the Game view to lock the mouse",
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
