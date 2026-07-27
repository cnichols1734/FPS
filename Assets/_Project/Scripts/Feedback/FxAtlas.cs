using UnityEngine;
#if UNITY_EDITOR
using System.IO;
using UnityEditor;
#endif

namespace ArenaFps.Feedback
{
    /// <summary>
    /// Procedurally baked texture atlases. Everything additive shares one atlas and every decal
    /// shares another, which is what lets the whole FX layer draw in two calls.
    /// </summary>
    public static class FxAtlas
    {
        const int Size = 512;
        const int Half = Size / 2;
        const string BakedAdditivePath = "Assets/_Project/Art/VFX/Fx_Additive_Atlas.png";
        const string BakedDecalPath = "Assets/_Project/Art/VFX/Fx_Decal_Atlas.png";

        // Quadrant UV rects, inset half a texel to keep bilinear taps inside their cell.
        const float Inset = 0.5f / Size;
        public static readonly Rect Dot = Quad(0, 0);
        public static readonly Rect Streak = Quad(1, 0);
        public static readonly Rect Star = Quad(0, 1);
        public static readonly Rect Smoke = Quad(1, 1);

        public static readonly Rect HoleConcrete = Quad(0, 0);
        public static readonly Rect HoleMetal = Quad(1, 0);
        public static readonly Rect BloodA = Quad(0, 1);
        public static readonly Rect BloodB = Quad(1, 1);

        static Texture2D _additive;
        static Texture2D _decal;
        static Material _additiveMaterial;
        static Material _decalMaterial;

        public static Texture2D AdditiveTexture => _additive != null ? _additive : _additive = TryLoadBaked(BakedAdditivePath) ?? BuildAdditive();
        public static Texture2D DecalTexture => _decal != null ? _decal : _decal = TryLoadBaked(BakedDecalPath) ?? BuildDecal();

        public static Material AdditiveMaterial
        {
            get
            {
                if (_additiveMaterial != null)
                    return _additiveMaterial;
                var shader = Shader.Find("ArenaFps/FxAdditive")
                    ?? Shader.Find("Universal Render Pipeline/Particles/Unlit")
                    ?? Shader.Find("Sprites/Default");
                _additiveMaterial = new Material(shader)
                {
                    name = "Fx_Additive_Runtime"
                };
                _additiveMaterial.mainTexture = AdditiveTexture;
                if (_additiveMaterial.HasProperty("_Intensity"))
                    _additiveMaterial.SetFloat("_Intensity", 2.35f);
                return _additiveMaterial;
            }
        }

        public static Material DecalMaterial
        {
            get
            {
                if (_decalMaterial != null)
                    return _decalMaterial;
                var shader = Shader.Find("ArenaFps/FxAlpha")
                    ?? Shader.Find("Universal Render Pipeline/Particles/Unlit")
                    ?? Shader.Find("Sprites/Default");
                _decalMaterial = new Material(shader)
                {
                    name = "Fx_Decal_Runtime"
                };
                _decalMaterial.mainTexture = DecalTexture;
                return _decalMaterial;
            }
        }

        static Rect Quad(int x, int y) => new Rect(x * 0.5f + Inset, y * 0.5f + Inset, 0.5f - Inset * 2f, 0.5f - Inset * 2f);

