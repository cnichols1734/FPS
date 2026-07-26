using ArenaFps.Audio;
using ArenaFps.Combat;
using ArenaFps.Player;
using ArenaFps.Weapons;
using UnityEngine;

namespace ArenaFps.UI
{
    /// <summary>
    /// Combat HUD: a crosshair that reports real spread, a hitmarker that pops, kill confirmation,
    /// directional damage indicators and a blood overlay. Deliberately immediate-mode and
    /// dependency-free — the authored UI pass replaces this wholesale later, but the feedback it
    /// carries is what makes shooting legible now.
    /// </summary>
    public sealed class HudView : MonoBehaviour
    {
        struct DamageMark
        {
            public float Angle;
            public float Age;
            public float Strength;
        }

        const int MaxMarks = 6;

        [SerializeField] WeaponController weapon;
        [SerializeField] Damageable damageable;
        [SerializeField] PlayerCombatFeedback feedback;

        [Header("Timing")]
        [SerializeField] float hitmarkerTime = 0.24f;
        [SerializeField] float killTime = 1.1f;
        [SerializeField] float damageMarkTime = 1.35f;

        Camera _camera;
        Transform _self;

        float _hitmarker;
        bool _hitWasHeadshot;
        float _kill;
        bool _killWasHeadshot;
        int _kills;

        readonly DamageMark[] _marks = new DamageMark[MaxMarks];
        int _nextMark;

        GUIStyle _rightStyle;
        GUIStyle _leftStyle;
        GUIStyle _centerStyle;
        Texture2D _chevron;
        Texture2D _blood;
        Texture2D _soft;

        int _cachedAmmo = -1;
        string _ammoText = string.Empty;

        void Awake()
        {
            _self = transform;
            if (weapon == null) weapon = GetComponent<WeaponController>() ?? FindAnyObjectByType<WeaponController>();
            if (damageable == null) damageable = GetComponent<Damageable>();
            if (feedback == null) feedback = GetComponent<PlayerCombatFeedback>();
            _camera = GetComponentInChildren<Camera>();
        }

        void OnEnable()
        {
            CombatEvents.PlayerHitConfirmed += OnPlayerHit;
            CombatEvents.Killed += OnKilled;
            CombatEvents.PlayerDamaged += OnPlayerDamaged;
        }

        void OnDisable()
        {
            CombatEvents.PlayerHitConfirmed -= OnPlayerHit;
            CombatEvents.Killed -= OnKilled;
            CombatEvents.PlayerDamaged -= OnPlayerDamaged;
        }

        void OnPlayerHit(Damageable target, DamageInfo info)
        {
            _hitmarker = hitmarkerTime;
            _hitWasHeadshot = info.IsHeadshot;
            Sfx3D.Instance.Play2D(info.IsHeadshot ? Sfx.HeadshotMarker : Sfx.Hitmarker, 0.5f, 0.02f);
        }

        void OnKilled(Damageable target, DamageInfo info)
        {
            if (target == null || target.IsPlayer || !info.FromPlayer)
                return;
            _kill = killTime;
            _killWasHeadshot = info.IsHeadshot;
            _kills++;
            Sfx3D.Instance.Play2D(Sfx.KillConfirm, 0.5f);
        }

        void OnPlayerDamaged(DamageInfo info)
        {
            // Store the bearing to the shooter, not the bullet, so the arrow points at the threat.
            var source = info.Attacker != null
                ? info.Attacker.transform.position
                : info.Point - info.Direction * 6f;

            var toSource = source - _self.position;
            toSource.y = 0f;
            if (toSource.sqrMagnitude < 0.01f)
                return;

            float angle = Vector3.SignedAngle(_self.forward, toSource.normalized, Vector3.up);
            _marks[_nextMark] = new DamageMark { Angle = angle, Age = 0f, Strength = Mathf.Clamp01(info.Amount / 26f) };
            _nextMark = (_nextMark + 1) % MaxMarks;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (_hitmarker > 0f) _hitmarker -= dt;
            if (_kill > 0f) _kill -= dt;
            for (int i = 0; i < _marks.Length; i++)
            {
                if (_marks[i].Strength > 0f)
                    _marks[i].Age += dt;
            }
        }

        void OnGUI()
        {
            EnsureResources();

            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;

            DrawBloodOverlay();
            DrawCrosshair(cx, cy);
            DrawHitmarker(cx, cy);
            DrawKillConfirm(cx, cy);
            DrawDamageMarks(cx, cy);
            DrawStatus();
        }

        void DrawCrosshair(float cx, float cy)
        {
            float spread = weapon != null ? weapon.SpreadDegrees : 1.5f;
            float fov = _camera != null ? _camera.fieldOfView : 75f;

            // Convert the real cone half-angle into pixels so the reticle never lies about accuracy.
            float gap = Screen.height * Mathf.Tan(spread * Mathf.Deg2Rad) /
                        (2f * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad));
            gap = Mathf.Clamp(gap, 4f, Screen.height * 0.22f);

