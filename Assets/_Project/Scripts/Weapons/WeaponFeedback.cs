using ArenaFps.Core;
using UnityEngine;

namespace ArenaFps.Weapons
{
    /// <summary>
    /// Physical anchors on the weapon — muzzle, ejection port — plus the muzzle light that throws
    /// the flash onto nearby geometry. The flash sprite itself is batched by the FX layer; this is
    /// only the part that has to be a real light to read on walls.
    /// </summary>
    public sealed class WeaponFeedback : MonoBehaviour
    {
        [SerializeField] Transform muzzle;
        [SerializeField] Transform ejectionPort;
        [SerializeField] float flashDuration = 0.038f;
        [SerializeField] float flashIntensity = 6.5f;

        Light _light;
        float _flashUntil;

        public Vector3 MuzzlePosition => muzzle != null ? muzzle.position : transform.position;
        public Vector3 EjectionPosition => ejectionPort != null ? ejectionPort.position : MuzzlePosition;

        void Awake()
        {
            var weaponRoot = transform.Find("CameraPivot/WeaponRoot") ?? transform.Find("WeaponRoot");
            if (weaponRoot == null)
                return;

            RebindAnchors(weaponRoot);
            EnsureMuzzleLight();
        }

        /// <summary>Re-resolve muzzle / ejection after the viewmodel is (re)built.</summary>
        public void RebindAnchors(Transform weaponRoot)
        {
            if (weaponRoot == null)
                return;

            muzzle = ScarHViewmodelBuilder.FindDeep(weaponRoot, "Muzzle");
            if (muzzle == null)
            {
                var go = new GameObject("Muzzle");
                go.transform.SetParent(weaponRoot, false);
                go.transform.localPosition = new Vector3(0f, 0.015f, 0.55f);
                muzzle = go.transform;
            }

            ejectionPort = ScarHViewmodelBuilder.FindDeep(weaponRoot, "EjectionPort");
            if (ejectionPort == null)
            {
                var go = new GameObject("EjectionPort");
                go.transform.SetParent(weaponRoot, false);
                go.transform.localPosition = new Vector3(0.045f, 0.03f, 0.08f);
                ejectionPort = go.transform;
            }

            if (_light != null)
                _light.transform.SetParent(muzzle, false);
        }

        void EnsureMuzzleLight()
        {
            if (_light != null || muzzle == null)
                return;

            var lightGo = new GameObject("MuzzleLight") { layer = GameLayers.Default };
            lightGo.transform.SetParent(muzzle, false);
            _light = lightGo.AddComponent<Light>();
            _light.type = LightType.Point;
            _light.range = 7f;
            _light.intensity = flashIntensity;
            _light.color = new Color(1f, 0.74f, 0.38f);
            _light.shadows = LightShadows.None;
            _light.enabled = false;
        }

        void Update()
        {
            if (_light == null)
                return;

            bool on = Time.time < _flashUntil;
            if (_light.enabled != on)
                _light.enabled = on;
            if (on)
            {
                float t = Mathf.Clamp01((_flashUntil - Time.time) / flashDuration);
                _light.intensity = flashIntensity * t * Random.Range(0.85f, 1.15f);
            }
        }

        public void Flash() => _flashUntil = Time.time + flashDuration;
    }
}