        static Texture2D BuildAdditive()
        {
            var pixels = new Color32[Size * Size];

            FillQuad(pixels, 0, 0, (u, v) =>
            {
                // Hot spark core with a tiny lens halo, used for debris glints and muzzle embers.
                float r = Radius(u, v);
                float core = Mathf.Pow(Mathf.Clamp01(1f - r * 3.8f), 1.4f);
                float halo = Mathf.Pow(Mathf.Clamp01(1f - r), 4.8f) * 0.5f;
                float grain = 0.82f + Noise(u * 23f, v * 23f, 13) * 0.26f;
                return new Color(1f, 1f, 1f, Mathf.Clamp01((core + halo) * grain));
            });

            FillQuad(pixels, 1, 0, (u, v) =>
            {
                // Tracer streak: needle core, warm bloom shoulder and tapered aerodynamic ends.
                float across = Mathf.Abs(v - 0.5f) * 2f;
                float along = Mathf.Abs(u - 0.5f) * 2f;
                float core = Mathf.Pow(Mathf.Clamp01(1f - across * 4.6f), 1.15f);
                float shoulder = Mathf.Pow(Mathf.Clamp01(1f - across * 1.35f), 2.8f) * 0.48f;
                float taper = Mathf.Pow(Mathf.Clamp01(1f - along), 0.55f);
                float head = Mathf.Exp(-Mathf.Pow((u - 0.72f) * 7f, 2f)) * 0.35f;
                float a = Mathf.Clamp01((core + shoulder + head) * taper);
                return new Color(1f, 1f, 1f, a);
            });

            FillQuad(pixels, 0, 1, (u, v) =>
            {
                // Muzzle flash: asymmetric horizontal blast with secondary diagonal tongues.
                float dx = (u - 0.5f) * 2f;
                float dy = (v - 0.5f) * 2f;
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                float core = Mathf.Pow(Mathf.Clamp01(1f - r * 3.1f), 0.85f);
                float horizontal = Mathf.Pow(Mathf.Clamp01(1f - Mathf.Abs(dy) * 8.5f), 1.2f)
                    * Mathf.Pow(Mathf.Clamp01(1f - Mathf.Abs(dx) * 0.78f), 0.42f);
                float forwardTongue = Mathf.Exp(-Mathf.Pow((dx - 0.28f) * 2.1f, 2f) - Mathf.Pow(dy * 5.4f, 2f));
                float diagA = Mathf.Pow(Mathf.Clamp01(1f - Mathf.Abs(dx - dy * 1.35f) * 5.8f), 1.6f);
                float diagB = Mathf.Pow(Mathf.Clamp01(1f - Mathf.Abs(dx + dy * 1.1f) * 7.2f), 1.8f);
                float breakup = 0.74f + Noise(u * 17f, v * 17f, 191) * 0.38f;
                float a = Mathf.Clamp01((core * 1.35f + horizontal * 0.9f + forwardTongue * 0.85f + (diagA + diagB) * 0.22f)
                    * Mathf.Clamp01(1.1f - r * 0.42f) * breakup);
                return new Color(1f, 1f, 1f, a);
            });

            FillQuad(pixels, 1, 1, (u, v) =>
            {
                // Smoke puff: layered soft volume with ragged alpha edges.
                float r = Radius(u, v);
                float n = Noise(u * 4.5f, v * 4.5f, 71) * 0.45f
                    + Noise(u * 10f, v * 10f, 907) * 0.28f
                    + Noise(u * 21f, v * 21f, 233) * 0.13f;
                float edge = 1.05f + n * 0.68f;
                float a = Mathf.Clamp01(1f - r * edge);
                a = Mathf.Pow(a, 2.15f) * (0.42f + n * 0.72f);
                return new Color(1f, 1f, 1f, Mathf.Clamp01(a));
            });

            return Commit(pixels, "Fx_Additive_Atlas");
        }

        static Texture2D BuildDecal()
        {
            var pixels = new Color32[Size * Size];

            FillQuad(pixels, 0, 0, (u, v) => BulletHole(u, v, 1231, new Color(0.035f, 0.032f, 0.028f), new Color(0.64f, 0.62f, 0.56f)));
            FillQuad(pixels, 1, 0, (u, v) => BulletHole(u, v, 5417, new Color(0.018f, 0.02f, 0.023f), new Color(0.88f, 0.9f, 0.92f)));
            FillQuad(pixels, 0, 1, (u, v) => BloodSplat(u, v, 8821));
            FillQuad(pixels, 1, 1, (u, v) => BloodSplat(u, v, 3391));

            return Commit(pixels, "Fx_Decal_Atlas");
        }

        static Color BulletHole(float u, float v, int seed, Color coreColor, Color ringColor)
        {
            float dx = (u - 0.5f) * 2f;
            float dy = (v - 0.5f) * 2f;
            float r = Mathf.Sqrt(dx * dx + dy * dy);
            float angle = Mathf.Atan2(dy, dx);

            // Irregular rim so holes never read as perfect circles.
            float wobble = Noise(Mathf.Cos(angle) * 2.4f + seed * 0.01f, Mathf.Sin(angle) * 2.4f, seed) * 0.16f;
            float coreRadius = 0.3f + wobble;
            float ringRadius = 0.78f + wobble * 1.6f;

            if (r > ringRadius)
                return new Color(0f, 0f, 0f, 0f);

            float core = Mathf.Clamp01((coreRadius - r) / 0.1f);
            float ring = Mathf.Clamp01((ringRadius - r) / (ringRadius - coreRadius));
            ring = Mathf.Pow(ring, 1.6f) * (0.45f + Noise(u * 9f, v * 9f, seed + 5) * 0.55f);

            // Radial spall streaks.
            float streak = Mathf.Clamp01(Mathf.Sin(angle * 11f + Noise(u * 3f, v * 3f, seed + 11) * 6f)) * ring * 0.44f;
            float dust = Noise(u * 18f, v * 18f, seed + 29) * ring * 0.2f;

            float alpha = Mathf.Clamp01(core + ring * 0.78f + streak + dust);
            var rgb = Color.Lerp(ringColor, coreColor, Mathf.Clamp01(core * 1.3f));
            return new Color(rgb.r, rgb.g, rgb.b, alpha);
        }

