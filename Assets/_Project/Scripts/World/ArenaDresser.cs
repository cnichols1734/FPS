using UnityEngine;
using UnityEngine.Rendering;

namespace ArenaFps.World
{
    /// <summary>
    /// LEGACY greybox dresser. DISABLED BY DEFAULT — do not re-enable on the dressed Arena map.
    ///
    /// This ran at runtime and silently reverted the authored scene every time you pressed Play:
    ///   - ApplySky() replaced the skybox with Resources/HDRI/abandoned_construction_4k
    ///     (the "old warehouse" sky) instead of the authored overcast_soil_puresky.
    ///   - ApplyNamedFallbacks()/ApplyTagged() overwrote real PBR materials — including Ground —
    ///     with flat runtime-built colours carrying no normal map.
    ///   - TuneLight() overrode the tuned sun and reset fog to a cold blue-grey at ~4x the
    ///     authored density.
    ///
    /// It was also marked [RuntimeInitializeOnLoadMethod], so it spawned itself even when absent
    /// from the scene — which is why the override could not be removed from the Hierarchy.
    ///
    /// It is kept only to dress an undressed greybox. To use it there, add the component manually
    /// and tick runtimeDressingEnabled. It will never self-spawn again.
    /// </summary>
    public sealed class ArenaDresser : MonoBehaviour
    {
        [Tooltip("Off by default. Only enable on an UNDRESSED greybox map — on the dressed Arena " +
                 "map this replaces authored PBR materials, sky, sun and fog with placeholders.")]
        [SerializeField] bool runtimeDressingEnabled;

        Material _asphalt;
        Material _brick;
        Material _concrete;
        Material _metal;
        Material _wood;
        Material _plaster;

        void Start()
        {
            if (!runtimeDressingEnabled)
            {
                // Authored art wins. Leave sky, materials, sun and fog exactly as saved.
                return;
            }

            Debug.LogWarning("[ArenaDresser] Runtime dressing is ENABLED — it is overwriting " +
                             "authored materials, sky, sun and fog with greybox placeholders.");
            BuildMaterials();
            ApplySky();
            ApplyTagged();
            ApplyNamedFallbacks();
            TuneLight();
        }

        void BuildMaterials()
        {
            _asphalt = Lit(new Color(0.22f, 0.22f, 0.23f), 0.92f, 0f);
            _brick = Lit(new Color(0.42f, 0.28f, 0.22f), 0.9f, 0f);
            _concrete = Lit(new Color(0.45f, 0.44f, 0.42f), 0.85f, 0f);
            _metal = Lit(new Color(0.38f, 0.39f, 0.41f), 0.4f, 0.85f);
            _wood = Lit(new Color(0.38f, 0.28f, 0.18f), 0.8f, 0.05f);
            _plaster = Lit(new Color(0.72f, 0.7f, 0.66f), 0.88f, 0f);

            // Prefer generated photoreal albedos when present; fall back to ambientCG / Poly Haven.
            AssignTex(_asphalt, "Textures/Generated/Asphalt_Color", 22f, null, null);
            if (_asphalt.GetTexture("_BaseMap") == null)
                AssignTex(_asphalt, "Textures/Asphalt/Asphalt033_Color", 10f,
                    "Textures/Asphalt/Asphalt033_NormalGL", "Textures/Asphalt/Asphalt033_Roughness");

            AssignTex(_brick, "Textures/Generated/BrickWall_Color", 3.2f, null, null);
            if (_brick.GetTexture("_BaseMap") == null)
                AssignTex(_brick, "Textures/Brick/brick_4_diff_2k", 4f, null, null);

            AssignTex(_concrete, "Textures/Generated/Concrete_Color", 4.5f, null, null);
            if (_concrete.GetTexture("_BaseMap") == null)
            {
                AssignTex(_concrete, "Textures/Concrete/Concrete048_2K_Color", 6f, null, null);
                if (_concrete.GetTexture("_BaseMap") == null)
                    AssignTex(_concrete, "Textures/Concrete/Concrete034_2K_Color", 6f, null, null);
            }

            AssignTex(_metal, "Textures/Generated/Metal_Color", 2.2f, null, null);
            if (_metal.GetTexture("_BaseMap") == null)
                AssignTex(_metal, "Textures/Metal/CorrugatedSteel009_Color", 3f,
                    "Textures/Metal/CorrugatedSteel009_NormalGL", "Textures/Metal/CorrugatedSteel009_Roughness");

            AssignTex(_wood, "Textures/Wood/Wood095_Color", 2.5f,
                "Textures/Wood/Wood095_NormalGL", "Textures/Wood/Wood095_Roughness");

            AssignTex(_plaster, "Textures/Generated/Plaster_Color", 3.5f, null, null);
            if (_plaster.GetTexture("_BaseMap") == null)
                AssignTex(_plaster, "Textures/Plaster/Plaster001_Color", 4f,
                    "Textures/Plaster/Plaster001_NormalGL", "Textures/Plaster/Plaster001_Roughness");
        }

