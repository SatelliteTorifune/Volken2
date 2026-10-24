using UnityEngine;

namespace Volken.Weather
{
    /// <summary>Fog incident light in the active rendering color space; independent of density.</summary>
    public readonly struct FogLighting
    {
        public readonly Color Ambient;
        public readonly Color Direct;
        public readonly float Daylight;

        private FogLighting(Color ambient, Color direct, float daylight)
        {
            Ambient = ambient;
            Direct = direct;
            Daylight = daylight;
        }

        /// <summary>Convert authored sRGB once; intensities and visibility remain linear multipliers.</summary>
        public static Color WorkingColor(Color color, bool linear)
        {
            color = new Color(Mathf.Max(0, color.r), Mathf.Max(0, color.g), Mathf.Max(0, color.b), 1);
            return linear ? color.linear : color;
        }

        public static FogLighting Evaluate(Color ambient, Color tint, float colorBlend, Color sunColor,
            float sunIntensity, bool sunEnabled, float sunVisibility, float sunElevation, bool linear)
        {
            ambient = WorkingColor(ambient, linear);
            tint = WorkingColor(tint, linear);
            sunColor = WorkingColor(sunColor, linear);
            float luminance = ambient.r * 0.2126f + ambient.g * 0.7152f + ambient.b * 0.0722f;
            var neutral = new Color(luminance, luminance, luminance, 1);
            Color ambientTerm = tint * Color.Lerp(neutral, ambient, Mathf.Clamp01(colorBlend));
            float daylight = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(-0.12f, 0.2f, sunElevation));
            float directAmount = sunEnabled
                ? daylight * Mathf.Clamp(sunIntensity, 0, 4) * Mathf.Clamp01(sunVisibility) * 0.35f : 0;
            Color directTerm = tint * sunColor * directAmount;
            ambientTerm.a = directTerm.a = 0;
            return new FogLighting(ambientTerm, directTerm, daylight);
        }
    }
}
