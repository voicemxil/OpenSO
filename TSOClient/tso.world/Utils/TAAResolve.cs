using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using FSO.Common;
using FSO.Common.Utils;

namespace FSO.LotView.Utils
{
    /// <summary>
    /// TAA resolve: blends current color with velocity-reprojected history under a neighborhood clamp.
    /// Runs as its own PPXDepthEngine.TAAFunc stage AFTER any spatial post-AA (FXAA/SMAA run first at
    /// render resolution when upscaling — see DrawBackbuffer's postFirst). Reads HistoryPrev, writes
    /// HistoryCurr; SwapHistory rotates roles each frame.
    ///
    /// Inputs are gathered into a TemporalFrameContract; persistent state (history/meta + layout +
    /// invalidation) lives in PPXDepthEngine.TAAHistory (TemporalHistoryState). Tuning values have ONE
    /// source of truth — TAATuning — uploaded every frame via reflection (UploadTunables), which also
    /// audits the loaded effect once: missing uniforms and baked shader defaults that drifted from
    /// TAATuning are reported instead of silently falling back.
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

        // debug: blit the meta target instead (only when the TAADebug technique actually runs — see Draw)
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

        private static TemporalFrameContract BuildContract(GraphicsDevice gd, RenderTarget2D src)
        {
            bool upscale = PPXDepthEngine.TAAUpscaleMode;
            float ss = PPXDepthEngine.SSAA;
            var jNdc = PPXDepthEngine.TAAJitterNDC;
            return new TemporalFrameContract
            {
                Color = src,
                Velocity = PPXDepthEngine.GetVelocityTarget(),
                History = PPXDepthEngine.TAAHistory,
                // Tier is refined to the ACTUAL technique that runs in Draw (a missing technique falls back)
                Tier = TemporalHistoryState.ResolveTier.Full,
                Upscale = upscale,
                OutputWidth = gd.Viewport.Width,
                OutputHeight = gd.Viewport.Height,
                JitterDeltaUV = JitterDeltaUV,
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
            var c = BuildContract(gd, src);
            if (effect == null || !c.Ready)
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

            // Technique selection decides the tier; the SAME flag drives layout declaration and the
            // debug blit below, so a fallback (e.g. TAADebug missing from an old xnb) can never show
            // normal metadata as if it were diagnostics.
            // TAALite is a user-facing lighter option on ALL backends ("Cosmic TAA Lite" in the AA
            // dropdown — fewer fetches, no lock/evidence machinery), no longer only a GL fallback (the
            // GL warble root cause — the aliased POINT historyDepthSampler — was fixed shader-wide via
            // FetchHistoryPoint's texel-center snap, so the full technique runs on GL too).
            // TAADebug diagnoses TAA_Core's meta encode — meaningless under lite, hence the !LiteMode.
            var tech = (DebugAccum && FSOEnvironment.DirectX && !LiteMode ? effect.Techniques["TAADebug"] : null)
                       ?? (LiteMode ? effect.Techniques["TAALite"] : null)
                       ?? effect.Techniques["TAA"];
            if (tech == null) return;
            bool usingDebugTechnique = tech.Name == "TAADebug";
            c.Tier = usingDebugTechnique ? TemporalHistoryState.ResolveTier.Debug
                   : (tech.Name == "TAALite" ? TemporalHistoryState.ResolveTier.Lite
                                             : TemporalHistoryState.ResolveTier.Full);

            // Declare the layout this resolve writes. History holding a different layout (tier/debug/
            // TAAU toggle, lot<->city presenter switch) or an explicit InvalidateTAAHistory (camera cut,
            // teleport) clears it here — the resolve then runs its ordinary first-frame warmup path
            // instead of decoding another mode's bytes as motion/reactivity.
            c.History.BeginResolve(gd, c.Tier, c.Upscale);

            // blend into the current history (COLOR0) + meta (COLOR1)
            var finalTarget = gd.GetRenderTargets();
            gd.SetRenderTargets(c.History.Curr, c.History.MetaCurr);

            gd.BlendState = BlendState.Opaque;
            effect.Parameters["colorTex"]?.SetValue(c.Color);
            effect.Parameters["historyTex"]?.SetValue(c.History.Prev);
            effect.Parameters["metaHistoryTex"]?.SetValue(c.History.MetaPrev);
            effect.Parameters["velocityTex"]?.SetValue(c.Velocity);
            // InvScreenSize = output/history grid; InvColorSize = input color grid (differ under TAAU)
            effect.Parameters["InvScreenSize"]?.SetValue(new Vector2(1f / c.History.Prev.Width, 1f / c.History.Prev.Height));
            effect.Parameters["InvColorSize"]?.SetValue(new Vector2(1f / c.Color.Width, 1f / c.Color.Height));
            effect.Parameters["BlendFactor"]?.SetValue(c.BlendFactor);
            effect.Parameters["MaxAccum"]?.SetValue(c.MaxAccum);
            effect.Parameters["JitterDelta"]?.SetValue(c.JitterDeltaUV);
            effect.Parameters["DepthRejectParams"]?.SetValue(c.DepthRejectParams);
            effect.Parameters["SampleJitterUV"]?.SetValue(c.SampleJitterUV);
            effect.Parameters["VelGatePxScale"]?.SetValue(c.VelGatePxScale);
            effect.Parameters["JitterPhases"]?.SetValue(c.JitterPhases);
            UploadTunables(effect);

            effect.CurrentTechnique = tech;
            effect.CurrentTechnique.Passes[0].Apply();

            gd.SetVertexBuffer(WorldContent.GetTextureVerts(gd));
            gd.DrawPrimitives(PrimitiveType.TriangleStrip, 0, 2);

            gd.SetRenderTargets(finalTarget);
            var display = usingDebugTechnique ? c.History.MetaCurr : c.History.Curr;
            if (PPXDepthEngine.TAASkipFinalBlit)
            {
                // pre-upscale mode: publish the render-res result for the chain to feed the upscaler
                PPXDepthEngine.TAAOutput = display;
            }
            else
            {
                // copy the result to the chain's bound target (screen or next ping-pong RT)
                gd.BlendState = BlendState.Opaque;
                var sb = BlitSB(gd);
                sb.Begin(blendState: BlendState.Opaque);
                sb.Draw(display, new Rectangle(0, 0, gd.Viewport.Width, gd.Viewport.Height), Color.White);
                sb.End();
            }

            PPXDepthEngine.SwapHistory();
        }

        // ---- Tunable upload + one-time binding audit -------------------------------------------
        // Every public static float on TAATuning maps to a shader uniform: "X" -> "TuneX", except the
        // Lite* set which maps by its own name ("LiteX" -> "LiteX"). Reflection builds the binding
        // table once per loaded effect, so adding a tunable is a TWO-file change (TAATuning + TAA.fx)
        // with the upload, the missing-uniform check, and the default-drift check all automatic.
        // Null-safe by construction: a uniform absent from the loaded xnb (older artifact, or the five
        // SM4-only ones under MojoShader/OGL) is reported once and skipped thereafter.
        private static Effect _boundEffect;
        private static List<(EffectParameter param, FieldInfo field)> _tuneBindings;
        // audit results, kept for debug UIs / tests
        public static readonly List<string> BindingWarnings = new List<string>();

        private static void UploadTunables(Effect effect)
        {
            if (_boundEffect != effect)
            {
                _boundEffect = effect;
                _tuneBindings = new List<(EffectParameter, FieldInfo)>();
                BindingWarnings.Clear();
                foreach (var field in typeof(TAATuning).GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (field.FieldType != typeof(float)) continue;
                    var name = field.Name.StartsWith("Lite") ? field.Name : "Tune" + field.Name;
                    var param = effect.Parameters[name];
                    if (param == null)
                    {
                        // expected on OGL for the SM4-only uniforms (MojoShader strips #if SM4 blocks);
                        // on DirectX it means the xnb drifted from TAATuning — rebuild shaders (CI).
                        Warn($"uniform '{name}' missing from loaded TAA effect (backend: {(FSOEnvironment.DirectX ? "DX" : "OGL")})");
                        continue;
                    }
                    if (FSOEnvironment.DirectX) // MojoShader/OGL doesn't reliably surface baked initializers
                    {
                        try
                        {
                            // the baked shader literal is the fallback when a param upload is ever skipped —
                            // it must equal the canonical TAATuning value or the fallback silently retunes
                            var baked = param.GetValueSingle();
                            var canon = (float)field.GetValue(null);
                            if (System.Math.Abs(baked - canon) > 1e-4f)
                                Warn($"'{name}' baked default {baked} != TAATuning.{field.Name} {canon} — TAA.fx literal drifted; resync + recompile");
                        }
                        catch { /* can't read baked defaults; the upload below still applies */ }
                    }
                    _tuneBindings.Add((param, field));
                }
            }
            foreach (var (param, field) in _tuneBindings)
            {
                param.SetValue((float)field.GetValue(null));
            }
        }

        private static void Warn(string msg)
        {
            BindingWarnings.Add(msg);
            System.Diagnostics.Debug.WriteLine("[TAA binding audit] " + msg);
        }
    }
}
