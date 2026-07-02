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
        // World-space normal (HalfVector4: .xyz normal, .a validity). MRT2, written by the same velocity-
        // aware shaders. Required for GTAO — derived ddx/ddy normals from NDC depth were noisy garbage.
        private static RenderTarget2D NormalTarget;
        // Motion-blur reconstruction-filter intermediates (McGuire 2012). Allocated alongside the velocity
        // target. TileMax reduces velocity to KxK tiles; NeighborMax dilates it 3x3 so fast streaks reach
        // neighbouring tiles. Both at velocity-res / MB_TILE_SIZE.
        private static RenderTarget2D MBTileMax, MBNeighborMax;
        public const int MB_TILE_SIZE = 20;
        private static RenderTarget2D HistoryA, HistoryB; //TAA history ping-pong (screen-res RGBA8: RGB color, A depth)
        // TAA meta ping-pong (screen-res RGBA8), swapped in lock-step with History. R = per-pixel accumulation
        // count N (normalized N/MAXN) that drives a variable blend rate for deep convergence + instant reset;
        // GB = octahedral-encoded previous-frame world normal for normal-based disocclusion rejection.
        private static RenderTarget2D MetaA, MetaB;
        private static bool _HistoryAIsPrev; //which buffer holds last frame's TAA output (governs History + Meta)
        // True when HistoryA/B were allocated as HalfVector4 (fp16). TAAResolve keys the shader's
        // DepthRejectParams off this so the RGBA8 fallback keeps the old quantization-blunted tuning.
        public static bool HistoryIsFP16 { get; private set; }
        private static SpriteBatch SB;
        public static float SSAA = 1f; //render scale: >1 supersample (downsample resolve), <1 upscale, 1 native
        public static int MSAA = 0;
        // Current frame's TAA sub-pixel jitter (NDC), published by World.PreDraw. Lets velocity-pass draws
        // that lack a WorldState (the sky dome) un-jitter their motion vectors. Zero when TAA is off.
        public static Vector2 TAAJitterNDC = Vector2.Zero;

        // Bloom mip chain (half, quarter, ... of viewport res). HalfVector4 so blurred highlights don't
        // clip while accumulating. Allocated in InitScreenTargets, used by BloomPass.
        public const int BLOOM_MIPS = 5;
        private static RenderTarget2D[] BloomMip;
        public static RenderTarget2D GetBloomMip(int i) => (BloomMip != null && i < BloomMip.Length) ? BloomMip[i] : null;
        public static int BloomMipCount => (BloomMip != null) ? BloomMip.Length : 0;

        // GTAO: noisy single-frame AO buffer + a filter pong (cross-bilateral blur destination) + a
        // temporal history ping-pong. SurfaceFormat.Color (R8 isn't universal).
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
            // Clamp to what the GPU can actually resolve. Selecting (or restoring) a higher count than the
            // hardware supports — e.g. 8x on Apple Silicon, which caps at 4x — produces a black screen. The
            // 2D supersample-fold path also forces MSAA up to 8; this catches that too.
            if (MSAA > FSOEnvironment.MaxMSAA) MSAA = FSOEnvironment.MaxMSAA;
            if (BackbufferDepth != null) BackbufferDepth.Dispose();
            BackbufferDepth = null;
            if (Backbuffer != null) Backbuffer.Dispose();
            var scale = 1;//FSOEnvironment.DPIScaleFactor;
            // Backbuffer is sized by the render scale (SSAA). Float scale -> round to whole pixels, min 1.
            int w = System.Math.Max(1, (int)System.Math.Round(SSAA * GD.Viewport.Width / scale));
            int h = System.Math.Max(1, (int)System.Math.Round(SSAA * GD.Viewport.Height / scale));
            if (!FSOEnvironment.Enable3D)
                BackbufferDepth = CreateRenderTarget(GD, 1, MSAA, SurfaceFormat.Color, w, h, DepthFormat.None);
            Backbuffer = CreateRenderTarget(GD, 1, MSAA, SurfaceFormat.Color, w, h, DepthFormat.Depth24Stencil8);
            // Screen-res intermediate (no MSAA) used to chain a sharpen pass after the scale/post-AA resolve.
            int rw = System.Math.Max(1, GD.Viewport.Width / scale), rh = System.Math.Max(1, GD.Viewport.Height / scale);
            if (ResolveTarget != null) ResolveTarget.Dispose();
            ResolveTarget = CreateRenderTarget(GD, 1, 0, SurfaceFormat.Color, rw, rh, DepthFormat.None);
            if (ResolveTarget2 != null) ResolveTarget2.Dispose();
            ResolveTarget2 = CreateRenderTarget(GD, 1, 0, SurfaceFormat.Color, rw, rh, DepthFormat.None);
            // RENDER-res intermediate for the upscaling path: spatial AA (FXAA/SMAA) must run at RENDER
            // resolution BEFORE the upscaler (AMD FSR1 guideline — EASU/TAAU want anti-aliased input on the
            // render grid; running them post-upscale smooths already-reconstructed pixels instead of the
            // real edges). Only needed when SSAA < 1.
            if (RenderPostTarget != null) { RenderPostTarget.Dispose(); RenderPostTarget = null; }
            if (SSAA < 0.999f)
                RenderPostTarget = CreateRenderTarget(GD, 1, 0, SurfaceFormat.Color, w, h, DepthFormat.None);

            // Bloom mip chain: half, quarter, ... of viewport res. HalfVector4 keeps highlights from clipping.
            if (BloomMip != null) foreach (var m in BloomMip) m?.Dispose();
            BloomMip = new RenderTarget2D[BLOOM_MIPS];
            for (int i = 0; i < BLOOM_MIPS; i++)
            {
                int mw = System.Math.Max(1, rw >> (i + 1));
                int mh = System.Math.Max(1, rh >> (i + 1));
                BloomMip[i] = new RenderTarget2D(GD, mw, mh, false, SurfaceFormat.HalfVector4, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            }

            // GTAO targets: SurfaceFormat.Color (R8 isn't universal). Four screen-res targets — noisy AO,
            // depth-aware spatial blur, and a temporal history ping-pong (absorbs the per-frame variation
            // from TAA-jittered depth/normals so AO doesn't flicker).
            AOTarget?.Dispose();
            AOTarget2?.Dispose();
            AOHistoryA?.Dispose();
            AOHistoryB?.Dispose();
            AOTarget = new RenderTarget2D(GD, rw, rh, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            AOTarget2 = new RenderTarget2D(GD, rw, rh, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            AOHistoryA = new RenderTarget2D(GD, rw, rh, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            AOHistoryB = new RenderTarget2D(GD, rw, rh, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            _AOHistoryAIsPrev = true;

            // Per-pixel screen-space velocity for TAA / motion blur. Only meaningful in 3D mode (the 2D path
            // is cached sprites with no per-object motion). HalfVector4: 2 channels for velocity is enough,
            // but the format gives float precision needed for reprojection accuracy. Allocated lazily by
            // EnableVelocityTarget so the cost (~16MB at 1080p) is opt-in.
            if (VelocityTarget != null) { VelocityTarget.Dispose(); VelocityTarget = null; }
            if (MBTileMax != null) { MBTileMax.Dispose(); MBTileMax = null; }
            if (MBNeighborMax != null) { MBNeighborMax.Dispose(); MBNeighborMax = null; }
        }

        // Allocate / dispose the velocity MRT (+ motion-blur tile intermediates) on demand. Engine binds
        // the velocity target as MRT1 alongside the backbuffer when this returns non-null. The caller
        // (World.ChangeAAMode) tracks whether TAA / motion blur are requested and which mode the world is in.
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
                // Vector4 (full 32-bit float per channel), NOT HalfVector4: the .b channel stores linear view
                // depth for the SSAO, and 16-bit half only has ~10 mantissa bits -> the depth quantizes to
                // visible steps that the AO depth-compare turns into banding/false occlusion. 32-bit float is
                // the proper deferred-depth precision (MonoGame can't bind the hardware depth buffer as a
                // texture, so linear depth lives in a colour target). .rg velocity also benefits.
                VelocityTarget = new RenderTarget2D(GD, Backbuffer.Width, Backbuffer.Height, false, SurfaceFormat.Vector4, DepthFormat.None, MSAA, RenderTargetUsage.PreserveContents);
                NormalTarget?.Dispose();
                NormalTarget = new RenderTarget2D(GD, Backbuffer.Width, Backbuffer.Height, false, SurfaceFormat.HalfVector4, DepthFormat.None, MSAA, RenderTargetUsage.PreserveContents);
                // Tile targets: ceil(res / K). Reallocated here whenever the velocity target is (re)sized.
                int tw = System.Math.Max(1, (Backbuffer.Width + MB_TILE_SIZE - 1) / MB_TILE_SIZE);
                int th = System.Math.Max(1, (Backbuffer.Height + MB_TILE_SIZE - 1) / MB_TILE_SIZE);
                MBTileMax?.Dispose();
                MBNeighborMax?.Dispose();
                MBTileMax = new RenderTarget2D(GD, tw, th, false, SurfaceFormat.HalfVector4, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
                MBNeighborMax = new RenderTarget2D(GD, tw, th, false, SurfaceFormat.HalfVector4, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            }
            return VelocityTarget;
        }

        // When set, GetVelocityTarget() reports "no velocity target" so every velocity-aware shader
        // (RCObject / Vitaboy / Grass / Wall) falls back to its single-output, non-velocity technique.
        // Used by the 3D lot-thumbnail render (WorldPlatform3D.GetLotThumb): it binds a single colour
        // target, so writing the velocity (COLOR1) / normal (COLOR2) MRT outputs into unbound slots
        // corrupts COLOR0 to opaque black on ps_4_0_level_9_3 -- the black thumbnail backdrop in 3D mode.
        public static bool SuppressVelocityTarget = false;
        public static RenderTarget2D GetVelocityTarget() => SuppressVelocityTarget ? null : VelocityTarget;
        public static RenderTarget2D GetNormalTarget() => NormalTarget;
        public static RenderTarget2D GetMBTileMax() => MBTileMax;

        /// <summary>
        /// Bind the backbuffer + velocity MRT (+ normal MRT if allocated) as MRTs for velocity-aware
        /// draws. All velocity-aware shaders write COLOR2 normal, so when the normal target exists it
        /// must be bound or the GPU writes garbage to MRT2.
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

        // --- Independent per-target blend for the velocity MRT --------------------------------------------
        // When color (MRT0) and velocity (MRT1) are written together, ONE blend state normally governs both.
        // Color needs alpha blending (the surrounding-lots fade + sky atmospheric blend), but velocity must
        // OVERWRITE — alpha-blending it dims/corrupts it at transparent edges. The old code forced
        // BlendState.Opaque to get clean velocity, which broke the color fade and brightened the far terrain
        // and sky. MonoGame supports independent per-target blend on GL 4.0+ / GL_ARB_draw_buffers_blend:
        // VelocityColorBlend builds target[0] = the requested color blend, target[1] = opaque velocity.
        // On older GPUs (where setting IndependentBlendEnable throws) it falls back to the plain color blend
        // (color correct; only cost is slightly attenuated velocity at transparent fade edges).
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

        // TAA history ping-pong. Each frame TAA reads from "prev" and writes to "curr", then SwapHistory
        // toggles roles for the next frame.
        //
        // Sizing: matches the surface TAA resolves on — the viewport normally (TAA runs after the SSAA
        // scale-resolve), but the RENDER-res Backbuffer when upscaling (SSAA < 1), where TAA now runs
        // BEFORE the EASU upscale (AMD FSR1 pipeline; see TAASkipFinalBlit / DrawBackbuffer).
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
            // TAA operates at the resolution of the surface it RESOLVES ON: NATIVE (viewport) normally and
            // under Cosmic TAAU (where the resolve is the upscaler and history is the native output grid),
            // but RENDER resolution when upscaling via FSR 1 (TAA runs BEFORE the EASU upscale — see
            // TAASkipFinalBlit). History/meta must match that surface 1:1. Callers (ChangeAAMode /
            // ConfigureCityAA) invoke this after InitScreenTargets, so Backbuffer is already (re)sized,
            // and set TAAUEnabled beforehand.
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
                // History wants fp16: the RGBA8 history was the TAA's double root cause — the packed depth in
                // alpha was 1/255-quantized (false depth-disocclusions at silhouettes; the reject curve had to
                // be blunted to hide it) and color accumulation stalled once per-frame deltas dropped below
                // one 8-bit LSB (convergence plateau ~N=32). fp16 fixes both; fall back to Color on hardware
                // that can't render to HalfVector4 — the shader's DepthRejectParams uniform keeps the old
                // blunted tuning on the fallback path (see TAAResolve).
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

                // Clear to defined contents so the first TAA frame can't read uninitialized GPU garbage. This
                // matters most for the META target: garbage could decode as a high accumulation count N and
                // make the resolve trust the (also-garbage) history heavily for a frame — a nondeterministic
                // bright/ghost flash on TAA-enable. History clears to transparent black (depth 0); meta clears
                // to (0,127,127,0) = N=0, GB prev-velocity decoding to ~zero (127/255 -> -0.0002 UV) so the
                // disparity reactive starts silent, and A=0 = zero luma-oscillation evidence (the anti-fizzle
                // gate starts untrusting and must earn ~10 consecutive alternations before deepening).
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
                // Clear VelocityTarget once per frame (when we're starting a fresh frame on the main
                // Backbuffer). It's bound transiently around the object draws in WorldEntities, not for the
                // whole 3D render — that's the only way to stop non-velocity-aware shaders from writing
                // garbage to MRT1 (level_9_3 hardware doesn't reliably preserve unwritten MRT slots).
                if (color == Backbuffer && VelocityTarget != null)
                {
                    gd.SetRenderTarget(VelocityTarget);
                    // Clear to (vel=0, depth=1 FAR, mask=0): unwritten pixels (sky, distant trees, anything
                    // without a velocity-aware shader) read as static far background. Depth MUST be far, not
                    // 0 — a 0 (near) clear would make the motion-blur depth test treat the empty background
                    // as foreground in front of moving objects and break the silhouette weighting.
                    gd.Clear(new Color(0f, 0f, 1f, 0f));
                    if (NormalTarget != null)
                    {
                        gd.SetRenderTarget(NormalTarget);
                        // Up-vector default + invalid mask. GTAO treats alpha<0.5 as no-geometry.
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
        // Diagnostic velocity visualizer. When non-null, DrawBackbuffer bypasses the entire post chain
        // and draws this directly to screen so the user can see raw MRT1 contents — useful for finding
        // which shaders are writing valid velocity and which aren't.
        public static Action<GraphicsDevice, RenderTarget2D> VelocityDebugFunc;
        // Optional per-pixel motion blur pass (3D). Reads color + the velocity MRT; sits BEFORE post-AA so
        // FXAA/SMAA smooth the blurred edges. null = off.
        public static Action<GraphicsDevice, RenderTarget2D> MotionBlurFunc;
        // Optional post-process resolve (FXAA/SMAA/FSR). Runs even when SSAA==1. null = disabled, in which
        // case DrawBackbuffer keeps the plain blit below, so there's zero behaviour change when AA is off.
        public static Action<GraphicsDevice, RenderTarget2D> PostProcessFunc;
        // Optional temporal AA (TAA). Its OWN chain stage, applied AFTER the spatial post-AA (FXAA/SMAA)
        // rather than in place of it — TAA temporally stabilizes the already edge-smoothed frame. Screen-res
        // in/out (same slot timing as PostProcessFunc). null = off.
        public static Action<GraphicsDevice, RenderTarget2D> TAAFunc;
        // TAA pre-upscale mode (render scale < 1): TAA runs at RENDER resolution BEFORE the EASU upscale —
        // AMD's documented FSR1 pipeline (TAA -> EASU -> RCAS). EASU is an edge-adaptive NONLINEAR upscaler
        // specified for anti-aliased input; feeding it the raw jittered frame made it re-detect and re-draw
        // edges differently every frame, so the TAA that ran after it received a shape-morphing, DOUBLE-
        // amplitude wobble (±0.5 render px = ±1 native px at 0.5 scale) it could never stabilise — the
        // "persistent jitter in the resolved output at low res". DrawBackbuffer sets TAASkipFinalBlit before
        // invoking TAAFunc; the resolve renders into its (render-res) history target, skips the stretch blit,
        // and publishes the resolved frame via TAAOutput for the chain to feed into the upscaler.
        public static bool TAASkipFinalBlit;
        public static RenderTarget2D TAAOutput;
        // Cosmic TAAU: the TAA resolve IS the upscaler (replaces EASU when render scale < 1). Set from
        // WorldConfig (TAA on + Upscaler == TAAU) BEFORE EnableHistoryTargets so history/meta size to the
        // NATIVE grid; the resolve then accumulates jittered render-res samples directly onto it — detail
        // beyond render resolution emerges from the sample positions (the supersampled-like resolve).
        public static bool TAAUEnabled;
        // True only while DrawBackbuffer invokes TAAFunc as the upscaler stage; TAAResolve reads it to bind
        // native-size history + set InvColorSize (render) vs InvScreenSize (native).
        public static bool TAAUpscaleMode;
        // Optional ambient-occlusion pass (GTAO). Sits BEFORE bloom in the chain so AO darkens crevices
        // before bloom adds highlights — the standard order. Reads the velocity buffer for depth + scene
        // color for the composite, writes scene*AO to the bound target. null = off.
        public static Action<GraphicsDevice, RenderTarget2D> AOFunc;
        // Optional bloom pass. Reads the current chain color, blooms it into its own mip chain, composites
        // scene+bloom to the bound target. Sits after post-AA (blooms the AA'd image), before sharpen. null = off.
        public static Action<GraphicsDevice, RenderTarget2D> BloomFunc;
        // Optional final sharpening pass (FSR RCAS). Reads the resolved frame and writes the screen. null = off.
        public static Action<GraphicsDevice, Texture2D> SharpenFunc;
        public static bool WithOpacity = true;

        public static void DrawBackbuffer(float opacity, float scale)
        {
            if (Backbuffer == null) return; //this gfx mode does not use a rendertarget backbuffer
            // Velocity-debug override: when on, ditch the whole chain and visualize MRT1 instead. The
            // visualizer reads VelocityTarget directly so the `src` param is unused but kept for shape.
            if (VelocityDebugFunc != null && VelocityTarget != null && scale == 1f && (!WithOpacity || opacity >= 1f))
            {
                GD.SetRenderTarget(null);
                VelocityDebugFunc(GD, Backbuffer);
                return;
            }
            bool nonNative = (SSAA > 1.001f || SSAA < 0.999f);
            // Post-AA / motion blur / sharpen run only outside fade/zoom transitions (those use the alpha blit below).
            bool postOk = scale == 1f && (!WithOpacity || opacity >= 1f);
            bool doMotionBlur = MotionBlurFunc != null && postOk;
            bool doPost = PostProcessFunc != null && postOk;
            bool doTAA = TAAFunc != null && postOk;
            bool doAO = AOFunc != null && AOTarget != null && AOTarget2 != null && AOHistoryA != null && AOHistoryB != null && VelocityTarget != null && postOk;
            bool doBloom = BloomFunc != null && ResolveTarget != null && ResolveTarget2 != null && postOk;
            bool doSharpen = SharpenFunc != null && ResolveTarget != null && ResolveTarget2 != null && postOk;

            if (nonNative || doMotionBlur || doPost || doTAA || doAO || doBloom || doSharpen)
            {
                // Ordered resolve chain: scale-resolve (box/EASU) -> motion blur -> post-AA (FXAA/SMAA) ->
                // TAA -> AO -> bloom -> sharpen (RCAS). Each stage samples the previous stage's result and
                // draws full-screen; intermediates ping-pong between the two screen-res ResolveTargets, and
                // the last active stage targets the screen. TAA runs AFTER FXAA/SMAA (temporal pass over the
                // spatially-AA'd frame), not in their place.
                RenderTarget2D src = Backbuffer;
                int remaining = (nonNative ? 1 : 0) + (doMotionBlur ? 1 : 0) + (doPost ? 1 : 0) + (doTAA ? 1 : 0) + (doAO ? 1 : 0) + (doBloom ? 1 : 0) + (doSharpen ? 1 : 0);
                int pong = 0;

                // UPSCALING (+TAA), two modes:
                //  * Cosmic TAAU (taau): the TAA resolve IS the upscaler — it runs in the nonNative stage
                //    slot below, accumulating render-res jittered samples onto the NATIVE history grid.
                //  * FSR 1 (taaFirst): TAA runs FIRST at render resolution on the raw jittered backbuffer,
                //    then EASU upscales the anti-aliased result (AMD FSR1 order; see TAASkipFinalBlit doc).
                //    remaining stays >= 1 here (nonNative still pending), so TAA never targets the screen.
                bool taau = doTAA && SSAA < 0.999f && TAAUEnabled;
                bool taaFirst = doTAA && SSAA < 0.999f && !taau;

                // UPSCALING: spatial AA (FXAA/SMAA) runs FIRST, at RENDER resolution — before the TAA stage
                // (same relative order as the native chain: spatial then temporal) and before the upscaler
                // (EASU/TAAU want anti-aliased input on the render grid; post-upscale FXAA/SMAA smooth
                // already-reconstructed pixels instead of the real edges).
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
                        // Cosmic TAAU occupies the resolve slot (stage counting unchanged): src is the
                        // render-res jittered frame; output/history are native.
                        TAAUpscaleMode = true;
                        TAAFunc(GD, src);
                        TAAUpscaleMode = false;
                    }
                    else SSAAFunc(GD, src);
                    src = dst;
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
                    GD.SetRenderTarget(dst);
                    TAAFunc(GD, src);
                    src = dst;
                }
                if (doAO)
                {
                    remaining--;
                    var dst = (remaining == 0) ? null : ((pong++ % 2 == 0) ? ResolveTarget : ResolveTarget2);
                    GD.SetRenderTarget(dst);
                    AOFunc(GD, src); // GTAO -> blur -> composite scene*ao to dst
                    src = dst;
                }
                if (doBloom)
                {
                    remaining--;
                    var dst = (remaining == 0) ? null : ((pong++ % 2 == 0) ? ResolveTarget : ResolveTarget2);
                    GD.SetRenderTarget(dst);
                    BloomFunc(GD, src); // blooms into its own mips, then composites scene+bloom to dst
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