            bool ads = weapon != null && weapon.AdsProgress > 0.6f;
            float alpha = ads ? 0.35f : 0.9f;
            float arm = ads ? 3f : Mathf.Lerp(6f, 11f, Mathf.Clamp01(gap / 60f));
            float thickness = 1.6f;

            var previous = GUI.color;
            GUI.color = new Color(0.92f, 0.95f, 0.96f, alpha);

            if (ads)
            {
                GUI.DrawTexture(new Rect(cx - 1.5f, cy - 1.5f, 3f, 3f), Texture2D.whiteTexture);
            }
            else
            {
                GUI.DrawTexture(new Rect(cx - gap - arm, cy - thickness * 0.5f, arm, thickness), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(cx + gap, cy - thickness * 0.5f, arm, thickness), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(cx - thickness * 0.5f, cy - gap - arm, thickness, arm), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(cx - thickness * 0.5f, cy + gap, thickness, arm), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(cx - 1f, cy - 1f, 2f, 2f), Texture2D.whiteTexture);
            }

            GUI.color = previous;
        }

        void DrawHitmarker(float cx, float cy)
        {
            if (_hitmarker <= 0f)
                return;

            float t = 1f - _hitmarker / hitmarkerTime;
            // Snap out then ease in: the pop is what registers as a hit at a glance.
            float pop = Mathf.Lerp(4f, 13f, Mathf.Sqrt(Mathf.Clamp01(t * 3.4f))) * (_hitWasHeadshot ? 1.35f : 1f);
            float length = _hitWasHeadshot ? 9f : 7f;
            float alpha = Mathf.Clamp01(1f - t * t);

            var previous = GUI.color;
            var matrix = GUI.matrix;
            GUI.color = _hitWasHeadshot
                ? new Color(1f, 0.78f, 0.2f, alpha)
                : new Color(1f, 1f, 1f, alpha);

            for (int i = 0; i < 4; i++)
            {
                float rotation = 45f + i * 90f;
                GUIUtility.RotateAroundPivot(rotation, new Vector2(cx, cy));
                GUI.DrawTexture(new Rect(cx - 1f, cy - pop - length, 2.2f, length), Texture2D.whiteTexture);
                GUI.matrix = matrix;
            }

            GUI.color = previous;
        }

