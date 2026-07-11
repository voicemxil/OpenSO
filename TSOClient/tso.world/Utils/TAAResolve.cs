using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using FSO.Common;
using FSO.Common.Utils;

namespace FSO.LotView.Utils
{
    /// <summary>
    /// TAA resolve: blends current color with velocity-reprojected history under a neighborhood clamp.
    /// Runs as PPXDepthEngine.TAAFunc. Reads HistoryPrev, writes HistoryCurr; a successful frame ends
    /// in TemporalHistoryState.Commit (rotate roles + stamp the frame id); any early exit leaves the
    /// committed state describing what history genuinely holds.
    /// Every per-frame input flows through BuildContract — Draw consumes nothing outside the contract
    /// value (the chain-routing statics TAASkipFinalBlit/TAAOutput are output plumbing, not inputs).
    /// </summary>
    public static class TAAResolve
    {
        // Part of the history layout signature: bump when the history/meta ENCODING or the
        // accumulation semantics change (shader meta layout, depth packing, N decode), so a new
        // resolve can never interpret bytes accumulated by an old one.
        private const int RESOLVE_VERSION = 2; // v2: meta.A re-packed sign(1) + osc(4) + amp(3)

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

        // uniform camera-zoom jacobian (per-axis 1 - prevScale/currScale), set by WorldState.PrepareCulling
        // on its once-per-frame prev-VP capture; the city jitter driver zeroes it (no city TAAU yet). The
        // shader applies it as histUV += fracd * InvColorSize * ZoomJacobian — the velocity texel the
        // resolve reprojects with sits fracd away from the output pixel center, and under zoom that offset
        // is a systematic sub-texel reprojection error.
        public static Vector2 ZoomJacobianUV;

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

        /// <summary>
        /// Gather everything this frame's resolve consumes into one value (see TemporalFrameContract).
        /// The one read-time in this file: any new resolve input is added HERE, not as another static
        /// read inside Draw at a different time.
        /// </summary>
        public static TemporalFrameContract BuildContract(GraphicsDevice gd, RenderTarget2D src, Effect effect)
        {
            // TAAU: this resolve is the upscaler - history/output native, color/velocity render-res
            bool upscale = PPXDepthEngine.TAAUpscaleMode;
            float ss = PPXDepthEngine.SSAA;
            var jNdc = PPXDepthEngine.TAAJitterNDC;

            // The declared tier must describe the meta layout the technique that RUNS will write, so
            // it derives from technique availability (an old xnb missing TAADebug/TAALite falls back
            // to the full technique and must declare Full, not the requested tier).
            var tier = TemporalHistoryState.ResolveTier.Full;
            if (DebugAccum && FSOEnvironment.DirectX && !LiteMode && effect?.Techniques["TAADebug"] != null)
                tier = TemporalHistoryState.ResolveTier.Debug;
            else if (LiteMode && effect?.Techniques["TAALite"] != null)
                tier = TemporalHistoryState.ResolveTier.Lite;

            return new TemporalFrameContract
            {
                Color = src,
                Velocity = PPXDepthEngine.GetVelocityTarget(),
                History = PPXDepthEngine.TAAHistory,
                Tier = tier,
                Upscale = upscale,
                OutputWidth = gd.Viewport.Width,
                OutputHeight = gd.Viewport.Height,
                FrameId = PPXDepthEngine.FrameId,
                ResolveVersion = RESOLVE_VERSION,
                RenderScale = ss,

                JitterDeltaUV = JitterDeltaUV,
                ZoomJacobian = ZoomJacobianUV,
                // un-jittered offset for the variance-box taps: content shifts by +jitter in NDC and UV y
                // is inverted, so SampleJitterUV = (-j.X*0.5, +j.Y*0.5). Zero when TAA jitter is off.
                SampleJitterUV = new Vector2(-jNdc.X * 0.5f, jNdc.Y * 0.5f),
                // motion gates think in native pixels: pre-upscale grids are render-res, so scale velocity
                // up; TAAU/native/supersample grids are already native-sized
                VelGatePxScale = (!upscale && ss < 1f && ss > 0f) ? 1f / ss : 1f,
                BlendFactor = ScaledBlendFactor(),
                MaxAccum = MAX_ACCUM,
                // must agree with R2Jitter.HaltonCycle: the trust ceiling sizes the accumulation window to
                // exceed the cycle, or the converged limit cycle shows as repeating shimmer
                JitterPhases = R2Jitter.HaltonCycle(ss),
                // depth-reject curve keyed to the history format: sharp for fp16, blunted for the RGBA8
                // fallback (hides 1/255 quantization). Dead-zone covers fp16 quantization on both compare sides.
                DepthRejectParams = PPXDepthEngine.HistoryIsFP16
                    ? new Vector4(0.0015f, 12f, 0f, 0.02f)
                    : new Vector4(2f / 255f, 6f, 0.25f, 0.05f),
            };
        }

