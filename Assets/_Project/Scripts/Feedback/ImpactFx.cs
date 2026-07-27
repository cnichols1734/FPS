using ArenaFps.Audio;
using ArenaFps.Ballistics;
using ArenaFps.Core;
using UnityEngine;

namespace ArenaFps.Feedback
{
    /// <summary>
    /// Single entry point for every bullet-visible effect. Callers describe what happened; this
    /// decides how it looks and sounds, so weapons, bots and ballistics stay free of FX code.
    /// </summary>
    public sealed class ImpactFx : MonoBehaviour
    {
        const int ParticleCapacity = 1800;
        const int DecalCapacity = 160;
        const int CasingCapacity = 24;

        static ImpactFx _instance;

        ParticleBatch _particles;
        DecalBatch _decals;
        Casing[] _casings;
        int _nextCasing;

        public static ImpactFx Instance
        {
            get
            {
                if (_instance != null)
                {
                    _instance.EnsureBuilt();
                    return _instance;
                }
                var go = new GameObject("__ImpactFx");
                _instance = go.AddComponent<ImpactFx>();
                _instance.EnsureBuilt();
                return _instance;
            }
        }

        void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            EnsureBuilt();
        }

        void EnsureBuilt()
        {
            if (_particles == null)
            {
                var particleGo = new GameObject("ParticleBatch");
                particleGo.transform.SetParent(transform, false);
                _particles = particleGo.AddComponent<ParticleBatch>();
                _particles.Initialise(ParticleCapacity, FxAtlas.AdditiveMaterial, "FxParticles");
            }

            if (_decals == null)
            {
                var decalGo = new GameObject("DecalBatch");
                decalGo.transform.SetParent(transform, false);
                _decals = decalGo.AddComponent<DecalBatch>();
                _decals.Initialise(DecalCapacity, FxAtlas.DecalMaterial, "FxDecals");
            }

            if (_casings == null)
                BuildCasingPool();
        }

        void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        public int LiveParticles => _particles != null ? _particles.Live : 0;
        public int LiveDecals => _decals != null ? _decals.Live : 0;

        // ---------------------------------------------------------------- world impacts

        public void SurfaceImpact(Vector3 point, Vector3 normal, Vector3 direction, SurfaceKind kind, bool playAudio = true)
        {
            switch (kind)
            {
                case SurfaceKind.MetalThin:
                case SurfaceKind.MetalThick:
                    ImpactCore(point, normal, new Color(3.4f, 2.35f, 1.1f, 1f), 0.5f);
                    Sparks(point, normal, direction, 22, 1.35f);
                    Dust(point, normal, 3, 0.18f, new Color(0.55f, 0.56f, 0.6f, 0.42f));
                    _decals.Add(point, normal, Random.Range(0.09f, 0.13f), FxAtlas.HoleMetal, Color.white, 22f);
                    break;

                case SurfaceKind.Wood:
                    ImpactCore(point, normal, new Color(1.5f, 0.82f, 0.32f, 0.9f), 0.34f);
                    Chips(point, normal, direction, 11, new Color(0.58f, 0.38f, 0.19f));
                    Dust(point, normal, 5, 0.26f, new Color(0.46f, 0.34f, 0.22f, 0.68f));
                    _decals.Add(point, normal, Random.Range(0.1f, 0.15f), FxAtlas.HoleConcrete, new Color(0.7f, 0.55f, 0.4f), 22f);
                    break;

                case SurfaceKind.Drywall:
                    ImpactCore(point, normal, new Color(1.15f, 1.05f, 0.82f, 0.65f), 0.28f);
                    Dust(point, normal, 11, 0.56f, new Color(0.94f, 0.92f, 0.87f, 0.82f));
                    Chips(point, normal, direction, 6, new Color(0.88f, 0.86f, 0.82f));
                    _decals.Add(point, normal, Random.Range(0.14f, 0.2f), FxAtlas.HoleConcrete, new Color(0.95f, 0.94f, 0.9f), 22f);
                    break;

                default:
                    ImpactCore(point, normal, new Color(1.7f, 1.28f, 0.62f, 0.75f), 0.36f);
                    Dust(point, normal, 9, 0.42f, new Color(0.68f, 0.66f, 0.6f, 0.78f));
                    Chips(point, normal, direction, 9, new Color(0.5f, 0.49f, 0.46f));
                    Sparks(point, normal, direction, 4, 0.58f);
                    _decals.Add(point, normal, Random.Range(0.11f, 0.16f), FxAtlas.HoleConcrete, Color.white, 22f);
                    break;
            }

            if (playAudio)
                Sfx3D.Instance.Play(SfxBank.ForSurface(kind), point, 0.75f, 0.09f, 60f);
        }

