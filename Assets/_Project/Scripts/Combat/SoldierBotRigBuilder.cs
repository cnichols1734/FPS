using ArenaFps.Core;
using UnityEngine;

namespace ArenaFps.Combat
{
    /// <summary>
    /// Builds a combat bot from the Mixamo Male Warrior. The FBX ships in a lounge bind pose, so
    /// we force a Humanoid standing pose (zero muscles + rifle carry) before hitboxes bind.
    /// </summary>
    public static class SoldierBotRigBuilder
    {
        public const string ResourcePath = "Characters/MaleWarrior";

        struct BoneMap
        {
            public Bone Id;
            public Bone Parent;
            public string Name;
            public float Radius;
            public float Mass;
            public HitboxPart Part;
            public float DamageMultiplier;
            public float FallbackLength;
        }

        static readonly BoneMap[] Maps =
        {
            new() { Id = Bone.Hips,      Parent = Bone.Count, Name = "Hips",           Radius = 0.14f,  Mass = 12f, Part = HitboxPart.Torso, DamageMultiplier = 1.0f,  FallbackLength = 0.16f },
            new() { Id = Bone.Spine,     Parent = Bone.Hips,  Name = "Spine1",         Radius = 0.15f,  Mass = 12f, Part = HitboxPart.Torso, DamageMultiplier = 1.0f,  FallbackLength = 0.15f },
            new() { Id = Bone.Chest,     Parent = Bone.Spine, Name = "Spine2",         Radius = 0.17f,  Mass = 16f, Part = HitboxPart.Torso, DamageMultiplier = 1.1f,  FallbackLength = 0.18f },
            new() { Id = Bone.Head,      Parent = Bone.Chest, Name = "Head",           Radius = 0.11f,  Mass = 5f,  Part = HitboxPart.Head,  DamageMultiplier = 2.0f,  FallbackLength = 0.18f },

            new() { Id = Bone.UpperArmL, Parent = Bone.Chest, Name = "LeftArm",        Radius = 0.055f, Mass = 2.5f, Part = HitboxPart.Limb, DamageMultiplier = 0.9f,  FallbackLength = 0.27f },
            new() { Id = Bone.LowerArmL, Parent = Bone.UpperArmL, Name = "LeftForeArm", Radius = 0.047f, Mass = 2f,  Part = HitboxPart.Limb, DamageMultiplier = 0.85f, FallbackLength = 0.27f },
            new() { Id = Bone.UpperArmR, Parent = Bone.Chest, Name = "RightArm",       Radius = 0.055f, Mass = 2.5f, Part = HitboxPart.Limb, DamageMultiplier = 0.9f,  FallbackLength = 0.27f },
            new() { Id = Bone.LowerArmR, Parent = Bone.UpperArmR, Name = "RightForeArm", Radius = 0.047f, Mass = 2f, Part = HitboxPart.Limb, DamageMultiplier = 0.85f, FallbackLength = 0.27f },

            new() { Id = Bone.ThighL,    Parent = Bone.Hips,  Name = "LeftUpLeg",      Radius = 0.085f, Mass = 8f,  Part = HitboxPart.Limb, DamageMultiplier = 0.9f,  FallbackLength = 0.42f },
            new() { Id = Bone.ShinL,     Parent = Bone.ThighL, Name = "LeftLeg",       Radius = 0.07f,  Mass = 5f,  Part = HitboxPart.Limb, DamageMultiplier = 0.8f,  FallbackLength = 0.42f },
            new() { Id = Bone.ThighR,    Parent = Bone.Hips,  Name = "RightUpLeg",     Radius = 0.085f, Mass = 8f,  Part = HitboxPart.Limb, DamageMultiplier = 0.9f,  FallbackLength = 0.42f },
            new() { Id = Bone.ShinR,     Parent = Bone.ThighR, Name = "RightLeg",      Radius = 0.07f,  Mass = 5f,  Part = HitboxPart.Limb, DamageMultiplier = 0.8f,  FallbackLength = 0.42f },
        };

