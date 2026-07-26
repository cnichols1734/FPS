using UnityEngine;

namespace ArenaFps.Ballistics
{
    public enum SurfaceKind
    {
        Concrete,
        MetalThin,
        MetalThick,
        Drywall,
        Wood,
        Flesh,
        Default
    }

    /// <summary>
    /// Per-surface ballistics: density drives penetration cost, hardness drives ricochet chance.
    /// </summary>
    [CreateAssetMenu(menuName = "Arena FPS/Surface Definition", fileName = "Surface_")]
    public sealed class SurfaceDefinition : ScriptableObject
    {
        public SurfaceKind kind = SurfaceKind.Default;
        [Tooltip("kg/m³ equivalent — higher = harder to punch through")]
        public float density = 2400f;
        [Range(0f, 1f)] public float hardness = 0.7f;
        [Tooltip("Max thickness (m) this surface typically represents when hit as a thin shell")]
        public float defaultThickness = 0.15f;
        public bool canBreak;
        public float breakHealth = 100f;
        public AudioClip[] impactClips;
        public GameObject impactVfx;
        public GameObject exitVfx;

        public static SurfaceDefinition Fallback { get; private set; }

        public static SurfaceDefinition GetOrCreateFallback()
        {
            if (Fallback != null)
                return Fallback;
            Fallback = CreateInstance<SurfaceDefinition>();
            Fallback.name = "Surface_Default_Runtime";
            Fallback.kind = SurfaceKind.Default;
            Fallback.density = 2000f;
            Fallback.hardness = 0.5f;
            Fallback.defaultThickness = 0.2f;
            return Fallback;
        }
    }
}
