using System;
using Microsoft.Xna.Framework;

namespace FSO.Common.Utils
{
    /// <summary>
    /// TAA sub-pixel jitter sequences.
    ///
    /// The shipping generator is <see cref="SampleHalton"/> — CYCLED Halton(2,3), the industry-standard
    /// TAA jitter (UE, the DLSS integration spec, FSR2, Unity all use it). Two properties matter for a
    /// RECENCY-weighted accumulator (TAA's exponential history, ~16-32 frame window):
    ///   1. Consecutive Halton samples jump irregularly/isotropically — no preferred direction.
    ///   2. A short repeating cycle makes the converged history reach a stationary limit cycle — the
    ///      resolved output goes visually STILL once converged.
    /// The cycle length follows the DLSS/FSR2 phase-count formula, 8 * ceil((1/renderScale)^2): 8 phases
    /// at native/supersample, 32 at 0.5x upscale (more distinct sub-pixel positions exactly when
    /// upscaling needs them; also what a TAAU resolve wants).
    ///
    /// <see cref="Sample"/> (R2 / plastic-number, Martin Roberts) is retained but SUPERSEDED for jitter:
    /// R2 is an additive recurrence — every consecutive sample steps by the same constant vector (mod 1).
    /// That is optimal for EQUAL-weight integration, but under TAA's recency weighting the constant-
    /// direction walk makes the effective sample centroid drift at constant velocity forever, which
    /// showed up in-game as aliasing patterns visibly crawling in one direction on partially-converged
    /// detail. No known shipped TAA uses R2 for projection jitter.
    /// </summary>
    public static class R2Jitter
    {
        // Plastic number (real root of x^3 = x + 1) and its reciprocal powers - the 2D generalization of
        // the golden ratio used by the classic 1D low-discrepancy sequence x_n = frac(n / phi).
        private const double G = 1.32471795724474602596;
        private const double A1 = 1.0 / G;
        private const double A2 = 1.0 / (G * G);

        /// <summary>
        /// Returns the n'th R2 sample (n >= 0) as a sub-pixel offset in [-0.5, +0.5). SUPERSEDED for TAA
        /// jitter by <see cref="SampleHalton"/> — see class summary for why (directional crawl under
        /// recency-weighted accumulation).
        /// </summary>
        public static Vector2 Sample(int n)
        {
            double x = (0.5 + n * A1) % 1.0;
            double y = (0.5 + n * A2) % 1.0;
            return new Vector2((float)(x - 0.5), (float)(y - 0.5));
        }

        /// <summary>
        /// Returns the n'th CYCLED Halton(2,3) sample as a sub-pixel offset in [-0.5, +0.5). The cycle
        /// length scales with the upscale factor per the DLSS/FSR2 phase-count formula (8 at native and
        /// supersample, 32 at 0.5x render scale). Index 0 of the raw Halton sequence is the degenerate
        /// origin, so the cycle indexes samples 1..cycle.
        /// </summary>
        /// <param name="n">Free-running frame counter (any non-negative int; wrapped internally).</param>
        /// <param name="renderScale">PPXDepthEngine.SSAA — &lt;1 upscale, 1 native, &gt;1 supersample.</param>
        public static Vector2 SampleHalton(int n, float renderScale)
        {
            float upscale = (renderScale > 0f && renderScale < 1f) ? (1f / renderScale) : 1f;
            int cycle = 8 * Math.Max(1, (int)Math.Ceiling(upscale * upscale));
            int i = (n % cycle) + 1; // skip the degenerate 0th sample (origin)
            return new Vector2(HaltonValue(i, 2) - 0.5f, HaltonValue(i, 3) - 0.5f);
        }

        // Radical-inverse (van der Corput) in the given base — the standard Halton component.
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