        // HumanTrait muscle indices for a rough rifle-ready upper body on a zeroed T-pose.
        static readonly (string name, float value)[] RifleMuscles =
        {
            // Hard offsets off T-pose into a compact rifle-ready stance.
            ("Left Shoulder Down-Up", -0.45f),
            ("Left Shoulder Front-Back", 0.25f),
            ("Left Arm Down-Up", -0.85f),
            ("Left Arm Front-Back", 0.75f),
            ("Left Arm Twist In-Out", -0.35f),
            ("Left Forearm Stretch", 0.55f),
            ("Right Shoulder Down-Up", -0.35f),
            ("Right Shoulder Front-Back", 0.35f),
            ("Right Arm Down-Up", -0.7f),
            ("Right Arm Front-Back", 0.9f),
            ("Right Arm Twist In-Out", 0.4f),
            ("Right Forearm Stretch", 0.65f),
            ("Spine Front-Back", 0.1f),
            ("Head Nod Down-Up", -0.08f),
            ("Left Foot Up-Down", 0.7f),
            ("Right Foot Up-Down", 0.7f),
            ("Left Toes Up-Down", 0.25f),
            ("Right Toes Up-Down", 0.25f),
            ("Left Lower Leg Stretch", 0.12f),
            ("Right Lower Leg Stretch", 0.12f),
        };

        public static bool TryBuild(GameObject root, Damageable owner, out BotRig rig)
        {
            rig = null;
            var prefab = Resources.Load<GameObject>(ResourcePath);
            if (prefab == null)
            {
                Debug.LogWarning("[Soldier] Missing Resources/Characters/MaleWarrior — falling back to capsule rig.");
                return false;
            }

            var instance = Object.Instantiate(prefab, root.transform, false);
            instance.name = "MaleWarrior";
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;

            StripImportedColliders(instance);
            var animator = EnsureHumanAnimator(instance);
            // Establish a sane standing pose before hitboxes bind, and as the fallback if the
            // avatar turns out not to be drivable. SoldierAnimator overwrites this every frame.
            ApplyStandingCombatPose(animator);
            ApplyRifleArmPose(instance.transform);
            // Freeze: a disabled Animator can't snap bones back to the lounge bind pose.
            animator.enabled = false;
            PlantFeet(instance);

            var bones = new BoneLink[(int)Bone.Count];
            foreach (var map in Maps)
            {
                var boneTransform = FindBone(instance.transform, map.Name);
                if (boneTransform == null)
                {
                    Debug.LogError($"[Soldier] Missing Mixamo bone '{map.Name}'.");
                    Object.Destroy(instance);
                    return false;
                }

                float length = MeasureLength(boneTransform, map.FallbackLength);
                boneTransform.gameObject.layer = GameLayers.Hitbox;

                // Use Unity null checks — destroyed components are not C# null, so ??. fails.
                var collider = boneTransform.GetComponent<CapsuleCollider>();
                if (collider == null)
                    collider = boneTransform.gameObject.AddComponent<CapsuleCollider>();
                collider.direction = 1;
                collider.radius = map.Radius;
                collider.height = Mathf.Max(length, map.Radius * 2f);
                collider.center = new Vector3(0f, length * 0.5f, 0f);

                var body = boneTransform.GetComponent<Rigidbody>();
                if (body == null)
                    body = boneTransform.gameObject.AddComponent<Rigidbody>();
                body.mass = map.Mass;
                body.isKinematic = true;
                body.useGravity = false;
                body.interpolation = RigidbodyInterpolation.Interpolate;
                body.solverIterations = 14;
                body.solverVelocityIterations = 6;

                var hitbox = boneTransform.GetComponent<Hitbox>();
                if (hitbox == null)
                    hitbox = boneTransform.gameObject.AddComponent<Hitbox>();
                hitbox.part = map.Part;
                hitbox.damageMultiplier = map.DamageMultiplier;
                hitbox.owner = owner;

                bones[(int)map.Id] = new BoneLink
                {
                    Id = map.Id,
                    Parent = map.Parent,
                    Transform = boneTransform,
                    Body = body,
                    Collider = collider,
                    Hitbox = hitbox,
                    BindPosition = boneTransform.localPosition,
                    BindRotation = boneTransform.localRotation,
                    Length = length,
                };
            }

            AlignHipsOverRoot(instance, bones[(int)Bone.Hips]);
            PlantFeet(instance);
            PlaceMuzzle(bones[(int)Bone.LowerArmR], instance.transform);

            foreach (var map in Maps)
            {
                var link = bones[(int)map.Id];
                if (link?.Transform == null)
                    continue;
                link.BindPosition = link.Transform.localPosition;
                link.BindRotation = link.Transform.localRotation;
            }

            var botRig = root.GetComponent<BotRig>() ?? root.AddComponent<BotRig>();
            botRig.Register(instance.transform, bones);
            rig = botRig;

            // Added last: the animator resolves the agent, rig and avatar in Awake, all of which
            // only exist once the body above is assembled.
            var soldierAnimator = root.GetComponent<SoldierAnimator>() ?? root.AddComponent<SoldierAnimator>();
            soldierAnimator.Bind();

            return true;
        }