        public static void Draw(GraphicsDevice gd, RenderTarget2D src)
        {
            var effect = WorldContent.TAA;
            var c = BuildContract(gd, src, effect);
            if (effect == null || !c.Ready)
            {
                // inputs missing or misaligned (e.g. the frame after a scale change) - pass through
                // un-resolved. No Commit happens, so the frame-gap check clears history when the
                // resolve next runs, instead of reprojecting one-frame motion across the outage.
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

            // Declare the layout + frame this resolve is about to write. TemporalHistoryState clears
            // history on any pending invalidation (blueprint init, lot<->city presenter switch via
            // DeclareOwner), layout/input-grid/version mismatch (tier/debug/upscale/render-scale
            // switch), or frame-continuity break. A reset reads as the ordinary warmup frame (meta
            // N=0), so this can't affect steady-state output.
            c.History.BeginResolve(gd, in c);

            var historyPrev = c.History.Prev;
            var metaPrev = c.History.MetaPrev;
            var historyCurr = c.History.Curr;
            var metaCurr = c.History.MetaCurr;

            // blend into the current history (COLOR0) + meta (COLOR1)
            var finalTarget = gd.GetRenderTargets();
            gd.SetRenderTargets(historyCurr, metaCurr);

            gd.BlendState = BlendState.Opaque;
            effect.Parameters["colorTex"]?.SetValue(c.Color);
            effect.Parameters["historyTex"]?.SetValue(historyPrev);
            effect.Parameters["metaHistoryTex"]?.SetValue(metaPrev);
            effect.Parameters["velocityTex"]?.SetValue(c.Velocity);
            // InvScreenSize = output/history grid; InvColorSize = input color grid (differ under TAAU)
            effect.Parameters["InvScreenSize"]?.SetValue(new Vector2(1f / historyPrev.Width, 1f / historyPrev.Height));
            effect.Parameters["InvColorSize"]?.SetValue(new Vector2(1f / c.Color.Width, 1f / c.Color.Height));
            effect.Parameters["BlendFactor"]?.SetValue(c.BlendFactor);
            effect.Parameters["MaxAccum"]?.SetValue(c.MaxAccum);
            effect.Parameters["JitterDelta"]?.SetValue(c.JitterDeltaUV);
            effect.Parameters["ZoomJacobian"]?.SetValue(c.ZoomJacobian);
            effect.Parameters["DepthRejectParams"]?.SetValue(c.DepthRejectParams);
            effect.Parameters["SampleJitterUV"]?.SetValue(c.SampleJitterUV);
            effect.Parameters["VelGatePxScale"]?.SetValue(c.VelGatePxScale);
            effect.Parameters["JitterPhases"]?.SetValue(c.JitterPhases);
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
            effect.Parameters["TuneDirectClampMix"]?.SetValue(TAATuning.DirectClampMix);
            effect.Parameters["TuneKarisFade"]?.SetValue(TAATuning.KarisFade);
            effect.Parameters["TuneGammaMotionDecay"]?.SetValue(TAATuning.GammaMotionDecay);
            effect.Parameters["TuneConfFadeN"]?.SetValue(TAATuning.ConfFadeN);
            effect.Parameters["TuneGrowOffPhase"]?.SetValue(TAATuning.GrowOffPhase);
            effect.Parameters["TuneDeepCapBase"]?.SetValue(TAATuning.DeepCapBase);
            effect.Parameters["TuneThinLineEps"]?.SetValue(TAATuning.ThinLineEps);
            // TAALite tunables (2026-07-07 promotion — Lite is a first-class tunable tier now; its
            // raw-motion character is the design target, see TAATuning).
            effect.Parameters["LiteGamma"]?.SetValue(TAATuning.LiteGamma);
            effect.Parameters["LiteGammaScale"]?.SetValue(TAATuning.LiteGammaScale);
            effect.Parameters["LiteDeepCap"]?.SetValue(TAATuning.LiteDeepCap);
            effect.Parameters["LiteRespEnd"]?.SetValue(TAATuning.LiteRespEnd);
            effect.Parameters["LiteMotionBoost"]?.SetValue(TAATuning.LiteMotionBoost);
            effect.Parameters["LiteConfFloor"]?.SetValue(TAATuning.LiteConfFloor);
            effect.Parameters["LiteMoveGateLo"]?.SetValue(TAATuning.LiteMoveGateLo);
            effect.Parameters["LiteMoveGateHi"]?.SetValue(TAATuning.LiteMoveGateHi);
            effect.Parameters["LiteHonestLo"]?.SetValue(TAATuning.LiteHonestLo);
            effect.Parameters["LiteHonestHi"]?.SetValue(TAATuning.LiteHonestHi);
            // Technique selection follows the tier declared in the contract (BuildContract already
            // resolved availability fallbacks, so the meta layout written matches the declaration).
            // TAALite is a user-facing lighter option on ALL backends ("Cosmic TAA Lite" in the AA
            // dropdown — fewer fetches, no lock/evidence machinery); TAADebug diagnoses TAA_Core's
            // meta encode, where meta.GB carries diagnostics and the GB consumers are compiled out.
            var tech = c.Tier == TemporalHistoryState.ResolveTier.Debug ? effect.Techniques["TAADebug"]
                     : c.Tier == TemporalHistoryState.ResolveTier.Lite ? effect.Techniques["TAALite"]
                     : effect.Techniques["TAA"];
            if (tech == null) { gd.SetRenderTargets(finalTarget); return; } // no Commit - frame gap handles it
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

            // transactional end: history now genuinely holds this frame
            c.History.Commit(c.FrameId);
        }
    }
}
