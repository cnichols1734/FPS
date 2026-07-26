using UnityEngine;

namespace ArenaFps.Ballistics
{
    /// <summary>
    /// Drop on colliders to assign ballistics material. Missing tag → Default.
    /// </summary>
    public sealed class SurfaceTag : MonoBehaviour
    {
        public SurfaceDefinition surface;
        public float thicknessOverride = -1f;

        public float Thickness => thicknessOverride > 0f
            ? thicknessOverride
            : (surface != null ? surface.defaultThickness : 0.2f);
    }
}
