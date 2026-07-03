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
        // Stable-area current-frame weight (the blend's diff-driven deep end is 1 - BLEND_FACTOR).
        // 0.06 ≈ a ~16-frame accumulation window at native resolution.
        private const float BLEND_FACTOR = 0.06f;

        // Resolution-scaled accumulation depth (FSR2-style): at render scale < 1 each pixel carries
        // proportionally more scene content, so its per-frame sample variance is higher and it needs a
        // DEEPER accumulation window for the same stability — the main reason low-res TAA looked worse
        // than industry references. 0.06 (16 frames) at native -> 0.03 (~32 frames) at 0.5x. Only kicks
        // in when upscaling (scale < 1); supersampling keeps the native window. Safe to deepen here
        // because the filtered-input reconstruction (see TAA.fx) collapses the per-frame variance first.
        private static float ScaledBlendFactor()
        {
            float scale = PPXDepthEngine.SSAA;
            if (scale >= 1f) return BLEND_FACTOR;
            return MathHelper.Clamp(BLEND_FACTOR * scale, 0.03f, BLEND_FACTOR);
        }

        // Cap on the per-pixel accumulation counter N (meta.R). The counter drives the WARMUP ramp (raw
        // image first after a history clear / off-screen reset, detail builds on top) — it does NOT deepen
        // blend trust past the diff-driven baseline (that direction ghosted in every variant tried). Must
        // match the shader's decode (metaR * MAX_ACCUM).
        private const float MAX_ACCUM = 64f;

        // Per-frame jitter delta (UV units), set by World.PreDraw. Added back during history reprojection
        // to cancel the jitter baked into the (jittered-projection) velocity buffer -> jitter-free reproject.
        public static Vector2 JitterDeltaUV;

        // Diagnostic (graphics options motion-blur "Debug" while TAA is on): blit the META target to the
        // screen instead of the resolved frame, via the TAADebug technique's diagnostic encode:
        // RED = effective history trust this frame (dark = taking current / warming up, bright = deep
        // accumulation), GREEN = depth/ghost reject strength, BLUE = non-reprojectable. The resolve itself
        // runs untouched underneath; this only changes the meta encode + which texture hits the screen.
        public static bool DebugAccum;

        public static void Draw(GraphicsDevice gd, RenderTarget2D src)
        {
            var effect = WorldContent.TAA;
            var velocity = PPXDepthEngine.GetVelocityTarget();
            var historyPrev = PPXDepthEngine.GetHistoryPrev();
            var historyCurr = PPXDepthEngine.GetHistoryCurr();
            var metaPrev = PPXDepthEngine.GetMetaPrev();
            var metaCurr = PPXDepthEngine.GetMetaCurr();
            // Cosmic TAAU: this resolve IS the upscaler — history/output native, color/velocity render-res.
            bool upscale = PPXDepthEngine.TAAUpscaleMode;
            if (effect == null || velocity == null || historyPrev == null || historyCurr == null
                || metaPrev == null || metaCurr == null
                // Size guard: history must match the surface being resolved on — the OUTPUT viewport under
                // TAAU, the render-res src otherwise. A transient mismatch (e.g. first frame after a scale
                // change, before ChangeAAMode re-sizes the targets) falls through rather than resolving
                // stretched/misaligned.
                || (upscale ? (historyPrev.Width != gd.Viewport.Width || historyPrev.Height != gd.Viewport.Height)
                            : (historyPrev.Width != src.Width || historyPrev.Height != src.Height)))
            {
                if (PPXDepthEngine.TAASkipFinalBlit)
                {
                    // Pre-upscale mode: pass the raw frame through to the upscaler unchanged this frame.
                    PPXDepthEngine.TAAOutput = src;
                    return;
                }
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

            // Render the TAA-blended result into the "current" history target (COLOR0) + the accumulation/
            // normal meta into the "current" meta target (COLOR1). The chain reads the history for the screen
            // blit below by re-binding it as src after this call.
            var finalTarget = gd.GetRenderTargets();
            gd.SetRenderTargets(historyCurr, metaCurr);

            gd.BlendState = BlendState.Opaque;
            effect.Parameters["colorTex"]?.SetValue(src);
            effect.Parameters["historyTex"]?.SetValue(historyPrev);
            effect.Parameters["metaHistoryTex"]?.SetValue(metaPrev);
            effect.Parameters["velocityTex"]?.SetValue(velocity);
            // InvScreenSize = the OUTPUT/history grid; InvColorSize = the INPUT color grid. Identical
            // normally; under TAAU history is native while color/velocity stay render-res.
            effect.Parameters["InvScreenSize"]?.SetValue(new Vector2(1f / historyPrev.Width, 1f / historyPrev.Height));
            effect.Parameters["InvColorSize"]?.SetValue(new Vector2(1f / src.Width, 1f / src.Height));
            effect.Parameters["BlendFactor"]?.SetValue(ScaledBlendFactor());
            effect.Parameters["MaxAccum"]?.SetValue(MAX_ACCUM);
            effect.Parameters["JitterDelta"]?.SetValue(JitterDeltaUV);
            // Depth-disocclusion tuning keyed to the ACTUAL history format. fp16 history stores depth at
            // ~11 effective bits, so the reject curve can be sharp (slope 12, no offset, small dead-zone);
            // the RGBA8 fallback keeps the old blunted curve that existed to hide 1/255 quantization.
            // Dead-zone 0.0015 (was 0.0005): the velocity buffer is fp16 now too, so BOTH sides of the
            // ghost compare carry ~5e-4 relative quantization — the zone covers their sum with margin.
            effect.Parameters["DepthRejectParams"]?.SetValue(PPXDepthEngine.HistoryIsFP16
                ? new Vector4(0.0015f, 12f, 0f, 0.02f)
                : new Vector4(2f / 255f, 6f, 0.25f, 0.05f));
            // Un-jittered offset for the variance-box taps. Sign derivation, verified against the velocity
            // shaders (which compute currNDC = clip.xy/w - JitterNDC to UN-jitter): jittered content sits at
            // unjittered + JitterNDC in NDC, i.e. content shifts by +j. In UV that's (+j.X/2, -j.Y/2) (UV y
            // inverted vs NDC y). The shader samples the box at uv - SampleJitterUV and needs boxUV =
            // uv + contentShift, so SampleJitterUV = -contentShift = (-j.X*0.5, +j.Y*0.5). (The first cut
            // used the opposite sign on both axes — box wobble DOUBLED and the image got jitterier, the
            // predicted wrong-sign symptom.) Zero when TAA jitter is off; the city view leaves it zero ->
            // behaves as before there.
            var jNdc = PPXDepthEngine.TAAJitterNDC;
            effect.Parameters["SampleJitterUV"]?.SetValue(new Vector2(-jNdc.X * 0.5f, jNdc.Y * 0.5f));
            // Motion gates must think in NATIVE pixels regardless of the resolve grid. In pre-upscale (FSR1)
            // mode the grid is render-res, so scene motion produced proportionally fewer grid-pixels of
            // velocity at low render scale — disocclusion rejection barely armed and the oscillation lock
            // survived on moving edges (the "ghosting + fizzle on disocclusion at low res, TAAU off" report).
            // TAAU and native/supersample grids are already native-sized -> scale 1.
            float ss = PPXDepthEngine.SSAA;
            effect.Parameters["VelGatePxScale"]?.SetValue((!upscale && ss < 1f && ss > 0f) ? 1f / ss : 1f);
            // The debug view uses a dedicated technique: meta.GB carries diagnostics there instead of the
            // prev-velocity encode, and the GB consumers are compiled out (self-consistent while debugging).
            var tech = (DebugAccum ? effect.Techniques["TAADebug"] : null) ?? effect.Techniques["TAA"];
            if (tech == null) { gd.SetRenderTargets(finalTarget); return; }
            effect.CurrentTechnique = tech;
            effect.CurrentTechnique.Passes[0].Apply();

            gd.SetVertexBuffer(WorldContent.GetTextureVerts(gd));
            gd.DrawPrimitives(PrimitiveType.TriangleStrip, 0, 2);

            gd.SetRenderTargets(finalTarget);
            if (PPXDepthEngine.TAASkipFinalBlit)
            {
                // Pre-upscale mode: no stretch blit — publish the render-res resolved frame (or the meta
                // diagnostic when debugging) for the chain to feed straight into the upscaler.
                PPXDepthEngine.TAAOutput = DebugAccum ? metaCurr : historyCurr;
            }
            else
            {
                // Copy the result to whatever target the chain originally bound (screen or next ping-pong
                // RT). Destination rect = current viewport so the blit matches the chain's working surface.
                gd.BlendState = BlendState.Opaque;
                using (var sb = new SpriteBatch(gd))
                {
                    sb.Begin(blendState: BlendState.Opaque);
                    sb.Draw(DebugAccum ? metaCurr : historyCurr, new Rectangle(0, 0, gd.Viewport.Width, gd.Viewport.Height), Color.White);
                    sb.End();
                }
            }

            // Rotate history roles for next frame: currCurr becomes "prev", the other becomes "curr".
            PPXDepthEngine.SwapHistory();
        }
    }
}
