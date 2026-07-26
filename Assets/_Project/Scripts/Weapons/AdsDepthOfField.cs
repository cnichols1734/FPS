using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ArenaFps.Weapons
{
    /// <summary>
    /// COD-style ADS focus: Bokeh depth of field with focus on the mid-range world so the
    /// near viewmodel (and ghost-ring irons) softens while the target plane stays sharp.
    /// URP Gaussian cannot do near blur — Bokeh is required for this look.
    /// </summary>
    public sealed class AdsDepthOfField : MonoBehaviour
    {
        [SerializeField] float focusDistance = 12f;
        [SerializeField] float hipAperture = 22f;
        // Softer than a full aimbot-blur — ring stays readable, gun softens, world stays usable.
        [SerializeField] float adsAperture = 2.8f;
        [SerializeField] float hipFocalLength = 35f;
        [SerializeField] float adsFocalLength = 55f;
        [SerializeField] int bladeCount = 6;
        [SerializeField] float activateThreshold = 0.04f;

        WeaponController _weapon;
        DepthOfField _dof;
        Volume _volume;

        void Awake()
        {
            _weapon = GetComponent<WeaponController>();

            var go = new GameObject("__AdsDepthOfField");
            go.transform.SetParent(transform, false);

            _volume = go.AddComponent<Volume>();
            _volume.isGlobal = true;
            _volume.priority = 70f;
            _volume.weight = 0f;

            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "AdsDof_Runtime";
            _volume.profile = profile;

            _dof = profile.Add<DepthOfField>(true);
            _dof.mode.overrideState = true;
            _dof.mode.value = DepthOfFieldMode.Bokeh;
            _dof.focusDistance.overrideState = true;
            _dof.focusDistance.value = focusDistance;
            _dof.focalLength.overrideState = true;
            _dof.focalLength.value = hipFocalLength;
            _dof.aperture.overrideState = true;
            _dof.aperture.value = hipAperture;
            _dof.bladeCount.overrideState = true;
            _dof.bladeCount.value = bladeCount;
            _dof.bladeCurvature.overrideState = true;
            _dof.bladeCurvature.value = 0.7f;
            _dof.bladeRotation.overrideState = true;
            _dof.bladeRotation.value = 0f;
        }

        void LateUpdate()
        {
            if (_dof == null || _volume == null)
                return;

            float ads = _weapon != null ? _weapon.AdsProgress : 0f;
            float eased = ads * ads * (3f - 2f * ads);

            if (eased <= activateThreshold)
            {
                _volume.weight = 0f;
                _dof.mode.value = DepthOfFieldMode.Off;
                return;
            }

            _dof.mode.value = DepthOfFieldMode.Bokeh;
            _volume.weight = Mathf.Clamp01((eased - activateThreshold) / (1f - activateThreshold));

            // Lower aperture → shallower DOF → stronger near blur on the gun / rear sight.
            _dof.aperture.value = Mathf.Lerp(hipAperture, adsAperture, eased);
            _dof.focalLength.value = Mathf.Lerp(hipFocalLength, adsFocalLength, eased);
            _dof.focusDistance.value = focusDistance;
        }
    }
}