        /// <summary>Spall thrown off the far side of penetrated cover.</summary>
        public void ExitSpall(Vector3 point, Vector3 direction, SurfaceKind kind)
        {
            Vector3 normal = direction.normalized;
            Dust(point, normal, 4, 0.3f, new Color(0.6f, 0.58f, 0.55f, 0.55f));
            Chips(point, normal, direction, 5, new Color(0.45f, 0.44f, 0.42f));
        }

        /// <summary>
        /// Cover giving way. Scaled off the object's own bounds so a crate and a wall panel do not
        /// produce the same puff, and loud enough that the player notices the sightline opening.
        /// </summary>
        public void CoverBreak(Vector3 center, Vector3 extents, Vector3 direction, SurfaceKind kind)
        {
            var dir = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.forward;
            float reach = Mathf.Max(0.25f, extents.magnitude);

            Color dust = kind switch
            {
                SurfaceKind.Wood => new Color(0.48f, 0.35f, 0.21f, 0.7f),
                SurfaceKind.Drywall => new Color(0.93f, 0.91f, 0.87f, 0.85f),
                SurfaceKind.MetalThin or SurfaceKind.MetalThick => new Color(0.52f, 0.52f, 0.55f, 0.55f),
                _ => new Color(0.68f, 0.66f, 0.62f, 0.8f),
            };
            Color chip = kind switch
            {
                SurfaceKind.Wood => new Color(0.5f, 0.34f, 0.18f),
                SurfaceKind.Drywall => new Color(0.9f, 0.88f, 0.84f),
                SurfaceKind.MetalThin or SurfaceKind.MetalThick => new Color(0.45f, 0.45f, 0.48f),
                _ => new Color(0.5f, 0.49f, 0.46f),
            };

            int puffs = Mathf.Clamp(Mathf.RoundToInt(reach * 9f), 6, 22);
            for (int i = 0; i < puffs; i++)
            {
                var at = center + new Vector3(
                    Random.Range(-extents.x, extents.x),
                    Random.Range(-extents.y, extents.y),
                    Random.Range(-extents.z, extents.z));
                _particles.Spawn(
                    at,
                    (dir * Random.Range(0.4f, 1.6f) + Random.insideUnitSphere * 1.4f + Vector3.up * 0.5f),
                    Random.Range(0.7f, 1.5f),
                    reach * 0.25f,
                    reach * Random.Range(0.9f, 1.7f),
                    dust,
                    new Color(dust.r, dust.g, dust.b, 0f),
                    FxAtlas.Smoke,
                    gravity: -0.5f,
                    drag: 1.7f,
                    spin: Random.Range(-1.3f, 1.3f));
            }

            int shards = Mathf.Clamp(Mathf.RoundToInt(reach * 14f), 10, 34);
            for (int i = 0; i < shards; i++)
            {
                var at = center + Random.insideUnitSphere * reach * 0.7f;
                var velocity = (dir * 0.8f + Random.insideUnitSphere).normalized * Random.Range(2f, 8f);
                _particles.Spawn(
                    at,
                    velocity,
                    Random.Range(0.8f, 1.8f),
                    Random.Range(0.02f, 0.055f),
                    Random.Range(0.014f, 0.03f),
                    chip,
                    new Color(chip.r, chip.g, chip.b, 0f),
                    FxAtlas.Dot,
                    gravity: 16f,
                    drag: 0.4f,
                    spin: Random.Range(-11f, 11f),
                    collide: i % 3 == 0);
            }

            if (kind is SurfaceKind.MetalThin or SurfaceKind.MetalThick)
                Sparks(center, Vector3.up, dir, 12, 1.1f);

            Sfx3D.Instance.Play(Sfx.CoverBreak, center, 0.9f, 0.1f, 90f);
        }

        public void Ricochet(Vector3 point, Vector3 normal, Vector3 newDirection)
        {
            Sparks(point, normal, newDirection, 14, 1.25f);
            _particles.Spawn(point, newDirection.normalized * 34f, 0.09f, 0.05f, 0.02f,
                new Color(1f, 0.85f, 0.5f), new Color(1f, 0.4f, 0.1f, 0f),
                FxAtlas.Streak, ParticleShape.Streak, length: 1.1f, axis: newDirection);
            Sfx3D.Instance.Play(Sfx.Ricochet, point, 0.7f, 0.12f, 70f);
        }

        // ---------------------------------------------------------------- flesh

