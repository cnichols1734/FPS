#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArenaFps.Combat;
using UnityEditor;
using UnityEngine;

namespace ArenaFps.Editor
{
    /// <summary>
    /// Turns raw Mixamo downloads into enemy mocap.
    ///
    /// Drop any number of animation FBX files into Art/Animations/Soldier and run this. Each one is
    /// imported as Humanoid so it retargets onto the Male Warrior, looped if it is locomotion,
    /// stripped of root motion, and copied where the runtime can find it. Marking the clips Humanoid
    /// is the step everyone misses: left on Generic they animate their own skeleton rather than the
    /// character's, which is why Mixamo imports so often end up sunken, skating, or splay-legged.
    /// </summary>
    public static class ImportSoldierAnimations
    {
        const string SourceFolder = "Assets/_Project/Art/Animations/Soldier";
        const string ResourcesFolder = "Assets/_Project/Resources/Animations/Soldier";
        const string CharacterFbx = "Assets/_Project/Resources/Characters/MaleWarrior.fbx";

        [MenuItem("Arena FPS/Import Soldier Animations")]
        public static void Run()
        {
            EnsureFolder(SourceFolder);
            EnsureFolder(ResourcesFolder);

            var avatar = AssetDatabase.LoadAllAssetsAtPath(CharacterFbx).OfType<Avatar>().FirstOrDefault();
            if (avatar == null)
            {
                Debug.LogError(
                    $"[Soldier] No Humanoid avatar on {CharacterFbx}. Run 'Arena FPS/Import Military Soldier' first.");
                return;
            }

            var sources = Directory.Exists(Full(SourceFolder))
                ? Directory.GetFiles(Full(SourceFolder), "*.fbx", SearchOption.AllDirectories)
                : System.Array.Empty<string>();

            if (sources.Length == 0)
            {
                Debug.LogWarning(
                    $"[Soldier] No animation FBX found in {SourceFolder}. " +
                    "Download rifle clips from mixamo.com (FBX, 'Without Skin') and drop them there.");
                return;
            }

            var imported = new List<string>();
            var unmatched = new List<string>();

            foreach (var absolute in sources)
            {
                string sourcePath = ToAssetPath(absolute);
                string fileName = Path.GetFileName(sourcePath);
                string destination = $"{ResourcesFolder}/{fileName}";

                if (File.Exists(Full(destination)))
                    AssetDatabase.DeleteAsset(destination);
                if (!AssetDatabase.CopyAsset(sourcePath, destination))
                {
                    Debug.LogError($"[Soldier] Could not copy {sourcePath}.");
                    continue;
                }

                Configure(sourcePath, false);
                Configure(destination, true);

                var role = SoldierClipLibrary.Classify(Path.GetFileNameWithoutExtension(fileName));
                if (role == SoldierClip.Count)
                    unmatched.Add(fileName);
                else
                    imported.Add($"{role} <- {fileName}");
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[Soldier] Imported {imported.Count} clip(s):\n  " + string.Join("\n  ", imported));
            ReportMissingRoles(imported);

            if (unmatched.Count > 0)
            {
                Debug.LogWarning(
                    "[Soldier] These files matched no role and will be ignored — rename them to include " +
                    "idle / walk / run / strafe / start / stop / jump / back / left / right / fire / " +
                    "reload / death:\n  " + string.Join("\n  ", unmatched));
            }
        }

        /// <summary>
        /// Names the roles nobody downloaded. Every one of them degrades silently at runtime, so
        /// without this the only symptom of a half-finished clip set is an enemy that looks
        /// slightly wrong in a way nobody can place.
        /// </summary>
        static void ReportMissingRoles(List<string> imported)
        {
            var missing = new List<string>();
            for (int i = 0; i < (int)SoldierClip.Count; i++)
            {
                var role = (SoldierClip)i;
                if (!imported.Exists(entry => entry.StartsWith(role + " ", System.StringComparison.Ordinal)))
                    missing.Add(role.ToString());
            }

            if (missing.Count > 0)
                Debug.LogWarning($"[Soldier] No clip for: {string.Join(", ", missing)}. These fall back at runtime.");
        }

        static void Configure(string path, bool isRuntimeCopy)
        {
            if (AssetImporter.GetAtPath(path) is not ModelImporter importer)
                return;

            importer.animationType = ModelImporterAnimationType.Human;
            // Each clip builds its own avatar rather than copying the Male Warrior's. Copying is the
            // usual advice, but it demands an identical transform hierarchy and Mixamo's skinless
            // animation downloads parent mixamorig:Hips straight to the file root, where the
            // character has an Armature node in between. Unity rejects the mismatch and then imports
            // no clip at all. Every download shares the same mixamorig skeleton, so auto-mapping
            // resolves identically across the set and retargeting happens through Humanoid anyway.
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.importAnimation = true;
            // Animation-only downloads carry a duplicate mesh and materials; importing them would
            // silently add a second soldier's worth of assets per clip.
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            if (isRuntimeCopy)
                importer.animationCompression = ModelImporterAnimationCompression.KeyframeReduction;

            var clips = importer.defaultClipAnimations;
            if (clips is { Length: > 0 })
            {
                for (int i = 0; i < clips.Length; i++)
                {
                    var role = SoldierClipLibrary.Classify(Path.GetFileNameWithoutExtension(path));

                    // Locomotion has to loop seamlessly; a one-shot that loops re-fires forever.
                    // Transitions, jumps, fire, reload and deaths all play once and hand back.
                    bool loops = role is SoldierClip.Idle or SoldierClip.WalkForward or SoldierClip.WalkBack
                        or SoldierClip.StrafeLeft or SoldierClip.StrafeRight
                        or SoldierClip.RunForward or SoldierClip.RunBack;

                    clips[i].name = Path.GetFileNameWithoutExtension(path);
                    clips[i].loopTime = loops;
                    clips[i].loopPose = loops;

                    // The agent owns movement, so the clip must animate in place. Baking rotation
                    // and XZ into the pose is what keeps the bot from drifting off its own navmesh
                    // path, and locking Y keeps it from sinking through the floor mid-stride.
                    clips[i].lockRootRotation = true;
                    clips[i].keepOriginalOrientation = true;
                    clips[i].lockRootHeightY = true;
                    clips[i].keepOriginalPositionY = true;
                    clips[i].lockRootPositionXZ = true;
                    clips[i].keepOriginalPositionXZ = false;
                }
                importer.clipAnimations = clips;
            }

            importer.SaveAndReimport();
        }

        static void EnsureFolder(string assetPath)
        {
            if (AssetDatabase.IsValidFolder(assetPath))
                return;
            Directory.CreateDirectory(Full(assetPath));
            AssetDatabase.Refresh();
        }

        static string Full(string assetPath) =>
            Path.Combine(Application.dataPath, assetPath["Assets/".Length..]);

        static string ToAssetPath(string absolute) =>
            "Assets" + absolute[Application.dataPath.Length..].Replace('\\', '/');
    }
}
#endif
