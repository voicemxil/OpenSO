using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using FSO.Common;
using FSO.Common.Utils;

namespace FSO.LotView.Utils
{
    /// <summary>
    /// TAA resolve: blends current color with velocity-reprojected history under a neighborhood clamp.
    /// Runs at PPXDepthEngine.PostProcessFunc, replacing FXAA/SMAA when TAA is on. Reads HistoryPrev,
    /// writes HistoryCurr; SwapHistory rotates roles each frame.
    /// </summary>
    public static class TAAResolve
    {
        // stable-area current-frame weight; 0.06 = ~16-frame accumulation window at native
        private const float BLEND_FACTOR = 0.06f;

        // FSR2-style: pixels at render scale < 1 have higher per-frame variance, so deepen the
        // accumulation window when upscaling (0.03 = ~32 frames at 0.5x)
        private static float ScaledBlendFactor()
        {
            float scale = PPXDepthEngine.SSAA;
            if (scale >= 1f) return BLEND_FACTOR;
            return MathHelper.Clamp(BLEND_FACTOR * scale, 0.03f, BLEND_FACTOR);
        }

        // cap on the per-pixel accumulation counter N (meta.R). 128 gives a window long enough to
        // average the full 72-phase jitter cycle at 1/3 scale. Must match the shader's decode (metaR * MAX_ACCUM).
        private const float MAX_ACCUM = 128f;

        // per-frame jitter delta (UV), set by World.PreDraw; cancels the jitter baked into the velocity buffer
        public static Vector2 JitterDeltaUV;

        // debug: blit the meta target instead (R = history trust, G = reject strength, B = non-reprojectable)
        public static bool DebugAccum;
        // User-selected "Cosmic TAA Lite" resolve tier (cfg.TAALite): run the lighter TAALite technique
        // instead of the full Cosmic TAA/TAAU. Same I/O contract and TAAU support; fewer fetches, no
        // lock/evidence machinery. Set by World.ChangeAAMode / World.ConfigureCityAA alongside DebugAccum.
        public static bool LiteMode;

        // Reusable blit SpriteBatch. The pass-through / final-copy blits below run every frame on the main
        // resolve path; a per-frame `new SpriteBatch` leaked a finalizable object each frame. Created lazily,
        // recreated only if the GraphicsDevice changes.
        private static SpriteBatch _blitSB;
        private static GraphicsDevice _blitGD;
        private static SpriteBatch BlitSB(GraphicsDevice gd)
        {
            if (_blitSB == null || _blitGD != gd)
            {
                _blitSB?.Dispose();
                _blitSB = new SpriteBatch(gd);
                _blitGD = gd;
            }
            return _blitSB;
        }