        public void FleshImpact(Vector3 point, Vector3 normal, Vector3 direction, bool headshot, Transform attach = null)
        {
            var dir = direction.normalized;
            float scale = headshot ? 1.7f : 1f;
            ImpactCore(point, normal, new Color(1.55f, 0.08f, 0.045f, 0.9f), 0.36f * scale);

            // Spray cone continuing through the target — the read that a round actually landed.
            int sprayCount = headshot ? 26 : 16;
            for (int i = 0; i < sprayCount; i++)
            {
                var jitter = Random.insideUnitSphere * 0.55f;
                var velocity = (dir + jitter).normalized * Random.Range(3.5f, 9f) * scale;
                _particles.Spawn(
                    point + dir * 0.03f,
                    velocity,
                    Random.Range(0.28f, 0.55f),
                    Random.Range(0.035f, 0.075f) * scale,
                    0.01f,
                    new Color(0.72f, 0.035f, 0.026f),
                    new Color(0.18f, 0.006f, 0.006f, 0f),
                    FxAtlas.Dot,
                    gravity: 9f,
                    drag: 2.2f);
            }

            // Mist puff at the entry point reads instantly even at 30 m.
            for (int i = 0; i < (headshot ? 9 : 6); i++)
            {
                _particles.Spawn(
                    point + Random.insideUnitSphere * 0.05f,
                    dir * Random.Range(0.6f, 1.8f) + Random.insideUnitSphere * 0.7f,
                    Random.Range(0.18f, 0.32f),
                    0.1f * scale,
                    0.34f * scale,
                    new Color(0.58f, 0.045f, 0.035f, 0.84f),
                    new Color(0.26f, 0.018f, 0.014f, 0f),
                    FxAtlas.Smoke,
                    drag: 3.5f);
            }

            if (attach != null)
                _decals.Add(point, normal, Random.Range(0.12f, 0.2f), Random.value < 0.5f ? FxAtlas.BloodA : FxAtlas.BloodB, Color.white, 14f, attach);

            // Splat whatever is behind the target.
            if (Physics.Raycast(point + dir * 0.05f, dir, out var behind, 3.5f, GameLayers.WorldMask, QueryTriggerInteraction.Ignore))
            {
                _decals.Add(behind.point, behind.normal, Random.Range(0.3f, 0.65f) * scale,
                    Random.value < 0.5f ? FxAtlas.BloodA : FxAtlas.BloodB, Color.white, 26f);
            }

            Sfx3D.Instance.Play(headshot ? Sfx.ImpactHeadshot : Sfx.ImpactFlesh, point, headshot ? 1f : 0.85f, 0.08f, 55f);
        }

        /// <summary>Pooled blood spray for a killing blow — heavier, wider, sticks around longer.</summary>
        public void DeathBurst(Vector3 point, Vector3 direction)
        {
            var dir = direction.normalized;
            for (int i = 0; i < 26; i++)
            {
                var velocity = (dir + Random.insideUnitSphere * 0.85f).normalized * Random.Range(2.5f, 11f);
                _particles.Spawn(
                    point,
                    velocity,
                    Random.Range(0.4f, 0.85f),
                    Random.Range(0.04f, 0.1f),
                    0.012f,
                    new Color(0.58f, 0.04f, 0.028f),
                    new Color(0.18f, 0.008f, 0.008f, 0f),
                    FxAtlas.Dot,
                    gravity: 11f,
                    drag: 1.4f,
                    collide: i % 4 == 0);
            }
        }

        // ---------------------------------------------------------------- weapon

        public void Tracer(Vector3 from, Vector3 to, float width = 0.035f, float speed = 340f)
        {
            var delta = to - from;
            float distance = delta.magnitude;
            if (distance < 0.4f)
                return;
            var dir = delta / distance;

            // A travelling streak rather than an instant line: reads as a round in flight and
            // gives the eye something to follow toward the target.
            float length = Mathf.Min(distance, 14f);
            float life = Mathf.Clamp(distance / speed, 0.03f, 0.4f);
            _particles.Spawn(
                from + dir * length * 0.5f,
                dir * speed,
                life,
                width * 0.55f,
                width * 0.24f,
                new Color(4.2f, 3.15f, 1.55f, 1f),
                new Color(1.8f, 0.58f, 0.18f, 0f),
                FxAtlas.Streak,
                ParticleShape.Streak,
                length: length,
                axis: dir);
            _particles.Spawn(
                from + dir * length * 0.46f,
                dir * (speed * 0.92f),
                life * 0.85f,
                width * 2.2f,
                width * 0.7f,
                new Color(1.45f, 0.92f, 0.38f, 0.42f),
                new Color(0.9f, 0.25f, 0.08f, 0f),
                FxAtlas.Streak,
                ParticleShape.Streak,
                length: length * 0.82f,
                axis: dir);
        }

