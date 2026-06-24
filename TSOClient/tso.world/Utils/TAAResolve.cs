using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using FSO.Common.Utils;

namespace FSO.LotView.Utils
{
    /// <summary>
    /// Temporal Anti-Aliasing resolve. Samples current color + previous-frame history (reprojected via the
    /// velocity buffer) and blends them with a neighborhood-clamp against ghosting. Slots into the resolve
    /// chain at PPXDepthEngine.PostProcessFunc, replacing FXAA/SMAA when TAA is enabled.
    ///
    /// Pipeline (per frame):
    ///   1. PPXDepthEngine.GetHistoryPrev() = last frame's TAA output (read).
    ///   2. PPXDepthEngine.GetHistoryCurr() = this frame's destination (write).
    ///   3. After Draw, the chain's blit takes care of getting current-history to the screen, then
    ///      SwapHistory rotates roles for next frame.
    /// </summary>
    public static class TAAResolve
    {
        // Stable-area current-frame weight. Lower = deeper accumulation (more effective samples = sharper
        // supersampling) but more lag. 0.06 ≈ ~16-frame accumulation, matching the Halton(2,3) period.
        // The shader widens this toward more-current on luminance changes (feedback) and on motion.
        private const float BLEND_FACTOR = 0.06f;

        // Per-frame jitter delta (UV units), set by World.PreDraw. Added back during history reprojection
        // to cancel the jitter baked into the (jittered-projection) velocity buffer -> jitter-free reproject.
        public static Vector2 JitterDeltaUV;

        public static void Draw(GraphicsDevice gd, RenderTarget2D src)
        {
            var effect = WorldContent.TAA;
            var velocity = PPXDepthEngine.GetVelocityTarget();
            var historyPrev = PPXDepthEngine.GetHistoryPrev();
            var historyCurr = PPXDepthEngine.GetHistoryCurr();
            if (effect == null || velocity == null || historyPrev == null || historyCurr == null)
            {
                // Shader / buffers missing -> fall through to plain blit so the frame still renders.
                gd.BlendState = BlendState.Opaque;
                using (var sb = new SpriteBatch(gd))
                {
                    sb.Begin(blendState: BlendState.Opaque);
                    sb.Draw(src, new Rectangle(0, 0, gd.Viewport.Width, gd.Viewport.Height), Color.White);
                    sb.End();
                }
                return;
            }

            // Render the TAA-blended result into the "current" history target. The chain will read history
            // for the screen blit below by re-binding it as src after this call, or it can stay where it is
            // if no further chain stages follow.
            var finalTarget = gd.GetRenderTargets();
            gd.SetRenderTarget(historyCurr);

            gd.BlendState = BlendState.Opaque;
            effect.Parameters["colorTex"]?.SetValue(src);
            effect.Parameters["historyTex"]?.SetValue(historyPrev);
            effect.Parameters["velocityTex"]?.SetValue(velocity);
            effect.Parameters["InvScreenSize"]?.SetValue(new Vector2(1f / src.Width, 1f / src.Height));
            effect.Parameters["BlendFactor"]?.SetValue(BLEND_FACTOR);
            effect.Parameters["JitterDelta"]?.SetValue(JitterDeltaUV);
            var tech = effect.Techniques["TAA"];
            if (tech == null) { gd.SetRenderTargets(finalTarget); return; }
            effect.CurrentTechnique = tech;
            effect.CurrentTechnique.Passes[0].Apply();

            gd.SetVertexBuffer(WorldContent.GetTextureVerts(gd));
            gd.DrawPrimitives(PrimitiveType.TriangleStrip, 0, 2);

            // Copy the result to whatever target the chain originally bound (screen or next ping-pong RT).
            // Use the current viewport size for the destination rectangle so the blit matches the chain's
            // working surface, not historyCurr's size (which now equals viewport, but stays robust if it
            // ever drifts).
            gd.SetRenderTargets(finalTarget);
            gd.BlendState = BlendState.Opaque;
            using (var sb = new SpriteBatch(gd))
            {
                sb.Begin(blendState: BlendState.Opaque);
                sb.Draw(historyCurr, new Rectangle(0, 0, gd.Viewport.Width, gd.Viewport.Height), Color.White);
                sb.End();
            }

            // Rotate history roles for next frame: currCurr becomes "prev", the other becomes "curr".
            PPXDepthEngine.SwapHistory();
        }
    }
}
