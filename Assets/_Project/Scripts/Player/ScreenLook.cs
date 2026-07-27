using ArenaFps.Weapons;
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
        [Tooltip("Bloom intensity while ADS on a holo optic — keeps the reticle crisp.")]
        [SerializeField] float opticAdsBloomIntensity = 0.08f;
        [SerializeField] float opticAdsBloomThreshold = 1.35f;

        Bloom _bloom;
        WeaponController _weapon;

        void Awake()
        {
            _weapon = GetComponent<WeaponController>()
                      ?? GetComponentInParent<WeaponController>();

            var go = new GameObject("__ScreenLook");
            go.transform.SetParent(transform, false);

            var volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 50f;

            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "ScreenLook_Runtime";
            volume.profile = profile;

            _bloom = profile.Add<Bloom>(true);
            _bloom.intensity.overrideState = true;
            _bloom.intensity.value = bloomIntensity;
            _bloom.threshold.overrideState = true;
            _bloom.threshold.value = bloomThreshold;
            _bloom.scatter.overrideState = true;
            _bloom.scatter.value = 0.62f;
            // Half-resolution bloom keeps this affordable inside the M1 Pro GPU budget.
            _bloom.downscale.overrideState = true;
            _bloom.downscale.value = BloomDownscaleMode.Half;

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

        void LateUpdate()
        {
            if (_bloom == null)
                return;

            if (_weapon == null)
                _weapon = GetComponent<WeaponController>()
                          ?? GetComponentInParent<WeaponController>()
                          ?? FindAnyObjectByType<WeaponController>();

            bool opticAds = _weapon != null
                            && _weapon.Slot == WeaponController.WeaponSlot.Carbine
                            && _weapon.AdsProgress > 0.05f;
            float t = opticAds
                ? Mathf.SmoothStep(0f, 1f, _weapon.AdsProgress)
                : 0f;

            _bloom.intensity.value = Mathf.Lerp(bloomIntensity, opticAdsBloomIntensity, t);
            _bloom.threshold.value = Mathf.Lerp(bloomThreshold, opticAdsBloomThreshold, t);
        }
    }
}
