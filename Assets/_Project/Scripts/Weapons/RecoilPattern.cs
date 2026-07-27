using UnityEngine;

namespace ArenaFps.Weapons
{
    /// <summary>
    /// Per-weapon recoil shape. Purely random kick reads as noise and cannot be learned; a shaped
    /// pattern with a jitter band is learnable, which is what makes sustained fire a skill instead
    /// of a dice roll. Vertical climbs hard early then eases; horizontal walks a signed path.
    /// </summary>
    [System.Serializable]
    public struct RecoilPattern
    {
        [Tooltip("Vertical kick in degrees for the first shot of a burst")]
        public float verticalFirst;

        [Tooltip("Vertical kick in degrees once the burst has settled")]
        public float verticalSustained;

        [Tooltip("Shots taken to fall from the first-shot kick to the sustained kick")]
        public int settleShots;

        [Tooltip("Horizontal magnitude in degrees")]
        public float horizontal;

        [Tooltip("Fraction of each kick that is randomised")]
        [Range(0f, 1f)] public float jitter;

        [Tooltip("How fast the view returns to the pre-fire aim")]
        public float recovery;

        [Tooltip("Seconds the view holds at full kick before recovering")]
        public float recoveryDelay;

        static readonly float[] Walk = { 0f, 0.15f, -0.35f, 0.55f, 0.8f, 0.35f, -0.45f, -0.85f, -0.6f, 0.2f, 0.7f, 0.95f };

        public Vector2 Sample(int shotIndex)
        {
            float settle = settleShots <= 0 ? 1f : Mathf.Clamp01(shotIndex / (float)settleShots);
            float vertical = Mathf.Lerp(verticalFirst, verticalSustained, settle * settle);

            float lateral = Walk[shotIndex % Walk.Length] * horizontal;

            float vJitter = 1f + Random.Range(-jitter, jitter);
            float hJitter = Random.Range(-jitter, jitter) * horizontal;

            return new Vector2(vertical * vJitter, lateral + hJitter);
        }

        public static RecoilPattern Rifle => new()
        {
            verticalFirst = 0.62f,
            verticalSustained = 0.34f,
            settleShots = 5,
            horizontal = 0.28f,
            jitter = 0.22f,
            recovery = 9.5f,
            recoveryDelay = 0.075f,
        };

        public static RecoilPattern Pistol => new()
        {
            verticalFirst = 1.35f,
            verticalSustained = 1.1f,
            settleShots = 2,
            horizontal = 0.42f,
            jitter = 0.3f,
            recovery = 12.5f,
            recoveryDelay = 0.04f,
        };

        /// <summary>5.56 carbine — snappier than the SCAR-H, still learnable.</summary>
        public static RecoilPattern Carbine => new()
        {
            verticalFirst = 0.48f,
            verticalSustained = 0.26f,
            settleShots = 4,
            horizontal = 0.22f,
            jitter = 0.2f,
            recovery = 11f,
            recoveryDelay = 0.055f,
        };
    }
}
