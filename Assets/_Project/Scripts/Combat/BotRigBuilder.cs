using ArenaFps.Core;
using UnityEngine;

namespace ArenaFps.Combat
{
    /// <summary>
    /// Builds the twelve-bone soldier from primitives. Proportions are a 1.8 m adult in a rifle
    /// carry: shoulders back, elbows in, weapon up. Silhouette is the readability contract here —
    /// helmet, chest rig and rifle have to be identifiable inside 200 ms at 30 m.
    /// </summary>
    public static class BotRigBuilder
    {
        struct BoneSpec
        {
            public Bone Id;
            public Bone Parent;
            public Vector3 LocalPosition;
            public Vector3 LocalEuler;
            public float Radius;
            public float Length;
            public float Mass;
            public HitboxPart Part;
            public float DamageMultiplier;
        }

        // Parent-space offsets: a child sits at (0, parentLength, 0), i.e. the distal end of its parent.
        static readonly BoneSpec[] Specs =
        {
            new() { Id = Bone.Hips,      Parent = Bone.Count, LocalPosition = new Vector3(0f, 0.90f, 0f),   LocalEuler = new Vector3(0f, 0f, 0f),    Radius = 0.150f, Length = 0.16f, Mass = 12f, Part = HitboxPart.Torso, DamageMultiplier = 1.0f },
            new() { Id = Bone.Spine,     Parent = Bone.Hips,  LocalPosition = new Vector3(0f, 0.16f, 0f),   LocalEuler = new Vector3(3f, 0f, 0f),    Radius = 0.165f, Length = 0.24f, Mass = 12f, Part = HitboxPart.Torso, DamageMultiplier = 1.0f },
            new() { Id = Bone.Chest,     Parent = Bone.Spine, LocalPosition = new Vector3(0f, 0.24f, 0f),   LocalEuler = new Vector3(3f, 0f, 0f),    Radius = 0.185f, Length = 0.26f, Mass = 16f, Part = HitboxPart.Torso, DamageMultiplier = 1.1f },
            new() { Id = Bone.Head,      Parent = Bone.Chest, LocalPosition = new Vector3(0f, 0.26f, 0f),   LocalEuler = new Vector3(-6f, 0f, 0f),   Radius = 0.112f, Length = 0.13f, Mass = 5f,  Part = HitboxPart.Head,  DamageMultiplier = 2.0f },

            new() { Id = Bone.UpperArmL, Parent = Bone.Chest, LocalPosition = new Vector3(-0.185f, 0.20f, 0f), LocalEuler = new Vector3(152f, 0f, -6f), Radius = 0.055f, Length = 0.27f, Mass = 2.5f, Part = HitboxPart.Limb, DamageMultiplier = 0.9f },
            new() { Id = Bone.LowerArmL, Parent = Bone.UpperArmL, LocalPosition = new Vector3(0f, 0.27f, 0f), LocalEuler = new Vector3(-68f, 0f, 0f),  Radius = 0.047f, Length = 0.26f, Mass = 2f,   Part = HitboxPart.Limb, DamageMultiplier = 0.85f },
            new() { Id = Bone.UpperArmR, Parent = Bone.Chest, LocalPosition = new Vector3(0.185f, 0.20f, 0f),  LocalEuler = new Vector3(158f, 0f, 6f),  Radius = 0.055f, Length = 0.27f, Mass = 2.5f, Part = HitboxPart.Limb, DamageMultiplier = 0.9f },
            new() { Id = Bone.LowerArmR, Parent = Bone.UpperArmR, LocalPosition = new Vector3(0f, 0.27f, 0f), LocalEuler = new Vector3(-84f, 0f, 0f),  Radius = 0.047f, Length = 0.26f, Mass = 2f,   Part = HitboxPart.Limb, DamageMultiplier = 0.85f },

            new() { Id = Bone.ThighL,    Parent = Bone.Hips,   LocalPosition = new Vector3(-0.095f, 0f, 0f), LocalEuler = new Vector3(176f, 0f, -2f), Radius = 0.085f, Length = 0.42f, Mass = 8f, Part = HitboxPart.Limb, DamageMultiplier = 0.9f },
            new() { Id = Bone.ShinL,     Parent = Bone.ThighL, LocalPosition = new Vector3(0f, 0.42f, 0f),   LocalEuler = new Vector3(6f, 0f, 0f),    Radius = 0.070f, Length = 0.42f, Mass = 5f, Part = HitboxPart.Limb, DamageMultiplier = 0.8f },
            new() { Id = Bone.ThighR,    Parent = Bone.Hips,   LocalPosition = new Vector3(0.095f, 0f, 0f),  LocalEuler = new Vector3(176f, 0f, 2f),  Radius = 0.085f, Length = 0.42f, Mass = 8f, Part = HitboxPart.Limb, DamageMultiplier = 0.9f },
            new() { Id = Bone.ShinR,     Parent = Bone.ThighR, LocalPosition = new Vector3(0f, 0.42f, 0f),   LocalEuler = new Vector3(6f, 0f, 0f),    Radius = 0.070f, Length = 0.42f, Mass = 5f, Part = HitboxPart.Limb, DamageMultiplier = 0.8f },
        };

