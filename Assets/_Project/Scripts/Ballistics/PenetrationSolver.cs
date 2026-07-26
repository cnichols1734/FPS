using UnityEngine;

namespace ArenaFps.Ballistics
{
    public struct BallisticHit
    {
        public bool didHit;
        public RaycastHit hit;
        public SurfaceDefinition surface;
        public float remainingEnergy;
        public bool penetrated;
        public bool ricocheted;
        public Vector3 exitPoint;
        public Vector3 continuedDirection;
    }

    /// <summary>
    /// Material-driven penetration + angle ricochet. Energy is unitless 0–1 starting at 1.
    /// </summary>
    public static class PenetrationSolver
    {
        const float MinEnergy = 0.08f;
        const int MaxSegments = 4;

        /// <summary>
        /// Fraction of remaining energy a barrier may cost and still be defeated. Tuned so thin
        /// metal, drywall and crate timber are shootable while structural concrete is genuinely
        /// cover — the previous 1.25 let rifle rounds through a 35 cm wall.
        /// </summary>
        const float PenetrationThreshold = 0.35f;

        /// <summary>Energy taken by any successful penetration, on top of the barrier's own cost.</summary>
        const float PenetrationToll = 0.12f;
        const float PenetrationCostScale = 2.2f;

        public static BallisticHit Trace(
            Vector3 origin,
            Vector3 direction,
            float range,
            float damage,
            LayerMask mask,
            QueryTriggerInteraction triggers = QueryTriggerInteraction.Ignore)
        {
            var result = new BallisticHit
            {
                remainingEnergy = 1f,
                continuedDirection = direction.normalized
            };

            Vector3 pos = origin;
            Vector3 dir = direction.normalized;
            float remainingRange = range;

            for (int segment = 0; segment < MaxSegments && remainingRange > 0.01f && result.remainingEnergy > MinEnergy; segment++)
            {
                if (!Physics.Raycast(pos, dir, out var hit, remainingRange, mask, triggers))
                {
                    if (segment == 0)
                        result.didHit = false;
                    break;
                }

                result.didHit = true;
                result.hit = hit;

                var tag = hit.collider.GetComponentInParent<SurfaceTag>();
                var surface = tag != null && tag.surface != null
                    ? tag.surface
                    : SurfaceDefinition.GetOrCreateFallback();
                result.surface = surface;

                float thickness = tag != null ? tag.Thickness : surface.defaultThickness;
                float incidence = Mathf.Clamp01(Vector3.Dot(-dir, hit.normal)); // 1 = head-on
                float graze = 1f - incidence;

                // Ricochet: glancing + hard surface
                float ricochetChance = surface.hardness * graze * graze;
                if (graze > 0.55f && Random.value < ricochetChance)
                {
                    result.ricocheted = true;
                    dir = Vector3.Reflect(dir, hit.normal).normalized;
                    result.remainingEnergy *= 0.55f + 0.35f * incidence;
                    result.continuedDirection = dir;
                    pos = hit.point + dir * 0.02f;
                    remainingRange -= hit.distance;
                    continue;
                }

                // Penetration cost ~ density * thickness, eased by a square-on impact.
                float cost = (surface.density / 1000f) * thickness * (0.6f + 0.4f * incidence);
                cost /= Mathf.Max(0.15f, result.remainingEnergy);

                if (cost < result.remainingEnergy * PenetrationThreshold && surface.kind != SurfaceKind.Flesh)
                {
                    result.penetrated = true;
                    result.remainingEnergy = Mathf.Max(0f,
                        result.remainingEnergy - (cost * PenetrationCostScale + PenetrationToll));
                    result.exitPoint = hit.point + dir * (thickness + 0.02f);
                    result.continuedDirection = dir;
                    pos = result.exitPoint;
                    remainingRange -= hit.distance + thickness;
                    // Keep looping for multi-material stacks
                    continue;
                }

                // Stopped in this surface
                result.remainingEnergy *= Mathf.Clamp01(1f - cost * 0.3f);
                result.continuedDirection = dir;
                break;
            }

            return result;
        }

        public static float DamageAfter(BallisticHit hit, float baseDamage)
        {
            if (!hit.didHit)
                return 0f;
            float m = hit.remainingEnergy;
            if (hit.ricocheted)
                m *= 0.65f;
            return baseDamage * m;
        }
    }
}
