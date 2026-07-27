using UnityEngine;

namespace ArenaFps.Combat
{
    /// <summary>
    /// Closed-form two-bone IK (law of cosines). Used for foot planting and rifle grip, where an
    /// iterative solver would be both slower and less stable across a dozen bots per frame.
    /// </summary>
    public static class TwoBoneIk
    {
        const float Epsilon = 1e-5f;

        /// <summary>
        /// Bends <paramref name="upper"/>/<paramref name="lower"/> so <paramref name="end"/> reaches
        /// <paramref name="target"/>, with the joint bending toward <paramref name="pole"/>.
        /// Rotations are blended by <paramref name="weight"/> so IK can fade in and out mid-stride.
        /// </summary>
        public static void Solve(
            Transform upper,
            Transform lower,
            Transform end,
            Vector3 target,
            Vector3 pole,
            float weight)
        {
            if (upper == null || lower == null || end == null)
                return;
            weight = Mathf.Clamp01(weight);
            if (weight <= 0f)
                return;

            var upperStart = upper.rotation;
            var lowerStart = lower.rotation;

            var a = upper.position;
            var b = lower.position;
            var c = end.position;

            var ab = b - a;
            var cb = b - c;
            var ac = c - a;
            var at = target - a;

            float lab = ab.magnitude;
            float lcb = cb.magnitude;
            if (lab < Epsilon || lcb < Epsilon || ac.sqrMagnitude < Epsilon)
                return;

            // Clamping short of full extension keeps the knee from snapping to a locked, popping
            // straight line when the target drifts past the leg's reach.
            float lat = Mathf.Clamp(at.magnitude, Epsilon, lab + lcb - Epsilon);

            float acAb0 = AngleBetween(ac, ab);
            float baBc0 = AngleBetween(a - b, c - b);
            float acAt0 = AngleBetween(ac, at);

            float acAb1 = Mathf.Acos(Mathf.Clamp((lcb * lcb - lab * lab - lat * lat) / (-2f * lab * lat), -1f, 1f));
            float baBc1 = Mathf.Acos(Mathf.Clamp((lat * lat - lab * lab - lcb * lcb) / (-2f * lab * lcb), -1f, 1f));

            // Bend plane: the chain folds toward the pole. Fall back to the current plane when the
            // pole is collinear, which happens on a fully straight limb.
            var bendAxis = Vector3.Cross(ac, pole - a);
            if (bendAxis.sqrMagnitude < Epsilon)
                bendAxis = Vector3.Cross(ac, ab);
            if (bendAxis.sqrMagnitude < Epsilon)
                return;
            bendAxis.Normalize();

            upper.rotation = Quaternion.AngleAxis((acAb1 - acAb0) * Mathf.Rad2Deg, bendAxis) * upper.rotation;
            lower.rotation = Quaternion.AngleAxis((baBc1 - baBc0) * Mathf.Rad2Deg, bendAxis) * lower.rotation;

            var swingAxis = Vector3.Cross(ac, at);
            if (swingAxis.sqrMagnitude > Epsilon)
            {
                upper.rotation = Quaternion.AngleAxis(acAt0 * Mathf.Rad2Deg, swingAxis.normalized) * upper.rotation;
            }

            if (weight < 1f)
            {
                var solvedLower = lower.rotation;
                upper.rotation = Quaternion.Slerp(upperStart, upper.rotation, weight);
                lower.rotation = Quaternion.Slerp(lowerStart, solvedLower, weight);
            }
        }

        static float AngleBetween(Vector3 from, Vector3 to)
        {
            float sq = from.sqrMagnitude * to.sqrMagnitude;
            if (sq < Epsilon)
                return 0f;
            return Mathf.Acos(Mathf.Clamp(Vector3.Dot(from, to) / Mathf.Sqrt(sq), -1f, 1f));
        }
    }
}
