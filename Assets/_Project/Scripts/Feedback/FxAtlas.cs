using UnityEngine;

namespace ArenaFps.Feedback
{
    /// <summary>
    /// Procedurally baked texture atlases. Everything additive shares one atlas and every decal
    /// shares another, which is what lets the whole FX layer draw in two calls.
    /// </summary>
    public static class FxAtlas
    {
        const int Size = 256;
        const int Half = Size / 2;

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

        public static Texture2D AdditiveTexture => _additive != null ? _additive : _additive = BuildAdditive();
        public static Texture2D DecalTexture => _decal != null ? _decal : _decal = BuildDecal();

        public static Material AdditiveMaterial
        {
            get
            {
                if (_additiveMaterial != null)
                    return _additiveMaterial;
                var shader = Shader.Find("ArenaFps/FxAdditive");
                _additiveMaterial = new Material(shader != null ? shader : Shader.Find("Sprites/Default"))
                {
                    name = "Fx_Additive_Runtime"
                };
                _additiveMaterial.mainTexture = AdditiveTexture;
                if (_additiveMaterial.HasProperty("_Intensity"))
                    _additiveMaterial.SetFloat("_Intensity", 2.6f);
                return _additiveMaterial;
            }
        }

        public static Material DecalMaterial
        {
            get
            {
                if (_decalMaterial != null)
                    return _decalMaterial;
                var shader = Shader.Find("ArenaFps/FxAlpha");
                _decalMaterial = new Material(shader != null ? shader : Shader.Find("Sprites/Default"))
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
                // Soft round spark core with a hot centre.
                float r = Radius(u, v);
                float a = Mathf.Clamp01(1f - r);
                a = a * a * a;
                return new Color(1f, 1f, 1f, a + Mathf.Clamp01(1f - r * 3.2f) * 0.6f);
            });

            FillQuad(pixels, 1, 0, (u, v) =>
            {
                // Tracer streak: bright core line, soft along the length, hard-ish across it.
                float across = Mathf.Abs(v - 0.5f) * 2f;
                float along = Mathf.Abs(u - 0.5f) * 2f;
                float a = Mathf.Clamp01(1f - across);
                a = Mathf.Pow(a, 2.4f);
                a *= Mathf.Clamp01(1f - along * along);
                return new Color(1f, 1f, 1f, a);
            });

            FillQuad(pixels, 0, 1, (u, v) =>
            {
                // Muzzle star: hot core plus four soft spikes.
                float dx = (u - 0.5f) * 2f;
                float dy = (v - 0.5f) * 2f;
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                float core = Mathf.Clamp01(1f - r * 2.6f);
                float spikes = Mathf.Clamp01(1f - r) * Mathf.Max(
                    Mathf.Clamp01(1f - Mathf.Abs(dy) * 9f),
                    Mathf.Clamp01(1f - Mathf.Abs(dx) * 9f));
                float diag = Mathf.Clamp01(1f - r * 1.4f) * Mathf.Max(
                    Mathf.Clamp01(1f - Mathf.Abs(dx - dy) * 7f),
                    Mathf.Clamp01(1f - Mathf.Abs(dx + dy) * 7f));
                float a = Mathf.Clamp01(core * 1.4f + spikes * 0.85f + diag * 0.45f);
                return new Color(1f, 1f, 1f, a);
            });

            FillQuad(pixels, 1, 1, (u, v) =>
            {
                // Smoke puff: soft blob broken up by low-frequency noise.
                float r = Radius(u, v);
                float n = Noise(u * 5.5f, v * 5.5f, 71) * 0.5f + Noise(u * 11f, v * 11f, 907) * 0.25f;
                float a = Mathf.Clamp01(1f - r * (1.05f + n * 0.5f));
                a = a * a * (0.55f + n * 0.6f);
                return new Color(1f, 1f, 1f, Mathf.Clamp01(a));
            });

            return Commit(pixels, "Fx_Additive_Atlas");
        }

        static Texture2D BuildDecal()
        {
            var pixels = new Color32[Size * Size];

            FillQuad(pixels, 0, 0, (u, v) => BulletHole(u, v, 1231, new Color(0.05f, 0.045f, 0.04f), new Color(0.62f, 0.6f, 0.57f)));
            FillQuad(pixels, 1, 0, (u, v) => BulletHole(u, v, 5417, new Color(0.03f, 0.03f, 0.035f), new Color(0.85f, 0.86f, 0.9f)));
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
            float streak = Mathf.Clamp01(Mathf.Sin(angle * 9f + Noise(u * 3f, v * 3f, seed + 11) * 6f)) * ring * 0.35f;

            float alpha = Mathf.Clamp01(core + ring * 0.75f + streak);
            var rgb = Color.Lerp(ringColor, coreColor, Mathf.Clamp01(core * 1.2f));
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
