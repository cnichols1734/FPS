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
            => Create(position, rotation, health, TeamId.Red);

        public static GameObject Create(Vector3 position, Quaternion rotation, float health, TeamId team)
        {
            var bot = new GameObject("Bot");
            bot.layer = GameLayers.Enemy;
            bot.transform.SetPositionAndRotation(position, rotation);

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
            agent.obstacleAvoidanceType = ObstacleAvoidanceType.MedQualityObstacleAvoidance;

            // Snap onto the baked mesh so Think() never spams "not on NavMesh".
            if (NavMesh.SamplePosition(position, out var snap, 4f, NavMesh.AllAreas))
            {
                agent.Warp(snap.position);
                bot.transform.position = snap.position;
            }

            var damageable = bot.AddComponent<Damageable>();
            damageable.ConfigureMaxHealth(health);

            var teamMember = bot.AddComponent<TeamMember>();
            teamMember.Team = team;

            if (!SoldierBotRigBuilder.TryBuild(bot, damageable, out _))
                BotRigBuilder.Build(bot, damageable);

            bot.AddComponent<BotPoseDriver>();
            var ragdoll = bot.AddComponent<RagdollDriver>();
            ragdoll.Rebind();
            damageable.Rebind();

            bot.AddComponent<BotWeapon>();
            bot.AddComponent<BotBrain>();

            ApplyTeamTint(bot, team);
            return bot;
        }

        static void ApplyTeamTint(GameObject bot, TeamId team)
        {
            var tint = TeamMember.Tint(team);
            foreach (var r in bot.GetComponentsInChildren<Renderer>())
            {
                // Cheap instance tint so blue/red is readable at a glance in the mid lane.
                foreach (var mat in r.materials)
                {
                    if (mat.HasProperty("_BaseColor"))
                    {
                        var c = mat.GetColor("_BaseColor");
                        mat.SetColor("_BaseColor", Color.Lerp(c, tint, 0.35f));
                    }
                    else if (mat.HasProperty("_Color"))
                    {
                        var c = mat.GetColor("_Color");
                        mat.SetColor("_Color", Color.Lerp(c, tint, 0.35f));
                    }
                }
            }
        }
    }
}
