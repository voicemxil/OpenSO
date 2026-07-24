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
        private static RenderTarget2D VelocityTarget; //3D-mode per-pixel screen-space velocity, MRT1 for TAA / motion blur / AO depth
        private static RenderTarget2D NormalTarget; //world-space normal MRT2 (HalfVector4: .xyz normal, .a = 0 invalid else 0.5+0.5*ambient light fraction), for SSAO
        // motion blur tile intermediates (McGuire 2012), at velocity-res / MB_TILE_SIZE
        private static RenderTarget2D MBTileMax, MBNeighborMax;
        public const int MB_TILE_SIZE = 20;
        // TAA history/meta ping-pong + explicit layout/invalidation state (see TemporalHistoryState).
        // The Get*/Swap/EnableHistoryTargets members below are thin forwards kept for existing callers.
        public static readonly TemporalHistoryState TAAHistory = new TemporalHistoryState();
        // true when history is HalfVector4; TAAResolve keys DepthRejectParams off this for the RGBA8 fallback
        public static bool HistoryIsFP16 => TAAHistory.IsFP16;
        private static SpriteBatch SB;
        public static float SSAA = 1f; //render scale: >1 supersample (downsample resolve), <1 upscale, 1 native
        public static int MSAA = 0;
        // current frame's TAA jitter (NDC), published by World.PreDraw; lets draws without a WorldState
        // (sky dome) un-jitter their motion vectors
        public static Vector2 TAAJitterNDC = Vector2.Zero;

        /// <summary>
        /// Apply a sub-pixel TAA jitter to a projection matrix as a TRUE constant NDC translation that is
        /// correct for ANY projection — perspective, orthographic, or a component-wise lerp between them
        /// (the lot's 2D&lt;-&gt;3D camera transition blends an ortho and a perspective matrix each frame).
        /// Offsetting only M31/M32 is a constant NDC shift ONLY for a pure-perspective matrix (clip.w = -z);
        /// on a blended/near-orthographic matrix the shift becomes depth-dependent and explodes near the
        /// blend's w-singularity — the "accentuated / drifted" jitter seen mid-transition. General form:
        /// new col1 += jx*col4, col2 += jy*col4, so ndc' = ndc + (jx, jy) for every vertex regardless of w.
        /// Bit-identical to the old M31/M32 path for a pure-perspective matrix (M14=M24=M44=0, M34=-1).
        /// </summary>
        public static Matrix JitterProjection(Matrix p, Vector2 ndcJitter)
        {
            if (ndcJitter.X == 0f && ndcJitter.Y == 0f) return p;
            p.M11 += ndcJitter.X * p.M14; p.M21 += ndcJitter.X * p.M24; p.M31 += ndcJitter.X * p.M34; p.M41 += ndcJitter.X * p.M44;
            p.M12 += ndcJitter.Y * p.M14; p.M22 += ndcJitter.Y * p.M24; p.M32 += ndcJitter.Y * p.M34; p.M42 += ndcJitter.Y * p.M44;
            return p;
        }

        // Bloom mip chain (half, quarter, ... of viewport res). HalfVector4 so blurred highlights don't
        // clip while accumulating. Allocated on demand by EnableBloomTargets, used by BloomPass.
        public const int BLOOM_MIPS = 5;
        private static RenderTarget2D[] BloomMip;
        public static RenderTarget2D GetBloomMip(int i) => (BloomMip != null && i < BloomMip.Length) ? BloomMip[i] : null;
        public static int BloomMipCount => (BloomMip != null) ? BloomMip.Length : 0;

        // SSAO: noisy AO buffer, blur ping-pong, temporal history ping-pong. Color format (R8 isn't
        // universal): .r = AO, .gb = SAO packed depth key for the bilateral blur.
        // while true, EnableVelocityTarget allocates the velocity/depth MRT as fp32 (Vector4) so SSAO
        // reads a full-precision linear depth buffer. Set by World.ChangeAAMode BEFORE EnableVelocityTarget.
        public static bool AOPreciseDepth;
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
            // Idempotent: only (re)create the SpriteBatch when the device actually changes. ConfigureCityAA
            // calls this every frame on the 3D city; the old unconditional `new SpriteBatch` abandoned a
            // finalizable batch per frame.
            if (GD == gd && SB != null) return;
            SB?.Dispose();
            GD = gd;
            SB = new SpriteBatch(gd);
        }

        // macOS native-Retina: the authoritative backbuffer pixel size, set by TSOGame whenever the
        // live PresentationParameters override is active. GraphicsDevice.Viewport is UNRELIABLE while
        // it's on - MonoGame's resize handling reverts it to the window's point size until the next
        // frame re-asserts it, and target (re)allocation can run from Update-phase code in that gap
        // (world sized to points -> quarter-cropped render + 2x-offset picking). Zero = use Viewport.
        public static Point ForcedBackbufferSize = Point.Zero;

        private static Point ScreenSize()
        {
            if (ForcedBackbufferSize.X > 0 && ForcedBackbufferSize.Y > 0) return ForcedBackbufferSize;
            return new Point(System.Math.Max(1, GD.Viewport.Width), System.Math.Max(1, GD.Viewport.Height));
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
            var screen = ScreenSize();
            // backbuffer is sized by the render scale (SSAA), rounded to whole pixels
            int w = System.Math.Max(1, (int)System.Math.Round(SSAA * screen.X / scale));
            int h = System.Math.Max(1, (int)System.Math.Round(SSAA * screen.Y / scale));
            if (!FSOEnvironment.Enable3D)
                BackbufferDepth = CreateRenderTarget(GD, 1, MSAA, SurfaceFormat.Color, w, h, DepthFormat.None);
            Backbuffer = CreateRenderTarget(GD, 1, MSAA, SurfaceFormat.Color, w, h, DepthFormat.Depth24Stencil8);
            // Screen-res intermediate (no MSAA) used to chain a sharpen pass after the scale/post-AA resolve.
            // ResolveTarget / ResolveTarget2 (2x screen RGBA8) are now allocated lazily by EnsureResolveTargets
            // the first frame any post stage runs — they were wasted VRAM for everyone with AA/post off.
            // RENDER-res intermediate for the upscaling path: spatial AA (FXAA/SMAA) must run at RENDER
            // resolution BEFORE the upscaler (AMD FSR1 guideline — EASU/TAAU want anti-aliased input on the
            // render grid; running them post-upscale smooths already-reconstructed pixels instead of the
            // real edges). Only needed when SSAA < 1.
            if (RenderPostTarget != null) { RenderPostTarget.Dispose(); RenderPostTarget = null; }
            if (SSAA < 0.999f)
                RenderPostTarget = CreateRenderTarget(GD, 1, 0, SurfaceFormat.Color, w, h, DepthFormat.None);

            // Bloom mip chain and SSAO targets are allocated on demand (EnableBloomTargets / EnableAOTargets,
            // driven by World.ChangeAAMode / ConfigureCityAA) so they cost nothing when bloom is off / AO is
            // disabled. Resize is handled inside those methods (size-checked, called after InitScreenTargets).

            // velocity target is (re)allocated on demand by EnableVelocityTarget
            if (VelocityTarget != null) { VelocityTarget.Dispose(); VelocityTarget = null; }
            if (MBTileMax != null) { MBTileMax.Dispose(); MBTileMax = null; }
            if (MBNeighborMax != null) { MBNeighborMax.Dispose(); MBNeighborMax = null; }
        }

        // The two screen-res (no-MSAA) ping-pong intermediates the resolve chain draws through. Allocated
        // the first frame a post stage actually runs (DrawBackbuffer, before the ResolveTarget-dependent
        // doBloom/doSharpen guards), sized to the viewport; size-checked so a resize reallocates. When AA /
        // post is entirely off they're never created (~16MB saved at 1080p).
        private static void EnsureResolveTargets()
        {
            if (GD == null) return;
            var screen = ScreenSize();
            int rw = screen.X, rh = screen.Y;
            if (ResolveTarget == null || ResolveTarget.Width != rw || ResolveTarget.Height != rh)
            {
                ResolveTarget?.Dispose();
                ResolveTarget = CreateRenderTarget(GD, 1, 0, SurfaceFormat.Color, rw, rh, DepthFormat.None);
            }
            if (ResolveTarget2 == null || ResolveTarget2.Width != rw || ResolveTarget2.Height != rh)
            {
                ResolveTarget2?.Dispose();
                ResolveTarget2 = CreateRenderTarget(GD, 1, 0, SurfaceFormat.Color, rw, rh, DepthFormat.None);
            }
        }

        // Bloom mip chain (half, quarter, ... of viewport, HalfVector4). Allocated only while bloom is
        // enabled (World.ChangeAAMode / ConfigureCityAA pass the bloom flag); size-checked so a resize
        // reallocates. BloomPass.Draw passes the scene through untouched when the chain is absent
        // (BloomMipCount < 2), so a transient null is safe.
        public static void EnableBloomTargets(bool enable)
        {
            if (!enable)
            {
                if (BloomMip != null) { foreach (var m in BloomMip) m?.Dispose(); BloomMip = null; }
                return;
            }
            if (GD == null) return;
            var screenR = ScreenSize();
            int rw = screenR.X, rh = screenR.Y;
            int m0w = System.Math.Max(1, rw >> 1), m0h = System.Math.Max(1, rh >> 1);
            if (BloomMip != null && BloomMip.Length == BLOOM_MIPS && BloomMip[0] != null
                && BloomMip[0].Width == m0w && BloomMip[0].Height == m0h) return; //already correct size
            if (BloomMip != null) foreach (var m in BloomMip) m?.Dispose();
            BloomMip = new RenderTarget2D[BLOOM_MIPS];
            for (int i = 0; i < BLOOM_MIPS; i++)
            {
                int mw = System.Math.Max(1, rw >> (i + 1));
                int mh = System.Math.Max(1, rh >> (i + 1));
                BloomMip[i] = new RenderTarget2D(GD, mw, mh, false, SurfaceFormat.HalfVector4, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            }
        }

        // SSAO targets (four screen-res Color buffers, ~32MB at 1080p). Allocated only while AO is
        // enabled (World.ChangeAAMode passes the AO predicate). Both consumers null-check: AOPass.Draw
        // passes through and DrawBackbuffer's doAO gates off while any target is missing.
        public static void EnableAOTargets(bool enable)
        {
            if (!enable)
            {
                AOTarget?.Dispose(); AOTarget = null;
                AOTarget2?.Dispose(); AOTarget2 = null;
                AOHistoryA?.Dispose(); AOHistoryA = null;
                AOHistoryB?.Dispose(); AOHistoryB = null;
                return;
            }
            if (GD == null) return;
            // AO computes on the depth/velocity G-buffer's grid when upscaling (render scale < 1): the
            // SAO pass derives face normals from screen-space derivatives of reconstructed position, and
            // a depth source coarser than the compute grid duplicates texels -> zero derivatives ->
            // broken normals. Capped at viewport res when supersampling (SSAA > 1): subsampled depth
            // still yields valid derivatives, and native-res AO avoids the supersample cost. The
            // composite upsamples the (low-frequency) AO buffer bilinearly to the output grid.
            var screenR = ScreenSize();
            int rw = screenR.X, rh = screenR.Y;
            if (Backbuffer != null)
            {
                rw = System.Math.Min(rw, Backbuffer.Width);
                rh = System.Math.Min(rh, Backbuffer.Height);
            }
            if (AOTarget != null && AOTarget.Width == rw && AOTarget.Height == rh) return; //already correct size
            AOTarget?.Dispose(); AOTarget2?.Dispose(); AOHistoryA?.Dispose(); AOHistoryB?.Dispose();
            AOTarget = new RenderTarget2D(GD, rw, rh, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            AOTarget2 = new RenderTarget2D(GD, rw, rh, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            AOHistoryA = new RenderTarget2D(GD, rw, rh, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            AOHistoryB = new RenderTarget2D(GD, rw, rh, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            _AOHistoryAIsPrev = true;
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
            // fp16 velocity halves the biggest bandwidth cost of the TAA path (its .b depth quantization
            // is covered by TAAResolve's disocclusion dead-zone). SSAO needs better: its estimator and
            // packed blur key read .b as a true linear depth buffer, so AO upgrades the target to fp32.
            var velFormat = AOPreciseDepth ? SurfaceFormat.Vector4 : SurfaceFormat.HalfVector4;
            if (VelocityTarget == null || VelocityTarget.Width != Backbuffer.Width || VelocityTarget.Height != Backbuffer.Height || VelocityTarget.Format != velFormat)
            {
                VelocityTarget?.Dispose();
                VelocityTarget = new RenderTarget2D(GD, Backbuffer.Width, Backbuffer.Height, false, velFormat, DepthFormat.None, MSAA, RenderTargetUsage.PreserveContents);
                NormalTarget?.Dispose();
                // NormalTarget (world-space normals, MRT2). The velocity shaders all declare 3 outputs
                // (color/velocity/normal). D3D11 silently DROPS the COLOR2 write when only 2 targets are
                // bound, but OpenGL/MojoShader does NOT — with 3 shader outputs and 2 bound draw buffers it
                // corrupts the COLOR1 (velocity) write, which silently broke every temporal effect on GL
                // (TAA warble, FSR, and the packed-depth debug view showing raw velocity) while DirectX was
                // fine. Bind the real 3rd target so the output/target counts always match. Costs 8 bytes/px
                // of bandwidth; consumed by SSAO (validity mask + debug view) and required for GL correctness
                // even when AO is off.
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

        // TAA history ping-pong: read "prev", write "curr"; TemporalHistoryState.Commit rotates roles
        // when (and only when) a resolve actually produced a frame.
        public static void EnableHistoryTargets(bool enable)
        {
            if (enable && GD == null) return;
            // history/meta must match the surface TAA resolves on 1:1 - native normally and under TAAU,
            // render-res under FSR1 (TAA runs before the EASU upscale, see TAASkipFinalBlit). Callers must
            // set TAAUEnabled and run InitScreenTargets first.
            int w = 1, h = 1;
            if (enable)
            {
                var screenH = ScreenSize();
                w = screenH.X;
                h = screenH.Y;
                if (SSAA < 0.999f && !TAAUEnabled && Backbuffer != null)
                {
                    w = Backbuffer.Width;
                    h = Backbuffer.Height;
                }
            }
            TAAHistory.Ensure(GD, enable, w, h);
        }
        /// <summary>Reset TAA history at the next resolve (camera cut, teleport, world switch, or any
        /// change the layout signature in TemporalHistoryState can't observe).</summary>
        public static void InvalidateTAAHistory(string reason) => TAAHistory.Invalidate(reason);
        public static RenderTarget2D GetHistoryPrev() => TAAHistory.Prev;
        public static RenderTarget2D GetHistoryCurr() => TAAHistory.Curr;
        public static RenderTarget2D GetMetaPrev() => TAAHistory.MetaPrev;
        public static RenderTarget2D GetMetaCurr() => TAAHistory.MetaCurr;

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
                        // up-vector default + invalid mask (SSAO treats alpha<0.5 as no-geometry)
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
        // per-pixel motion blur (3D); runs AFTER the temporal resolve (history must accumulate
        // unblurred color) and before post-AA so FXAA/SMAA smooth the blurred edges. null = off
        public static Action<GraphicsDevice, RenderTarget2D> MotionBlurFunc;
        // post-process resolve (FXAA/SMAA/FSR); runs even when SSAA==1. null = plain blit (no behaviour change)
        public static Action<GraphicsDevice, RenderTarget2D> PostProcessFunc;
        // temporal AA; its own stage, before motion blur in every mode. null = off
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
        // ambient occlusion (SSAO); runs before bloom. null = off
        public static Action<GraphicsDevice, RenderTarget2D> AOFunc;
        // bloom; runs after post-AA, before sharpen. null = off
        public static Action<GraphicsDevice, RenderTarget2D> BloomFunc;
        // final sharpen (FSR RCAS); writes the screen. null = off
        public static Action<GraphicsDevice, Texture2D> SharpenFunc;
        public static bool WithOpacity = true;

        // Monotonic presented-frame counter for the temporal resolve's continuity check: the resolve
        // commits the FrameId it consumed; any frame presented WITHOUT a commit (fade/zoom transition,
        // velocity-debug bypass, missing-target fall-through) advances this past HistoryFrameId+1 and
        // the next BeginResolve clears history instead of reprojecting across the gap.
        public static long FrameId { get; private set; }

        public static void DrawBackbuffer(float opacity, float scale)
        {
            if (Backbuffer == null) return; //this gfx mode does not use a rendertarget backbuffer
            FrameId++;
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
            // Lazily allocate the resolve ping-pong the moment any post stage could run. Done BEFORE the
            // ResolveTarget-dependent doBloom/doSharpen guards so they see the freshly-allocated targets
            // (otherwise a bloom/sharpen-only frame would never allocate them and stay off forever). anyPost
            // is a strict superset of the chain-entry condition below, so whenever the chain runs the targets
            // exist -> no NRE; when everything is off they're never created.
            bool anyPost = nonNative || doMotionBlur || doPost || doTAA
                || (AOFunc != null && postOk) || (BloomFunc != null && postOk) || (SharpenFunc != null && postOk);
            if (anyPost) EnsureResolveTargets();
            bool doAO = AOFunc != null && AOTarget != null && AOTarget2 != null && AOHistoryA != null && AOHistoryB != null && VelocityTarget != null && postOk;
            bool doBloom = BloomFunc != null && ResolveTarget != null && ResolveTarget2 != null && postOk;
            bool doSharpen = SharpenFunc != null && ResolveTarget != null && ResolveTarget2 != null && postOk;

            if (nonNative || doMotionBlur || doPost || doTAA || doAO || doBloom || doSharpen)
            {
                // resolve chain: scale-resolve -> TAA -> motion blur -> post-AA -> AO -> bloom -> sharpen.
                // Temporal resolve BEFORE motion blur in every mode (TAAU already was, by occupying the
                // nonNative slot): history must accumulate unblurred color — blurred frames extend trails
                // through the accumulation and corrupt history rejection. Blur applies per-frame after.
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
                // native/supersample TAA slot: AFTER the downsample (history is native-sized, so at
                // SSAA>1 the resolve consumes the resolved native grid), BEFORE motion blur (above)
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
                // Integer scales (1x pass-through, the 2x Retina apparent-Near double) point-sample:
                // exact texel duplication - linear here BLENDS across sprite boundaries, smearing 1px
                // outlines/depth fringes into visible seams. (An earlier point-sampled build looked
                // blocky-broken, but that was the half-pixel ortho phase bug, since fixed - on an
                // aligned grid an integer point double is clean.) Fractional zoom scales stay linear.
                var integerScale = System.Math.Abs(scale - System.Math.Round(scale)) < 0.001f;
                var sampler = integerScale ? SamplerState.PointClamp : SamplerState.LinearClamp;
                if (!WithOpacity)
                {
                    SB.Begin(blendState: BlendState.Opaque, samplerState: sampler);
                    opacity = 1;
                }
                else
                    SB.Begin(blendState: BlendState.AlphaBlend, samplerState: sampler);
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

            // Honor the caller's dformat: this previously hardcoded Depth24Stencil8, silently attaching
            // a depth/stencil buffer to every target that asked for None (ResolveTarget/2,
            // RenderPostTarget, BackbufferDepth, UI cache targets — ~4 bytes/px of pure VRAM waste each).
            // EXCEPTION — mobile keeps the old behavior: under SoftwareDepth, RenderPPXDepth runs stencil
            // ops against whatever color target is bound, and the non-MRT path depth-tests against the
            // packed-depth color target; both relied on every target having D24S8 regardless of request.
            // Upstream behavior: EVERY engine target gets D24S8, ignoring the requested dformat.
            // The 2D depth-channel passes depth-test against whatever target is bound (packed-depth
            // buffer et al) and rely on it having a real depth attachment - honoring DepthFormat.None
            // turns their depth testing silently off (last-drawn-wins: 2D sprite mis-sorting).
            dformat = DepthFormat.Depth24Stencil8;
            return new RenderTarget2D(device,
                width, height, (numberLevels > 1), surface,
                dformat, multisample, RenderTargetUsage.PreserveContents);
        }
    }
}