        void DrawKillConfirm(float cx, float cy)
        {
            if (_kill <= 0f)
                return;

            float t = 1f - _kill / killTime;
            float alpha = Mathf.Clamp01(1f - t * t * t);
            float size = Mathf.Lerp(26f, 34f, Mathf.Sqrt(Mathf.Clamp01(t * 4f)));

            var previous = GUI.color;
            var matrix = GUI.matrix;

            GUI.color = new Color(0.95f, 0.2f, 0.15f, alpha * 0.95f);
            GUIUtility.RotateAroundPivot(45f, new Vector2(cx, cy - 46f));
            GUI.DrawTexture(new Rect(cx - size * 0.5f, cy - 46f - size * 0.5f, size, 2.4f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(cx - size * 0.5f, cy - 46f + size * 0.5f, size, 2.4f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(cx - size * 0.5f, cy - 46f - size * 0.5f, 2.4f, size), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(cx + size * 0.5f, cy - 46f - size * 0.5f, 2.4f, size), Texture2D.whiteTexture);
            GUI.matrix = matrix;

            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.Label(new Rect(cx - 120f, cy - 96f, 240f, 26f),
                _killWasHeadshot ? "ELIMINATED · HEADSHOT" : "ELIMINATED", _centerStyle);

            GUI.color = previous;
        }

        void DrawDamageMarks(float cx, float cy)
        {
            var previous = GUI.color;
            var matrix = GUI.matrix;

            for (int i = 0; i < _marks.Length; i++)
            {
                ref var mark = ref _marks[i];
                if (mark.Strength <= 0f || mark.Age >= damageMarkTime)
                    continue;

                float t = mark.Age / damageMarkTime;
                float alpha = Mathf.Clamp01(1f - t) * (0.5f + mark.Strength * 0.5f);
                float radius = Mathf.Lerp(64f, 92f, t);

                GUI.color = new Color(1f, 0.16f, 0.12f, alpha);
                GUIUtility.RotateAroundPivot(mark.Angle, new Vector2(cx, cy));
                GUI.DrawTexture(new Rect(cx - 26f, cy - radius - 16f, 52f, 16f), _chevron);
                GUI.matrix = matrix;
            }

            GUI.color = previous;
            GUI.matrix = matrix;
        }

        void DrawBloodOverlay()
        {
            float level = feedback != null ? feedback.HurtLevel : 0f;
            if (level <= 0.01f)
                return;

            var previous = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(level) * 0.8f);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), _blood);
            GUI.color = previous;
        }

        void DrawStatus()
        {
            if (weapon != null)
            {
                if (weapon.Ammo != _cachedAmmo)
                {
                    _cachedAmmo = weapon.Ammo;
                    _ammoText = $"{weapon.Ammo}<size=22> / {weapon.Current.magSize}</size>";
                }

                bool low = weapon.Ammo <= Mathf.CeilToInt(weapon.Current.magSize * 0.25f);
                var previous = GUI.color;
                GUI.color = low ? new Color(1f, 0.42f, 0.28f) : new Color(0.94f, 0.95f, 0.96f);
                GUI.Label(new Rect(Screen.width - 300f, Screen.height - 86f, 268f, 48f), _ammoText, _rightStyle);
                GUI.color = new Color(0.72f, 0.74f, 0.76f);
                GUI.Label(new Rect(Screen.width - 300f, Screen.height - 48f, 268f, 24f),
                    weapon.IsReloading ? "RELOADING" : weapon.Current.name, _rightStyle);
                GUI.color = previous;
            }

            if (damageable == null)
                return;

            float normalized = damageable.Normalized;
            const float width = 210f;
            const float height = 6f;
            float x = 32f;
            float y = Screen.height - 54f;

            var old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.45f);
            GUI.DrawTexture(new Rect(x - 2f, y - 2f, width + 4f, height + 4f), _soft);
            GUI.color = normalized > 0.5f
                ? new Color(0.86f, 0.9f, 0.92f, 0.95f)
                : Color.Lerp(new Color(1f, 0.25f, 0.18f), new Color(1f, 0.72f, 0.28f), normalized * 2f);
            GUI.DrawTexture(new Rect(x, y, width * normalized, height), Texture2D.whiteTexture);

            GUI.color = new Color(0.75f, 0.77f, 0.79f);
            GUI.Label(new Rect(x, y - 34f, 220f, 28f), $"{Mathf.CeilToInt(damageable.Current)}", _leftStyle);

            if (_kills > 0)
            {
                GUI.color = new Color(0.8f, 0.82f, 0.84f);
                GUI.Label(new Rect(32f, 28f, 200f, 26f), $"KILLS  {_kills}", _leftStyle);
            }

            GUI.color = old;
        }

        void EnsureResources()
        {
            if (_rightStyle != null)
                return;

            _rightStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 34,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleRight,
                richText = true,
            };
            _rightStyle.normal.textColor = Color.white;

            _leftStyle = new GUIStyle(_rightStyle) { alignment = TextAnchor.MiddleLeft, fontSize = 24 };
            _centerStyle = new GUIStyle(_rightStyle) { alignment = TextAnchor.MiddleCenter, fontSize = 18 };

            _soft = Solid(new Color(1f, 1f, 1f, 1f));
            _chevron = BuildChevron();
            _blood = BuildBloodOverlay();
        }

        static Texture2D Solid(Color color)
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }

        /// <summary>Soft-edged arc used for the damage direction indicator.</summary>
        static Texture2D BuildChevron()
        {
            const int w = 64;
            const int h = 20;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                float v = y / (float)(h - 1);
                for (int x = 0; x < w; x++)
                {
                    float u = x / (float)(w - 1);
                    float centre = 1f - Mathf.Abs(u - 0.5f) * 2f;
                    float band = Mathf.Clamp01(1f - Mathf.Abs(v - 0.72f) * 5.5f);
                    float alpha = Mathf.Clamp01(Mathf.Pow(centre, 0.7f) * band);
                    pixels[y * w + x] = new Color(1f, 1f, 1f, alpha);
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            return tex;
        }

        /// <summary>Edge-weighted blood spatter, so the middle of the screen stays playable.</summary>
        static Texture2D BuildBloodOverlay()
        {
            const int size = 256;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[size * size];
            var rng = new System.Random(90210);

            var blobs = new (float x, float y, float r)[14];
            for (int i = 0; i < blobs.Length; i++)
            {
                float angle = (float)(rng.NextDouble() * Mathf.PI * 2.0);
                float distance = 0.55f + (float)rng.NextDouble() * 0.5f;
                blobs[i] = (0.5f + Mathf.Cos(angle) * distance * 0.5f,
                            0.5f + Mathf.Sin(angle) * distance * 0.5f,
                            0.07f + (float)rng.NextDouble() * 0.16f);
            }

            for (int y = 0; y < size; y++)
            {
                float v = y / (float)size;
                for (int x = 0; x < size; x++)
                {
                    float u = x / (float)size;
                    float alpha = 0f;
                    foreach (var blob in blobs)
                    {
                        float d = Mathf.Sqrt((u - blob.x) * (u - blob.x) + (v - blob.y) * (v - blob.y));
                        alpha = Mathf.Max(alpha, Mathf.Clamp01(1f - d / blob.r));
                    }
                    float edge = Mathf.Clamp01((Mathf.Abs(u - 0.5f) + Mathf.Abs(v - 0.5f)) * 1.5f);
                    alpha = Mathf.Pow(alpha, 1.7f) * (0.35f + edge * 0.9f);
                    pixels[y * size + x] = new Color(0.42f, 0.03f, 0.02f, Mathf.Clamp01(alpha) * 0.9f);
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply();
            return tex;
        }
    }
}