        static Material _uniform;
        static Material _kit;
        static Material _accent;
        static Material _skin;

        public static BotRig Build(GameObject root, Damageable owner)
        {
            var rigRoot = new GameObject("Rig").transform;
            rigRoot.SetParent(root.transform, false);

            var bones = new BoneLink[(int)Bone.Count];

            foreach (var spec in Specs)
            {
                var parent = spec.Parent == Bone.Count ? rigRoot : bones[(int)spec.Parent].Transform;

                var go = new GameObject(spec.Id.ToString());
                go.layer = GameLayers.Hitbox;
                go.transform.SetParent(parent, false);
                go.transform.localPosition = spec.LocalPosition;
                go.transform.localRotation = Quaternion.Euler(spec.LocalEuler);

                var collider = go.AddComponent<CapsuleCollider>();
                collider.direction = 1; // local Y runs down the bone
                collider.radius = spec.Radius;
                collider.height = Mathf.Max(spec.Length, spec.Radius * 2f);
                collider.center = new Vector3(0f, spec.Length * 0.5f, 0f);

                var body = go.AddComponent<Rigidbody>();
                body.mass = spec.Mass;
                body.isKinematic = true;
                body.useGravity = false;
                body.interpolation = RigidbodyInterpolation.Interpolate;
                body.solverIterations = 14;
                body.solverVelocityIterations = 6;

                var hitbox = go.AddComponent<Hitbox>();
                hitbox.part = spec.Part;
                hitbox.damageMultiplier = spec.DamageMultiplier;
                hitbox.owner = owner;

                AddVisual(go.transform, spec);

                bones[(int)spec.Id] = new BoneLink
                {
                    Id = spec.Id,
                    Parent = spec.Parent,
                    Transform = go.transform,
                    Body = body,
                    Collider = collider,
                    Hitbox = hitbox,
                    BindPosition = spec.LocalPosition,
                    BindRotation = Quaternion.Euler(spec.LocalEuler),
                    Length = spec.Length,
                };
            }

            AddKit(bones);

            var rig = root.GetComponent<BotRig>() ?? root.AddComponent<BotRig>();
            rig.Register(rigRoot, bones);
            return rig;
        }