        public static void Draw(GraphicsDevice gd, RenderTarget2D src)
        {
            var effect = WorldContent.TAA;
            var velocity = PPXDepthEngine.GetVelocityTarget();
            var historyPrev = PPXDepthEngine.GetHistoryPrev();
            var historyCurr = PPXDepthEngine.GetHistoryCurr();
            var metaPrev = PPXDepthEngine.GetMetaPrev();
            var metaCurr = PPXDepthEngine.GetMetaCurr();
            // TAAU: this resolve is the upscaler - history/output native, color/velocity render-res
            bool upscale = PPXDepthEngine.TAAUpscaleMode;
            if (effect == null || velocity == null || historyPrev == null || historyCurr == null
                || metaPrev == null || metaCurr == null
                // history must match the resolve surface; transient mismatches (e.g. the frame after a
                // scale change) fall through rather than resolving misaligned
                || (upscale ? (historyPrev.Width != gd.Viewport.Width || historyPrev.Height != gd.Viewport.Height)
                            : (historyPrev.Width != src.Width || historyPrev.Height != src.Height)))
            {
                if (PPXDepthEngine.TAASkipFinalBlit)
                {
                    // pre-upscale mode: pass the raw frame through to the upscaler this frame
                    PPXDepthEngine.TAAOutput = src;
                    return;
                }
                // shader/buffers missing - plain blit
                gd.BlendState = BlendState.Opaque;
                var sb = BlitSB(gd);
                sb.Begin(blendState: BlendState.Opaque);
                sb.Draw(src, new Rectangle(0, 0, gd.Viewport.Width, gd.Viewport.Height), Color.White);
                sb.End();
                return;
            }

            // blend into the current history (COLOR0) + meta (COLOR1)
            var finalTarget = gd.GetRenderTargets();
            gd.SetRenderTargets(historyCurr, metaCurr);

            gd.BlendState = BlendState.Opaque;
            effect.Parameters["colorTex"]?.SetValue(src);
            effect.Parameters["historyTex"]?.SetValue(historyPrev);
            effect.Parameters["metaHistoryTex"]?.SetValue(metaPrev);
            effect.Parameters["velocityTex"]?.SetValue(velocity);
            // InvScreenSize = output/history grid; InvColorSize = input color grid (differ under TAAU)
            effect.Parameters["InvScreenSize"]?.SetValue(new Vector2(1f / historyPrev.Width, 1f / historyPrev.Height));
            effect.Parameters["InvColorSize"]?.SetValue(new Vector2(1f / src.Width, 1f / src.Height));
            effect.Parameters["BlendFactor"]?.SetValue(ScaledBlendFactor());
            effect.Parameters["MaxAccum"]?.SetValue(MAX_ACCUM);
            effect.Parameters["JitterDelta"]?.SetValue(JitterDeltaUV);
            // depth-reject curve keyed to the history format: sharp for fp16, blunted for the RGBA8
            // fallback (hides 1/255 quantization). Dead-zone covers fp16 quantization on both compare sides.
            effect.Parameters["DepthRejectParams"]?.SetValue(PPXDepthEngine.HistoryIsFP16
                ? new Vector4(0.0015f, 12f, 0f, 0.02f)
                : new Vector4(2f / 255f, 6f, 0.25f, 0.05f));
            // un-jittered offset for the variance-box taps: content shifts by +jitter in NDC and UV y is
            // inverted, so SampleJitterUV = (-j.X*0.5, +j.Y*0.5). Zero when TAA jitter is off.
            var jNdc = PPXDepthEngine.TAAJitterNDC;
            effect.Parameters["SampleJitterUV"]?.SetValue(new Vector2(-jNdc.X * 0.5f, jNdc.Y * 0.5f));
            // motion gates think in native pixels: pre-upscale grids are render-res, so scale velocity up;
            // TAAU/native/supersample grids are already native-sized
            float ss = PPXDepthEngine.SSAA;
            effect.Parameters["VelGatePxScale"]?.SetValue((!upscale && ss < 1f && ss > 0f) ? 1f / ss : 1f);
            // must agree with R2Jitter.HaltonCycle: the trust ceiling sizes the accumulation window to
            // exceed the cycle, or the converged limit cycle shows as repeating shimmer
            effect.Parameters["JitterPhases"]?.SetValue((float)R2Jitter.HaltonCycle(ss));
            // Live-tuning uniforms (TAA_Core only — TAALite keeps its literals). Defaults in TAATuning are
            // IDENTICAL to the pre-promotion shader literals, so shipping behavior is unchanged; they exist
            // as uniforms so the FSO.TAALab harness can tune the resolve lifecycle live. Null-safe pattern:
            // an older xnb without these parameters just keeps its baked-in defaults.
            effect.Parameters["TuneMotionBoostFloor"]?.SetValue(TAATuning.MotionBoostFloor);
            effect.Parameters["TuneMotionBoostMax"]?.SetValue(TAATuning.MotionBoostMax);
            effect.Parameters["TuneStillGateFloor"]?.SetValue(TAATuning.StillGateFloor);
            effect.Parameters["TuneMoveGateLo"]?.SetValue(TAATuning.MoveGateLo);
            effect.Parameters["TuneMoveGateHi"]?.SetValue(TAATuning.MoveGateHi);
            effect.Parameters["TuneRespEnd"]?.SetValue(TAATuning.RespEnd);
            effect.Parameters["TuneMotionTrustCap"]?.SetValue(TAATuning.MotionTrustCap);
            effect.Parameters["TuneMotionClampTighten"]?.SetValue(TAATuning.MotionClampTighten);
            effect.Parameters["TuneRawSoftenOnset"]?.SetValue(TAATuning.RawSoftenOnset);
            effect.Parameters["TuneRawSoftenSlope"]?.SetValue(TAATuning.RawSoftenSlope);
            effect.Parameters["TuneRawSoftenMotionSup"]?.SetValue(TAATuning.RawSoftenMotionSup);
            effect.Parameters["TuneGamma"]?.SetValue(TAATuning.Gamma);
            effect.Parameters["TuneTexDetailFloor"]?.SetValue(TAATuning.TexDetailFloor);
            effect.Parameters["TuneConfFloor"]?.SetValue(TAATuning.ConfFloor);
            effect.Parameters["TuneRingLo"]?.SetValue(TAATuning.RingLo);
            effect.Parameters["TuneRingHi"]?.SetValue(TAATuning.RingHi);
            // The debug view uses a dedicated technique: meta.GB carries diagnostics there instead of the
            // prev-velocity encode, and the GB consumers are compiled out (self-consistent while debugging).
            // Technique selection. TAALite is now a user-facing lighter option on ALL backends ("Cosmic
            // TAA Lite" in the AA dropdown — fewer fetches, no lock/evidence machinery), no longer only a
            // GL fallback (the GL warble root cause — the aliased POINT historyDepthSampler — was fixed
            // shader-wide via FetchHistoryPoint's texel-center snap, so the full technique runs on GL too).
            // TAADebug diagnoses TAA_Core's meta encode — meaningless under lite, hence the !LiteMode.
            var tech = (DebugAccum && FSOEnvironment.DirectX && !LiteMode ? effect.Techniques["TAADebug"] : null)
                       ?? (LiteMode ? effect.Techniques["TAALite"] : null)
                       ?? effect.Techniques["TAA"];
            if (tech == null) { gd.SetRenderTargets(finalTarget); return; }
            effect.CurrentTechnique = tech;
            effect.CurrentTechnique.Passes[0].Apply();

            gd.SetVertexBuffer(WorldContent.GetTextureVerts(gd));
            gd.DrawPrimitives(PrimitiveType.TriangleStrip, 0, 2);

            gd.SetRenderTargets(finalTarget);
            if (PPXDepthEngine.TAASkipFinalBlit)
            {
                // pre-upscale mode: publish the render-res result for the chain to feed the upscaler
                PPXDepthEngine.TAAOutput = DebugAccum ? metaCurr : historyCurr;
            }
            else
            {
                // copy the result to the chain's bound target (screen or next ping-pong RT)
                gd.BlendState = BlendState.Opaque;
                var sb = BlitSB(gd);
                sb.Begin(blendState: BlendState.Opaque);
                sb.Draw(DebugAccum ? metaCurr : historyCurr, new Rectangle(0, 0, gd.Viewport.Width, gd.Viewport.Height), Color.White);
                sb.End();
            }

            PPXDepthEngine.SwapHistory();
        }
    }
}
