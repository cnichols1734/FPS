using ArenaFps.Combat;
using ArenaFps.Core;
using UnityEngine;
using UnityEngine.AI;

namespace ArenaFps.AI
{
    /// <summary>
    /// Assembles a combat bot at runtime. Component order matters: the rig has to exist before
    /// anything that poses or ragdolls it, and the health component has to exist before the rig so
    /// every limb hitbox can point at its owner.
    /// </summary>
    public static class BotFactory
    {
        public static GameObject Create(Vector3 position, Quaternion rotation, float health = 100f)
        {
            var bot = new GameObject("Bot");
            bot.layer = GameLayers.Enemy;
            bot.transform.SetPositionAndRotation(position, rotation);

            // Navigation presence only. Bullets are resolved against limb colliders, never this.
            var capsule = bot.AddComponent<CapsuleCollider>();
            capsule.height = 1.8f;
            capsule.radius = 0.34f;
            capsule.center = new Vector3(0f, 0.9f, 0f);

            var body = bot.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;

            var agent = bot.AddComponent<NavMeshAgent>();
            agent.height = 1.8f;
            agent.radius = 0.34f;
            agent.speed = 3.7f;
            agent.acceleration = 16f;
            agent.angularSpeed = 480f;
            agent.stoppingDistance = 1.2f;
            agent.autoBraking = false;
            agent.baseOffset = 0f;

            var damageable = bot.AddComponent<Damageable>();
            damageable.ConfigureMaxHealth(health);

            BotRigBuilder.Build(bot, damageable);

            bot.AddComponent<BotPoseDriver>();
            var ragdoll = bot.AddComponent<RagdollDriver>();
            ragdoll.Rebind();
            damageable.Rebind();

            bot.AddComponent<BotWeapon>();
            bot.AddComponent<BotBrain>();

            return bot;
        }
    }
}
