using System;
using ArenaFps.Ballistics;
using UnityEngine;

namespace ArenaFps.Combat
{
    /// <summary>
    /// Everything the feedback layer needs to know about one hit. Carrying the struck collider all
    /// the way through is what lets a ragdoll take an impulse on the limb that was actually shot.
    /// </summary>
    public struct DamageInfo
    {
        public float Amount;
        public Vector3 Point;
        public Vector3 Direction;
        public Vector3 Normal;
        public Collider Collider;
        public GameObject Attacker;
        public bool FromPlayer;
        public HitboxPart Part;
        public float Multiplier;
        public SurfaceKind Surface;
        public bool Ricochet;
        public bool Penetrated;

        public bool IsHeadshot => Part == HitboxPart.Head;

        public static DamageInfo Simple(float amount, Vector3 point, Vector3 direction)
            => new()
            {
                Amount = amount,
                Point = point,
                Direction = direction.sqrMagnitude > 1e-6f ? direction.normalized : Vector3.forward,
                Normal = -direction.normalized,
                Part = HitboxPart.Torso,
                Multiplier = 1f,
                Surface = SurfaceKind.Default,
            };
    }

    /// <summary>
    /// Combat notifications the UI and screen-effect layers listen to. Keeps weapons and AI free of
    /// direct HUD references, so either side can be replaced without touching the other.
    /// </summary>
    public static class CombatEvents
    {
        /// <summary>The player landed a shot on something with health. Drives the hitmarker.</summary>
        public static event Action<Damageable, DamageInfo> PlayerHitConfirmed;

        /// <summary>Anything died. Drives the kill confirm and the score feed.</summary>
        public static event Action<Damageable, DamageInfo> Killed;

        /// <summary>The player took damage. Drives direction indicators and screen effects.</summary>
        public static event Action<DamageInfo> PlayerDamaged;

        public static void RaisePlayerHit(Damageable target, DamageInfo info) => PlayerHitConfirmed?.Invoke(target, info);
        public static void RaiseKilled(Damageable target, DamageInfo info) => Killed?.Invoke(target, info);
        public static void RaisePlayerDamaged(DamageInfo info) => PlayerDamaged?.Invoke(info);
    }
}
