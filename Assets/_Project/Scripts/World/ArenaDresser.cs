using UnityEngine;
using UnityEngine.Rendering;

namespace ArenaFps.World
{
    /// <summary>
    /// Runtime dress of the greybox — applies materials + HDRI sky if textures are in the project.
    /// Safe to run every Play; no-ops gracefully if assets aren't imported yet.
    /// </summary>
    public sealed class ArenaDresser : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Object.FindAnyObjectByType<ArenaDresser>() != null)
                return;
            var go = new GameObject("__ArenaDresser");
            go.AddComponent<ArenaDresser>();
        }

        void Start()
        {
            ApplySky();
            ApplyGroundAndWalls();
            TuneLight();
            Debug.Log("[ArenaDresser] Arena materials/lighting applied.");
        }

        void ApplySky()
        {
            var hdr = Resources.Load<Cubemap>("HDRI/abandoned_bakery_4k");
            // Prefer Texture imported as Default/Cube — also try LoadAll from path via AssetDatabase isn't available runtime.
            // Runtime: use ReflectionProbe + material if we find a Texture2D in Resources.
            var tex = Resources.Load<Texture>("HDRI/abandoned_bakery_4k");
            if (tex == null)
            {
                // Soft gradient sky color fallback so the box isn't clinical white
                RenderSettings.skybox = BuildGradientSky();
                RenderSettings.ambientMode = AmbientMode.Trilight;
                RenderSettings.ambientSkyColor = new Color(0.55f, 0.62f, 0.75f);
                RenderSettings.ambientEquatorColor = new Color(0.4f, 0.38f, 0.35f);
                RenderSettings.ambientGroundColor = new Color(0.15f, 0.14f, 0.12f);
                return;
            }

            var sky = BuildHdrSky(tex);
            RenderSettings.skybox = sky;
            RenderSettings.ambientMode = AmbientMode.Skybox;
            DynamicGI.UpdateEnvironment();
        }

        void ApplyGroundAndWalls()
        {
            var concrete = BuildLit(new Color(0.45f, 0.44f, 0.42f), 0.85f, 0f);
            var brick = BuildLit(new Color(0.42f, 0.28f, 0.22f), 0.9f, 0f);
            var metal = BuildLit(new Color(0.35f, 0.36f, 0.38f), 0.45f, 0.85f);
            var cover = BuildLit(new Color(0.38f, 0.32f, 0.22f), 0.75f, 0.05f);

            // Try pull albedo textures from Resources if present
            var concreteTex = Resources.Load<Texture2D>("Textures/Concrete/Concrete048_2K_Color");
            if (concreteTex == null)
                concreteTex = Resources.Load<Texture2D>("Textures/Concrete/Concrete034_2K_Color");
            if (concreteTex != null)
            {
                concrete.SetTexture("_BaseMap", concreteTex);
                concrete.mainTextureScale = new Vector2(8f, 8f);
            }

            var brickTex = Resources.Load<Texture2D>("Textures/Brick/brick_4_diff_2k");
            if (brickTex != null)
            {
                brick.SetTexture("_BaseMap", brickTex);
                brick.mainTextureScale = new Vector2(4f, 4f);
            }

            SetMat("Ground", concrete);
            SetMat("Wall_West", brick);
            SetMat("Wall_East", brick);
            SetMat("Wall_North", brick);
            SetMat("Wall_South", brick);
            SetMat("Cover_A", cover);
            SetMat("Cover_B", metal);
            SetMat("Cover_C", cover);
        }

        void TuneLight()
        {
            var light = Object.FindAnyObjectByType<Light>();
            if (light == null || light.type != LightType.Directional) return;
            light.color = new Color(1f, 0.93f, 0.82f);
            light.intensity = 1.35f;
            light.shadows = LightShadows.Soft;
            light.transform.rotation = Quaternion.Euler(38f, -40f, 0f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.012f;
            RenderSettings.fogColor = new Color(0.55f, 0.58f, 0.62f);
        }

        static void SetMat(string name, Material mat)
        {
            var go = GameObject.Find(name);
            if (go == null) return;
            var r = go.GetComponent<MeshRenderer>();
            if (r != null)
                r.sharedMaterial = mat;
        }

        static Material BuildLit(Color color, float roughness, float metallic)
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
                return BuildLit(new Color(0.5f, 0.55f, 0.65f), 1f, 0f);
            var m = new Material(shader);
            if (m.HasProperty("_SunSize"))
                m.SetFloat("_SunSize", 0.02f);
            if (m.HasProperty("_AtmosphereThickness"))
                m.SetFloat("_AtmosphereThickness", 0.9f);
            if (m.HasProperty("_SkyTint"))
                m.SetColor("_SkyTint", new Color(0.5f, 0.55f, 0.65f));
            if (m.HasProperty("_GroundColor"))
                m.SetColor("_GroundColor", new Color(0.25f, 0.22f, 0.2f));
            if (m.HasProperty("_Exposure"))
                m.SetFloat("_Exposure", 1.1f);
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
                m.SetFloat("_Exposure", 1.0f);
            return m;
        }
    }
}
