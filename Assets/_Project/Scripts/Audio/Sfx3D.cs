using UnityEngine;

namespace ArenaFps.Audio
{
    /// <summary>
    /// Pooled positional one-shots. Unity's PlayClipAtPoint allocates a GameObject per call, which
    /// at automatic fire rates is a steady GC drip — this reuses a fixed ring of sources instead.
    /// </summary>
    public sealed class Sfx3D : MonoBehaviour
    {
        const int Voices = 28;

        static Sfx3D _instance;

        AudioSource[] _voices;
        int _next;

        public static Sfx3D Instance
        {
            get
            {
                if (_instance != null)
                    return _instance;
                var go = new GameObject("__Sfx3D");
                _instance = go.AddComponent<Sfx3D>();
                DontDestroyOnLoad(go);
                return _instance;
            }
        }

        void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;

            _voices = new AudioSource[Voices];
            for (int i = 0; i < Voices; i++)
            {
                var child = new GameObject($"Voice_{i}");
                child.transform.SetParent(transform, false);
                var src = child.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.spatialBlend = 1f;
                src.rolloffMode = AudioRolloffMode.Logarithmic;
                src.dopplerLevel = 0f;
                src.minDistance = 2.5f;
                src.maxDistance = 90f;
                _voices[i] = src;
            }
        }

        void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        /// <summary>Plays at a world position. Returns the voice so callers can tweak it further.</summary>
        public AudioSource Play(Sfx sfx, Vector3 position, float volume = 1f, float pitchJitter = 0.06f, float maxDistance = 90f)
        {
            var clip = SfxBank.Get(sfx);
            if (clip == null)
                return null;

            var src = NextVoice();
            src.transform.position = position;
            src.clip = clip;
            src.volume = volume;
            src.pitch = 1f + Random.Range(-pitchJitter, pitchJitter);
            src.maxDistance = maxDistance;
            src.spatialBlend = 1f;
            src.Play();
            return src;
        }

        /// <summary>Non-positional cue — hitmarkers, kill confirms, the player's own weapon.</summary>
        public AudioSource Play2D(Sfx sfx, float volume = 1f, float pitchJitter = 0.03f)
        {
            var clip = SfxBank.Get(sfx);
            if (clip == null)
                return null;

            var src = NextVoice();
            src.transform.localPosition = Vector3.zero;
            src.clip = clip;
            src.volume = volume;
            src.pitch = 1f + Random.Range(-pitchJitter, pitchJitter);
            src.spatialBlend = 0f;
            src.Play();
            return src;
        }

        AudioSource NextVoice()
        {
            // Prefer an idle voice; fall back to stealing the oldest slot in the ring.
            for (int i = 0; i < _voices.Length; i++)
            {
                int index = (_next + i) % _voices.Length;
                if (!_voices[index].isPlaying)
                {
                    _next = (index + 1) % _voices.Length;
                    return _voices[index];
                }
            }
            var stolen = _voices[_next];
            _next = (_next + 1) % _voices.Length;
            return stolen;
        }
    }
}
