using ArenaFps.Core;
using ArenaFps.Feedback;
using UnityEngine;
using UnityEngine.Events;

namespace ArenaFps.Ballistics
{
    /// <summary>
    /// Thin cover that degrades under fire and collapses, opening sightlines. Authored debris is
    /// optional: with nothing assigned it shatters into chunks generated from its own bounds and
    /// material, so any block in the scene can be made breakable without art work.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public sealed class BreakableCover : MonoBehaviour
    {
        [SerializeField] float maxHealth = 100f;
        [SerializeField] SurfaceDefinition surface;
        [Tooltip("Optional. Defaults to whichever object on this cover carries the renderer.")]
        [SerializeField] GameObject unbrokenVisual;
        [Tooltip("Optional. Left empty, chunks are generated at break time.")]
        [SerializeField] GameObject brokenDebris;
        [SerializeField] bool disableColliderOnBreak = true;

        [Header("Generated Debris")]
        [SerializeField] int debrisChunks = 8;
        [SerializeField] float debrisLifetime = 7f;

        public UnityEvent onBroken;

        float _health;
        bool _broken;
        SurfaceTag _tag;
        Renderer _renderer;

        public bool IsBroken => _broken;
        public float HealthNormalized => maxHealth > 0f ? Mathf.Clamp01(_health / maxHealth) : 0f;

        void Awake()
        {
            _health = maxHealth;

            _tag = GetComponent<SurfaceTag>();
            if (_tag == null)
                _tag = gameObject.AddComponent<SurfaceTag>();

            if (surface != null)
                _tag.surface = surface;
            else
                surface = _tag.surface;

            if (surface != null)
            {
                surface.canBreak = true;
                surface.breakHealth = maxHealth;
            }

            _renderer = GetComponent<Renderer>() ?? GetComponentInChildren<Renderer>();
            if (unbrokenVisual == null && _renderer != null)
                unbrokenVisual = _renderer.gameObject;

            if (brokenDebris != null)
                brokenDebris.SetActive(false);
        }

        public void ApplyBallisticDamage(float damage, Vector3 point, Vector3 direction)
        {
            if (_broken || damage <= 0f)
                return;

            _health -= damage;
            if (_health <= 0f)
                Break(point, direction);
        }

        void Break(Vector3 point, Vector3 direction)
        {
            if (_broken)
                return;
            _broken = true;

            var bounds = WorldBounds();

            if (brokenDebris != null)
            {
                brokenDebris.SetActive(true);
                foreach (var rb in brokenDebris.GetComponentsInChildren<Rigidbody>())
                    rb.AddForceAtPosition(direction.normalized * 4f, point, ForceMode.Impulse);
            }
            else
            {
                SpawnChunks(bounds, direction);
            }

            if (unbrokenVisual != null)
                unbrokenVisual.SetActive(false);

            if (disableColliderOnBreak)
            {
                foreach (var c in GetComponentsInChildren<Collider>())
                    c.enabled = false;
            }

            ImpactFx.Instance.CoverBreak(bounds.center, bounds.extents, direction,
                surface != null ? surface.kind : SurfaceKind.Default);

            onBroken?.Invoke();
            CoverBrokenBus.Notify(transform.position);
        }

        Bounds WorldBounds()
        {
            if (_renderer != null)
                return _renderer.bounds;
            var collider = GetComponent<Collider>();
            return collider != null ? collider.bounds : new Bounds(transform.position, Vector3.one * 0.5f);
        }

        /// <summary>
        /// Splits the cover's volume into loose chunks that inherit its look. They live on the FX
        /// layer so the wreckage never stops a bullet or trips a bot after the cover is gone.
        /// </summary>
        void SpawnChunks(Bounds bounds, Vector3 direction)
        {
            int count = Mathf.Clamp(debrisChunks, 0, 24);
            if (count == 0)
                return;

            var material = _renderer != null ? _renderer.sharedMaterial : null;
            var dir = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.forward;

            // Roughly a third of the original span per axis, so chunks read as pieces of this object.
            var chunkSize = Vector3.Max(bounds.size / 3f, Vector3.one * 0.08f);

            for (int i = 0; i < count; i++)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = $"{name}_Chunk_{i}";
                go.layer = GameLayers.Fx;
                go.transform.position = bounds.center + new Vector3(
                    Random.Range(-bounds.extents.x, bounds.extents.x),
                    Random.Range(-bounds.extents.y, bounds.extents.y),
                    Random.Range(-bounds.extents.z, bounds.extents.z));
                go.transform.rotation = Random.rotation;
                go.transform.localScale = Vector3.Scale(chunkSize,
                    new Vector3(Random.Range(0.5f, 1.1f), Random.Range(0.5f, 1.1f), Random.Range(0.5f, 1.1f)));

                if (material != null)
                    go.GetComponent<MeshRenderer>().sharedMaterial = material;

                var rb = go.AddComponent<Rigidbody>();
                rb.mass = 4f;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                rb.AddForce((dir * Random.Range(1.5f, 4f) + Random.insideUnitSphere * 2.5f + Vector3.up * 1.5f),
                    ForceMode.VelocityChange);
                rb.AddTorque(Random.insideUnitSphere * 6f, ForceMode.VelocityChange);

                Destroy(go, debrisLifetime * Random.Range(0.75f, 1.25f));
            }
        }
    }

    public static class CoverBrokenBus
    {
        public static event System.Action<Vector3> Broken;

        public static void Notify(Vector3 position) => Broken?.Invoke(position);
    }
}
