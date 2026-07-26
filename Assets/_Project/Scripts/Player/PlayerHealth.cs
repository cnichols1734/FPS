using ArenaFps.Combat;
using ArenaFps.Input;
using UnityEngine;
using UnityEngine.Events;

namespace ArenaFps.Player
{
    /// <summary>
    /// Player-facing health facade over <see cref="Damageable"/> plus DualSense feedback.
    /// Creates its own Damageable if the prefab predates it, rather than dying in Awake.
    /// </summary>
    public sealed class PlayerHealth : MonoBehaviour
    {
        [SerializeField] DualSenseDriver dualSense;
        [SerializeField] Damageable damageable;

        public Damageable Damageable => damageable;
        public float MaxHealth => damageable != null ? damageable.MaxHealth : 100f;
        public float Current => damageable != null ? damageable.Current : 0f;
        public bool IsDead => damageable != null && damageable.IsDead;

        public UnityEvent onDamaged = new UnityEvent();
        public UnityEvent onDeath = new UnityEvent();
        public UnityEvent onRespawn = new UnityEvent();

        void Awake()
        {
            if (damageable == null)
                damageable = GetComponent<Damageable>();
            if (damageable == null)
                damageable = gameObject.AddComponent<Damageable>();
            damageable.MarkAsPlayer();

            if (dualSense == null)
                dualSense = GetComponentInChildren<DualSenseDriver>();

            damageable.onDamaged.AddListener(_ =>
            {
                dualSense?.PulseHit();
                onDamaged?.Invoke();
            });
            damageable.onDeath.AddListener(() =>
            {
                dualSense?.SetLightState(DualSenseDriver.LightState.Dead);
                onDeath?.Invoke();
            });
        }

        public void ApplyDamage(float amount, Vector3 hitDirection)
        {
            damageable.ApplyDamage(amount, transform.position, hitDirection);
        }

        public void Respawn()
        {
            damageable.Respawn();
            dualSense?.SetLightState(DualSenseDriver.LightState.Idle);
            onRespawn?.Invoke();
        }
    }
}
