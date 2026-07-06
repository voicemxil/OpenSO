using System;
using Microsoft.Xna.Framework;

namespace FSO.Common.Utils
{
    /// <summary>
    /// TAA sub-pixel jitter sequences. Use <see cref="SampleHalton"/> (cycled Halton(2,3));
    /// <see cref="Sample"/> (R2) drifts directionally under TAA's recency-weighted accumulation.
    /// </summary>
    public static class R2Jitter
    {
        // plastic number (real root of x^3 = x + 1) and its reciprocal powers
        private const double G = 1.32471795724474602596;
        private const double A1 = 1.0 / G;
        private const double A2 = 1.0 / (G * G);

        /// <summary>
        /// n'th R2 sample as a sub-pixel offset in [-0.5, +0.5). Superseded by SampleHalton for TAA jitter.
        /// </summary>
        public static Vector2 Sample(int n)
        {
            double x = (0.5 + n * A1) % 1.0;
            double y = (0.5 + n * A2) % 1.0;
            return new Vector2((float)(x - 0.5), (float)(y - 0.5));
        }

        /// <summary>
        /// n'th cycled Halton(2,3) sample as a sub-pixel offset in [-0.5, +0.5).
        /// renderScale is PPXDepthEngine.SSAA (&lt;1 upscale, 1 native, &gt;1 supersample).
        /// </summary>
        public static Vector2 SampleHalton(int n, float renderScale)
        {
            int cycle = HaltonCycle(renderScale);
            int i = (n % cycle) + 1; // skip the degenerate 0th sample (origin)
            return new Vector2(HaltonValue(i, 2) - 0.5f, HaltonValue(i, 3) - 0.5f);
        }

        /// <summary>
        /// Jitter cycle length: DLSS/FSR2 phase count, 8 * ceil((1/renderScale)^2).
        /// Must agree with TAAResolve.Draw's JitterPhases uniform; the accumulation window
        /// (MaxAccum) must exceed the cycle for the converged limit cycle to be invisible.
        /// </summary>
        public static int HaltonCycle(float renderScale)
        {
            float upscale = (renderScale > 0f && renderScale < 1f) ? (1f / renderScale) : 1f;
            return 8 * Math.Max(1, (int)Math.Ceiling(upscale * upscale));
        }

        // radical inverse (van der Corput) in the given base
        private static float HaltonValue(int index, int b)
        {
            float f = 1f, r = 0f;
            while (index > 0)
            {
                f /= b;
                r += f * (index % b);
                index /= b;
            }
            return r;
        }
    }
}
