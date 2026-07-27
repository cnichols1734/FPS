using ArenaFps.Core;
using ArenaFps.Feedback;
using UnityEngine;
using UnityEngine.Events;

namespace ArenaFps.Combat
{
    public sealed class Damageable : MonoBehaviour
    {
        [SerializeField] float maxHealth = 100f;
        [SerializeField] bool isPlayer;
        [SerializeField] RagdollDriver ragdoll;

        BotPoseDriver _pose;
        TeamMember _team;

        public float MaxHealth => maxHealth;
        public float Current { get; private set; }
        public bool IsDead => Current <= 0f;
        public bool IsPlayer => isPlayer;
        public float Normalized => maxHealth > 0f ? Mathf.Clamp01(Current / maxHealth) : 0f;

        // Constructed eagerly, not left to the serializer. Bots and the player both get their
        // Damageable from AddComponent at runtime, and AddComponent does not populate serialized
        // UnityEvent fields — subscribers were throwing on a null event before they ever ran.
        public UnityEvent<float> onDamaged = new UnityEvent<float>();
        public UnityEvent onDeath = new UnityEvent();

        void Awake()
        {
            // Components deserialized from an older prefab can still arrive with null events.
            onDamaged ??= new UnityEvent<float>();
            onDeath ??= new UnityEvent();

            Current = maxHealth;
            Rebind();
        }

        /// <summary>
        /// Re-resolves siblings. The runtime bot factory necessarily adds components in a different
        /// order than a prefab would, and a null ragdoll silently swallows all hit feel.
        /// </summary>
        public void Rebind()
        {
            if (ragdoll == null)
                ragdoll = GetComponent<RagdollDriver>();
            if (_pose == null)
                _pose = GetComponent<BotPoseDriver>();
        }

        public void ConfigureMaxHealth(float value)
        {
            maxHealth = Mathf.Max(1f, value);
            Current = maxHealth;
        }

        public void MarkAsPlayer() => isPlayer = true;

        public void ApplyDamage(DamageInfo info)
        {
            if (IsDead || info.Amount <= 0f)
                return;
            if (ragdoll == null)
                Rebind();

            // A bullet strikes a limb collider, but health lives on the actor — forward the whole
            // payload so the resolved part and the struck collider both survive the hop.
            var hitbox = info.Collider != null ? info.Collider.GetComponent<Hitbox>() : null;
            if (hitbox != null && hitbox.owner != null && hitbox.owner != this)
            {
                hitbox.owner.ApplyDamage(info);
                return;
            }

            if (IsFriendlyFire(info.Attacker))
                return;

            info.Part = hitbox != null ? hitbox.part : HitboxPart.Torso;
            info.Multiplier = hitbox != null ? hitbox.damageMultiplier : 1f;

            float applied = info.Amount * info.Multiplier;
            Current = Mathf.Max(0f, Current - applied);
            info.Amount = applied;

            onDamaged?.Invoke(applied);

            if (isPlayer)
            {
                CombatEvents.RaisePlayerDamaged(info);
            }
            else
            {
                var normal = info.Normal.sqrMagnitude > 1e-6f ? info.Normal : -info.Direction;
                var attach = info.Collider != null ? info.Collider.transform : transform;
                ImpactFx.Instance.FleshImpact(info.Point, normal, info.Direction, info.IsHeadshot, attach);

                if (info.FromPlayer)
                    CombatEvents.RaisePlayerHit(this, info);
            }

            if (Current <= 0f)
            {
                onDeath?.Invoke();
                CombatEvents.RaiseKilled(this, info);
                ragdoll?.Activate(info);
            }
            else
            {
                ragdoll?.Flinch(info);
                _pose?.AddStagger(Mathf.Clamp01(applied / 45f));
            }
        }

        public void ApplyDamage(float amount, Vector3 hitPoint, Vector3 hitDirection, Collider hitCollider = null)
        {
            var info = DamageInfo.Simple(amount, hitPoint, hitDirection);
            info.Collider = hitCollider;
            ApplyDamage(info);
        }

        public void Heal(float amount)
        {
            if (IsDead || amount <= 0f)
                return;
            Current = Mathf.Min(maxHealth, Current + amount);
        }

        public void Respawn(float health = -1f)
        {
            Current = health > 0f ? health : maxHealth;
            ragdoll?.ResetPose();
        }

        bool IsFriendlyFire(GameObject attacker)
        {
            if (attacker == null)
                return false;
            if (_team == null)
                _team = GetComponent<TeamMember>();
            var other = attacker.GetComponent<TeamMember>();
            if (_team == null || other == null)
                return false;
            if (_team.Team == TeamId.None || other.Team == TeamId.None)
                return false;
            return _team.Team == other.Team;
        }
    }
}
