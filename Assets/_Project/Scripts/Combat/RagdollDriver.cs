using ArenaFps.Audio;
using ArenaFps.Feedback;
using UnityEngine;
using UnityEngine.AI;

namespace ArenaFps.Combat
{
    /// <summary>
    /// Physical death for a <see cref="BotRig"/>. While alive the bones are kinematic and posed by
    /// <see cref="BotPoseDriver"/>; on death the joints are created from the pose the body died in
    /// and physics takes over, so the corpse falls out of whatever stance it was caught in.
    /// </summary>
    public sealed class RagdollDriver : MonoBehaviour
    {
        [SerializeField] BotRig rig;
        [SerializeField] NavMeshAgent agent;
        [SerializeField] Collider livingCollider;
        [SerializeField] Rigidbody rootBody;

        [Header("Impact")]
        [SerializeField] float punchStiffness = 190f;
        [SerializeField] float punchDamping = 13f;
        [SerializeField] float punchScale = 1.6f;

        bool _active;
        Vector3 _spawnPosition;
        Quaternion _spawnRotation;

        public bool IsRagdolled => _active;

        void Awake()
        {
            _spawnPosition = transform.position;
            _spawnRotation = transform.rotation;
            Rebind();
        }

        /// <summary>
        /// The runtime bot factory cannot satisfy every dependency at AddComponent time, so
        /// references resolve on demand rather than once in Awake.
        /// </summary>
        public void Rebind()
        {
            if (rig == null) rig = GetComponent<BotRig>();
            if (agent == null) agent = GetComponent<NavMeshAgent>();
            if (rootBody == null) rootBody = GetComponent<Rigidbody>();
            if (livingCollider == null)
            {
                foreach (var c in GetComponents<Collider>())
                {
                    livingCollider = c;
                    break;
                }
            }
        }

        void Update()
        {
            if (_active)
                return;
            if (rig == null)
            {
                Rebind();
                if (rig == null)
                    return;
            }

            // Spring the impact punch back toward the animated pose.
            float dt = Time.deltaTime;
            foreach (var bone in rig.Bones)
            {
                if (bone == null || (bone.PunchAngles.sqrMagnitude < 1e-6f && bone.PunchVelocity.sqrMagnitude < 1e-6f))
                    continue;

                var accel = -bone.PunchAngles * punchStiffness - bone.PunchVelocity * punchDamping;
                bone.PunchVelocity += accel * dt;
                bone.PunchAngles += bone.PunchVelocity * dt;

                if (bone.PunchAngles.sqrMagnitude < 1e-6f && bone.PunchVelocity.sqrMagnitude < 1e-5f)
                {
                    bone.PunchAngles = Vector3.zero;
                    bone.PunchVelocity = Vector3.zero;
                }
            }
        }

        /// <summary>Visible jolt on the limb that was struck, without disturbing navigation.</summary>
        public void Flinch(DamageInfo info)
        {
            if (_active)
                return;
            if (rig == null)
                Rebind();
            if (rig == null)
                return;

            var bone = rig.Find(info.Collider) ?? rig.Chest;
            if (bone?.Transform == null)
                return;

            // Convert the shot direction into a rotation the limb visibly snaps through: a push along
            // the bone's forward pitches it, a lateral push rolls it, and a push down its length only
            // twists. Bones point along local +Y, so that axis must not drive pitch.
            var local = bone.Transform.InverseTransformDirection(info.Direction.normalized);
            float magnitude = Mathf.Clamp(info.Amount * punchScale, 4f, 42f);
            var kick = new Vector3(local.z, local.y * 0.25f, -local.x) * magnitude;

            bone.PunchVelocity += kick * 9f;

            // Bleed a fraction up the chain so the whole body registers the hit.
            var parent = bone.Parent != Bone.Count ? rig[bone.Parent] : null;
            if (parent != null)
                parent.PunchVelocity += kick * 2.4f;
        }

        public void Flinch(Vector3 direction, float amount) =>
            Flinch(DamageInfo.Simple(amount, transform.position, direction));

        public void Activate(DamageInfo info)
        {
            if (_active)
                return;
            _active = true;
            Rebind();

            if (agent != null)
                agent.enabled = false;
            if (livingCollider != null)
                livingCollider.enabled = false;
            if (rootBody != null)
            {
                rootBody.detectCollisions = false;
                rootBody.isKinematic = true;
            }

            var pose = GetComponent<BotPoseDriver>();
            if (pose != null)
                pose.enabled = false;

            if (rig == null)
                return;

            GoDynamic();
            ApplyDeathImpulse(info);

            var origin = info.Point == Vector3.zero ? transform.position + Vector3.up * 1.2f : info.Point;
            ImpactFx.Instance.DeathBurst(origin, info.Direction);
            Sfx3D.Instance.Play(Sfx.Death, transform.position + Vector3.up * 1.4f, 0.8f, 0.1f, 45f);
            Invoke(nameof(PlayBodyFall), 0.35f);
        }