        static Animator EnsureHumanAnimator(GameObject instance)
        {
            var animator = instance.GetComponent<Animator>() ?? instance.GetComponentInChildren<Animator>();
            if (animator == null)
                animator = instance.AddComponent<Animator>();

            // Never play the authored lounge clip — standing pose is applied via HumanPose.
            animator.runtimeAnimatorController = null;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.enabled = true;
            return animator;
        }

        /// <summary>
        /// Zero muscles = Mecanim T-pose for this avatar. Then nudge arms into a rifle carry.
        /// </summary>
        static void ApplyStandingCombatPose(Animator animator)
        {
            if (animator == null || !animator.isHuman || animator.avatar == null || !animator.avatar.isValid)
            {
                Debug.LogWarning("[Soldier] Humanoid avatar missing — enemy may stay in lounge bind pose.");
                return;
            }

            var handler = new HumanPoseHandler(animator.avatar, animator.transform);
            var pose = new HumanPose();
            handler.GetHumanPose(ref pose);

            for (int i = 0; i < pose.muscles.Length; i++)
                pose.muscles[i] = 0f;

            pose.bodyRotation = Quaternion.identity;
            pose.bodyPosition = new Vector3(0f, 1f, 0f);

            foreach (var (name, value) in RifleMuscles)
            {
                int index = MuscleIndex(name);
                if (index >= 0 && index < pose.muscles.Length)
                    pose.muscles[index] = Mathf.Clamp(value, -1f, 1f);
            }

            handler.SetHumanPose(ref pose);
            handler.Dispose();

            // Flatten pointed T-pose feet so soles read as planted.
            FlattenFoot(FindBone(animator.transform, "LeftFoot"));
            FlattenFoot(FindBone(animator.transform, "RightFoot"));
        }

        static void FlattenFoot(Transform foot)
        {
            if (foot == null)
                return;
            // Keep yaw, kill pitch/roll toward a level sole.
            var euler = foot.localEulerAngles;
            foot.localRotation = Quaternion.Euler(0f, euler.y, 0f);
        }

        /// <summary>
        /// Humanoid muscle knobs are soft — snap the arm bones into a readable rifle carry.
        /// Mixamo limbs point along local +Y toward their child.
        /// </summary>
        static void ApplyRifleArmPose(Transform root)
        {
            // Don't touch Shoulders — twisting them stretches the skin into spikes.
            var forward = root.forward;
            var right = root.right;

            AimBone(FindBone(root, "LeftArm"), (forward + Vector3.down * 0.65f + right * 0.25f).normalized);
            AimBone(FindBone(root, "RightArm"), (forward + Vector3.down * 0.5f - right * 0.15f).normalized);
            AimBone(FindBone(root, "LeftForeArm"), (forward + Vector3.down * 0.15f).normalized);
            AimBone(FindBone(root, "RightForeArm"), (forward + Vector3.down * 0.1f).normalized);
            AimBone(FindBone(root, "LeftHand"), forward);
            AimBone(FindBone(root, "RightHand"), forward);
        }

