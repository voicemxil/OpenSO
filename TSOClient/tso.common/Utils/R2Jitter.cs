using Microsoft.Xna.Framework;

namespace FSO.Common.Utils
{
    /// <summary>
    /// R2 low-discrepancy sequence (Martin Roberts' plastic-number sequence) for TAA sub-pixel jitter.
    /// Replaces the previous Halton(2,3) generator: at the small sample counts TAA actually uses (8-32
    /// per "period"), R2 spreads points more evenly (larger average nearest-neighbour distance, fewer
    /// wasted/duplicated sub-pixel cells) and never repeats, so there's no artificial period to pick -
    /// callers can just keep incrementing the frame index forever.
    /// </summary>
    public static class R2Jitter
    {
        // Plastic number (real root of x^3 = x + 1) and its reciprocal powers - the 2D generalization of
        // the golden ratio used by the classic 1D low-discrepancy sequence x_n = frac(n / phi).
        private const double G = 1.32471795724474602596;
        private const double A1 = 1.0 / G;
        private const double A2 = 1.0 / (G * G);

        /// <summary>
        /// Returns the n'th R2 sample (n >= 0) as a sub-pixel offset in [-0.5, +0.5), matching the range
        /// the old HaltonValue(i,b)-0.5 calls produced. No modulo/period needed - n can just be a
        /// free-running frame counter.
        /// </summary>
        public static Vector2 Sample(int n)
        {
            double x = (0.5 + n * A1) % 1.0;
            double y = (0.5 + n * A2) % 1.0;
            return new Vector2((float)(x - 0.5), (float)(y - 0.5));
        }
    }
}
