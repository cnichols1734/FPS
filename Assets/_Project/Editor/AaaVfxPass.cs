#if UNITY_EDITOR
using ArenaFps.Feedback;
using UnityEditor;
using UnityEngine;

namespace ArenaFps.Editor
{
    /// <summary>
    /// One-click upkeep for the combat VFX stack: bake atlas previews, keep shaders from stripping,
    /// and make the refreshed assets visible to the editor before visual QA.
    /// </summary>
    public static class AaaVfxPass
    {
        [MenuItem("Arena FPS/AAA VFX Pass", priority = 19)]
        public static void Run()
        {
            FxAtlas.BakeAtlasesToProject();
            EnsureFxShaders.Apply();
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            Debug.Log("[AAA VFX Pass] Baked combat FX atlases and verified URP FX shaders.");
        }
    }
}
#endif
