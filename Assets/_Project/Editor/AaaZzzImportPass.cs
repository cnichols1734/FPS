#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ArenaFps.Editor
{
    /// <summary>
    /// Idempotent import of the staged CC0 zzz drop into Assets/_Project/Art/Imported/Zzz.
    /// Correct texture types (NormalMap / linear data / sRGB albedo), compressed max-sizes,
    /// and URP Lit materials with Gloss→smoothness (never raw roughness-as-smoothness).
    /// Menu: Arena FPS / AAA Zzz Import Pass
    /// </summary>
    public static class AaaZzzImportPass
    {
        const string IncomingRoot = "_incoming/zzz";
        const string DestRoot = "Assets/_Project/Art/Imported/Zzz";
        const string MatRoot = "Assets/_Project/Art/Materials/Zzz";
        const string MaskRoot = "Assets/_Project/Art/Textures/Generated/ZzzMasks";
        const string ReportPath = "_research/zzz_import_pass.txt";

        static readonly string[] Categories = { "ground", "decals", "props", "cloth", "vehicles" };
        static readonly HashSet<string> SkipExt = new(StringComparer.OrdinalIgnoreCase)
        {
            ".blend", ".mtlx", ".usdc", ".usda", ".usd", ".json", ".txt", ".md", ".html", ".url",
            ".ds_store", ".rar", ".zip", ".7z"
        };

        [MenuItem("Arena FPS/AAA Zzz Import Pass")]
        public static void Run()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.isPlaying = false;
                Debug.LogWarning("[ZzzImport] Exit play mode and re-run.");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("=== AaaZzzImportPass " + DateTime.Now.ToString("o") + " ===");

            string incomingAbs = Path.GetFullPath(IncomingRoot);
            if (!Directory.Exists(incomingAbs))
            {
                Debug.LogError("[ZzzImport] Missing staged root: " + incomingAbs);
                return;
            }

            EnsureAssetFolder(DestRoot);
            EnsureAssetFolder(MatRoot);
            EnsureAssetFolder(MaskRoot);

            int copied = 0, skipped = 0, failed = 0;
            var failedList = new List<string>();

            foreach (var cat in Categories)
            {
                string srcCat = Path.Combine(incomingAbs, cat);
                if (!Directory.Exists(srcCat)) continue;
                foreach (var assetDir in Directory.GetDirectories(srcCat))
                {
                    string assetName = Path.GetFileName(assetDir);
                    string destRel = $"{DestRoot}/{cat}/{Sanitize(assetName)}";
                    EnsureAssetFolder(destRel);
                    int c, s, f;
                    CopyTree(assetDir, Path.GetFullPath(destRel), out c, out s, out f, failedList);
                    copied += c; skipped += s; failed += f;
                }
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            int texFixed = ConfigureAllTextures(sb);
            int matsBuilt = BuildMaterials(sb);

            sb.AppendLine($"copied={copied} skippedExisting={skipped} copyFailed={failed}");
            sb.AppendLine($"texturesConfigured={texFixed} materialsBuilt={matsBuilt}");
            if (failedList.Count > 0)
            {
                sb.AppendLine("FAILURES:");
                foreach (var e in failedList.Take(40)) sb.AppendLine("  " + e);
            }

            Directory.CreateDirectory("_research");
            File.WriteAllText(ReportPath, sb.ToString());
            AssetDatabase.SaveAssets();
            Debug.Log($"[ZzzImport] Done. copied={copied} skip={skipped} fail={failed} tex={texFixed} mats={matsBuilt}. See {ReportPath}");
        }

        static string Sanitize(string name) =>
            name.Replace(" ", "_").Replace("(", "").Replace(")", "").Trim();

        static void EnsureAssetFolder(string assetPath)
        {
            if (AssetDatabase.IsValidFolder(assetPath)) return;
            string[] parts = assetPath.Split('/');
            string cur = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
            string full = Path.GetFullPath(assetPath);
            if (!Directory.Exists(full)) Directory.CreateDirectory(full);
        }

        static void CopyTree(string srcDir, string dstDir, out int copied, out int skipped, out int failed, List<string> failedList)
        {
            copied = skipped = failed = 0;
            if (!Directory.Exists(dstDir)) Directory.CreateDirectory(dstDir);

            foreach (var file in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file);
                if (SkipExt.Contains(ext)) continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}.") && !Path.GetFileName(file).StartsWith("."))
                {
                    // keep normal files; skip hidden junk
                }
                if (Path.GetFileName(file).StartsWith(".")) continue;

                string rel = file.Substring(srcDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string dest = Path.Combine(dstDir, rel);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    if (File.Exists(dest))
                    {
                        var si = new FileInfo(file);
                        var di = new FileInfo(dest);
                        if (si.Length == di.Length)
                        {
                            skipped++;
                            continue;
                        }
                    }
                    File.Copy(file, dest, overwrite: true);
                    copied++;
                }
                catch (Exception ex)
                {
                    failed++;
                    failedList.Add(rel + " :: " + ex.Message);
                }
            }
        }

        static int ConfigureAllTextures(StringBuilder sb)
        {
            string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { DestRoot });
            int fixedCount = 0;
            var dirtyImporters = new List<string>();

            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var imp = AssetImporter.GetAtPath(path) as TextureImporter;
                if (imp == null) continue;

                string file = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                bool isNormal = file.Contains("normal") || file.EndsWith("_bump") || file.Contains("_bump")
                                || file.Contains("nrm") || file.Contains("nor_gl") || file.Contains("nor_dx");
                bool isData = file.Contains("rough") || file.Contains("roughness")
                              || file.Contains("_ao") || file.EndsWith("ao") || file.Contains("ambientocclusion")
                              || file.Contains("gloss") || file.Contains("cavity")
                              || file.Contains("displacement") || file.Contains("height")
                              || file.Contains("specular") || file.Contains("metal")
                              || file.Contains("opacity") || file.Contains("mask")
                              || file.Contains("orm") || file.Contains("arm");
                bool isBase = file.Contains("basecolor") || file.Contains("albedo") || file.Contains("diffuse")
                              || file.Contains("_color") || file.EndsWith("color") || file.Contains("col_");

                bool heroGround = path.Contains("/ground/") &&
                                  (path.Contains("asphalt") || path.Contains("concrete_pavement")
                                   || path.Contains("road_debris") || path.Contains("military_trenches_ground"));

                bool changed = false;

                if (isNormal)
                {
                    if (imp.textureType != TextureImporterType.NormalMap)
                    { imp.textureType = TextureImporterType.NormalMap; changed = true; }
                }
                else
                {
                    if (imp.textureType != TextureImporterType.Default)
                    { imp.textureType = TextureImporterType.Default; changed = true; }

                    bool wantSrgb = isBase || (!isData && !isNormal);
                    // Opacity for cutout overlays still linear data when used as alpha source,
                    // but when it's a dedicated opacity map keep linear.
                    if (isData)
                    {
                        if (imp.sRGBTexture) { imp.sRGBTexture = false; changed = true; }
                    }
                    else if (wantSrgb)
                    {
                        if (!imp.sRGBTexture) { imp.sRGBTexture = true; changed = true; }
                    }
                }

                int maxSize = 2048;
                if (heroGround && (isBase || isNormal)) maxSize = 4096;
                else if (path.Contains("/urban_trash_low/")) maxSize = 1024;
                else if (path.Contains("/decals/")) maxSize = 2048;
                else if (path.Contains("/cloth/") || path.Contains("/vehicles/") || path.Contains("/props/"))
                    maxSize = 2048;
                else if (isData) maxSize = 2048;

                if (imp.maxTextureSize != maxSize) { imp.maxTextureSize = maxSize; changed = true; }

                if (imp.textureCompression != TextureImporterCompression.Compressed)
                { imp.textureCompression = TextureImporterCompression.Compressed; changed = true; }

                var wrap = (path.Contains("/decals/") || path.Contains("opacity") || path.Contains("crack"))
                    ? TextureWrapMode.Clamp
                    : TextureWrapMode.Repeat;
                // Ground PBR should tile
                if (path.Contains("/ground/") && !path.Contains("crack") && !path.Contains("opacity"))
                    wrap = TextureWrapMode.Repeat;
                if (imp.wrapMode != wrap) { imp.wrapMode = wrap; changed = true; }

                if (imp.mipmapEnabled != true) { imp.mipmapEnabled = true; changed = true; }
                if (imp.streamingMipmaps != true) { imp.streamingMipmaps = true; changed = true; }

                if (changed)
                {
                    dirtyImporters.Add(path);
                    fixedCount++;
                }
            }

            // Apply via WriteImportSettingsIfDirty + single refresh so we don't
            // force hundreds of synchronous SaveAndReimport stalls.
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var path in dirtyImporters)
                {
                    var imp = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (imp == null) continue;
                    // Re-apply (idempotent) in case GetAtPath returned a fresh instance
                    ApplyTextureSettings(imp, path);
                    AssetDatabase.WriteImportSettingsIfDirty(path);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            sb.AppendLine($"textureImportersTouched={fixedCount} of {guids.Length}");
            return fixedCount;
        }

        static void ApplyTextureSettings(TextureImporter imp, string path)
        {
            string file = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            bool isNormal = file.Contains("normal") || file.EndsWith("_bump") || file.Contains("_bump")
                            || file.Contains("nrm") || file.Contains("nor_gl") || file.Contains("nor_dx");
            bool isData = file.Contains("rough") || file.Contains("roughness")
                          || file.Contains("_ao") || file.EndsWith("ao") || file.Contains("ambientocclusion")
                          || file.Contains("gloss") || file.Contains("cavity")
                          || file.Contains("displacement") || file.Contains("height")
                          || file.Contains("specular") || file.Contains("metal")
                          || file.Contains("opacity") || file.Contains("mask")
                          || file.Contains("orm") || file.Contains("arm");
            bool isBase = file.Contains("basecolor") || file.Contains("albedo") || file.Contains("diffuse")
                          || file.Contains("_color") || file.EndsWith("color") || file.Contains("col_");
            bool heroGround = path.Contains("/ground/") &&
                              (path.Contains("asphalt") || path.Contains("concrete_pavement")
                               || path.Contains("road_debris") || path.Contains("military_trenches_ground"));

            if (isNormal) imp.textureType = TextureImporterType.NormalMap;
            else
            {
                imp.textureType = TextureImporterType.Default;
                imp.sRGBTexture = !isData || isBase;
                if (isData) imp.sRGBTexture = false;
                else if (isBase) imp.sRGBTexture = true;
            }

            int maxSize = 2048;
            if (heroGround && (isBase || isNormal)) maxSize = 4096;
            else if (path.Contains("/urban_trash_low/")) maxSize = 1024;
            else if (isData) maxSize = 2048;
            imp.maxTextureSize = maxSize;
            imp.textureCompression = TextureImporterCompression.Compressed;

            var wrap = TextureWrapMode.Repeat;
            if ((path.Contains("/decals/") || file.Contains("opacity") || file.Contains("crack"))
                && !path.Contains("/ground/concrete") && !path.Contains("asphalt_v") && !path.Contains("asphalt_s"))
            {
                if (file.Contains("opacity") || file.Contains("crack") || path.Contains("/decals/"))
                    wrap = TextureWrapMode.Clamp;
            }
            if (path.Contains("/ground/") && !file.Contains("crack") && !file.Contains("opacity"))
                wrap = TextureWrapMode.Repeat;
            imp.wrapMode = wrap;
            imp.mipmapEnabled = true;
            imp.streamingMipmaps = true;
        }

        static int BuildMaterials(StringBuilder sb)
        {
            int built = 0;
            // Ground PBR sets (explicit)
            string[] groundSets =
            {
                "ground/concrete_pavement_wlrvaf3_4k",
                "ground/damaged_asphalt_vizcebf_4k",
                "ground/damaged_asphalt_vizhdcz_4k",
                "ground/rough_asphalt_vlpqdf1_4k",
                "ground/wet_destroyed_asphalt_si1odala_4k",
                "ground/road_debris_sgvlofg_4k",
                "ground/military_trenches_ground_patch_rock_s_04_yd0lfcq_mid",
            };

            foreach (var rel in groundSets)
            {
                string folder = $"{DestRoot}/{rel}";
                if (!Directory.Exists(Path.GetFullPath(folder))) continue;
                string matName = "Zzz_" + Path.GetFileName(rel);
                bool cutout = rel.Contains("road_debris");
                if (BuildLitFromFolder(folder, matName, cutout, sb) != null) built++;
            }

            // Crack overlay (albedo + maybe opacity from jpg)
            string crackFolder = $"{DestRoot}/ground/cracks-in-asfalt-road-free";
            if (Directory.Exists(Path.GetFullPath(crackFolder)))
            {
                if (BuildCrackMaterial(crackFolder, sb) != null) built++;
            }

            // Decals / graffiti
            foreach (var dir in Directory.GetDirectories(Path.GetFullPath($"{DestRoot}/decals"), "*", SearchOption.TopDirectoryOnly))
            {
                string assetPath = ToAssetPath(dir);
                string matName = "Zzz_" + Path.GetFileName(dir);
                if (BuildLitFromFolder(assetPath, matName, cutout: true, sb) != null) built++;
            }

            // Cloth / vehicle / prop folders that look like PBR sets (have BaseColor)
            foreach (var cat in new[] { "cloth", "vehicles", "props" })
            {
                string catAbs = Path.GetFullPath($"{DestRoot}/{cat}");
                if (!Directory.Exists(catAbs)) continue;
                foreach (var dir in Directory.GetDirectories(catAbs, "*", SearchOption.TopDirectoryOnly))
                {
                    // Skip mega trash kit individual subfolders — build one mat per leaf that has BaseColor
                    if (Path.GetFileName(dir) == "urban_trash_low")
                    {
                        built += BuildUrbanTrashMats(dir, sb);
                        continue;
                    }
                    string assetPath = ToAssetPath(dir);
                    if (FindMap(assetPath, "basecolor", "albedo", "diffuse", "color") == null) continue;
                    string matName = "Zzz_" + Path.GetFileName(dir);
                    if (BuildLitFromFolder(assetPath, matName, cutout: false, sb) != null) built++;
                }
            }

            sb.AppendLine($"materialsUpserted={built}");
            return built;
        }

        static int BuildUrbanTrashMats(string absRoot, StringBuilder sb)
        {
            int n = 0;
            foreach (var dir in Directory.GetDirectories(absRoot, "*", SearchOption.TopDirectoryOnly))
            {
                string assetPath = ToAssetPath(dir);
                if (FindMap(assetPath, "basecolor", "albedo", "diffuse", "color") == null) continue;
                string matName = "Zzz_trash_" + Path.GetFileName(dir);
                if (BuildLitFromFolder(assetPath, matName, cutout: false, sb) != null) n++;
            }
            sb.AppendLine($"urbanTrashMats={n}");
            return n;
        }

        static Material BuildCrackMaterial(string folder, StringBuilder sb)
        {
            string alb = FindMap(folder, "asfalt-crack", "crack", "basecolor", "color", "albedo");
            if (alb == null)
            {
                // any jpg under textures/
                string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { folder });
                foreach (var g in guids)
                {
                    string p = AssetDatabase.GUIDToAssetPath(g);
                    if (p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    { alb = p; break; }
                }
            }
            if (alb == null) { sb.AppendLine("crack: no albedo"); return null; }

            string matPath = $"{MatRoot}/Zzz_asphalt_cracks.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"))
                { name = "Zzz_asphalt_cracks" };
                AssetDatabase.CreateAsset(mat, matPath);
            }

            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(alb);
            mat.SetTexture("_BaseMap", tex);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.12f);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);
            // Alpha from albedo if present — use transparent for overlay
            SetupTransparent(mat, cutout: true);
            EditorUtility.SetDirty(mat);
            sb.AppendLine("mat Zzz_asphalt_cracks <- " + alb);
            return mat;
        }

        static Material BuildLitFromFolder(string folder, string matName, bool cutout, StringBuilder sb)
        {
            string baseMap = FindMap(folder, "basecolor", "albedo", "diffuse");
            if (baseMap == null) baseMap = FindMap(folder, "_color", "color");
            string normal = FindMap(folder, "normal");
            if (normal == null) normal = FindMap(folder, "_bump", "bump");
            string ao = FindMap(folder, "_ao", "ambientocclusion", "occlusion");
            string gloss = FindMap(folder, "gloss");
            string rough = FindMap(folder, "roughness", "rough");
            string opacity = FindMap(folder, "opacity");

            if (baseMap == null && normal == null)
            {
                sb.AppendLine($"skip {matName}: no PBR maps in {folder}");
                return null;
            }

            string matPath = $"{MatRoot}/{matName}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"))
                { name = matName };
                AssetDatabase.CreateAsset(mat, matPath);
            }

            if (baseMap != null)
            {
                // If opacity separate, merge into albedo alpha for cutout/transparent
                Texture2D albTex;
                if (opacity != null && cutout)
                    albTex = MergeAlbedoOpacity(matName + "_Alpha", baseMap, opacity);
                else
                    albTex = AssetDatabase.LoadAssetAtPath<Texture2D>(baseMap);
                if (albTex != null) mat.SetTexture("_BaseMap", albTex);
            }

            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", Color.white);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);

            if (normal != null)
            {
                EnsureNormal(normal);
                var nrm = AssetDatabase.LoadAssetAtPath<Texture2D>(normal);
                if (nrm != null && mat.HasProperty("_BumpMap"))
                {
                    mat.SetTexture("_BumpMap", nrm);
                    mat.EnableKeyword("_NORMALMAP");
                    if (mat.HasProperty("_BumpScale")) mat.SetFloat("_BumpScale", 1f);
                }
            }

            if (ao != null && mat.HasProperty("_OcclusionMap"))
            {
                mat.SetTexture("_OcclusionMap", AssetDatabase.LoadAssetAtPath<Texture2D>(ao));
                if (mat.HasProperty("_OcclusionStrength")) mat.SetFloat("_OcclusionStrength", 0.85f);
            }

            // Smoothness: prefer Gloss (already inverse of roughness). Pack into MetallicGlossMap alpha.
            Texture2D mask = null;
            if (gloss != null)
                mask = PackSmoothnessMask(matName + "_Mask", gloss, invert: false);
            else if (rough != null)
                mask = PackSmoothnessMask(matName + "_Mask", rough, invert: true);

            if (mask != null && mat.HasProperty("_MetallicGlossMap"))
            {
                mat.SetTexture("_MetallicGlossMap", mask);
                mat.EnableKeyword("_METALLICSPECGLOSSMAP");
                if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 1f); // multiply by map
            }
            else if (mat.HasProperty("_Smoothness"))
            {
                mat.SetFloat("_Smoothness", cutout ? 0.15f : 0.22f);
            }

            if (cutout || opacity != null)
                SetupTransparent(mat, cutout: true);
            else
                SetupOpaque(mat);

            EditorUtility.SetDirty(mat);
            sb.AppendLine($"mat {matName} base={Path.GetFileName(baseMap)} nrm={Path.GetFileName(normal)} gloss={Path.GetFileName(gloss)} rough={Path.GetFileName(rough)} opa={Path.GetFileName(opacity)}");
            return mat;
        }

        static void SetupOpaque(Material mat)
        {
            if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 0f);
            if (mat.HasProperty("_AlphaClip")) mat.SetFloat("_AlphaClip", 0f);
            mat.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.SetOverrideTag("RenderType", "Opaque");
            mat.renderQueue = -1;
        }

        static void SetupTransparent(Material mat, bool cutout)
        {
            if (cutout)
            {
                if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 0f);
                if (mat.HasProperty("_AlphaClip")) mat.SetFloat("_AlphaClip", 1f);
                if (mat.HasProperty("_Cutoff")) mat.SetFloat("_Cutoff", 0.35f);
                mat.EnableKeyword("_ALPHATEST_ON");
                mat.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.SetOverrideTag("RenderType", "TransparentCutout");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
            }
            else
            {
                if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
        }

        static string FindMap(string folder, params string[] tokens)
        {
            if (!AssetDatabase.IsValidFolder(folder) && !Directory.Exists(Path.GetFullPath(folder)))
                return null;
            string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { folder });
            // Prefer exact BaseColor / Normal suffixes before fuzzy token scoring.
            foreach (var g in guids)
            {
                string p = AssetDatabase.GUIDToAssetPath(g);
                string f = Path.GetFileNameWithoutExtension(p).ToLowerInvariant();
                foreach (var tok in tokens)
                {
                    string t = tok.ToLowerInvariant();
                    if (t is "basecolor" or "albedo" or "diffuse")
                    {
                        if (f.EndsWith("_basecolor") || f.EndsWith("_albedo") || f.EndsWith("_diffuse"))
                            return p;
                    }
                    if (t == "normal" && (f.EndsWith("_normal") || f.EndsWith("_normalgl") || f.EndsWith("_normaldx")))
                        return p;
                }
            }
            string best = null;
            int bestScore = -1;
            foreach (var g in guids)
            {
                string p = AssetDatabase.GUIDToAssetPath(g);
                string f = Path.GetFileNameWithoutExtension(p).ToLowerInvariant();
                // Prefer exact channel names; avoid picking Normal when looking for color etc.
                foreach (var tok in tokens)
                {
                    string t = tok.ToLowerInvariant();
                    if (!f.Contains(t)) continue;
                    int score = 10;
                    if (f.EndsWith(t) || f.Contains("_" + t)) score += 5;
                    // Penalize wrong channels
                    if (t.Contains("color") || t.Contains("albedo") || t.Contains("diffuse") || t == "basecolor")
                    {
                        if (f.Contains("normal") || f.Contains("rough") || f.Contains("gloss") || f.Contains("ao")
                            || f.Contains("bump") || f.Contains("specular") || f.Contains("cavity")
                            || f.Contains("displacement") || f.Contains("opacity"))
                            score -= 20;
                    }
                    if (t.Contains("normal") && (f.Contains("bump") && !f.Contains("normal"))) score -= 2;
                    if (score > bestScore) { bestScore = score; best = p; }
                }
            }
            return bestScore > 0 ? best : null;
        }

        static void EnsureNormal(string path)
        {
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null) return;
            bool dirty = false;
            if (imp.textureType != TextureImporterType.NormalMap)
            { imp.textureType = TextureImporterType.NormalMap; dirty = true; }
            if (imp.wrapMode != TextureWrapMode.Repeat)
            { imp.wrapMode = TextureWrapMode.Repeat; dirty = true; }
            if (dirty) imp.SaveAndReimport();
        }

        static void EnsureReadable(string path)
        {
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null || imp.isReadable) return;
            imp.isReadable = true;
            imp.SaveAndReimport();
        }

        static Texture2D PackSmoothnessMask(string name, string srcPath, bool invert)
        {
            string outPath = $"{MaskRoot}/{name}.png";
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
            if (existing != null) return existing;

            EnsureReadable(srcPath);
            var src = AssetDatabase.LoadAssetAtPath<Texture2D>(srcPath);
            if (src == null || !src.isReadable) return null;

            int maxDim = 2048;
            int w = Mathf.Min(src.width, maxDim);
            int h = Mathf.Min(src.height, maxDim);
            // Sample via GetPixels with scale
            var srcPx = src.GetPixels32();
            int sw = src.width, sh = src.height;
            var dst = new Color32[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int sx = x * sw / w;
                int sy = y * sh / h;
                byte v = srcPx[sy * sw + sx].r;
                if (invert) v = (byte)(255 - v);
                // Asphalt shouldn't be mirror-wet — gently compress high gloss
                float f = v / 255f;
                f = Mathf.Lerp(0.05f, 0.55f, f);
                v = (byte)Mathf.Clamp(Mathf.RoundToInt(f * 255f), 0, 255);
                dst[y * w + x] = new Color32(0, 0, 0, v);
            }

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true, true);
            tex.SetPixels32(dst);
            tex.Apply(true);
            EnsureAssetFolder(MaskRoot);
            File.WriteAllBytes(Path.GetFullPath(outPath), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(outPath);
            var imp = AssetImporter.GetAtPath(outPath) as TextureImporter;
            if (imp != null)
            {
                imp.sRGBTexture = false;
                imp.wrapMode = TextureWrapMode.Repeat;
                imp.alphaSource = TextureImporterAlphaSource.FromInput;
                imp.alphaIsTransparency = false;
                imp.maxTextureSize = 2048;
                imp.textureCompression = TextureImporterCompression.Compressed;
                imp.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
        }

        static Texture2D MergeAlbedoOpacity(string name, string albedoPath, string opacityPath)
        {
            string outPath = $"{MaskRoot}/{name}.png";
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
            if (existing != null) return existing;

            EnsureReadable(albedoPath);
            EnsureReadable(opacityPath);
            var alb = AssetDatabase.LoadAssetAtPath<Texture2D>(albedoPath);
            var opa = AssetDatabase.LoadAssetAtPath<Texture2D>(opacityPath);
            if (alb == null || opa == null || !alb.isReadable || !opa.isReadable) return alb;

            int maxDim = 2048;
            int w = Mathf.Min(alb.width, maxDim);
            int h = Mathf.Min(alb.height, maxDim);
            var ap = alb.GetPixels32();
            var op = opa.GetPixels32();
            int aw = alb.width, ah = alb.height, ow = opa.width, oh = opa.height;
            var dst = new Color32[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int ax = x * aw / w, ay = y * ah / h;
                int ox = x * ow / w, oy = y * oh / h;
                var c = ap[ay * aw + ax];
                dst[y * w + x] = new Color32(c.r, c.g, c.b, op[oy * ow + ox].r);
            }

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true);
            tex.SetPixels32(dst);
            tex.Apply(true);
            EnsureAssetFolder(MaskRoot);
            File.WriteAllBytes(Path.GetFullPath(outPath), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(outPath);
            var imp = AssetImporter.GetAtPath(outPath) as TextureImporter;
            if (imp != null)
            {
                imp.sRGBTexture = true;
                imp.alphaSource = TextureImporterAlphaSource.FromInput;
                imp.alphaIsTransparency = true;
                imp.wrapMode = TextureWrapMode.Clamp;
                imp.maxTextureSize = 2048;
                imp.textureCompression = TextureImporterCompression.Compressed;
                imp.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
        }

        static string ToAssetPath(string absOrAsset)
        {
            if (absOrAsset.StartsWith("Assets/")) return absOrAsset.Replace('\\', '/');
            string data = Application.dataPath.Replace('\\', '/');
            string full = absOrAsset.Replace('\\', '/');
            if (full.StartsWith(data))
                return "Assets" + full.Substring(data.Length);
            // fallback via project root
            string proj = Directory.GetParent(Application.dataPath)!.FullName.Replace('\\', '/');
            if (full.StartsWith(proj))
                return full.Substring(proj.Length + 1);
            return absOrAsset;
        }
    }
}
#endif
