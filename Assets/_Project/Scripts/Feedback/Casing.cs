using ArenaFps.Audio;
using UnityEngine;

namespace ArenaFps.Feedback
{
    /// <summary>
    /// One pooled brass casing. Plays a single tink on its first bounce, then retires itself.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class Casing : MonoBehaviour
    {
        const float Lifetime = 4.5f;

        Rigidbody _rb;
        float _retireAt;
        bool _tinked;

        void Awake() => _rb = GetComponent<Rigidbody>();

        public void Eject(Vector3 position, Vector3 velocity)
        {
            if (_rb == null)
                _rb = GetComponent<Rigidbody>();

            gameObject.SetActive(true);
            _tinked = false;
            _retireAt = Time.time + Lifetime;

            transform.SetPositionAndRotation(position, Random.rotation);
            _rb.linearVelocity = velocity;
            _rb.angularVelocity = Random.insideUnitSphere * 22f;
        }

        void Update()
        {
            if (Time.time >= _retireAt)
                gameObject.SetActive(false);
        }

        void OnCollisionEnter(Collision collision)
        {
            if (_tinked || collision.relativeVelocity.sqrMagnitude < 0.6f)
                return;
            _tinked = true;
            Sfx3D.Instance.Play(Sfx.CasingTink, transform.position, 0.34f, 0.2f, 18f);
        }
    }
}
