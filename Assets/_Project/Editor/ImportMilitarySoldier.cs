#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ArenaFps.Editor
{
    /// <summary>
    /// Configures the Mixamo Male Warrior as a Humanoid enemy. The FBX bind pose is a lounge
    /// clip — runtime standing is applied via HumanPose, so we do not import that animation.
    /// </summary>
    public static class ImportMilitarySoldier
    {
        const string ArtFbx = "Assets/_Project/Art/Models/Characters/MilitarySoldier/MaleWarrior.fbx";
        const string ResourcesFbx = "Assets/_Project/Resources/Characters/MaleWarrior.fbx";

        const int ImportVersion = 2;
        const string VersionKey = "ArenaFps.MilitarySoldier.ImportVersion";

        [MenuItem("Arena FPS/Import Military Soldier")]
        public static void Run()
        {
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "_Project/Resources/Characters"));
            if (File.Exists(Full(ArtFbx)) && !File.Exists(Full(ResourcesFbx)))
                AssetDatabase.CopyAsset(ArtFbx, ResourcesFbx);

            // Drop any lounge AnimatorController from the first import pass.
            const string staleController = "Assets/_Project/Resources/Characters/MaleWarrior.controller";
            if (File.Exists(Full(staleController)))
                AssetDatabase.DeleteAsset(staleController);

            ConfigureFbx(ArtFbx);
            ConfigureFbx(ResourcesFbx);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorPrefs.SetInt(VersionKey, ImportVersion);
            Debug.Log("[ArenaFps] Military soldier imported as Humanoid (standing pose applied at runtime).");
        }

        [InitializeOnLoadMethod]
        static void AutoImportIfNeeded()
        {
            if (!File.Exists(Full(ResourcesFbx)))
                return;
            if (EditorPrefs.GetInt(VersionKey, 0) >= ImportVersion)
                return;

            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                    return;
                Run();
            };
        }

        static string Full(string assetPath) => assetPath.Replace("Assets/", Application.dataPath + "/");

        static void ConfigureFbx(string path)
        {
            if (!File.Exists(Full(path)))
                return;

            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null)
                return;

            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.importAnimation = false;
            importer.skinWeights = ModelImporterSkinWeights.Standard;
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            importer.materialLocation = ModelImporterMaterialLocation.InPrefab;
            importer.SaveAndReimport();
        }
    }
}
#endif
