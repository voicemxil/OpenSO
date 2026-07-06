using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FSO.Common.Utils
{
    public class PPXDepthEngine
    {
        private static GraphicsDevice GD;
        private static RenderTarget2D BackbufferDepth;
        private static RenderTarget2D Backbuffer;
        private static RenderTarget2D ResolveTarget;  //screen-res intermediate for multi-pass resolves
        private static RenderTarget2D ResolveTarget2; //2nd ping-pong target (scale -> FXAA -> sharpen needs two)
        private static RenderTarget2D RenderPostTarget; //RENDER-res intermediate: spatial AA before the upscaler (SSAA<1 only)
        private static RenderTarget2D VelocityTarget; //3D-mode per-pixel screen-space velocity (HalfVector4), MRT1 for TAA / motion blur
        private static RenderTarget2D NormalTarget; //world-space normal MRT2 (HalfVector4: .xyz normal, .a validity), for GTAO
        // motion blur tile intermediates (McGuire 2012), at velocity-res / MB_TILE_SIZE
        private static RenderTarget2D MBTileMax, MBNeighborMax;
        public const int MB_TILE_SIZE = 20;
        private static RenderTarget2D HistoryA, HistoryB; //TAA history ping-pong (RGB color, A depth)
        // TAA meta ping-pong, swapped in lock-step with History (R = accumulation count N, GB = prev normal encode)
        private static RenderTarget2D MetaA, MetaB;
        private static bool _HistoryAIsPrev; //which buffer holds last frame's TAA output (governs History + Meta)
        // true when HistoryA/B are HalfVector4; TAAResolve keys DepthRejectParams off this for the RGBA8 fallback
        public static bool HistoryIsFP16 { get; private set; }
        private static SpriteBatch SB;
        public static float SSAA = 1f; //render scale: >1 supersample (downsample resolve), <1 upscale, 1 native
        public static int MSAA = 0;
        // current frame's TAA jitter (NDC), published by World.PreDraw; lets draws without a WorldState
        // (sky dome) un-jitter their motion vectors
        public static Vector2 TAAJitterNDC = Vector2.Zero;

        // bloom mip chain (half, quarter, ... of viewport res); HalfVector4 so highlights don't clip
        public const int BLOOM_MIPS = 5;
        private static RenderTarget2D[] BloomMip;
        public static RenderTarget2D GetBloomMip(int i) => (BloomMip != null && i < BloomMip.Length) ? BloomMip[i] : null;
        public static int BloomMipCount => (BloomMip != null) ? BloomMip.Length : 0;

        // GTAO: noisy AO buffer, blur destination, temporal history ping-pong. Color format (R8 isn't universal).
        private static RenderTarget2D AOTarget, AOTarget2;
        private static RenderTarget2D AOHistoryA, AOHistoryB;
        private static bool _AOHistoryAIsPrev;
        public static RenderTarget2D GetAOTarget() => AOTarget;
        public static RenderTarget2D GetAOTarget2() => AOTarget2;
        public static RenderTarget2D GetAOHistoryPrev() => _AOHistoryAIsPrev ? AOHistoryA : AOHistoryB;
        public static RenderTarget2D GetAOHistoryCurr() => _AOHistoryAIsPrev ? AOHistoryB : AOHistoryA;
        public static void SwapAOHistory() { _AOHistoryAIsPrev = !_AOHistoryAIsPrev; }

        public static void InitGD(GraphicsDevice gd)
        {
            GD = gd;
            SB = new SpriteBatch(gd);
        }

        public static void InitScreenTargets()
        {
            if (GD == null) return;
            // clamp to hardware MSAA - a higher count than supported (e.g. 8x on Apple Silicon) black-screens
            if (MSAA > FSOEnvironment.MaxMSAA) MSAA = FSOEnvironment.MaxMSAA;
            if (BackbufferDepth != null) BackbufferDepth.Dispose();
            BackbufferDepth = null;
            if (Backbuffer != null) Backbuffer.Dispose();
            var scale = 1;//FSOEnvironment.DPIScaleFactor;
            // backbuffer is sized by the render scale (SSAA), rounded to whole pixels
            int w = System.Math.Max(1, (int)System.Math.Round(SSAA * GD.Viewport.Width / scale));
            int h = System.Math.Max(1, (int)System.Math.Round(SSAA * GD.Viewport.Height / scale));
            if (!FSOEnvironment.Enable3D)
                BackbufferDepth = CreateRenderTarget(GD, 1, MSAA, SurfaceFormat.Color, w, h, DepthFormat.None);
            Backbuffer = CreateRenderTarget(GD, 1, MSAA, SurfaceFormat.Color, w, h, DepthFormat.Depth24Stencil8);
            int rw = System.Math.Max(1, GD.Viewport.Width / scale), rh = System.Math.Max(1, GD.Viewport.Height / scale);
            if (ResolveTarget != null) ResolveTarget.Dispose();
            ResolveTarget = CreateRenderTarget(GD, 1, 0, SurfaceFormat.Color, rw, rh, DepthFormat.None);
            if (ResolveTarget2 != null) ResolveTarget2.Dispose();
            ResolveTarget2 = CreateRenderTarget(GD, 1, 0, SurfaceFormat.Color, rw, rh, DepthFormat.None);
            // render-res intermediate: spatial AA must run before the upscaler (EASU/TAAU want
            // anti-aliased input on the render grid). Only needed when SSAA < 1.
            if (RenderPostTarget != null) { RenderPostTarget.Dispose(); RenderPostTarget = null; }
            if (SSAA < 0.999f)
                RenderPostTarget = CreateRenderTarget(GD, 1, 0, SurfaceFormat.Color, w, h, DepthFormat.None);

            if (BloomMip != null) foreach (var m in BloomMip) m?.Dispose();
            BloomMip = new RenderTarget2D[BLOOM_MIPS];
            for (int i = 0; i < BLOOM_MIPS; i++)
            {
                int mw = System.Math.Max(1, rw >> (i + 1));
                int mh = System.Math.Max(1, rh >> (i + 1));
                BloomMip[i] = new RenderTarget2D(GD, mw, mh, false, SurfaceFormat.HalfVector4, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            }

            AOTarget?.Dispose();
            AOTarget2?.Dispose();
            AOHistoryA?.Dispose();
            AOHistoryB?.Dispose();
            AOTarget = new RenderTarget2D(GD, rw, rh, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            AOTarget2 = new RenderTarget2D(GD, rw, rh, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            AOHistoryA = new RenderTarget2D(GD, rw, rh, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            AOHistoryB = new RenderTarget2D(GD, rw, rh, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            _AOHistoryAIsPrev = true;

            // velocity target is (re)allocated on demand by EnableVelocityTarget
            if (VelocityTarget != null) { VelocityTarget.Dispose(); VelocityTarget = null; }
            if (MBTileMax != null) { MBTileMax.Dispose(); MBTileMax = null; }
            if (MBNeighborMax != null) { MBNeighborMax.Dispose(); MBNeighborMax = null; }
        }

        // allocate/dispose the velocity MRT (+ motion blur tile intermediates) on demand (World.ChangeAAMode)
        public static RenderTarget2D EnableVelocityTarget(bool enable)
        {
            if (!enable)
            {
                if (VelocityTarget != null) { VelocityTarget.Dispose(); VelocityTarget = null; }
                if (NormalTarget != null) { NormalTarget.Dispose(); NormalTarget = null; }
                if (MBTileMax != null) { MBTileMax.Dispose(); MBTileMax = null; }
                if (MBNeighborMax != null) { MBNeighborMax.Dispose(); MBNeighborMax = null; }
                return null;
            }
            if (Backbuffer == null) return null;
            if (VelocityTarget == null || VelocityTarget.Width != Backbuffer.Width || VelocityTarget.Height != Backbuffer.Height)
            {
                VelocityTarget?.Dispose();
                // fp16 halves the biggest bandwidth cost of the TAA path; the .b depth quantization is
                // covered by TAAResolve's disocclusion dead-zone. Restore Vector4 if AO is ever revived.
                VelocityTarget = new RenderTarget2D(GD, Backbuffer.Width, Backbuffer.Height, false, SurfaceFormat.HalfVector4, DepthFormat.None, MSAA, RenderTargetUsage.PreserveContents);
                NormalTarget?.Dispose();
                // NormalTarget not allocated - its only consumer (GTAO) is dead code, and writing it cost
                // 8 bytes/px on every velocity-aware draw. BindVelocityMRT binds 2 targets when null;
                // unbound COLOR2 writes are dropped. Re-allocate here if AO is revived.
                NormalTarget = null;
                int tw = System.Math.Max(1, (Backbuffer.Width + MB_TILE_SIZE - 1) / MB_TILE_SIZE);
                int th = System.Math.Max(1, (Backbuffer.Height + MB_TILE_SIZE - 1) / MB_TILE_SIZE);
                MBTileMax?.Dispose();
                MBNeighborMax?.Dispose();
                MBTileMax = new RenderTarget2D(GD, tw, th, false, SurfaceFormat.HalfVector4, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
                MBNeighborMax = new RenderTarget2D(GD, tw, th, false, SurfaceFormat.HalfVector4, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            }
            return VelocityTarget;
        }

        // when set, GetVelocityTarget() reports null so velocity-aware shaders use their non-velocity
        // techniques. Used by single-target renders (lot thumbnails): MRT writes to unbound slots corrupt
        // COLOR0 on ps_4_0_level_9_3.
        public static bool SuppressVelocityTarget = false;
        public static RenderTarget2D GetVelocityTarget() => SuppressVelocityTarget ? null : VelocityTarget;
        public static RenderTarget2D GetNormalTarget() => NormalTarget;
        public static RenderTarget2D GetMBTileMax() => MBTileMax;

        /// <summary>
        /// Bind color + velocity (+ normal if allocated) as MRTs for velocity-aware draws.
        /// </summary>
        public static void BindVelocityMRT(GraphicsDevice gd, RenderTarget2D velocityRT)
        {
            BindVelocityMRT(gd, Backbuffer, velocityRT);
        }
        public static void BindVelocityMRT(GraphicsDevice gd, RenderTarget2D colorRT, RenderTarget2D velocityRT)
        {
            if (NormalTarget != null) gd.SetRenderTargets(colorRT, velocityRT, NormalTarget);
            else gd.SetRenderTargets(colorRT, velocityRT);
        }
        public static RenderTarget2D GetMBNeighborMax() => MBNeighborMax;

        // Independent per-target blend for the velocity MRT: target[0] keeps the requested color blend,
        // target[1+] overwrite (velocity must not be alpha-blended). Falls back to the plain color blend
        // where independent blend is unsupported.
        private static bool? _independentBlend;
        private static readonly System.Collections.Generic.Dictionary<BlendState, BlendState> _velBlendCache
            = new System.Collections.Generic.Dictionary<BlendState, BlendState>();

        private static bool IndependentBlendSupported(GraphicsDevice gd)
        {
            if (_independentBlend.HasValue) return _independentBlend.Value;
            bool ok = false;
            try
            {
                const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var caps = typeof(GraphicsDevice).GetProperty("GraphicsCapabilities", F)?.GetValue(gd);
                var sep = caps?.GetType().GetProperty("SupportsSeparateBlendStates", F)?.GetValue(caps);
                ok = sep is bool b && b;
            }
            catch { ok = false; }
            _independentBlend = ok;
            return ok;
        }

        public static BlendState VelocityColorBlend(GraphicsDevice gd, BlendState colorBlend)
        {
            if (_velBlendCache.TryGetValue(colorBlend, out var cached)) return cached;
            BlendState result;
            if (!IndependentBlendSupported(gd))
            {
                result = colorBlend; // fallback B: correct color, slightly attenuated edge velocity
            }
            else
            {
                var bs = new BlendState { IndependentBlendEnable = true };
                bs[0].ColorSourceBlend = colorBlend.ColorSourceBlend;
                bs[0].ColorDestinationBlend = colorBlend.ColorDestinationBlend;
                bs[0].ColorBlendFunction = colorBlend.ColorBlendFunction;
                bs[0].AlphaSourceBlend = colorBlend.AlphaSourceBlend;
                bs[0].AlphaDestinationBlend = colorBlend.AlphaDestinationBlend;
                bs[0].AlphaBlendFunction = colorBlend.AlphaBlendFunction;
                bs[0].ColorWriteChannels = ColorWriteChannels.All;
                for (int i = 1; i < 4; i++) // MRT1 velocity (+ MRT2 normals if bound): opaque overwrite
                {
                    bs[i].ColorSourceBlend = Blend.One;
                    bs[i].ColorDestinationBlend = Blend.Zero;
                    bs[i].ColorBlendFunction = BlendFunction.Add;
                    bs[i].AlphaSourceBlend = Blend.One;
                    bs[i].AlphaDestinationBlend = Blend.Zero;
                    bs[i].AlphaBlendFunction = BlendFunction.Add;
                    bs[i].ColorWriteChannels = ColorWriteChannels.All;
                }
                result = bs;
            }
            _velBlendCache[colorBlend] = result;
            return result;
        }

        // TAA history ping-pong: read "prev", write "curr", SwapHistory toggles roles each frame.
        public static void EnableHistoryTargets(bool enable)
        {
            if (!enable)
            {
                if (HistoryA != null) { HistoryA.Dispose(); HistoryA = null; }
                if (HistoryB != null) { HistoryB.Dispose(); HistoryB = null; }
                if (MetaA != null) { MetaA.Dispose(); MetaA = null; }
                if (MetaB != null) { MetaB.Dispose(); MetaB = null; }
                return;
            }
            if (GD == null) return;
            // history/meta must match the surface TAA resolves on 1:1 - native normally and under TAAU,
            // render-res under FSR1 (TAA runs before the EASU upscale, see TAASkipFinalBlit). Callers must
            // set TAAUEnabled and run InitScreenTargets first.
            int w = System.Math.Max(1, GD.Viewport.Width);
            int h = System.Math.Max(1, GD.Viewport.Height);
            if (SSAA < 0.999f && !TAAUEnabled && Backbuffer != null)
            {
                w = Backbuffer.Width;
                h = Backbuffer.Height;
            }
            if (HistoryA == null || HistoryA.Width != w || HistoryA.Height != h)
            {
                HistoryA?.Dispose();
                HistoryB?.Dispose();
                MetaA?.Dispose();
                MetaB?.Dispose();
                // fp16 history: RGBA8 quantized the packed depth (false disocclusions) and stalled color
                // accumulation at one 8-bit LSB. Fall back to Color where HalfVector4 isn't renderable;
                // TAAResolve keeps the blunted reject tuning on that path.
                try
                {
                    HistoryA = new RenderTarget2D(GD, w, h, false, SurfaceFormat.HalfVector4, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
                    HistoryB = new RenderTarget2D(GD, w, h, false, SurfaceFormat.HalfVector4, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
                    HistoryIsFP16 = true;
                }
                catch
                {
                    HistoryA?.Dispose(); HistoryB?.Dispose();
                    HistoryA = new RenderTarget2D(GD, w, h, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
                    HistoryB = new RenderTarget2D(GD, w, h, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
                    HistoryIsFP16 = false;
                }
                MetaA = new RenderTarget2D(GD, w, h, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
                MetaB = new RenderTarget2D(GD, w, h, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
                _HistoryAIsPrev = true;

                // clear to defined contents so the first TAA frame can't read garbage: meta (0,127,127,0)
                // = N=0, prev-velocity ~zero, no oscillation evidence
                var prevRTs = GD.GetRenderTargets();
                var metaClear = new Color(0, 127, 127, 0);
                GD.SetRenderTarget(HistoryA); GD.Clear(Color.Transparent);
                GD.SetRenderTarget(HistoryB); GD.Clear(Color.Transparent);
                GD.SetRenderTarget(MetaA); GD.Clear(metaClear);
                GD.SetRenderTarget(MetaB); GD.Clear(metaClear);
                if (prevRTs != null && prevRTs.Length > 0) GD.SetRenderTargets(prevRTs); else GD.SetRenderTarget(null);
            }
        }
        public static RenderTarget2D GetHistoryPrev() => _HistoryAIsPrev ? HistoryA : HistoryB;
        public static RenderTarget2D GetHistoryCurr() => _HistoryAIsPrev ? HistoryB : HistoryA;
        public static RenderTarget2D GetMetaPrev() => _HistoryAIsPrev ? MetaA : MetaB;
        public static RenderTarget2D GetMetaCurr() => _HistoryAIsPrev ? MetaB : MetaA;
        public static void SwapHistory() { _HistoryAIsPrev = !_HistoryAIsPrev; }

        private static RenderTarget2D ActiveColor;
        private static RenderTarget2D ActiveDepth;
        private static int StencilValue;

        public static void SetPPXTarget(RenderTarget2D color, RenderTarget2D depth, bool clear)
        {
            SetPPXTarget(color, depth, clear, ColorExtensions.TransparentBlack);
        }

        public static void SetPPXTarget(RenderTarget2D color, RenderTarget2D depth, bool clear, Color clearColor)
        {
            if (color == null && depth == null && Backbuffer != null) color = Backbuffer;
            ActiveColor = color;
            if (color == Backbuffer && depth == null && BackbufferDepth != null) depth = BackbufferDepth;
            ActiveDepth = depth;

            //if (color != null && depth != null) depth.InheritDepthStencil(color);
            var gd = GD;
            gd.SetRenderTarget(color); //can have null subresource when switching to 2d with supersampling enabled, which is odd since the texture is not disposed
            if (clear)
            {
                StencilValue = 1;

                gd.Clear(clearColor);// FSO.Common.Rendering.Framework.GameScreen.ClearColor);
                if (depth != null)
                {
                    gd.SetRenderTarget(depth);
                    gd.Clear(Color.White);
                }
                // clear VelocityTarget once per frame; it's only bound transiently around object draws,
                // since non-velocity-aware shaders would write garbage to MRT1
                if (color == Backbuffer && VelocityTarget != null)
                {
                    gd.SetRenderTarget(VelocityTarget);
                    // (vel=0, depth=1 FAR, mask=0): unwritten pixels read as static far background.
                    // depth must be far or the motion-blur depth test treats them as foreground.
                    gd.Clear(new Color(0f, 0f, 1f, 0f));
                    if (NormalTarget != null)
                    {
                        gd.SetRenderTarget(NormalTarget);
                        // up-vector default + invalid mask (GTAO treats alpha<0.5 as no-geometry)
                        gd.Clear(new Color(0.5f, 1f, 0.5f, 0f));
                    }
                    gd.SetRenderTarget(color);
                }
            }
            if (FSOEnvironment.UseMRT)
            {
                if (depth != null) gd.SetRenderTargets(color, depth);
            }
        }

        public static RenderTarget2D GetBackbuffer()
        {
            return Backbuffer;
        }

        public delegate void RenderPPXProcedureDelegate(bool depthPass);
        public static void RenderPPXDepth(Effect effect, bool forceDepth,
            RenderPPXProcedureDelegate proc)
        {
            var color = ActiveColor;
            var depth = ActiveDepth;
            var gd = GD;
            if (FSOEnvironment.SoftwareDepth && depth != null)
            {
                var oldDS = gd.DepthStencilState;
                //completely special case.
                gd.SetRenderTarget(color);
                gd.DepthStencilState = new DepthStencilState
                {
                    StencilEnable = true,
                    StencilFunction = CompareFunction.Always,
                    StencilFail = StencilOperation.Keep,
                    StencilPass = StencilOperation.Replace,
                    CounterClockwiseStencilPass = StencilOperation.Replace,
                    StencilDepthBufferFail = StencilOperation.Keep,
                    DepthBufferEnable = forceDepth, //(ActiveColor == null),
                    DepthBufferWriteEnable = forceDepth, //(ActiveColor == null),
                    ReferenceStencil = StencilValue,
                    TwoSidedStencilMode = true
                };
                effect.Parameters["depthMap"].SetValue(depth);
                effect.Parameters["depthOutMode"].SetValue(false);
                proc(false);

                //now draw the depth using the depth test information we got previously.

                //unbind depth map since we are writing to it
                effect.Parameters["depthMap"].SetValue((Texture2D)null);
                effect.Parameters["depthOutMode"].SetValue(true);
                gd.SetRenderTarget(depth);
                gd.DepthStencilState = new DepthStencilState
                {
                    StencilEnable = true,
                    StencilFunction = CompareFunction.Equal,
                    DepthBufferEnable = forceDepth,
                    DepthBufferWriteEnable = forceDepth,
                    ReferenceStencil = StencilValue,
                };
                proc(true);

                gd.DepthStencilState = oldDS;
                StencilValue++; //can increment up to 254 times. Assume we're not going to be rendering that much between clears.
                if (StencilValue > 255) StencilValue = 1;
                gd.SetRenderTarget(color);
                effect.Parameters["depthOutMode"].SetValue(false);
            }
            else if (!FSOEnvironment.UseMRT && depth != null)
            {
                //draw color then draw depth
                gd.SetRenderTarget(color);
                proc(false);
                effect.Parameters["depthOutMode"].SetValue(true);
                gd.SetRenderTarget(depth);
                proc(true);
                effect.Parameters["depthOutMode"].SetValue(false);
            }
            else
            {
                //mrt already bound. draw in both.
                proc(false);
            }
        }

        public static Action<GraphicsDevice, RenderTarget2D> SSAAFunc;
        // velocity debug visualizer: when non-null, DrawBackbuffer bypasses the post chain and draws raw MRT1
        public static Action<GraphicsDevice, RenderTarget2D> VelocityDebugFunc;
        // per-pixel motion blur (3D); runs before post-AA so FXAA/SMAA smooth the blurred edges. null = off
        public static Action<GraphicsDevice, RenderTarget2D> MotionBlurFunc;
        // post-process resolve (FXAA/SMAA/FSR); runs even when SSAA==1. null = plain blit (no behaviour change)
        public static Action<GraphicsDevice, RenderTarget2D> PostProcessFunc;
        // temporal AA; its own stage AFTER spatial post-AA, not in place of it. null = off
        public static Action<GraphicsDevice, RenderTarget2D> TAAFunc;
        // TAA pre-upscale (FSR1: TAA -> EASU -> RCAS). EASU needs anti-aliased input, so at scale < 1
        // DrawBackbuffer sets TAASkipFinalBlit; the resolve skips its stretch blit and publishes the
        // render-res result via TAAOutput for the upscaler.
        public static bool TAASkipFinalBlit;
        public static RenderTarget2D TAAOutput;
        // TAAU: the TAA resolve IS the upscaler (replaces EASU at render scale < 1). Must be set BEFORE
        // EnableHistoryTargets so history/meta size to the native grid.
        public static bool TAAUEnabled;
        // negative texture LOD bias under TAA at render scale < 1 (log2(scale), clamped -2; 0 otherwise).
        // Set by World.ChangeAAMode/ConfigureCityAA; pushed by every velocity-technique param push.
        public static float TAAMipBias;
        // true only while DrawBackbuffer invokes TAAFunc as the upscaler stage (TAAResolve binds native history)
        public static bool TAAUpscaleMode;
        // ambient occlusion (GTAO); runs before bloom. null = off
        public static Action<GraphicsDevice, RenderTarget2D> AOFunc;
        // bloom; runs after post-AA, before sharpen. null = off
        public static Action<GraphicsDevice, RenderTarget2D> BloomFunc;
        // final sharpen (FSR RCAS); writes the screen. null = off
        public static Action<GraphicsDevice, Texture2D> SharpenFunc;
        public static bool WithOpacity = true;

        public static void DrawBackbuffer(float opacity, float scale)
        {
            if (Backbuffer == null) return; //this gfx mode does not use a rendertarget backbuffer
            // velocity-debug override: skip the whole chain and visualize MRT1 instead
            if (VelocityDebugFunc != null && VelocityTarget != null && scale == 1f && (!WithOpacity || opacity >= 1f))
            {
                GD.SetRenderTarget(null);
                VelocityDebugFunc(GD, Backbuffer);
                return;
            }
            bool nonNative = (SSAA > 1.001f || SSAA < 0.999f);
            // post stages only run outside fade/zoom transitions (those use the alpha blit below)
            bool postOk = scale == 1f && (!WithOpacity || opacity >= 1f);
            bool doMotionBlur = MotionBlurFunc != null && postOk;
            bool doPost = PostProcessFunc != null && postOk;
            bool doTAA = TAAFunc != null && postOk;
            bool doAO = AOFunc != null && AOTarget != null && AOTarget2 != null && AOHistoryA != null && AOHistoryB != null && VelocityTarget != null && postOk;
            bool doBloom = BloomFunc != null && ResolveTarget != null && ResolveTarget2 != null && postOk;
            bool doSharpen = SharpenFunc != null && ResolveTarget != null && ResolveTarget2 != null && postOk;

            if (nonNative || doMotionBlur || doPost || doTAA || doAO || doBloom || doSharpen)
            {
                // resolve chain: scale-resolve -> motion blur -> post-AA -> TAA -> AO -> bloom -> sharpen.
                // Intermediates ping-pong between the two ResolveTargets; the last active stage targets the screen.
                RenderTarget2D src = Backbuffer;

                // upscaling + TAA: TAAU = the TAA resolve is the upscaler (runs in the nonNative slot);
                // FSR1 (taaFirst) = TAA runs first at render-res, then EASU upscales
                bool taau = doTAA && SSAA < 0.999f && TAAUEnabled;
                bool taaFirst = doTAA && SSAA < 0.999f && !taau;

                // under TAAU the doTAA slot never runs (the resolve occupies the nonNative slot) - counting
                // it would leave `remaining` above zero and the last real stage would miss the screen
                int remaining = (nonNative ? 1 : 0) + (doMotionBlur ? 1 : 0) + (doPost ? 1 : 0) + ((doTAA && !taau) ? 1 : 0) + (doAO ? 1 : 0) + (doBloom ? 1 : 0) + (doSharpen ? 1 : 0);
                int pong = 0;

                // when upscaling, spatial AA runs first at render-res (EASU/TAAU want anti-aliased input)
                bool postFirst = doPost && SSAA < 0.999f && RenderPostTarget != null;
                if (postFirst)
                {
                    remaining--;
                    GD.SetRenderTarget(RenderPostTarget);
                    PostProcessFunc(GD, src);
                    src = RenderPostTarget;
                }

                if (taaFirst)
                {
                    remaining--;
                    TAASkipFinalBlit = true;
                    TAAOutput = null;
                    TAAFunc(GD, src);
                    TAASkipFinalBlit = false;
                    if (TAAOutput != null) { src = TAAOutput; TAAOutput = null; }
                }

                if (nonNative)
                {
                    remaining--;
                    var dst = (remaining == 0) ? null : ((pong++ % 2 == 0) ? ResolveTarget : ResolveTarget2);
                    GD.SetRenderTarget(dst);
                    if (taau)
                    {
                        // TAAU: src is render-res, output/history native. When a later stage follows,
                        // skip the blit and hand it the history target directly.
                        TAAUpscaleMode = true;
                        if (dst != null)
                        {
                            TAASkipFinalBlit = true;
                            TAAOutput = null;
                            TAAFunc(GD, src);
                            TAASkipFinalBlit = false;
                            TAAUpscaleMode = false;
                            if (TAAOutput != null) { src = TAAOutput; TAAOutput = null; }
                        }
                        else
                        {
                            TAAFunc(GD, src);
                            TAAUpscaleMode = false;
                            src = dst;
                        }
                    }
                    else { SSAAFunc(GD, src); src = dst; }
                }
                if (doMotionBlur)
                {
                    remaining--;
                    var dst = (remaining == 0) ? null : ((pong++ % 2 == 0) ? ResolveTarget : ResolveTarget2);
                    GD.SetRenderTarget(dst);
                    MotionBlurFunc(GD, src);
                    src = dst;
                }
                if (doPost && !postFirst)
                {
                    remaining--;
                    var dst = (remaining == 0) ? null : ((pong++ % 2 == 0) ? ResolveTarget : ResolveTarget2);
                    GD.SetRenderTarget(dst);
                    PostProcessFunc(GD, src);
                    src = dst;
                }
                if (doTAA && !taaFirst && !taau)
                {
                    remaining--;
                    var dst = (remaining == 0) ? null : ((pong++ % 2 == 0) ? ResolveTarget : ResolveTarget2);
                    if (dst != null)
                    {
                        // a later stage consumes the result - let it read the history target directly
                        // instead of paying a fullscreen copy
                        TAASkipFinalBlit = true;
                        TAAOutput = null;
                        TAAFunc(GD, src);
                        TAASkipFinalBlit = false;
                        if (TAAOutput != null) { src = TAAOutput; TAAOutput = null; }
                    }
                    else
                    {
                        // TAA is the final stage: the resolve's blit is the only way onto the screen
                        GD.SetRenderTarget(null);
                        TAAFunc(GD, src);
                        src = null;
                    }
                }
                if (doAO)
                {
                    remaining--;
                    var dst = (remaining == 0) ? null : ((pong++ % 2 == 0) ? ResolveTarget : ResolveTarget2);
                    GD.SetRenderTarget(dst);
                    AOFunc(GD, src);
                    src = dst;
                }
                if (doBloom)
                {
                    remaining--;
                    var dst = (remaining == 0) ? null : ((pong++ % 2 == 0) ? ResolveTarget : ResolveTarget2);
                    GD.SetRenderTarget(dst);
                    BloomFunc(GD, src);
                    src = dst;
                }
                if (doSharpen)
                {
                    GD.SetRenderTarget(null);
                    SharpenFunc(GD, src);
                }

                return;
            }

            {
                if (!WithOpacity)
                {
                    SB.Begin(blendState: BlendState.Opaque);
                    opacity = 1;
                }
                else
                    SB.Begin(blendState: BlendState.AlphaBlend);
                SB.Draw(Backbuffer, new Vector2(Backbuffer.Width * (1 - scale) / 2, Backbuffer.Height * (1 - scale) / 2), null, Color.White * opacity, 0f, new Vector2(), scale,
                    SpriteEffects.None, 0);
                SB.End();
            }
        }

        public static Point GetWidthHeight()
        {
            return new Point(Backbuffer.Width, Backbuffer.Height);
        }

        public static RenderTarget2D CreateRenderTarget(GraphicsDevice device, int numberLevels, int multisample, SurfaceFormat surface, int width, int height, DepthFormat dformat)
        {
            //apparently in xna4, there is no way to check device format... (it looks for the closest format if desired is not supported) need to look into if this affects anything.

            /*MultiSampleType type = device.PresentationParameters.MultiSampleType;

            // If the card can't use the surface format
            if (!GraphicsAdapter.DefaultAdapter.CheckDeviceFormat(
                DeviceType.Hardware,
                GraphicsAdapter.DefaultAdapter.CurrentDisplayMode.Format,
                TextureUsage.None,
                QueryUsages.None,
                ResourceType.RenderTarget,
                surface))
            {
                // Fall back to current display format
                surface = device.DisplayMode.Format;
            }
            // Or it can't accept that surface format 
            // with the current AA settings
            else if (!GraphicsAdapter.DefaultAdapter.CheckDeviceMultiSampleType(
                DeviceType.Hardware, surface,
                device.PresentationParameters.IsFullScreen, type))
            {
                // Fall back to no antialiasing
                type = MultiSampleType.None;
            }*/

            /*int width, height;

            // See if we can use our buffer size as our texture
            CheckTextureSize(device.PresentationParameters.BackBufferWidth,
                device.PresentationParameters.BackBufferHeight,
                out width, out height);*/

            // Create our render target
            return new RenderTarget2D(device,
                width, height, (numberLevels > 1), surface,
                DepthFormat.Depth24Stencil8, multisample, RenderTargetUsage.PreserveContents);
        }
    }
}