        void ApplyTagged()
        {
            foreach (var tag in Object.FindObjectsByType<MapMaterialTag>())
            {
                var mat = Resolve(tag.materialKey);
                if (mat == null) continue;
                var r = tag.GetComponent<MeshRenderer>();
                if (r != null)
                    r.sharedMaterial = mat;
            }
        }

        void ApplyNamedFallbacks()
        {
            SetMat("Ground", _asphalt);
            foreach (var name in new[] { "Wall_West", "Wall_East", "Wall_North", "Wall_South" })
                SetMat(name, _brick);
            SetMat("Cover_A", _wood);
            SetMat("Cover_B", _metal);
            SetMat("Cover_C", _concrete);
        }

        Material Resolve(string key) => key switch
        {
            "Mat_Asphalt" => _asphalt,
            "Mat_Brick" => _brick,
            "Mat_Concrete" => _concrete,
            "Mat_Metal" => _metal,
            "Mat_Wood" => _wood,
            "Mat_Plaster" => _plaster,
            _ => _concrete,
        };

        void ApplySky()
        {
            var tex = Resources.Load<Texture>("HDRI/abandoned_construction_4k");
            if (tex == null)
                tex = Resources.Load<Texture>("HDRI/abandoned_bakery_4k");

            if (tex == null)
            {
                RenderSettings.skybox = BuildGradientSky();
                RenderSettings.ambientMode = AmbientMode.Trilight;
                RenderSettings.ambientSkyColor = new Color(0.55f, 0.62f, 0.75f);
                RenderSettings.ambientEquatorColor = new Color(0.4f, 0.38f, 0.35f);
                RenderSettings.ambientGroundColor = new Color(0.15f, 0.14f, 0.12f);
                return;
            }

            RenderSettings.skybox = BuildHdrSky(tex);
            RenderSettings.ambientMode = AmbientMode.Skybox;
            DynamicGI.UpdateEnvironment();
        }

        void TuneLight()
        {
            var light = Object.FindAnyObjectByType<Light>();
            if (light == null || light.type != LightType.Directional) return;
            light.color = new Color(1f, 0.93f, 0.82f);
            light.intensity = 1.45f;
            light.shadows = LightShadows.Soft;
            light.transform.rotation = Quaternion.Euler(42f, -35f, 0f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.0085f;
            RenderSettings.fogColor = new Color(0.52f, 0.56f, 0.6f);
        }

        static void SetMat(string name, Material mat)
        {
            var go = GameObject.Find(name);
            if (go == null || mat == null) return;
            var r = go.GetComponent<MeshRenderer>();
            if (r != null)
                r.sharedMaterial = mat;
        }

        static void AssignTex(Material m, string colorPath, float tiling, string normalPath, string roughPath)
        {
            var color = Resources.Load<Texture2D>(colorPath);
            if (color != null)
            {
                m.SetTexture("_BaseMap", color);
                m.mainTextureScale = new Vector2(tiling, tiling);
            }

            if (!string.IsNullOrEmpty(normalPath))
            {
                var n = Resources.Load<Texture2D>(normalPath);
                if (n != null && m.HasProperty("_BumpMap"))
                {
                    m.SetTexture("_BumpMap", n);
                    m.EnableKeyword("_NORMALMAP");
                }
            }

            if (!string.IsNullOrEmpty(roughPath) && m.HasProperty("_Smoothness"))
            {
                // Roughness maps aren't a perfect URP smoothness source without inversion packing;
                // keep a sensible default and let albedo carry the look.
                m.SetFloat("_Smoothness", 0.28f);
            }
        }

        static Material Lit(Color color, float roughness, float metallic)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var m = new Material(shader);
            if (m.HasProperty("_BaseColor"))
                m.SetColor("_BaseColor", color);
            else
                m.color = color;
            if (m.HasProperty("_Smoothness"))
                m.SetFloat("_Smoothness", 1f - roughness);
            if (m.HasProperty("_Metallic"))
                m.SetFloat("_Metallic", metallic);
            return m;
        }

        static Material BuildGradientSky()
        {
            var shader = Shader.Find("Skybox/Procedural");
            if (shader == null)
                return Lit(new Color(0.5f, 0.55f, 0.65f), 1f, 0f);
            var m = new Material(shader);
            if (m.HasProperty("_SunSize")) m.SetFloat("_SunSize", 0.02f);
            if (m.HasProperty("_AtmosphereThickness")) m.SetFloat("_AtmosphereThickness", 0.85f);
            if (m.HasProperty("_SkyTint")) m.SetColor("_SkyTint", new Color(0.45f, 0.52f, 0.62f));
            if (m.HasProperty("_GroundColor")) m.SetColor("_GroundColor", new Color(0.22f, 0.2f, 0.18f));
            if (m.HasProperty("_Exposure")) m.SetFloat("_Exposure", 1.15f);
            return m;
        }

        static Material BuildHdrSky(Texture tex)
        {
            var shader = Shader.Find("Skybox/Panoramic");
            if (shader == null)
                return BuildGradientSky();
            var m = new Material(shader);
            m.SetTexture("_MainTex", tex);
            if (m.HasProperty("_Exposure"))
                m.SetFloat("_Exposure", 1.05f);
            return m;
        }
    }
}
