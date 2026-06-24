using System;

namespace FSO.Files.RC.Utils
{
    /// <summary>
    /// Conditions a raw 8-bit sprite depth buffer before it is turned into a 3D mesh.
    ///
    /// The original reconstruction fed the raw quantized depth straight into the mesher, producing
    /// terracing (only 256 depth levels -> stair-steps on slopes) and noisy normals.
    ///
    /// This pass denoises and softens that terracing with a monotone, edge-aware bilateral filter.
    /// "Monotone" matters: the output at each pixel is a weighted average of its neighbours, so it can
    /// never overshoot past a neighbour's value. That guarantees we don't push a vertex out far enough
    /// to break the mesher's edge-length test and open a new hole — an earlier plane-fit version could,
    /// and a depth-step-split speck/island removal here fragmented solid surfaces. Scatter removal now
    /// happens after triangulation instead (cull small disconnected triangle islands), where a solid is
    /// a single island and stays whole.
    /// </summary>
    public class DepthConditioner
    {
        /// <summary>Conditioned depth, 0..1 (1 == far/at base plane). Invalid pixels are set to 1.</summary>
        public float[] Depth;
        /// <summary>Per-pixel validity. False for background (matches the original "raw &lt; 255" test).</summary>
        public bool[] Valid;

        // Pixels with this raw depth are empty/background (matches the original "d < 0.999f").
        private const byte EmptySentinel = 255;
        // Bilateral filter window radius (pixels).
        private const int Radius = 2;

        public static DepthConditioner Process(byte[] raw, int w, int h, DGRPRCParams config)
        {
            var result = new DepthConditioner();
            int n = w * h;

            var valid = new bool[n];
            var depth = new float[n];
            for (int i = 0; i < n; i++)
            {
                bool v = raw[i] < EmptySentinel;
                valid[i] = v;
                depth[i] = v ? raw[i] / 255f : 1f;
            }

            bool condition = config == null || config.DepthConditioning;
            if (condition)
            {
                var defaults = new DGRPRCParams();
                float discontinuity = config?.DepthDiscontinuity ?? defaults.DepthDiscontinuity;
                float strength = config?.DepthFilterStrength ?? defaults.DepthFilterStrength;
                // Fill isolated missing-depth pixels first. TSO z-buffers have scattered invalid pixels
                // (especially on flat tops/lids); left alone, the fusion silhouette carve punches a hole
                // straight through the solid at each one. Only pinholes (mostly-surrounded by valid) are
                // filled, so real silhouette edges and corners are untouched.
                FillHoles(valid, depth, w, h);
                depth = Bilateral(valid, depth, w, h, discontinuity, strength);
            }

            result.Depth = depth;
            result.Valid = valid;
            return result;
        }

        // An invalid pixel becomes valid if at least this many of its 8 neighbours are valid. High
        // enough that silhouette edges (which have several invalid neighbours) are never filled.
        private const int MinNeighboursToFill = 6;
        private const int FillPasses = 2;

        /// <summary>
        /// Inpaints isolated invalid (missing-depth) pixels by averaging valid neighbours, closing
        /// pinholes in the z-buffer without filling genuine silhouette gaps.
        /// </summary>
        private static void FillHoles(bool[] valid, float[] depth, int w, int h)
        {
            for (int pass = 0; pass < FillPasses; pass++)
            {
                // Snapshot validity so fills within a pass don't cascade off freshly-filled pixels.
                var wasValid = (bool[])valid.Clone();
                bool any = false;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (wasValid[i]) continue;

                        int count = 0;
                        float sum = 0f;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int ny = y + dy;
                            if (ny < 0 || ny >= h) continue;
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int nx = x + dx;
                                if (nx < 0 || nx >= w) continue;
                                int ni = ny * w + nx;
                                if (wasValid[ni]) { count++; sum += depth[ni]; }
                            }
                        }

                        if (count >= MinNeighboursToFill)
                        {
                            depth[i] = sum / count;
                            valid[i] = true;
                            any = true;
                        }
                    }
                }
                if (!any) break;
            }
        }

        /// <summary>
        /// Edge-aware bilateral filter. Each valid pixel becomes a weighted average of nearby valid
        /// pixels, weighted by spatial distance and depth similarity, hard-gated at discontinuities so
        /// neighbours across a silhouette/step don't bleed in. Removes 8-bit terracing and noise without
        /// ever producing a value outside the local neighbourhood's range.
        /// </summary>
        private static float[] Bilateral(bool[] valid, float[] depth, int w, int h,
            float discontinuity, float strength)
        {
            int n = w * h;
            var outDepth = new float[n];
            Array.Copy(depth, outDepth, n);

            float spatialSigma = Radius / 2f;
            float spatialDenom = 2f * spatialSigma * spatialSigma;
            // Range tightness scales with strength (expressed in quantization steps).
            float rangeSigma = (1f / 255f) * (1f + 6f * Math.Max(0.01f, strength));
            float rangeDenom = 2f * rangeSigma * rangeSigma;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (!valid[i]) continue;
                    float dc = depth[i];

                    double sum = 0, wsum = 0;
                    for (int dy = -Radius; dy <= Radius; dy++)
                    {
                        int ny = y + dy;
                        if (ny < 0 || ny >= h) continue;
                        for (int dx = -Radius; dx <= Radius; dx++)
                        {
                            int nx = x + dx;
                            if (nx < 0 || nx >= w) continue;
                            int ni = ny * w + nx;
                            if (!valid[ni]) continue;
                            float dz = depth[ni] - dc;
                            if (Math.Abs(dz) > discontinuity) continue; // don't smooth across a step

                            float spatial = (float)Math.Exp(-(dx * dx + dy * dy) / spatialDenom);
                            float range = (float)Math.Exp(-(dz * dz) / rangeDenom);
                            double wgt = spatial * range;
                            sum += wgt * depth[ni];
                            wsum += wgt;
                        }
                    }

                    if (wsum > 0) outDepth[i] = (float)(sum / wsum);
                }
            }

            return outDepth;
        }
    }
}
