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
        private static RenderTarget2D VelocityTarget; //3D-mode per-pixel screen-space velocity (HalfVector4), MRT1 for TAA / motion blur
        // World-space normal (HalfVector4: .xyz normal, .a validity). MRT2, written by the same velocity-
        // aware shaders. Required for GTAO — derived ddx/ddy normals from NDC depth were noisy garbage.
        private static RenderTarget2D NormalTarget;
        // Motion-blur reconstruction-filter intermediates (McGuire 2012). Allocated alongside the velocity
        // target. TileMax reduces velocity to KxK tiles; NeighborMax dilates it 3x3 so fast streaks reach
        // neighbouring tiles. Both at velocity-res / MB_TILE_SIZE.
        private static RenderTarget2D MBTileMax, MBNeighborMax;
        public const int MB_TILE_SIZE = 20;
        private static RenderTarget2D HistoryA, HistoryB; //TAA history ping-pong (screen-res RGBA8)
        private static bool _HistoryAIsPrev; //which buffer holds last frame's TAA output
        private static SpriteBatch SB;
        public static float SSAA = 1f; //render scale: >1 supersample (downsample resolve), <1 upscale, 1 native
        public static int MSAA = 0;

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

        public static RenderTarget2D GetVelocityTarget() => VelocityTarget;
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

        // TAA history ping-pong. Each frame TAA reads from "prev" and writes to "curr", then SwapHistory
        // toggles roles for the next frame.
        //
        // Size MUST match ResolveTarget (screen viewport size), NOT Backbuffer — TAA slots into the resolve
        // chain after the SSAA scale-resolve step, so its input/output are always screen-res. Sizing History
        // to Backbuffer broke render scaling: when SSAA<1 (downscale), History was smaller than the chain's
        // working surface and the SpriteBatch blit at the end of TAAResolve drew under-sized content.
        public static void EnableHistoryTargets(bool enable)
        {
            if (!enable)
            {
                if (HistoryA != null) { HistoryA.Dispose(); HistoryA = null; }
                if (HistoryB != null) { HistoryB.Dispose(); HistoryB = null; }
                return;
            }
            if (GD == null) return;
            int w = System.Math.Max(1, GD.Viewport.Width);
            int h = System.Math.Max(1, GD.Viewport.Height);
            if (HistoryA == null || HistoryA.Width != w || HistoryA.Height != h)
            {
                HistoryA?.Dispose();
                HistoryB?.Dispose();
                HistoryA = new RenderTarget2D(GD, w, h, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
                HistoryB = new RenderTarget2D(GD, w, h, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
                _HistoryAIsPrev = true;
            }
        }
        public static RenderTarget2D GetHistoryPrev() => _HistoryAIsPrev ? HistoryA : HistoryB;
        public static RenderTarget2D GetHistoryCurr() => _HistoryAIsPrev ? HistoryB : HistoryA;
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

                if (nonNative)
                {
                    remaining--;
                    var dst = (remaining == 0) ? null : ((pong++ % 2 == 0) ? ResolveTarget : ResolveTarget2);
                    GD.SetRenderTarget(dst);
                    SSAAFunc(GD, src);
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
                if (doPost)
                {
                    remaining--;
                    var dst = (remaining == 0) ? null : ((pong++ % 2 == 0) ? ResolveTarget : ResolveTarget2);
                    GD.SetRenderTarget(dst);
                    PostProcessFunc(GD, src);
                    src = dst;
                }
                if (doTAA)
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