        public void Activate(Vector3 impulse, Vector3 hitPoint)
        {
            var info = DamageInfo.Simple(impulse.magnitude, hitPoint, impulse.normalized);
            Activate(info);
        }

        void PlayBodyFall() => Sfx3D.Instance.Play(Sfx.BodyFall, transform.position, 0.6f, 0.12f, 35f);

        /// <summary>
        /// Joints are built here rather than at spawn: while alive they would be inert overhead, and
        /// creating them now makes the death pose the joint rest pose for free.
        /// </summary>
        void GoDynamic()
        {
            foreach (var bone in rig.Bones)
            {
                if (bone?.Body == null)
                    continue;
                bone.Body.isKinematic = false;
                bone.Body.useGravity = true;
                bone.Body.linearDamping = 0.06f;
                bone.Body.angularDamping = 0.28f;
            }

            foreach (var bone in rig.Bones)
            {
                if (bone == null || bone.Parent == Bone.Count)
                    continue;
                var parent = rig[bone.Parent];
                if (parent?.Body == null)
                    continue;

                var joint = bone.Transform.gameObject.AddComponent<CharacterJoint>();
                joint.connectedBody = parent.Body;
                joint.anchor = Vector3.zero;
                joint.axis = Vector3.right;
                joint.swingAxis = Vector3.up;
                joint.enableProjection = true;
                joint.projectionDistance = 0.06f;
                joint.projectionAngle = 12f;
                joint.enablePreprocessing = false;

                var (twist, swing1, swing2) = LimitsFor(bone.Id);
                joint.lowTwistLimit = new SoftJointLimit { limit = -twist };
                joint.highTwistLimit = new SoftJointLimit { limit = twist };
                joint.swing1Limit = new SoftJointLimit { limit = swing1 };
                joint.swing2Limit = new SoftJointLimit { limit = swing2 };
            }
        }

        static (float twist, float swing1, float swing2) LimitsFor(Bone bone) => bone switch
        {
            Bone.Spine => (18f, 22f, 14f),
            Bone.Chest => (14f, 18f, 12f),
            Bone.Head => (35f, 38f, 22f),
            Bone.UpperArmL or Bone.UpperArmR => (30f, 76f, 44f),
            // Elbows and knees are near-hinges; leaving them loose is what makes bad ragdolls
            // look like dropped laundry.
            Bone.LowerArmL or Bone.LowerArmR => (6f, 78f, 5f),
            Bone.ThighL or Bone.ThighR => (22f, 68f, 28f),
            Bone.ShinL or Bone.ShinR => (5f, 82f, 5f),
            _ => (20f, 40f, 25f),
        };

        void ApplyDeathImpulse(DamageInfo info)
        {
            var struck = rig.Find(info.Collider) ?? rig.Chest;
            var direction = info.Direction.sqrMagnitude > 1e-6f ? info.Direction.normalized : transform.forward;

            // Weight the impulse to the limb that was hit so headshots snap the head back and leg
            // shots drop the body, instead of every death reading identically.
            float primary = Mathf.Clamp(info.Amount * 0.9f, 6f, 30f) * (info.IsHeadshot ? 1.6f : 1f);

            if (struck?.Body != null)
            {
                var point = info.Point == Vector3.zero ? BotRig.Center(struck) : info.Point;
                struck.Body.AddForceAtPosition(direction * primary + Vector3.up * primary * 0.18f, point, ForceMode.Impulse);
            }

            float bleed = primary * 0.16f;
            foreach (var bone in rig.Bones)
            {
                if (bone?.Body == null || bone == struck)
                    continue;
                bone.Body.AddForce(direction * bleed, ForceMode.Impulse);
                bone.Body.AddTorque(Random.insideUnitSphere * bleed * 0.35f, ForceMode.Impulse);
            }
        }

        public void ResetPose()
        {
            if (rig != null)
            {
                foreach (var bone in rig.Bones)
                {
                    if (bone?.Body == null)
                        continue;
                    var joint = bone.Transform.GetComponent<CharacterJoint>();
                    if (joint != null)
                        Destroy(joint);

                    bone.Body.isKinematic = true;
                    bone.Body.useGravity = false;
                    bone.Transform.localPosition = bone.BindPosition;
                    bone.Transform.localRotation = bone.BindRotation;
                    bone.PunchAngles = Vector3.zero;
                    bone.PunchVelocity = Vector3.zero;
                }
            }

            transform.SetPositionAndRotation(_spawnPosition, _spawnRotation);

            if (rootBody != null)
            {
                rootBody.detectCollisions = true;
                rootBody.isKinematic = true;
            }
            if (livingCollider != null)
                livingCollider.enabled = true;
            if (agent != null)
                agent.enabled = true;

            var pose = GetComponent<BotPoseDriver>();
            if (pose != null)
                pose.enabled = true;

            _active = false;
        }
    }
}
