using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ArenaFps.Player
{
    /// <summary>
    /// The base grade: bloom so muzzle flashes and tracers actually bloom, neutral tonemapping, a
    /// touch of contrast, and restrained grain. Built at runtime into its own profile so nothing
    /// here dirties a project asset, and kept below the damage volume in priority.
    /// </summary>
    public sealed class ScreenLook : MonoBehaviour
    {
        [SerializeField] float bloomIntensity = 0.62f;
        [SerializeField] float bloomThreshold = 0.95f;
        [SerializeField] float contrast = 9f;
        [SerializeField] float saturation = -5f;
        [SerializeField] float grain = 0.16f;
        [SerializeField] float baseVignette = 0.18f;

        void Awake()
        {
            var go = new GameObject("__ScreenLook");
            go.transform.SetParent(transform, false);

            var volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 50f;

            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "ScreenLook_Runtime";
            volume.profile = profile;

            var bloom = profile.Add<Bloom>(true);
            bloom.intensity.overrideState = true;
            bloom.intensity.value = bloomIntensity;
            bloom.threshold.overrideState = true;
            bloom.threshold.value = bloomThreshold;
            bloom.scatter.overrideState = true;
            bloom.scatter.value = 0.62f;
            // Half-resolution bloom keeps this affordable inside the M1 Pro GPU budget.
            bloom.downscale.overrideState = true;
            bloom.downscale.value = BloomDownscaleMode.Half;

            var tonemapping = profile.Add<Tonemapping>(true);
            tonemapping.mode.overrideState = true;
            tonemapping.mode.value = TonemappingMode.Neutral;

            var grade = profile.Add<ColorAdjustments>(true);
            grade.postExposure.overrideState = true;
            grade.postExposure.value = 0.1f;
            grade.contrast.overrideState = true;
            grade.contrast.value = contrast;
            grade.saturation.overrideState = true;
            grade.saturation.value = saturation;

            var film = profile.Add<FilmGrain>(true);
            film.type.overrideState = true;
            film.type.value = FilmGrainLookup.Medium1;
            film.intensity.overrideState = true;
            film.intensity.value = grain;
            film.response.overrideState = true;
            film.response.value = 0.75f;

            var vignette = profile.Add<Vignette>(true);
            vignette.intensity.overrideState = true;
            vignette.intensity.value = baseVignette;
            vignette.smoothness.overrideState = true;
            vignette.smoothness.value = 0.4f;
        }
    }
}