        static Color BloodSplat(float u, float v, int seed)
        {
            float dx = (u - 0.5f) * 2f;
            float dy = (v - 0.5f) * 2f;
            float r = Mathf.Sqrt(dx * dx + dy * dy);
            float angle = Mathf.Atan2(dy, dx);

            float lobes = Noise(Mathf.Cos(angle) * 1.8f, Mathf.Sin(angle) * 1.8f, seed);
            float edge = 0.42f + lobes * 0.3f;
            float alpha = Mathf.Clamp01((edge - r) / 0.14f);

            // Satellite droplets thrown clear of the main pool.
            for (int i = 0; i < 7; i++)
            {
                float a = Hash(seed + i * 37) * Mathf.PI * 2f;
                float d = 0.5f + Hash(seed + i * 91) * 0.42f;
                float size = 0.035f + Hash(seed + i * 53) * 0.06f;
                float px = Mathf.Cos(a) * d;
                float py = Mathf.Sin(a) * d;
                float dist = Mathf.Sqrt((dx - px) * (dx - px) + (dy - py) * (dy - py));
                alpha = Mathf.Max(alpha, Mathf.Clamp01((size - dist) / (size * 0.6f)));
            }

            if (alpha <= 0f)
                return new Color(0f, 0f, 0f, 0f);

            float depth = Noise(u * 7f, v * 7f, seed + 3);
            var rgb = Color.Lerp(new Color(0.26f, 0.018f, 0.014f), new Color(0.46f, 0.045f, 0.03f), depth);
            return new Color(rgb.r, rgb.g, rgb.b, alpha * 0.94f);
        }

        static float Radius(float u, float v)
        {
            float dx = (u - 0.5f) * 2f;
            float dy = (v - 0.5f) * 2f;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        static void FillQuad(Color32[] pixels, int qx, int qy, System.Func<float, float, Color> sample)
        {
            int ox = qx * Half;
            int oy = qy * Half;
            for (int y = 0; y < Half; y++)
            {
                float v = (y + 0.5f) / Half;
                for (int x = 0; x < Half; x++)
                {
                    float u = (x + 0.5f) / Half;
                    pixels[(oy + y) * Size + ox + x] = sample(u, v);
                }
            }
        }

        static Texture2D Commit(Color32[] pixels, string name)
        {
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, true, false)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 4
            };
            tex.SetPixels32(pixels);
            tex.Apply(true, false);
            return tex;
        }

        static Texture2D TryLoadBaked(string path)
        {
#if UNITY_EDITOR
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
#else
            return null;
#endif
        }

#if UNITY_EDITOR
        public static void BakeAtlasesToProject()
        {
            SaveTexture(BuildAdditive(), BakedAdditivePath);
            SaveTexture(BuildDecal(), BakedDecalPath);

            _additive = AssetDatabase.LoadAssetAtPath<Texture2D>(BakedAdditivePath);
            _decal = AssetDatabase.LoadAssetAtPath<Texture2D>(BakedDecalPath);
            if (_additiveMaterial != null)
                _additiveMaterial.mainTexture = AdditiveTexture;
            if (_decalMaterial != null)
                _decalMaterial.mainTexture = DecalTexture;
        }

        static void SaveTexture(Texture2D texture, string path)
        {
            string directory = Path.GetDirectoryName(path);
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllBytes(path, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
                return;

            importer.textureType = TextureImporterType.Default;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = true;
            importer.sRGBTexture = true;
            importer.mipmapEnabled = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.anisoLevel = 4;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.maxTextureSize = 1024;
            importer.SaveAndReimport();
        }
#endif

        static float Hash(int n)
        {
            n = (n << 13) ^ n;
            return ((n * (n * n * 15731 + 789221) + 1376312589) & 0x7fffffff) / 2147483647f;
        }

        /// <summary>Value noise — smooth enough for texture breakup, cheap enough to bake inline.</summary>
        static float Noise(float x, float y, int seed)
        {
            int xi = Mathf.FloorToInt(x);
            int yi = Mathf.FloorToInt(y);
            float xf = x - xi;
            float yf = y - yi;
            float sx = xf * xf * (3f - 2f * xf);
            float sy = yf * yf * (3f - 2f * yf);

            float n00 = Hash(xi * 374761393 + yi * 668265263 + seed);
            float n10 = Hash((xi + 1) * 374761393 + yi * 668265263 + seed);
            float n01 = Hash(xi * 374761393 + (yi + 1) * 668265263 + seed);
            float n11 = Hash((xi + 1) * 374761393 + (yi + 1) * 668265263 + seed);

            return Mathf.Lerp(Mathf.Lerp(n00, n10, sx), Mathf.Lerp(n01, n11, sx), sy);
        }
    }
}
