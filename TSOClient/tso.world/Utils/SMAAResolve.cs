using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FSO.LotView.Utils
{
    /// <summary>
    /// SMAA (Enhanced Subpixel Morphological AA) resolve. Runs the 3 passes of Jorge Jimenez's reference
    /// shader — edge detect, blend-weight calc (using the AreaTex + SearchTex lookup textures), and
    /// neighborhood blending — over a screen-res source, writing the final blend to the currently bound
    /// render target. Matches the <see cref="PostProcessAA.Draw"/> signature so it slots into the same
    /// resolve-chain hook (PostProcessFunc) without engine changes. Internally manages two intermediate
    /// targets (edges/weights), resized when the source dimensions change.
    /// </summary>
    public static class SMAAResolve
    {
        private static RenderTarget2D EdgesRT;
        private static RenderTarget2D WeightsRT;

        private static void EnsureTargets(GraphicsDevice gd, int w, int h)
        {
            if (EdgesRT == null || EdgesRT.Width != w || EdgesRT.Height != h)
            {
                EdgesRT?.Dispose();
                EdgesRT = new RenderTarget2D(gd, w, h, false, SurfaceFormat.Color, DepthFormat.None);
            }
            if (WeightsRT == null || WeightsRT.Width != w || WeightsRT.Height != h)
            {
                WeightsRT?.Dispose();
                WeightsRT = new RenderTarget2D(gd, w, h, false, SurfaceFormat.Color, DepthFormat.None);
            }
        }

        public static void Draw(GraphicsDevice gd, RenderTarget2D src)
        {
            var effect = WorldContent.SMAA;
            if (effect == null || WorldContent.SMAAAreaTex == null || WorldContent.SMAASearchTex == null)
            {
                // Fallback: SMAA pieces missing on this content profile -> route through FXAA (or the plain
                // blit via PostProcessAA's own fallback) so the picture still appears.
                PostProcessAA.Draw(gd, src);
                return;
            }

            int w = src.Width, h = src.Height;
            EnsureTargets(gd, w, h);

            // Remember the final destination (the chain may bind null = screen or a ping-pong RT).
            var finalTarget = gd.GetRenderTargets();

            effect.Parameters["SMAA_RT_METRICS"].SetValue(new Vector4(1f / w, 1f / h, w, h));

            // Pass 1: color -> edges. Edge-detect uses 'discard' to skip non-edge pixels, so the target
            // must be cleared to 0 first.
            gd.SetRenderTarget(EdgesRT);
            gd.Clear(Color.Transparent);
            gd.BlendState = BlendState.Opaque;
            effect.Parameters["colorTex_t"].SetValue(src);
            effect.CurrentTechnique = effect.Techniques["EdgeDetect"];
            effect.CurrentTechnique.Passes[0].Apply();
            gd.SetVertexBuffer(WorldContent.GetTextureVerts(gd));
            gd.DrawPrimitives(PrimitiveType.TriangleStrip, 0, 2);

            // Pass 2: edges + AreaTex + SearchTex -> blend weights. Same cleared-to-0 pattern.
            gd.SetRenderTarget(WeightsRT);
            gd.Clear(Color.Transparent);
            gd.BlendState = BlendState.Opaque;
            effect.Parameters["edgesTex_t"].SetValue(EdgesRT);
            effect.Parameters["areaTex_t"].SetValue(WorldContent.SMAAAreaTex);
            effect.Parameters["searchTex_t"].SetValue(WorldContent.SMAASearchTex);
            effect.CurrentTechnique = effect.Techniques["BlendWeights"];
            effect.CurrentTechnique.Passes[0].Apply();
            gd.SetVertexBuffer(WorldContent.GetTextureVerts(gd));
            gd.DrawPrimitives(PrimitiveType.TriangleStrip, 0, 2);

            // Pass 3: source color + blend weights -> the original destination (whatever the chain bound).
            gd.SetRenderTargets(finalTarget);
            gd.BlendState = BlendState.Opaque;
            effect.Parameters["colorTex_t"].SetValue(src);
            effect.Parameters["blendTex_t"].SetValue(WeightsRT);
            effect.CurrentTechnique = effect.Techniques["NeighborBlend"];
            effect.CurrentTechnique.Passes[0].Apply();
            gd.SetVertexBuffer(WorldContent.GetTextureVerts(gd));
            gd.DrawPrimitives(PrimitiveType.TriangleStrip, 0, 2);
        }
    }
}
