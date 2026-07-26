using ArenaFps.Audio;
using ArenaFps.Combat;
using ArenaFps.Weapons;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ArenaFps.Player
{
    /// <summary>
    /// What it feels like to be shot: view flinch, red vignette, chromatic punch, a grunt, a
    /// heartbeat when nearly dead, delayed regeneration, and a death-to-respawn loop. Without this
    /// the player has no idea where fire is coming from or how close they are to dying.
    /// </summary>
    public sealed class PlayerCombatFeedback : MonoBehaviour
    {
        [Header("Regeneration")]
        [SerializeField] float regenDelay = 2.7f;
        [SerializeField] float regenRate = 32f;

        [Header("Screen")]
        [SerializeField] float vignetteMax = 0.52f;
        [SerializeField] float aberrationMax = 0.85f;
        [SerializeField] float shakeDecay = 5.5f;

        [Header("Death")]
        [SerializeField] float respawnDelay = 3.2f;

        Damageable _health;
        FpsController _controller;
        WeaponController _weapon;
        Transform _camera;
        Vector3 _cameraHome;

        Volume _volume;
        Vignette _vignette;
        ChromaticAberration _aberration;

        float _hurtLevel;
        float _lastDamageAt = -99f;
        Vector3 _shake;
        Vector3 _shakeVelocity;
        float _nextHeartbeat;
        float _deathAt = -1f;
        Vector3 _spawnPosition;
        Quaternion _spawnRotation;

        /// <summary>0 to 1 flash used by the HUD to draw the blood overlay.</summary>
        public float HurtLevel => _hurtLevel;
        public bool IsDead => _health != null && _health.IsDead;

        void Awake()
        {
            _health = GetComponent<Damageable>();
            _controller = GetComponent<FpsController>();
            _weapon = GetComponent<WeaponController>();

            var cam = GetComponentInChildren<Camera>();
            if (cam != null)
            {
                _camera = cam.transform;
                _cameraHome = _camera.localPosition;

                // Post-processing has to be on for any of the screen feedback below to render.
                var urpData = cam.GetComponent<UniversalAdditionalCameraData>()
                              ?? cam.gameObject.AddComponent<UniversalAdditionalCameraData>();
                urpData.renderPostProcessing = true;
                urpData.antialiasing = AntialiasingMode.TemporalAntiAliasing;
            }

            _spawnPosition = transform.position;
            _spawnRotation = transform.rotation;

            BuildVolume();
        }

        void OnEnable() => CombatEvents.PlayerDamaged += OnDamaged;
        void OnDisable() => CombatEvents.PlayerDamaged -= OnDamaged;

        /// <summary>
        /// A local volume with its own profile instance: overriding the scene's shared profile would
        /// dirty a project asset every time the player got shot.
        /// </summary>
        void BuildVolume()
        {
            var go = new GameObject("__PlayerScreenFx");
            go.transform.SetParent(transform, false);
            _volume = go.AddComponent<Volume>();
            _volume.isGlobal = true;
            _volume.priority = 100f;

            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "PlayerScreenFx_Runtime";
            _volume.profile = profile;

            _vignette = profile.Add<Vignette>(true);
            _vignette.color.overrideState = true;
            _vignette.intensity.overrideState = true;
            _vignette.smoothness.overrideState = true;
            _vignette.color.value = new Color(0.5f, 0.02f, 0.02f);
            _vignette.smoothness.value = 0.55f;
            _vignette.intensity.value = 0f;

            _aberration = profile.Add<ChromaticAberration>(true);
            _aberration.intensity.overrideState = true;
            _aberration.intensity.value = 0f;
        }

        void OnDamaged(DamageInfo info)
        {
            _lastDamageAt = Time.time;
            float severity = Mathf.Clamp01(info.Amount / 30f);
            _hurtLevel = Mathf.Clamp01(_hurtLevel + 0.45f + severity * 0.5f);

            // Flinch the view away from the shot, so return fire needs a correction.
            if (_weapon != null)
            {
                var local = transform.InverseTransformDirection(info.Direction.normalized);
                _weapon.AddViewPunch(new Vector2(severity * 1.5f, -local.x * severity * 2.2f), 0.09f);
            }

            _shakeVelocity += new Vector3(
                Random.Range(-1f, 1f),
                Random.Range(-1f, 1f),
                Random.Range(-0.4f, 0.4f)) * (0.12f + severity * 0.22f);

            Sfx3D.Instance.Play2D(Sfx.PlayerHurt, 0.5f + severity * 0.35f, 0.12f);
        }

        void Update()
        {
            float dt = Time.deltaTime;
            _hurtLevel = Mathf.MoveTowards(_hurtLevel, 0f, dt * 1.35f);

            TickRegen(dt);
            TickScreen(dt);
            TickShake(dt);
            TickHeartbeat();
            TickDeath();
        }

        void TickRegen(float dt)
        {
            if (_health == null || _health.IsDead)
                return;
            if (Time.time - _lastDamageAt < regenDelay)
                return;
            _health.Heal(regenRate * dt);
        }

        void TickScreen(float dt)
        {
            if (_vignette == null || _health == null)
                return;

            // Two contributions: a spike from the last hit, and a floor set by how hurt you are.
            float wounded = 1f - _health.Normalized;
            float steady = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((wounded - 0.45f) / 0.55f));
            float target = Mathf.Clamp01(_hurtLevel * 0.8f + steady * 0.75f);

            _vignette.intensity.value = Mathf.Lerp(_vignette.intensity.value, target * vignetteMax, 1f - Mathf.Exp(-9f * dt));
            _aberration.intensity.value = Mathf.Lerp(_aberration.intensity.value, _hurtLevel * aberrationMax, 1f - Mathf.Exp(-12f * dt));
        }

        void TickShake(float dt)
        {
            if (_camera == null)
                return;

            var accel = -_shake * 320f - _shakeVelocity * 22f;
            _shakeVelocity += accel * dt;
            _shake += _shakeVelocity * dt;
            _shake = Vector3.MoveTowards(_shake, Vector3.zero, dt * shakeDecay * 0.02f);

            _camera.localPosition = _cameraHome + _shake * 0.06f;
        }

        void TickHeartbeat()
        {
            if (_health == null || _health.IsDead || _health.Normalized > 0.32f)
                return;
            if (Time.time < _nextHeartbeat)
                return;

            float urgency = Mathf.InverseLerp(0.32f, 0.05f, _health.Normalized);
            _nextHeartbeat = Time.time + Mathf.Lerp(1.05f, 0.62f, urgency);
            Sfx3D.Instance.Play2D(Sfx.Heartbeat, 0.28f + urgency * 0.3f, 0.05f);
        }

        void TickDeath()
        {
            if (_health == null)
                return;

            if (!_health.IsDead)
            {
                _deathAt = -1f;
                return;
            }

            if (_deathAt < 0f)
            {
                _deathAt = Time.time;
                if (_controller != null)
                    _controller.enabled = false;
                // The weapon also owns camera rotation, so it has to yield for the collapse.
                if (_weapon != null)
                    _weapon.enabled = false;
            }

            if (_camera != null)
            {
                // Collapse: drop and roll the view rather than freezing on the last frame.
                float t = Mathf.Clamp01((Time.time - _deathAt) / 0.9f);
                _camera.localPosition = _cameraHome + Vector3.down * (0.95f * Mathf.SmoothStep(0f, 1f, t));
                _camera.localRotation = Quaternion.Euler(0f, 0f, 62f * Mathf.SmoothStep(0f, 1f, t));
            }

            if (Time.time - _deathAt >= respawnDelay)
                Respawn();
        }

        void Respawn()
        {
            _deathAt = -1f;
            _hurtLevel = 0f;
            _lastDamageAt = -99f;

            transform.SetPositionAndRotation(_spawnPosition, _spawnRotation);
            _health.Respawn();

            if (_camera != null)
            {
                _camera.localPosition = _cameraHome;
                _camera.localRotation = Quaternion.identity;
            }
            if (_controller != null)
                _controller.enabled = true;
            if (_weapon != null)
                _weapon.enabled = true;
        }
    }
}