        public void MuzzleFlash(Vector3 position, Vector3 forward, float scale = 1f)
        {
            _particles.Spawn(
                position + forward * 0.08f,
                forward * 1.6f,
                0.052f,
                0.28f * scale,
                0.075f * scale,
                new Color(2.8f, 1.85f, 0.72f, 0.86f),
                new Color(0.7f, 0.18f, 0.04f, 0f),
                FxAtlas.Star,
                spin: Random.Range(-14f, 14f));

            _particles.Spawn(
                position + forward * 0.28f,
                forward * 9f,
                0.045f,
                0.085f * scale,
                0.025f * scale,
                new Color(2.6f, 1.35f, 0.42f, 0.72f),
                new Color(0.65f, 0.16f, 0.035f, 0f),
                FxAtlas.Streak,
                ParticleShape.Streak,
                length: 0.48f * scale,
                axis: forward);

            // Burning gas beads inside the flash give the burst texture when firing full-auto.
            for (int i = 0; i < 6; i++)
            {
                _particles.Spawn(
                    position + forward * Random.Range(0.04f, 0.16f),
                    (forward + Random.insideUnitSphere * 0.32f) * Random.Range(2.2f, 6.2f),
                    Random.Range(0.045f, 0.11f),
                    Random.Range(0.035f, 0.07f) * scale,
                    Random.Range(0.1f, 0.19f) * scale,
                    new Color(1.9f, 0.92f, 0.28f, 0.72f),
                    new Color(0.45f, 0.11f, 0.025f, 0f),
                    FxAtlas.Dot,
                    drag: 6f);
            }

            // Smoke that lingers just long enough to be noticed.
            for (int i = 0; i < 2; i++)
            {
                _particles.Spawn(
                    position + forward * Random.Range(0.1f, 0.22f),
                    forward * Random.Range(0.8f, 1.5f) + Vector3.up * Random.Range(0.22f, 0.55f) + Random.insideUnitSphere * 0.18f,
                    Random.Range(0.34f, 0.58f),
                    Random.Range(0.06f, 0.1f) * scale,
                    Random.Range(0.36f, 0.54f) * scale,
                    new Color(0.34f, 0.32f, 0.28f, 0.3f),
                    new Color(0.27f, 0.27f, 0.25f, 0f),
                    FxAtlas.Smoke,
                    gravity: -0.12f,
                    drag: 2.4f,
                    spin: Random.Range(-2.4f, 2.4f));
            }
        }

        /// <summary>Near-miss crack. Only fires when a round passes close enough to matter.</summary>
        public void Whizz(Vector3 position, float volume)
        {
            Sfx3D.Instance.Play(Sfx.BulletWhizz, position, volume, 0.15f, 12f);
        }

        public void EjectCasing(Vector3 position, Vector3 right, Vector3 up, Vector3 inherited)
        {
            if (_casings == null)
                return;
            var casing = _casings[_nextCasing];
            _nextCasing = (_nextCasing + 1) % _casings.Length;
            casing.Eject(position, (right * 2.6f + up * 1.9f + Random.insideUnitSphere * 0.7f) + inherited);

            var puffDirection = (right * 0.55f + up * 0.35f + Random.insideUnitSphere * 0.16f).normalized;
            _particles.Spawn(
                position + puffDirection * 0.035f,
                puffDirection * Random.Range(0.55f, 1.1f) + inherited * 0.04f,
                Random.Range(0.18f, 0.32f),
                0.035f,
                Random.Range(0.16f, 0.24f),
                new Color(0.38f, 0.35f, 0.29f, 0.28f),
                new Color(0.22f, 0.22f, 0.2f, 0f),
                FxAtlas.Smoke,
                gravity: -0.08f,
                drag: 3.2f,
                spin: Random.Range(-3f, 3f));
        }

        // ---------------------------------------------------------------- emitters

