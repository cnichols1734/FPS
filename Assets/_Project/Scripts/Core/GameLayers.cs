using UnityEngine;

namespace ArenaFps.Core
{
    /// <summary>
    /// Layer indices are the source of truth; names are cosmetic and applied by an editor pass.
    /// Bone hitboxes live on their own layer so a bot's line of sight can never be blocked by
    /// its own body, and so the navigation capsule never absorbs a bullet meant for a limb.
    /// </summary>
    public static class GameLayers
    {
        public const int Default = 0;
        public const int IgnoreRaycast = 2;
        public const int Player = 6;
        public const int Enemy = 7;
        public const int Hitbox = 8;
        public const int Fx = 9;
        public const int Viewmodel = 10;

        public static readonly int WorldMask = 1 << Default;
        public static readonly int PlayerMask = 1 << Player;
        public static readonly int EnemyMask = 1 << Enemy;
        public static readonly int HitboxMask = 1 << Hitbox;

        /// <summary>What a player bullet may strike: world geometry and character limbs.</summary>
        public static readonly int PlayerBulletMask = WorldMask | HitboxMask;

        /// <summary>What a bot bullet may strike: world geometry and the player capsule.</summary>
        public static readonly int BotBulletMask = WorldMask | PlayerMask;

        /// <summary>Sight and cover queries only care about static world geometry.</summary>
        public static readonly int SightMask = WorldMask;

        /// <summary>
        /// What the navmesh baker is allowed to see. Without this it also collects the
        /// first-person viewmodel and loose debris, which both warn about non-readable meshes
        /// and can carve holes in the walkable surface.
        /// </summary>
        public static readonly int NavMeshMask =
            ~((1 << Viewmodel) | (1 << Fx) | (1 << Hitbox) | (1 << Player) | (1 << Enemy));

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void ConfigureCollisionMatrix()
        {
            // Ragdoll limbs fall through characters, not through the world.
            Physics.IgnoreLayerCollision(Hitbox, Player, true);
            Physics.IgnoreLayerCollision(Hitbox, Enemy, true);

            // Two bots never shove each other's navigation capsules.
            Physics.IgnoreLayerCollision(Enemy, Enemy, true);

            // Casings and debris bounce off the world and nothing else.
            Physics.IgnoreLayerCollision(Fx, Player, true);
            Physics.IgnoreLayerCollision(Fx, Enemy, true);
            Physics.IgnoreLayerCollision(Fx, Hitbox, true);
            Physics.IgnoreLayerCollision(Fx, Fx, true);

            Physics.IgnoreLayerCollision(Viewmodel, Default, true);
            Physics.IgnoreLayerCollision(Viewmodel, Player, true);
            Physics.IgnoreLayerCollision(Viewmodel, Enemy, true);
            Physics.IgnoreLayerCollision(Viewmodel, Hitbox, true);
        }

        public static void ApplyRecursive(GameObject root, int layer)
        {
            root.layer = layer;
            foreach (Transform child in root.transform)
                ApplyRecursive(child.gameObject, layer);
        }
    }
}
