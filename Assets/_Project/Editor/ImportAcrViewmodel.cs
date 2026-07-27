#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace ArenaFps.Editor
{
    /// <summary>
    /// Configures the ACR FPS hands+rifle pack: Generic avatar, renamed clips from the multi-take
    /// FBX, packed metallic/smoothness maps, and URP Lit materials under Resources.
    /// </summary>
    public static class ImportAcrViewmodel
    {
        const string ArtDir = "Assets/_Project/Art/Models/Weapons/ACR";
        const string ArtFbx = ArtDir + "/FP_ACR.fbx";
        const string ResourcesFbx = "Assets/_Project/Resources/Weapons/FP_ACR.fbx";
        const string TextureDir = ArtDir + "/Textures";
        const string ResourcesTexDir = "Assets/_Project/Resources/Weapons/ACR";
        const string ControllerPath = ArtDir + "/Animation/ACR_Viewmodel.controller";

        const int ImportVersion = 2;
        const string VersionKey = "ArenaFps.ACR.ImportVersion";

        // Multi-take stack names from Arm3.fbx. Frame counts from Blender import (~30fps).
        static readonly (string name, string take, float lastFrame, bool loop)[] Clips =
        {
            ("ACR_Draw", "Armature|Arms_FPS_Anim_Draw", 35f, false),
            ("ACR_Idle", "Armature|Arms_FPS_Anim_Idle", 80f, true),
            ("ACR_Fire", "Armature|Arms_FPS_Anim_Shoot", 10f, false),
            ("ACR_Reload", "Armature|Arms_FPS_Anim_Reload_Fast", 100f, false),
            ("ACR_Inspect", "Armature|Arms_FPS_Anim_rifle_inspect", 160f, false),
            ("ACR_Walk", "Armature|Arms_FPS_Anim_Walk", 50f, true),
            ("ACR_Run", "Armature|Arms_FPS_Anim_Run", 40f, true),
            ("ACR_OneShot", "Armature|Arms_FPS_Anim_OneShot", 15f, false),
        };

        const int PackedMapSize = 2048;

        [MenuItem("Arena FPS/Import ACR Viewmodel")]
        public static void Run()
        {
            EnsureFolders();
            BuildPackedMap("ACR_Rifle");
            BuildPackedMap("ACR_Pmag");
            BuildPackedMap("ACR_Scope");
            BuildPackedMap("ACR_Silencer");
            BuildPackedMap("ACR_Arms");
            BakeScopeBaseWithGlassCutout();
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
            Debug.Log("[ArenaFps] ACR viewmodel imported: clips renamed, packed PBR maps + materials ready.");
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

        static void BuildPackedMap(string stem)
        {
            string outAsset = $"{TextureDir}/{stem}_MG.png";
            string outFull = Full(outAsset);

            var rough = LoadRaw($"{TextureDir}/{stem}_R.png");
            if (rough == null)
                return;

            var metal = LoadRaw($"{TextureDir}/{stem}_M.png");

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
                        : (byte)0;
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

            Debug.Log($"[ArenaFps] Packed {stem}_MG.png at {size}px.");
        }

        static Color32 Sample(Color32[] pixels, int width, int height, float u, float v)
        {
            int x = Mathf.Clamp((int)(u * width), 0, width - 1);
            int y = Mathf.Clamp((int)(v * height), 0, height - 1);
            return pixels[y * width + x];
        }

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

        /// <summary>
        /// EOTech glass UVs are solid black in the albedo. Bake opacity into alpha so URP Lit
        /// alpha-clip can punch clear windows while keeping the red emissive reticle.
        /// </summary>
        static void BakeScopeBaseWithGlassCutout()
        {
            string outAsset = $"{TextureDir}/ACR_Scope_Base.png";
            string outFull = Full(outAsset);
            string albedoPath = $"{TextureDir}/ACR_Scope_D.png";
            string opacityPath = $"{TextureDir}/ACR_Scope_Opacity.png";
            if (!File.Exists(Full(albedoPath)) || !File.Exists(Full(opacityPath)))
                return;

            if (File.Exists(outFull))
            {
                var stamp = File.GetLastWriteTimeUtc(outFull);
                bool stale = File.GetLastWriteTimeUtc(Full(albedoPath)) > stamp
                             || File.GetLastWriteTimeUtc(Full(opacityPath)) > stamp;
                if (!stale)
                    return;
            }

            var albedo = LoadRaw(albedoPath);
            var opacity = LoadRaw(opacityPath);
            if (albedo == null || opacity == null)
            {
                if (albedo != null) Object.DestroyImmediate(albedo);
                if (opacity != null) Object.DestroyImmediate(opacity);
                return;
            }

            int w = albedo.width;
            int h = albedo.height;
            var rgb = albedo.GetPixels32();
            var op = opacity.GetPixels32();
            var baked = new Color32[w * h];
            for (int i = 0; i < baked.Length; i++)
            {
                // Opacity map: ~147 on glass, 255 on housing. Hard-cut glass for a clear window.
                byte a = op[i].r < 200 ? (byte)0 : (byte)255;
                var c = rgb[i];
                baked[i] = new Color32(c.r, c.g, c.b, a);
            }

            var output = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            output.SetPixels32(baked);
            output.Apply(false, false);
            File.WriteAllBytes(outFull, output.EncodeToPNG());
            Object.DestroyImmediate(output);
            Object.DestroyImmediate(albedo);
            Object.DestroyImmediate(opacity);
            Debug.Log("[ArenaFps] Baked ACR_Scope_Base.png with glass alpha cutout.");
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
                    bool isData = path.EndsWith("_M.png") || path.EndsWith("_R.png")
                                  || path.EndsWith("_AO.png") || path.EndsWith("_MG.png")
                                  || path.EndsWith("_Opacity.png");
                    bool needsAlpha = path.EndsWith("_Base.png") || path.Contains("_Scope_Base");

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

                    if (needsAlpha && !importer.alphaIsTransparency)
                    {
                        importer.alphaIsTransparency = true;
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
            // Scope sight solve samples submesh indices at build time.
            importer.isReadable = true;
            importer.meshCompression = ModelImporterMeshCompression.Off;
            importer.importLights = false;
            importer.importCameras = false;
            importer.bakeAxisConversion = true;
            // Pack already authors centimetre-ish mesh scale (0.01). Keep file scale so the
            // Head_Cam alignment lands in metre space without a second guess.
            importer.useFileScale = true;
            importer.globalScale = 1f;
            importer.preserveHierarchy = true;
            importer.skinWeights = ModelImporterSkinWeights.Standard;
            importer.optimizeGameObjects = false;

            var clips = new ModelImporterClipAnimation[Clips.Length];
            for (int i = 0; i < Clips.Length; i++)
            {
                var (name, take, lastFrame, loop) = Clips[i];
                clips[i] = new ModelImporterClipAnimation
                {
                    name = name,
                    takeName = take,
                    firstFrame = 0f,
                    lastFrame = lastFrame,
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

        static void CreateMaterials()
        {
            MakeMat("ACR_Rifle", "ACR_Rifle_D", "ACR_Rifle_N", "ACR_Rifle_MG", "ACR_Rifle_AO", null, 1f, false);
            MakeMat("ACR_Pmag", "ACR_Pmag_D", "ACR_Pmag_N", "ACR_Pmag_MG", null, null, 1f, false);
            // Base map includes glass alpha cutout so the optic window is clear, not black.
            MakeMat("ACR_Scope", "ACR_Scope_Base", "ACR_Scope_N", "ACR_Scope_MG", null, "ACR_Scope_E", 1f, true);
            MakeMat("ACR_Silencer", "ACR_Silencer_D", "ACR_Silencer_N", "ACR_Silencer_MG", null, null, 1f, false);
            MakeMat("ACR_Arms", "ACR_Arms_D", "ACR_Arms_N", "ACR_Arms_MG", "ACR_Arms_AO", null, 1f, false);
            // Foregrip ships without dedicated maps in this pack — reuse the rifle sheet.
            MakeMat("ACR_Foregrip", "ACR_Rifle_D", "ACR_Rifle_N", "ACR_Rifle_MG", "ACR_Rifle_AO", null, 1f, false);
        }

        static void MakeMat(string name, string albedo, string normal, string packed, string ao,
                            string emission, float smoothness, bool glassCutout)
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
                if (mat.HasProperty("_Metallic"))
                    mat.SetFloat("_Metallic", 1f);
                if (mat.HasProperty("_Smoothness"))
                    mat.SetFloat("_Smoothness", smoothness);
            }
            else
            {
                mat.DisableKeyword("_METALLICSPECGLOSSMAP");
                if (mat.HasProperty("_Metallic"))
                    mat.SetFloat("_Metallic", name.Contains("Arms") ? 0f : 0.55f);
                if (mat.HasProperty("_Smoothness"))
                    mat.SetFloat("_Smoothness", name.Contains("Arms") ? 0.28f : 0.4f);
            }

            var aoMap = Tex(ao);
            if (aoMap != null && mat.HasProperty("_OcclusionMap"))
            {
                mat.SetTexture("_OcclusionMap", aoMap);
                mat.EnableKeyword("_OCCLUSIONMAP");
                if (mat.HasProperty("_OcclusionStrength"))
                    mat.SetFloat("_OcclusionStrength", 1f);
            }

            var emit = Tex(emission);
            if (emit != null && mat.HasProperty("_EmissionMap"))
            {
                mat.SetTexture("_EmissionMap", emit);
                mat.EnableKeyword("_EMISSION");
                // Keep emission at 1× — HDR boost blooms the glass window and softens the reticle.
                if (mat.HasProperty("_EmissionColor"))
                    mat.SetColor("_EmissionColor", Color.white);
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }

            if (glassCutout)
                ApplyOpaqueAlphaClip(mat);
            else
                ClearAlphaClip(mat);

            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
        }

        static void ApplyOpaqueAlphaClip(Material mat)
        {
            if (mat.HasProperty("_Surface"))
                mat.SetFloat("_Surface", 0f); // Opaque
            if (mat.HasProperty("_AlphaClip"))
                mat.SetFloat("_AlphaClip", 1f);
            if (mat.HasProperty("_Cutoff"))
                mat.SetFloat("_Cutoff", 0.5f);
            if (mat.HasProperty("_Blend"))
                mat.SetFloat("_Blend", 0f);
            if (mat.HasProperty("_SrcBlend"))
                mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
            if (mat.HasProperty("_DstBlend"))
                mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
            if (mat.HasProperty("_ZWrite"))
                mat.SetFloat("_ZWrite", 1f);
            if (mat.HasProperty("_Cull"))
                mat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Back);

            mat.EnableKeyword("_ALPHATEST_ON");
            mat.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.SetOverrideTag("RenderType", "TransparentCutout");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
        }

        static void ClearAlphaClip(Material mat)
        {
            if (mat.HasProperty("_AlphaClip"))
                mat.SetFloat("_AlphaClip", 0f);
            mat.DisableKeyword("_ALPHATEST_ON");
            if (mat.GetTag("RenderType", false) == "TransparentCutout")
            {
                mat.SetOverrideTag("RenderType", "Opaque");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;
            }
        }

        static void CreateAnimatorController()
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath)
                             ?? AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);

            while (controller.layers.Length > 0)
                controller.RemoveLayer(0);
            controller.AddLayer("Base");

            var root = controller.layers[0].stateMachine;
            var idle = root.AddState("Idle", new Vector3(300, 0, 0));
            idle.motion = FindClip("ACR_Idle");
            root.defaultState = idle;

            AddState(root, "Fire", "ACR_Fire", new Vector3(300, 80, 0));
            AddState(root, "Reload", "ACR_Reload", new Vector3(300, 160, 0));
            AddState(root, "Draw", "ACR_Draw", new Vector3(300, -80, 0));
            AddState(root, "Walk", "ACR_Walk", new Vector3(500, 0, 0));
            AddState(root, "Run", "ACR_Run", new Vector3(500, 80, 0));

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