        static void AddVisual(Transform bone, BoneSpec spec)
        {
            bool isHead = spec.Id == Bone.Head;
            var visual = GameObject.CreatePrimitive(isHead ? PrimitiveType.Sphere : PrimitiveType.Capsule);
            visual.name = "Visual";
            visual.layer = GameLayers.Hitbox;
            Object.Destroy(visual.GetComponent<Collider>());
            visual.transform.SetParent(bone, false);

            if (isHead)
            {
                visual.transform.localPosition = new Vector3(0f, spec.Length * 0.45f, 0f);
                visual.transform.localScale = new Vector3(spec.Radius * 1.85f, spec.Radius * 2.1f, spec.Radius * 1.95f);
            }
            else
            {
                visual.transform.localPosition = new Vector3(0f, spec.Length * 0.5f, 0f);
                visual.transform.localScale = new Vector3(spec.Radius * 2f, spec.Length * 0.5f, spec.Radius * 2f);
            }

            visual.GetComponent<MeshRenderer>().sharedMaterial = isHead ? Skin() : Uniform();
        }

        /// <summary>
        /// Helmet, goggles, chest rig, team accent and a rifle. Kit is what turns a stack of
        /// capsules into something the eye parses as a soldier.
        /// </summary>
        static void AddKit(BoneLink[] bones)
        {
            var head = bones[(int)Bone.Head].Transform;
            var chest = bones[(int)Bone.Chest].Transform;

            var helmet = Block(head, "Helmet", new Vector3(0f, 0.085f, -0.004f), new Vector3(0.245f, 0.2f, 0.26f), Kit(), PrimitiveType.Sphere);
            helmet.transform.localScale = new Vector3(0.245f, 0.19f, 0.26f);

            Block(head, "Goggles", new Vector3(0f, 0.052f, 0.085f), new Vector3(0.2f, 0.055f, 0.06f), Kit());
            Block(head, "Brim", new Vector3(0f, 0.105f, 0.075f), new Vector3(0.23f, 0.028f, 0.09f), Kit());

            Block(chest, "PlateCarrier", new Vector3(0f, 0.135f, 0.03f), new Vector3(0.34f, 0.3f, 0.22f), Kit());
            Block(chest, "Pouches", new Vector3(0f, 0.045f, 0.135f), new Vector3(0.26f, 0.09f, 0.07f), Kit());
            Block(chest, "Shoulders", new Vector3(0f, 0.225f, 0f), new Vector3(0.44f, 0.09f, 0.2f), Kit());
            Block(chest, "TeamPatch", new Vector3(0.155f, 0.185f, 0.055f), new Vector3(0.075f, 0.05f, 0.09f), Accent());

            // Rifle rides the right forearm, and its muzzle is where bot tracers originate.
            var forearm = bones[(int)Bone.LowerArmR].Transform;
            Block(forearm, "Rifle_Body", new Vector3(-0.02f, 0.2f, -0.035f), new Vector3(0.05f, 0.5f, 0.09f), Kit());
            Block(forearm, "Rifle_Mag", new Vector3(-0.02f, 0.13f, 0.055f), new Vector3(0.04f, 0.13f, 0.06f), Kit());
            var muzzle = new GameObject("Muzzle");
            muzzle.transform.SetParent(forearm, false);
            muzzle.transform.localPosition = new Vector3(-0.02f, 0.46f, -0.035f);
            muzzle.transform.localRotation = Quaternion.identity;
        }

        static GameObject Block(Transform parent, string name, Vector3 localPosition, Vector3 scale, Material material, PrimitiveType type = PrimitiveType.Cube)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.layer = GameLayers.Hitbox;
            Object.Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = scale;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }

        static Material Uniform() => _uniform ??= Make("Bot_Uniform", new Color(0.19f, 0.21f, 0.16f), 0f, 0.28f);
        static Material Kit() => _kit ??= Make("Bot_Kit", new Color(0.075f, 0.078f, 0.082f), 0.12f, 0.35f);
        static Material Accent() => _accent ??= Make("Bot_Accent", new Color(0.42f, 0.055f, 0.05f), 0f, 0.4f);
        static Material Skin() => _skin ??= Make("Bot_Balaclava", new Color(0.11f, 0.11f, 0.12f), 0f, 0.3f);

        static Material Make(string name, Color color, float metallic, float smoothness)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = new Material(shader) { name = name };
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", color);
            else
                mat.color = color;
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metallic);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
            return mat;
        }
    }
}
