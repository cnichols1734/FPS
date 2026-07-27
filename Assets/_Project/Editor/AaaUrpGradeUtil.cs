#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ArenaFps.Editor
{
    /// <summary>
    /// Shared URP color-grade safety for LiftGammaGain / ShadowsMidtonesHighlights.
    ///
    /// CRITICAL: In URP the RGB components of those Vector4s are MULTIPLIERS centred on 1.0.
    /// The w component is a master luminance OFFSET centred on 0.0.
    /// Writing rgb ≈ 0 multiplies the image by zero → pure black Game View with HUD still drawing.
    /// </summary>
    public static class AaaUrpGradeUtil
    {
        public const float RgbMin = 0.5f;
        public const float RgbMax = 1.5f;

        /// <summary>
        /// Convert a legacy offset-style Vector4 (rgb centred on 0) to URP multiplier style (rgb centred on 1).
        /// Heuristic: if all |rgb| are clearly small offsets (&lt; 0.5), add 1.0 to rgb; otherwise leave as-is.
        /// w is never modified.
        /// </summary>
        public static Vector4 FromOffsetStyle(Vector4 offsetStyle)
        {
            bool looksLikeOffset =
                Mathf.Abs(offsetStyle.x) < 0.5f &&
                Mathf.Abs(offsetStyle.y) < 0.5f &&
                Mathf.Abs(offsetStyle.z) < 0.5f &&
                // Already multiplier-style if any channel is near 1 with others near 1
                !(offsetStyle.x > 0.5f && offsetStyle.y > 0.5f && offsetStyle.z > 0.5f);

            if (!looksLikeOffset)
                return offsetStyle;

            return new Vector4(
                offsetStyle.x + 1f,
                offsetStyle.y + 1f,
                offsetStyle.z + 1f,
                offsetStyle.w);
        }

        public static void SetGradeVec(Vector4Parameter parameter, Vector4 value, string passName, string fieldName)
        {
            value = ClampGradeRgb(value, passName, fieldName);
            parameter.overrideState = true;
            parameter.value = value;
        }

        /// <summary>
        /// Write a legacy offset-style Vector4 safely (auto-converts rgb 0-centred → 1-centred).
        /// </summary>
        public static void SetGradeVecFromOffset(Vector4Parameter parameter, Vector4 offsetStyle, string passName, string fieldName)
        {
            SetGradeVec(parameter, FromOffsetStyle(offsetStyle), passName, fieldName);
        }

        public static Vector4 ClampGradeRgb(Vector4 v, string passName, string fieldName)
        {
            bool bad = false;
            for (int i = 0; i < 3; i++)
            {
                if (v[i] < RgbMin || v[i] > RgbMax)
                {
                    bad = true;
                    v[i] = Mathf.Clamp(v[i], RgbMin, RgbMax);
                }
            }

            if (bad)
            {
                Debug.LogError(
                    $"[AaaUrpGradeUtil] GRADE GUARD ({passName}.{fieldName}): rgb outside [{RgbMin},{RgbMax}] — clamped to {v}. " +
                    "URP Lift/SMH rgb are multipliers centred on 1.0; writing ~0 blacks the Game View.");
            }

            return v;
        }

        /// <summary>
        /// The one true dusty-Peshawar grade. Every pass must call this instead of writing its own
        /// ColorAdjustments / WhiteBalance / LGG / SMH values.
        ///
        /// Six passes previously hardcoded their own warm values. Because each stage pushed warm in
        /// the same direction — warm colorFilter, +14 temperature, warm lift AND warm shadows — the
        /// pushes compounded and collapsed the frame into a single orange hue bucket (measured: 2 of
        /// 12 hue buckets live, 87% of saturated pixels in one bucket, R/B 1.40-1.69).
        ///
        /// The fix is complementary grading: warm sun in the highlights, cool sky-bounce in the
        /// shadows. That split is what produces colour separation instead of a sepia wash.
        ///
        /// postExposure is deliberately NOT written here — AaaSkyExposurePass solves for it against
        /// a measured luminance target, so clobbering it would undo the exposure balance.
        /// </summary>
        public static void ApplyCanonicalDustyGrade(VolumeProfile profile, string passName)
        {
            if (profile == null) return;

            if (!profile.TryGet(out ColorAdjustments color))
                color = profile.Add<ColorAdjustments>(true);

            // Contrast stays moderate: high contrast amplifies whatever cast is present.
            color.contrast.Override(14f);
            color.colorFilter.Override(new Color(1f, 0.985f, 0.965f));
            color.hueShift.Override(0f);
            // Negative saturation was hiding the real material colours; the cast reduction
            // supplies the mood instead.
            color.saturation.Override(0f);

            if (!profile.TryGet(out WhiteBalance wb))
                wb = profile.Add<WhiteBalance>(true);
            wb.temperature.Override(5f);
            wb.tint.Override(1f);

            if (profile.TryGet(out LiftGammaGain lgg))
            {
                SetGradeVec(lgg.lift,  new Vector4(0.990f, 1.000f, 1.035f, 0.01f), passName, "lift");
                SetGradeVec(lgg.gamma, new Vector4(1.000f, 1.000f, 1.000f, 0.00f), passName, "gamma");
                SetGradeVec(lgg.gain,  new Vector4(1.030f, 1.005f, 0.975f, 0.00f), passName, "gain");
            }

            if (profile.TryGet(out ShadowsMidtonesHighlights smh))
            {
                SetGradeVec(smh.shadows,    new Vector4(0.975f, 1.000f, 1.060f,  0.02f), passName, "shadows");
                SetGradeVec(smh.midtones,   new Vector4(1.010f, 1.000f, 0.995f,  0.00f), passName, "midtones");
                SetGradeVec(smh.highlights, new Vector4(1.035f, 1.005f, 0.965f, -0.05f), passName, "highlights");
            }

            AssertGradeSafe(profile, passName);
            AssertCastSafe(profile, passName);
        }

        /// <summary>
        /// Guard against a pass reintroducing a one-hue wash. Warm highlights with warm shadows means
        /// there is no complementary split left, which is how the sepia collapse happened.
        /// </summary>
        public static void AssertCastSafe(VolumeProfile profile, string passName)
        {
            if (profile == null) return;

            if (profile.TryGet(out WhiteBalance wb) && wb.temperature.value > 8f)
            {
                Debug.LogError(
                    $"[AaaUrpGradeUtil] CAST GUARD ({passName}): WhiteBalance.temperature={wb.temperature.value} " +
                    "exceeds 8. Stacked warm pushes collapse the frame to a single hue. Clamping to 5.");
                wb.temperature.Override(5f);
            }

            if (profile.TryGet(out ShadowsMidtonesHighlights smh))
            {
                // shadows must stay cooler than they are warm: blue channel >= red channel.
                var s = smh.shadows.value;
                if (s.x > s.z)
                {
                    Debug.LogError(
                        $"[AaaUrpGradeUtil] CAST GUARD ({passName}): SMH.shadows is warm ({s.x:F3}R vs {s.z:F3}B). " +
                        "Shadows carry cool sky bounce; warm shadows remove all colour separation.");
                }
            }
        }

        public static void AssertGradeSafe(VolumeProfile profile, string passName)
        {
            if (profile == null) return;

            if (profile.TryGet(out LiftGammaGain lgg))
            {
                ApplyClamp(lgg.lift, passName, "lift");
                ApplyClamp(lgg.gamma, passName, "gamma");
                ApplyClamp(lgg.gain, passName, "gain");
            }

            if (profile.TryGet(out ShadowsMidtonesHighlights smh))
            {
                ApplyClamp(smh.shadows, passName, "shadows");
                ApplyClamp(smh.midtones, passName, "midtones");
                ApplyClamp(smh.highlights, passName, "highlights");
            }
        }

        static void ApplyClamp(Vector4Parameter parameter, string passName, string fieldName)
        {
            if (parameter == null) return;
            var clamped = ClampGradeRgb(parameter.value, passName, fieldName);
            parameter.overrideState = true;
            parameter.value = clamped;
            Debug.Log($"[AaaUrpGradeUtil] {passName}.{fieldName}={clamped}");
        }
    }
}
#endif
