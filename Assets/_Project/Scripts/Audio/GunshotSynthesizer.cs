using UnityEngine;

namespace ArenaFps.Audio
{
    /// <summary>
    /// Procedural layered gunshots — no audio files. Transient + body + crack + tail + mech.
    /// </summary>
    public sealed class GunshotSynthesizer : MonoBehaviour
    {
        [SerializeField] AudioSource source;
        [SerializeField] float masterGain = 0.55f;

        void Awake()
        {
            if (source == null)
            {
                source = gameObject.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.spatialBlend = 0f; // first-person: dry/local
                source.loop = false;
            }
        }

        public void PlayGunshot(bool pistol)
        {
            var clip = BuildShot(pistol, Random.Range(0, int.MaxValue));
            source.pitch = Random.Range(0.96f, 1.05f);
            source.PlayOneShot(clip, masterGain);
            Destroy(clip, 2f);
        }

        public void PlayReloadClick()
        {
            var clip = BuildMechClick(Random.Range(0, int.MaxValue));
            source.pitch = Random.Range(0.95f, 1.05f);
            source.PlayOneShot(clip, masterGain * 0.45f);
            Destroy(clip, 1f);
        }

        static AudioClip BuildShot(bool pistol, int seed)
        {
            var rng = new System.Random(seed);
            int sampleRate = 44100;
            float duration = pistol ? 0.35f : 0.45f;
            int samples = Mathf.CeilToInt(sampleRate * duration);
            var data = new float[samples];

            float bodyFreq = pistol ? 95f : 72f;
            float crackFreq = pistol ? 2200f : 1800f;

            for (int i = 0; i < samples; i++)
            {
                float t = i / (float)sampleRate;
                float n = (float)(rng.NextDouble() * 2.0 - 1.0);

                // Transient click
                float click = Mathf.Exp(-t * 220f) * n * 0.9f;

                // Low body thump
                float bodyEnv = Mathf.Exp(-t * (pistol ? 28f : 22f));
                float body = Mathf.Sin(2f * Mathf.PI * bodyFreq * t) * bodyEnv * 0.7f;
                body += Mathf.Sin(2f * Mathf.PI * (bodyFreq * 0.5f) * t) * bodyEnv * 0.35f;

                // Bright crack
                float crackEnv = Mathf.Exp(-t * 55f);
                float crack = Mathf.Sin(2f * Mathf.PI * crackFreq * t) * crackEnv * 0.25f;
                crack += n * crackEnv * 0.35f;

                // Noise tail
                float tailEnv = Mathf.Exp(-t * (pistol ? 10f : 7f)) * (1f - Mathf.Exp(-t * 80f));
                float tail = n * tailEnv * 0.22f;

                // Mechanical layer (bolt-ish)
                float mech = 0f;
                if (t > 0.02f && t < 0.08f)
                {
                    float mt = t - 0.02f;
                    mech = Mathf.Sin(2f * Mathf.PI * 3400f * mt) * Mathf.Exp(-mt * 90f) * 0.12f;
                    mech += n * Mathf.Exp(-mt * 70f) * 0.06f;
                }

                float s = click + body + crack + tail + mech;
                data[i] = Mathf.Clamp(s * 0.55f, -1f, 1f);
            }

            var clip = AudioClip.Create(pistol ? "PistolShot" : "ArShot", samples, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        static AudioClip BuildMechClick(int seed)
        {
            var rng = new System.Random(seed);
            int sampleRate = 44100;
            int samples = Mathf.CeilToInt(sampleRate * 0.12f);
            var data = new float[samples];
            for (int i = 0; i < samples; i++)
            {
                float t = i / (float)sampleRate;
                float n = (float)(rng.NextDouble() * 2.0 - 1.0);
                float env = Mathf.Exp(-t * 60f);
                data[i] = (Mathf.Sin(2f * Mathf.PI * 1800f * t) * 0.3f + n * 0.5f) * env * 0.4f;
            }
            var clip = AudioClip.Create("ReloadClick", samples, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
