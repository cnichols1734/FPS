using UnityEngine;

namespace ArenaFps.Combat
{
    public enum Bone
    {
        Hips,
        Spine,
        Chest,
        Head,
        UpperArmL,
        LowerArmL,
        UpperArmR,
        LowerArmR,
        ThighL,
        ShinL,
        ThighR,
        ShinR,
        Count,
    }

    /// <summary>
    /// One physical bone: the body a bullet can strike, animate, and later ragdoll.
    /// </summary>
    public sealed class BoneLink
    {
        public Bone Id;
        public Bone Parent;
        public Transform Transform;
        public Rigidbody Body;
        public CapsuleCollider Collider;
        public Hitbox Hitbox;

        public Vector3 BindPosition;
        public Quaternion BindRotation;

        /// <summary>Length along the bone's local +Y, used to place children and impulses.</summary>
        public float Length;

        // Additive impact punch, sprung back toward the animated pose.
        public Vector3 PunchAngles;
        public Vector3 PunchVelocity;
    }

    /// <summary>
    /// A twelve-bone humanoid built from primitives. Replaces the single-capsule placeholder so a
    /// bullet strikes a shin or a helmet rather than an undifferentiated blob, and so death is a
    /// jointed ragdoll instead of a box tipping over.
    /// </summary>
    public sealed class BotRig : MonoBehaviour
    {
        [SerializeField] Transform rigRoot;

        readonly BoneLink[] _bones = new BoneLink[(int)Bone.Count];

        public Transform RigRoot => rigRoot;
        public BoneLink this[Bone bone] => _bones[(int)bone];
        public BoneLink Head => _bones[(int)Bone.Head];
        public BoneLink Chest => _bones[(int)Bone.Chest];

        public void Register(Transform root, BoneLink[] bones)
        {
            rigRoot = root;
            for (int i = 0; i < bones.Length && i < _bones.Length; i++)
                _bones[i] = bones[i];
        }

        public BoneLink Find(Collider collider)
        {
            if (collider == null)
                return null;
            for (int i = 0; i < _bones.Length; i++)
            {
                if (_bones[i] != null && _bones[i].Collider == collider)
                    return _bones[i];
            }
            return null;
        }

        public BoneLink[] Bones => _bones;

        /// <summary>Midpoint of a bone in world space — where an impact visually reads from.</summary>
        public static Vector3 Center(BoneLink bone) =>
            bone.Transform.position + bone.Transform.up * (bone.Length * 0.5f);
    }
}
