#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ArenaFps.Editor
{
    /// <summary>
    /// The FX materials are built at runtime via <c>Shader.Find</c>, and a shader that no scene or
    /// asset references gets stripped from the player build. Registering them as always-included
    /// keeps impacts, tracers and decals alive outside the editor.
    /// </summary>
    [InitializeOnLoad]
    public static class EnsureFxShaders
    {
        static readonly string[] Wanted =
        {
            "ArenaFps/FxAdditive",
            "ArenaFps/FxAlpha",
        };

        static EnsureFxShaders() => EditorApplication.delayCall += Apply;

        [MenuItem("Arena FPS/Ensure FX Shaders Included")]
        static void Apply()
        {
            var settings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
            if (settings == null || settings.Length == 0)
                return;

            var graphics = new SerializedObject(settings[0]);
            var included = graphics.FindProperty("m_AlwaysIncludedShaders");
            if (included == null || !included.isArray)
                return;

            var present = new HashSet<Object>();
            for (int i = 0; i < included.arraySize; i++)
            {
                var reference = included.GetArrayElementAtIndex(i).objectReferenceValue;
                if (reference != null)
                    present.Add(reference);
            }

            bool changed = false;
            foreach (var name in Wanted)
            {
                var shader = Shader.Find(name);
                if (shader == null)
                {
                    Debug.LogWarning($"[EnsureFxShaders] {name} not found — FX will fall back to Sprites/Default.");
                    continue;
                }
                if (present.Contains(shader))
                    continue;

                included.InsertArrayElementAtIndex(included.arraySize);
                included.GetArrayElementAtIndex(included.arraySize - 1).objectReferenceValue = shader;
                present.Add(shader);
                changed = true;
            }

            if (changed)
                graphics.ApplyModifiedProperties();
        }
    }
}
#endif
