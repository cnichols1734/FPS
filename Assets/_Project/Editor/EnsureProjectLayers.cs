#if UNITY_EDITOR
using ArenaFps.Core;
using UnityEditor;
using UnityEngine;

namespace ArenaFps.Editor
{
    /// <summary>
    /// Names the layers <see cref="GameLayers"/> reserves. Purely cosmetic — the game addresses
    /// layers by index — but an empty inspector dropdown makes scene debugging miserable.
    /// </summary>
    [InitializeOnLoad]
    public static class EnsureProjectLayers
    {
        static readonly (int index, string name)[] Wanted =
        {
            (GameLayers.Player, "Player"),
            (GameLayers.Enemy, "Enemy"),
            (GameLayers.Hitbox, "Hitbox"),
            (GameLayers.Fx, "Fx"),
            (GameLayers.Viewmodel, "Viewmodel"),
        };

        static EnsureProjectLayers() => EditorApplication.delayCall += Apply;

        [MenuItem("Arena FPS/Ensure Layers")]
        static void Apply()
        {
            var asset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (asset == null || asset.Length == 0)
                return;

            var tagManager = new SerializedObject(asset[0]);
            var layers = tagManager.FindProperty("layers");
            if (layers == null || !layers.isArray)
                return;

            bool changed = false;
            foreach (var (index, name) in Wanted)
            {
                if (index >= layers.arraySize)
                    continue;
                var slot = layers.GetArrayElementAtIndex(index);
                if (slot.stringValue == name)
                    continue;
                slot.stringValue = name;
                changed = true;
            }

            if (changed)
                tagManager.ApplyModifiedProperties();
        }
    }
}
#endif
