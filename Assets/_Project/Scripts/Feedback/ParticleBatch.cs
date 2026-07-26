using UnityEngine;

namespace ArenaFps.Feedback
{
    public enum ParticleShape
    {
        /// <summary>Camera-facing quad — sparks, dust, blood droplets, flashes.</summary>
        Billboard,

        /// <summary>Quad stretched along an axis and rolled to face the camera — tracers.</summary>
        Streak,
    }

    /// <summary>
    /// Every additive effect in the game lives in this one dynamic mesh, so sparks, blood, smoke,
    /// tracers and muzzle flashes cost a single draw call no matter how fast the player fires.
    /// Fixed capacity, zero per-particle allocation.
    /// </summary>
    public sealed class ParticleBatch : MonoBehaviour
    {
        struct Particle
        {
            public Vector3 Position;
            public Vector3 Velocity;
            public Vector3 Axis;
            public float Age;
            public float Life;
            public float SizeStart;
            public float SizeEnd;
            public float Length;
            public float Gravity;
            public float Drag;
            public float Spin;
            public float Rotation;
            public Color ColorStart;
            public Color ColorEnd;
            public Rect Uv;
            public ParticleShape Shape;
            public bool Collide;
        }

        int _capacity;
        Particle[] _particles;
        int _count;

        Mesh _mesh;
        Vector3[] _vertices;
        Vector2[] _uvs;
        Color[] _colors;
        Transform _camera;

        public int Live => _count;

        public void Initialise(int capacity, Material material, string label)
        {
            _capacity = capacity;
            _particles = new Particle[capacity];
            _vertices = new Vector3[capacity * 4];
            _uvs = new Vector2[capacity * 4];
            _colors = new Color[capacity * 4];

            var triangles = new int[capacity * 6];
            for (int i = 0; i < capacity; i++)
            {
                int v = i * 4;
                int t = i * 6;
                triangles[t + 0] = v + 0;
                triangles[t + 1] = v + 1;
                triangles[t + 2] = v + 2;
                triangles[t + 3] = v + 0;
                triangles[t + 4] = v + 2;
                triangles[t + 5] = v + 3;
            }

            _mesh = new Mesh { name = $"{label}_Mesh" };
            _mesh.MarkDynamic();
            _mesh.indexFormat = capacity * 4 > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            _mesh.vertices = _vertices;
            _mesh.uv = _uvs;
            _mesh.colors = _colors;
            _mesh.triangles = triangles;
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 4000f);

            var filter = gameObject.AddComponent<MeshFilter>();
            filter.sharedMesh = _mesh;

            var renderer = gameObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        }

        public void Spawn(
            Vector3 position,
            Vector3 velocity,
            float life,
            float sizeStart,
            float sizeEnd,
            Color colorStart,
            Color colorEnd,
            Rect uv,
            ParticleShape shape = ParticleShape.Billboard,
            float gravity = 0f,
            float drag = 0f,
            float length = 0f,
            Vector3 axis = default,
            float spin = 0f,
            bool collide = false)
        {
            if (_particles == null)
                return;

            // At capacity, overwrite the oldest slot: a dropped spark beats a growing allocation.
            int index = _count < _capacity ? _count++ : Random.Range(0, _capacity);

            _particles[index] = new Particle
            {
                Position = position,
                Velocity = velocity,
                Axis = axis.sqrMagnitude < 1e-8f ? velocity.normalized : axis.normalized,
                Age = 0f,
                Life = Mathf.Max(0.01f, life),
                SizeStart = sizeStart,
                SizeEnd = sizeEnd,
                Length = length,
                Gravity = gravity,
                Drag = drag,
                Spin = spin,
                Rotation = Random.Range(0f, Mathf.PI * 2f),
                ColorStart = colorStart,
                ColorEnd = colorEnd,
                Uv = uv,
                Shape = shape,
                Collide = collide,
            };
        }

        void LateUpdate()
        {
            if (_particles == null)
                return;

            var cam = Camera.main;
            if (cam != null)
                _camera = cam.transform;

            float dt = Time.deltaTime;
            Simulate(dt);
            BuildMesh();
        }