        static void AimBone(Transform bone, Vector3 worldDir)
        {
            if (bone == null || bone.childCount == 0 || worldDir.sqrMagnitude < 1e-6f)
                return;

            Transform child = bone.GetChild(0);
            for (int i = 0; i < bone.childCount; i++)
            {
                var c = bone.GetChild(i);
                if (c.name.Contains("End", System.StringComparison.Ordinal))
                    continue;
                child = c;
                break;
            }

            var current = (child.position - bone.position).normalized;
            if (current.sqrMagnitude < 1e-6f)
                return;

            bone.rotation = Quaternion.FromToRotation(current, worldDir.normalized) * bone.rotation;
        }

        static int MuscleIndex(string muscleName)
        {
            var names = HumanTrait.MuscleName;
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i] == muscleName)
                    return i;
            }
            return -1;
        }

        static void StripImportedColliders(GameObject instance)
        {
            // Must be immediate during setup — deferred Destroy leaves zombies that break AddComponent.
            var cols = instance.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < cols.Length; i++)
            {
                if (cols[i] != null)
                    Object.DestroyImmediate(cols[i]);
            }
        }

        static void AlignHipsOverRoot(GameObject instance, BoneLink hips)
        {
            if (hips?.Transform == null)
                return;

            var delta = hips.Transform.position - instance.transform.parent.position;
            delta.y = 0f;
            instance.transform.position -= delta;
        }

        static void PlantFeet(GameObject instance)
        {
            var left = FindBone(instance.transform, "LeftFoot");
            var right = FindBone(instance.transform, "RightFoot");
            if (left == null && right == null)
            {
                var smr = instance.GetComponentInChildren<SkinnedMeshRenderer>(true);
                if (smr == null)
                    return;
                float sink = smr.bounds.min.y - instance.transform.parent.position.y;
                if (Mathf.Abs(sink) > 0.01f)
                    instance.transform.localPosition += new Vector3(0f, -sink, 0f);
                return;
            }

            float footY = float.MaxValue;
            if (left != null) footY = Mathf.Min(footY, left.position.y);
            if (right != null) footY = Mathf.Min(footY, right.position.y);

            // Boots extend a bit below the ankle bone.
            const float soleOffset = 0.04f;
            float groundY = instance.transform.parent != null
                ? instance.transform.parent.position.y
                : 0f;
            float deltaY = groundY - (footY - soleOffset);
            if (Mathf.Abs(deltaY) > 0.001f)
                instance.transform.position += new Vector3(0f, deltaY, 0f);
        }

        static void PlaceMuzzle(BoneLink forearm, Transform soldierRoot)
        {
            if (forearm?.Transform == null)
                return;

            var hand = FindBone(soldierRoot, "RightHand") ?? forearm.Transform;
            var muzzle = new GameObject("Muzzle");
            muzzle.transform.SetParent(forearm.Transform, false);
            var handLocal = forearm.Transform.InverseTransformPoint(hand.position);
            muzzle.transform.localPosition = handLocal + new Vector3(0f, 0.18f, 0.06f);
            muzzle.transform.localRotation = Quaternion.identity;
        }

        static float MeasureLength(Transform bone, float fallback)
        {
            if (bone.childCount == 0)
                return fallback;

            Transform child = bone.GetChild(0);
            for (int i = 0; i < bone.childCount; i++)
            {
                var c = bone.GetChild(i);
                if (c.name.Contains("End", System.StringComparison.Ordinal))
                    continue;
                child = c;
                break;
            }

            float along = Mathf.Abs(child.localPosition.y);
            if (along < 0.05f)
                along = child.localPosition.magnitude;
            return along > 0.05f ? along : fallback;
        }

        static Transform FindBone(Transform root, string shortName)
        {
            string prefixed = "mixamorig:" + shortName;
            return FindDeep(root, prefixed) ?? FindDeep(root, shortName);
        }

        static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name)
                return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var found = FindDeep(root.GetChild(i), name);
                if (found != null)
                    return found;
            }
            return null;
        }
    }
}