        void ImpactCore(Vector3 point, Vector3 normal, Color color, float scale)
        {
            var hotFade = new Color(color.r * 0.35f, color.g * 0.28f, color.b * 0.22f, 0f);
            _particles.Spawn(
                point + normal * 0.018f,
                normal * Random.Range(0.2f, 0.7f),
                Random.Range(0.045f, 0.075f),
                0.12f * scale,
                0.035f * scale,
                color,
                hotFade,
                FxAtlas.Dot,
                drag: 9f);

            _particles.Spawn(
                point + normal * 0.024f,
                normal * Random.Range(0.1f, 0.35f),
                Random.Range(0.055f, 0.095f),
                0.2f * scale,
                0.045f * scale,
                new Color(color.r * 0.75f, color.g * 0.6f, color.b * 0.45f, color.a * 0.75f),
                Color.clear,
                FxAtlas.Star,
                spin: Random.Range(-10f, 10f));
        }

        void Sparks(Vector3 point, Vector3 normal, Vector3 direction, int count, float energy)
        {
            var reflected = Vector3.Reflect(direction.normalized, normal);
            for (int i = 0; i < count; i++)
            {
                var velocity = (reflected + normal * 0.25f + Random.insideUnitSphere * 0.78f).normalized * Random.Range(5f, 17f) * energy;
                _particles.Spawn(
                    point,
                    velocity,
                    Random.Range(0.1f, 0.32f),
                    Random.Range(0.012f, 0.026f),
                    0.004f,
                    new Color(3.2f, 2.25f, 0.95f, 1f),
                    new Color(1.2f, 0.3f, 0.05f, 0f),
                    FxAtlas.Streak,
                    ParticleShape.Streak,
                    gravity: 16f,
                    drag: 1.25f,
                    length: Random.Range(0.18f, 0.44f) * energy,
                    axis: velocity);
            }
        }

        void Dust(Vector3 point, Vector3 normal, int count, float size, Color color)
        {
            for (int i = 0; i < count; i++)
            {
                _particles.Spawn(
                    point + Random.insideUnitSphere * 0.03f,
                    (normal + Random.insideUnitSphere * 0.7f) * Random.Range(0.9f, 3.1f),
                    Random.Range(0.42f, 0.9f),
                    size * 0.35f,
                    size * Random.Range(1.7f, 3.1f),
                    color,
                    new Color(color.r, color.g, color.b, 0f),
                    FxAtlas.Smoke,
                    gravity: -0.6f,
                    drag: 2.15f,
                    spin: Random.Range(-1.6f, 1.6f));
            }
        }

        void Chips(Vector3 point, Vector3 normal, Vector3 direction, int count, Color color)
        {
            var reflected = Vector3.Reflect(direction.normalized, normal);
            for (int i = 0; i < count; i++)
            {
                var velocity = (reflected * 0.6f + normal * 0.8f + Random.insideUnitSphere).normalized * Random.Range(3f, 8.5f);
                _particles.Spawn(
                    point,
                    velocity,
                    Random.Range(0.5f, 1.1f),
                    Random.Range(0.014f, 0.032f),
                    Random.Range(0.01f, 0.02f),
                    color,
                    new Color(color.r, color.g, color.b, 0f),
                    FxAtlas.Dot,
                    gravity: 15f,
                    drag: 0.5f,
                    spin: Random.Range(-9f, 9f),
                    collide: true);
            }
        }

        void BuildCasingPool()
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var brass = new Material(shader) { name = "Casing_Brass_Runtime" };
            if (brass.HasProperty("_BaseColor"))
                brass.SetColor("_BaseColor", new Color(0.72f, 0.55f, 0.2f));
            else
                brass.color = new Color(0.72f, 0.55f, 0.2f);
            if (brass.HasProperty("_Metallic")) brass.SetFloat("_Metallic", 0.95f);
            if (brass.HasProperty("_Smoothness")) brass.SetFloat("_Smoothness", 0.72f);

            var root = new GameObject("Casings");
            root.transform.SetParent(transform, false);

            _casings = new Casing[CasingCapacity];
            for (int i = 0; i < CasingCapacity; i++)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                go.name = $"Casing_{i}";
                go.layer = GameLayers.Fx;
                go.transform.SetParent(root.transform, false);
                go.transform.localScale = new Vector3(0.009f, 0.023f, 0.009f);
                go.GetComponent<MeshRenderer>().sharedMaterial = brass;

                // The cylinder primitive already ships a capsule collider; reshape it rather than
                // swapping it, which would leave a duplicate alive until end of frame.
                if (go.GetComponent<Collider>() is CapsuleCollider capsule)
                {
                    capsule.radius = 0.5f;
                    capsule.height = 2f;
                    capsule.direction = 1;
                }

                var rb = go.AddComponent<Rigidbody>();
                rb.mass = 0.012f;
                rb.angularDamping = 0.05f;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

                _casings[i] = go.AddComponent<Casing>();
                go.SetActive(false);
            }
        }
    }
}
