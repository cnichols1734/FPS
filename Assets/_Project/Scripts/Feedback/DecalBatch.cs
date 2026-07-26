using UnityEngine;

namespace ArenaFps.Feedback
{
    /// <summary>
    /// Bullet holes and blood splats as a single batched quad mesh. URP's Decal Renderer Feature
    /// is not enabled on this renderer, and projected decals would cost a depth-normals pass we
    /// cannot afford on the M1 Pro budget — camera-independent quads read the same at gameplay
    /// distance for a fraction of the cost.
    /// </summary>
    public sealed class DecalBatch : MonoBehaviour
    {
        struct Decal
        {
            public Vector3 Position;
            public Vector3 Normal;
            public Vector3 Right;
            public Vector3 Up;
            public float HalfSize;
            public float Age;
            public float Life;
            public float FadeIn;
            public Color Tint;
            public Rect Uv;
            public Transform Attach;
            public bool Attached;
            public Vector3 LocalOffset;
        }

        int _capacity;
        Decal[] _decals;
        int _next;
        int _live;

        public int Live => _live;

        Mesh _mesh;
        Vector3[] _vertices;
        Vector3[] _normals;
        Vector2[] _uvs;
        Color[] _colors;

        public void Initialise(int capacity, Material material, string label)
        {
            _capacity = capacity;
            _decals = new Decal[capacity];
            _vertices = new Vector3[capacity * 4];
            _normals = new Vector3[capacity * 4];
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
            _mesh.vertices = _vertices;
            _mesh.normals = _normals;
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
        }

        /// <param name="attach">Optional parent so splats on a body ride the ragdoll as it falls.</param>
        public void Add(Vector3 position, Vector3 normal, float size, Rect uv, Color tint, float life, Transform attach = null)
        {
            if (_decals == null)
                return;

            var up = Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > 0.95f ? Vector3.forward : Vector3.up;
            var right = Vector3.Cross(normal, up).normalized;
            up = Vector3.Cross(right, normal).normalized;

            float roll = Random.Range(0f, Mathf.PI * 2f);
            float cos = Mathf.Cos(roll);
            float sin = Mathf.Sin(roll);
            var rolledRight = right * cos + up * sin;
            var rolledUp = up * cos - right * sin;

            var offsetPosition = position + normal * 0.012f;

            int index = _next;
            _next = (_next + 1) % _capacity;
            if (_live < _capacity)
                _live++;

            _decals[index] = new Decal
            {
                Position = offsetPosition,
                Normal = normal,
                Right = rolledRight,
                Up = rolledUp,
                HalfSize = size * 0.5f,
                Age = 0f,
                Life = life,
                FadeIn = 0.04f,
                Tint = tint,
                Uv = uv,
                Attach = attach,
                Attached = attach != null,
                LocalOffset = attach != null ? attach.InverseTransformPoint(offsetPosition) : Vector3.zero,
            };
        }

        void LateUpdate()
        {
            if (_decals == null)
                return;

            float dt = Time.deltaTime;
            for (int i = 0; i < _capacity; i++)
            {
                ref var d = ref _decals[i];
                int v = i * 4;

                // A splat whose body was recycled has nowhere to be. Retire it rather than let it
                // fall back to the world position it was born at and hang in mid-air.
                if (d.Attached && d.Attach == null)
                    d.Life = 0f;

                if (d.Life <= 0f || d.Age >= d.Life)
                {
                    if (_colors[v].a != 0f)
                    {
                        if (_live > 0)
                            _live--;
                        _colors[v + 0] = Color.clear;
                        _colors[v + 1] = Color.clear;
                        _colors[v + 2] = Color.clear;
                        _colors[v + 3] = Color.clear;
                        _vertices[v + 0] = Vector3.zero;
                        _vertices[v + 1] = Vector3.zero;
                        _vertices[v + 2] = Vector3.zero;
                        _vertices[v + 3] = Vector3.zero;
                    }
                    continue;
                }

                d.Age += dt;

                Vector3 center = d.Position;
                Vector3 right = d.Right;
                Vector3 up = d.Up;
                Vector3 normal = d.Normal;

                if (d.Attach != null)
                {
                    center = d.Attach.TransformPoint(d.LocalOffset);
                    right = d.Attach.TransformDirection(d.Right);
                    up = d.Attach.TransformDirection(d.Up);
                    normal = d.Attach.TransformDirection(d.Normal);
                }

                float alpha = d.Tint.a;
                if (d.Age < d.FadeIn)
                    alpha *= d.Age / d.FadeIn;
                float remaining = d.Life - d.Age;
                if (remaining < 1.5f)
                    alpha *= remaining / 1.5f;

                var color = new Color(d.Tint.r, d.Tint.g, d.Tint.b, Mathf.Clamp01(alpha));
                right *= d.HalfSize;
                up *= d.HalfSize;

                _vertices[v + 0] = center - right - up;
                _vertices[v + 1] = center - right + up;
                _vertices[v + 2] = center + right + up;
                _vertices[v + 3] = center + right - up;

                _normals[v + 0] = normal;
                _normals[v + 1] = normal;
                _normals[v + 2] = normal;
                _normals[v + 3] = normal;

                _uvs[v + 0] = new Vector2(d.Uv.xMin, d.Uv.yMin);
                _uvs[v + 1] = new Vector2(d.Uv.xMin, d.Uv.yMax);
                _uvs[v + 2] = new Vector2(d.Uv.xMax, d.Uv.yMax);
                _uvs[v + 3] = new Vector2(d.Uv.xMax, d.Uv.yMin);

                _colors[v + 0] = color;
                _colors[v + 1] = color;
                _colors[v + 2] = color;
                _colors[v + 3] = color;
            }

            _mesh.vertices = _vertices;
            _mesh.normals = _normals;
            _mesh.uv = _uvs;
            _mesh.colors = _colors;
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 4000f);
        }
    }
}
