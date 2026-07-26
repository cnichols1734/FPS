using UnityEngine;

namespace ArenaFps.Combat
{
    public enum HitboxPart { Torso, Head, Limb }

    public sealed class Hitbox : MonoBehaviour
    {
        public HitboxPart part = HitboxPart.Torso;
        public float damageMultiplier = 1f;
        public Damageable owner;

        void Awake()
        {
            if (owner == null)
                owner = GetComponentInParent<Damageable>();
            if (part == HitboxPart.Head && damageMultiplier <= 1f)
                damageMultiplier = 1.8f;
            else if (part == HitboxPart.Limb && damageMultiplier >= 1f)
                damageMultiplier = 0.75f;
        }
    }
}