        void Simulate(float dt)
        {
            for (int i = _count - 1; i >= 0; i--)
            {
                ref var p = ref _particles[i];
                p.Age += dt;
                if (p.Age >= p.Life)
                {
                    _particles[i] = _particles[--_count];
                    continue;
                }

                p.Velocity.y -= p.Gravity * dt;
                if (p.Drag > 0f)
                    p.Velocity *= Mathf.Exp(-p.Drag * dt);
                p.Rotation += p.Spin * dt;

                var step = p.Velocity * dt;
                if (p.Collide && step.sqrMagnitude > 1e-6f)
                {
                    float distance = step.magnitude;
                    if (Physics.Raycast(p.Position, step / distance, out var hit, distance,
                            Core.GameLayers.WorldMask, QueryTriggerInteraction.Ignore))
                    {
                        // Skitter along the surface rather than sinking into it.
                        p.Position = hit.point + hit.normal * 0.01f;
                        p.Velocity = Vector3.Reflect(p.Velocity, hit.normal) * 0.32f;
                        continue;
                    }
                }

                p.Position += step;
            }
        }

        void BuildMesh()
        {
            Vector3 camRight = _camera != null ? _camera.right : Vector3.right;
            Vector3 camUp = _camera != null ? _camera.up : Vector3.up;
            Vector3 camPos = _camera != null ? _camera.position : Vector3.zero;

            for (int i = 0; i < _count; i++)
            {
                ref var p = ref _particles[i];
                float t = p.Age / p.Life;
                float size = Mathf.Lerp(p.SizeStart, p.SizeEnd, t) * 0.5f;
                var color = Color.Lerp(p.ColorStart, p.ColorEnd, t);

                Vector3 right, up;
                if (p.Shape == ParticleShape.Streak)
                {
                    Vector3 axis = p.Axis;
                    Vector3 toCam = (camPos - p.Position).normalized;
                    Vector3 side = Vector3.Cross(axis, toCam);
                    if (side.sqrMagnitude < 1e-6f)
                        side = camRight;
                    right = axis * (Mathf.Lerp(p.Length, p.Length * 0.6f, t) * 0.5f);
                    up = side.normalized * size;
                }
                else
                {
                    float cos = Mathf.Cos(p.Rotation);
                    float sin = Mathf.Sin(p.Rotation);
                    right = (camRight * cos + camUp * sin) * size;
                    up = (camUp * cos - camRight * sin) * size;
                }

                int v = i * 4;
                _vertices[v + 0] = p.Position - right - up;
                _vertices[v + 1] = p.Position - right + up;
                _vertices[v + 2] = p.Position + right + up;
                _vertices[v + 3] = p.Position + right - up;

                _uvs[v + 0] = new Vector2(p.Uv.xMin, p.Uv.yMin);
                _uvs[v + 1] = new Vector2(p.Uv.xMin, p.Uv.yMax);
                _uvs[v + 2] = new Vector2(p.Uv.xMax, p.Uv.yMax);
                _uvs[v + 3] = new Vector2(p.Uv.xMax, p.Uv.yMin);

                _colors[v + 0] = color;
                _colors[v + 1] = color;
                _colors[v + 2] = color;
                _colors[v + 3] = color;
            }

            // Collapse retired quads instead of shrinking the buffer.
            for (int i = _count; i < _capacity; i++)
            {
                int v = i * 4;
                if (_vertices[v] == Vector3.zero && _colors[v].a == 0f)
                    continue;
                _vertices[v + 0] = Vector3.zero;
                _vertices[v + 1] = Vector3.zero;
                _vertices[v + 2] = Vector3.zero;
                _vertices[v + 3] = Vector3.zero;
                _colors[v + 0] = Color.clear;
                _colors[v + 1] = Color.clear;
                _colors[v + 2] = Color.clear;
                _colors[v + 3] = Color.clear;
            }

            _mesh.vertices = _vertices;
            _mesh.uv = _uvs;
            _mesh.colors = _colors;
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 4000f);
        }
    }
}
