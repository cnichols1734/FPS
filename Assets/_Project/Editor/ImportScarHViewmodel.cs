#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace ArenaFps.Editor
{
    /// <summary>
    /// Configures the Sketchfab SCAR-H FBX: Generic avatar, game clips sliced from the showcase
    /// take, 4K textures with correct colour spaces, and URP Lit materials fed by a packed
    /// metallic/smoothness map built from the pack's separate metal and roughness sheets.
    /// </summary>
    public static class ImportScarHViewmodel
    {
        const string ArtDir = "Assets/_Project/Art/Models/Weapons/ScarH";
        const string ArtFbx = ArtDir + "/FP_ScarH.fbx";
        const string ResourcesFbx = "Assets/_Project/Resources/Weapons/FP_ScarH.fbx";
        const string TextureDir = ArtDir + "/Textures";
        const string ResourcesTexDir = "Assets/_Project/Resources/Weapons/ScarH";
        const string ControllerPath = ArtDir + "/Animation/ScarH_Viewmodel.controller";

        /// <summary>Bumped whenever these import rules change, so a stale project re-runs once.</summary>
        const int ImportVersion = 4;
        const string VersionKey = "ArenaFps.ScarH.ImportVersion";

        // Showcase take is 24fps, 280 frames (11.667s). Ranges from motion-energy analysis.
        static readonly (string name, float start, float end, bool loop)[] Clips =
        {
            ("ScarH_Draw", 0f, 1.85f, false),
            ("ScarH_Idle", 1.85f, 3.15f, true),
            ("ScarH_Fire", 3.15f, 3.50f, false),
            ("ScarH_Reload", 4.20f, 7.15f, false),
            ("ScarH_ReloadEmpty", 7.55f, 9.45f, false),
            ("ScarH_Holster", 10.40f, 11.667f, false),
        };

        const float SampleRate = 24f;

        /// <summary>Metallic/smoothness needs far less resolution than albedo or normals.</summary>
        const int PackedMapSize = 2048;

        [MenuItem("Arena FPS/Import Scar-H Viewmodel")]
        public static void Run()
        {
            EnsureFolders();
            BuildPackedMap("ScarH_Body");
            BuildPackedMap("ScarH_Buttock");
            SyncTexturesToResources();
            AssetDatabase.Refresh();

            ConfigureTextures();
            ConfigureFbx(ArtFbx);
            ConfigureFbx(ResourcesFbx);
            CreateMaterials();
            CreateAnimatorController();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorPrefs.SetInt(VersionKey, ImportVersion);
            Debug.Log("[ArenaFps] SCAR-H viewmodel imported: clips sliced, packed PBR maps + materials ready.");
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

        static void EnsureFolders()
        {
            Directory.CreateDirectory(Full(ArtDir + "/Animation"));
            Directory.CreateDirectory(Full(ResourcesTexDir));
        }

        /// <summary>
        /// URP Lit reads metallic from RGB and smoothness from alpha of one map, but the pack ships
        /// separate metal and roughness sheets. Combine them once so the rifle gets real specular
        /// response instead of a flat guessed constant.
        /// </summary>
        static void BuildPackedMap(string stem)
        {
            string outAsset = $"{TextureDir}/{stem}_MG.png";
            string outFull = Full(outAsset);

            var rough = LoadRaw($"{TextureDir}/{stem}_R.png");
            if (rough == null)
                return;

            var metal = LoadRaw($"{TextureDir}/{stem}_M.png");

            // Only rebuild when the sources are newer than the packed result.
            if (File.Exists(outFull))
            {
                var stamp = File.GetLastWriteTimeUtc(outFull);
                bool stale = File.GetLastWriteTimeUtc(Full($"{TextureDir}/{stem}_R.png")) > stamp
                             || (metal != null && File.GetLastWriteTimeUtc(Full($"{TextureDir}/{stem}_M.png")) > stamp);
                if (!stale)
                {
                    Object.DestroyImmediate(rough);
                    if (metal != null)
                        Object.DestroyImmediate(metal);
                    return;
                }
            }

            int size = Mathf.Min(PackedMapSize, Mathf.Max(rough.width, rough.height));
            var roughPixels = rough.GetPixels32();
            var metalPixels = metal != null ? metal.GetPixels32() : null;
            var packed = new Color32[size * size];

            for (int y = 0; y < size; y++)
            {
                float v = (y + 0.5f) / size;
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size;
                    byte roughness = Sample(roughPixels, rough.width, rough.height, u, v).r;
                    byte metallic = metalPixels != null
                        ? Sample(metalPixels, metal.width, metal.height, u, v).r
                        : (byte)255;
                    packed[y * size + x] = new Color32(metallic, metallic, metallic, (byte)(255 - roughness));
                }
            }

            var output = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            output.SetPixels32(packed);
            output.Apply(false, false);
            File.WriteAllBytes(outFull, output.EncodeToPNG());

            Object.DestroyImmediate(output);
            Object.DestroyImmediate(rough);
            if (metal != null)
                Object.DestroyImmediate(metal);

            Debug.Log($"[ArenaFps] Packed {stem}_MG.png at {size}px (RGB metallic, A smoothness).");
        }

        static Color32 Sample(Color32[] pixels, int width, int height, float u, float v)
        {
            int x = Mathf.Clamp((int)(u * width), 0, width - 1);
            int y = Mathf.Clamp((int)(v * height), 0, height - 1);
            return pixels[y * width + x];
        }

        /// <summary>Reads a PNG off disk so pixels are available without flipping Read/Write on the importer.</summary>
        static Texture2D LoadRaw(string assetPath)
        {
            string full = Full(assetPath);
            if (!File.Exists(full))
                return null;

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            if (tex.LoadImage(File.ReadAllBytes(full), false))
                return tex;

            Object.DestroyImmediate(tex);
            return null;
        }

        static void SyncTexturesToResources()
        {
            string srcDir = Full(TextureDir);
            if (!Directory.Exists(srcDir))
                return;

            string dstDir = Full(ResourcesTexDir);
            foreach (var src in Directory.GetFiles(srcDir, "*.png"))
            {
                var dest = Path.Combine(dstDir, Path.GetFileName(src));
                if (File.Exists(dest) && File.GetLastWriteTimeUtc(dest) >= File.GetLastWriteTimeUtc(src))
                    continue;
                File.Copy(src, dest, overwrite: true);
            }
        }

        static void ConfigureTextures()
        {
            foreach (var dir in new[] { TextureDir, ResourcesTexDir })
            {
                if (!Directory.Exists(Full(dir)))
                    continue;

                foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { dir }))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (importer == null)
                        continue;

                    bool isNormal = path.EndsWith("_N.png");
                    // Metal, roughness, AO and the packed map are data, not colour.
                    bool isData = path.EndsWith("_M.png") || path.EndsWith("_R.png")
                                  || path.EndsWith("_AO.png") || path.EndsWith("_MG.png");

                    var wanted = isNormal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                    bool changed = false;

                    if (importer.textureType != wanted)
                    {
                        importer.textureType = wanted;
                        changed = true;
                    }

                    if (!isNormal && importer.sRGBTexture == isData)
                    {
                        importer.sRGBTexture = !isData;
                        changed = true;
                    }

                    if (!importer.mipmapEnabled)
                    {
                        importer.mipmapEnabled = true;
                        changed = true;
                    }

                    int maxSize = isData ? 2048 : 4096;
                    if (importer.maxTextureSize != maxSize)
                    {
                        importer.maxTextureSize = maxSize;
                        changed = true;
                    }

                    if (importer.textureCompression != TextureImporterCompression.CompressedHQ)
                    {
                        importer.textureCompression = TextureImporterCompression.CompressedHQ;
                        changed = true;
                    }

                    // Viewmodels are viewed at grazing angles constantly; aniso earns its keep.
                    if (importer.anisoLevel < 4)
                    {
                        importer.anisoLevel = 4;
                        changed = true;
                    }

                    if (importer.filterMode != FilterMode.Trilinear)
                    {
                        importer.filterMode = FilterMode.Trilinear;
                        changed = true;
                    }

                    if (changed)
                        importer.SaveAndReimport();
                }
            }
        }

        static void ConfigureFbx(string path)
        {
            if (!File.Exists(Full(path)))
            {
                Debug.LogWarning($"[ArenaFps] FBX missing at {path}");
                return;
            }

            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null)
                return;

            importer.animationType = ModelImporterAnimationType.Generic;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.importAnimation = true;
            importer.resampleCurves = true;
            importer.animationCompression = ModelImporterAnimationCompression.Optimal;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.importBlendShapes = true;
            importer.isReadable = false;
            importer.meshCompression = ModelImporterMeshCompression.Off;
            importer.importLights = false;
            importer.importCameras = false;
            importer.bakeAxisConversion = true;
            // The pack's node scales are meaningless (121x on meshes, 406x on the armature), so no
            // importer scale can be "right". ScarHViewmodelBuilder solves a uniform scale from the
            // posed rig instead; leaving this at 1 keeps it from compounding with Convert Units.
            importer.useFileScale = true;
            importer.globalScale = 1f;
            importer.preserveHierarchy = true;
            importer.skinWeights = ModelImporterSkinWeights.Standard;
            importer.optimizeGameObjects = false;

            var clips = new ModelImporterClipAnimation[Clips.Length];
            for (int i = 0; i < Clips.Length; i++)
            {
                var (name, start, end, loop) = Clips[i];
                clips[i] = new ModelImporterClipAnimation
                {
                    name = name,
                    takeName = "Dragunov_FP_Rig|Dragunov_FP_RigAction",
                    firstFrame = Mathf.Round(start * SampleRate),
                    lastFrame = Mathf.Round(end * SampleRate),
                    loopTime = loop,
                    loopPose = loop,
                    lockRootRotation = true,
                    lockRootHeightY = true,
                    lockRootPositionXZ = true,
                    keepOriginalOrientation = true,
                    keepOriginalPositionY = true,
                    keepOriginalPositionXZ = true,
                };
            }

            importer.clipAnimations = clips;
            importer.SaveAndReimport();
        }

        /// <summary>Materials live in Resources so the runtime builder can load them by name.</summary>
        static void CreateMaterials()
        {
            MakeMat("ScarH_Body", "ScarH_Body_D", "ScarH_Body_N", "ScarH_Body_MG", null, 0.4f);
            MakeMat("ScarH_Buttock", "ScarH_Buttock_D", "ScarH_Buttock_N", "ScarH_Buttock_MG", null, 0.4f);
            MakeMat("ScarH_Hands", "FPS_Hands_D", "FPS_Hands_N", null, "FPS_Hands_AO", 0.3f);
        }

        static void MakeMat(string name, string albedo, string normal, string packed, string ao, float smoothness)
        {
            string path = $"{ResourcesTexDir}/{name}.mat";
            var shader = Shader.Find("Universal Render Pipeline/Lit")
                         ?? Shader.Find("Universal Render Pipeline/Simple Lit");
            if (shader == null)
                return;

            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(mat, path);
            }
            else
            {
                mat.shader = shader;
            }

            Texture2D Tex(string file)
            {
                if (string.IsNullOrEmpty(file))
                    return null;
                return AssetDatabase.LoadAssetAtPath<Texture2D>($"{ResourcesTexDir}/{file}.png")
                       ?? AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureDir}/{file}.png");
            }

            var baseMap = Tex(albedo);
            if (baseMap != null && mat.HasProperty("_BaseMap"))
            {
                mat.SetTexture("_BaseMap", baseMap);
                if (mat.HasProperty("_MainTex"))
                    mat.SetTexture("_MainTex", baseMap);
            }

            var normalMap = Tex(normal);
            if (normalMap != null && mat.HasProperty("_BumpMap"))
            {
                mat.SetTexture("_BumpMap", normalMap);
                mat.EnableKeyword("_NORMALMAP");
                if (mat.HasProperty("_BumpScale"))
                    mat.SetFloat("_BumpScale", 1f);
            }

            var packedMap = Tex(packed);
            if (packedMap != null && mat.HasProperty("_MetallicGlossMap"))
            {
                mat.SetTexture("_MetallicGlossMap", packedMap);
                mat.EnableKeyword("_METALLICSPECGLOSSMAP");
                // With the map bound, URP treats these as multipliers.
                if (mat.HasProperty("_Metallic"))
                    mat.SetFloat("_Metallic", 1f);
                if (mat.HasProperty("_Smoothness"))
                    mat.SetFloat("_Smoothness", 1f);
            }
            else
            {
                mat.DisableKeyword("_METALLICSPECGLOSSMAP");
                if (mat.HasProperty("_Metallic"))
                    mat.SetFloat("_Metallic", name.Contains("Hands") ? 0f : 0.6f);
                if (mat.HasProperty("_Smoothness"))
                    mat.SetFloat("_Smoothness", smoothness);
            }

            var aoMap = Tex(ao);
            if (aoMap != null && mat.HasProperty("_OcclusionMap"))
            {
                mat.SetTexture("_OcclusionMap", aoMap);
                mat.EnableKeyword("_OCCLUSIONMAP");
                if (mat.HasProperty("_OcclusionStrength"))
                    mat.SetFloat("_OcclusionStrength", 1f);
            }

            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
        }

        static void CreateAnimatorController()
        {
            // Editor-only convenience for scrubbing clips. Runtime uses ViewmodelAnimator (Playables).
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath)
                             ?? AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);

            while (controller.layers.Length > 0)
                controller.RemoveLayer(0);
            controller.AddLayer("Base");

            var root = controller.layers[0].stateMachine;
            var idle = root.AddState("Idle", new Vector3(300, 0, 0));
            idle.motion = FindClip("ScarH_Idle");
            root.defaultState = idle;

            AddState(root, "Fire", "ScarH_Fire", new Vector3(300, 80, 0));
            AddState(root, "Reload", "ScarH_Reload", new Vector3(300, 160, 0));
            AddState(root, "Draw", "ScarH_Draw", new Vector3(300, -80, 0));

            EditorUtility.SetDirty(controller);
        }

        static void AddState(AnimatorStateMachine machine, string stateName, string clipName, Vector3 position)
        {
            var clip = FindClip(clipName);
            if (clip == null)
                return;
            machine.AddState(stateName, position).motion = clip;
        }

        static AnimationClip FindClip(string name)
        {
            foreach (var path in new[] { ResourcesFbx, ArtFbx })
            {
                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (asset is AnimationClip clip && clip.name == name)
                        return clip;
                }
            }

            return null;
        }
    }
}
#endif
