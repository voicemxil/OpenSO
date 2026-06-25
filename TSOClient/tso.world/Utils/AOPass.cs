using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using FSO.Common.Utils;
using FSO.Common.Rendering.Framework.Camera;

namespace FSO.LotView.Utils
{
    /// <summary>
    /// GTAO (Ground-Truth Ambient Occlusion) post-process. Three-pass orchestrator:
    ///   1. GTAO       — noisy single-frame AO into AOTarget (uses velocity-buffer depth + ddx/ddy normals)
    ///   2. Blur       — depth-aware cross-bilateral 3x3 into AOTarget2 (denoise)
    ///   3. Composite  — multiply scene * (1 - (1-AO) * Intensity) into the chain's destination
    ///
    /// Slots into the resolve chain at PPXDepthEngine.AOFunc, before bloom — AO darkens crevices BEFORE
    /// bloom adds highlights (standard order).
    /// </summary>
    public static class AOPass
    {
        // Frame counter for per-frame noise rotation (helps temporal averaging through TAA).
        private static int _Frame;

        // Diagnostic mode, set transiently from graphics options: 0 = normal composite, 1 = raw AO
        // (pre blur/temporal) as grayscale, 2 = G-buffer world normals as colour, 3 = depth as grayscale.
        public static int DebugMode;

        // Active camera projection params — set by World.PreDraw each frame so the AO shader can
        // linearize depth + reconstruct view-space positions without back-referencing the camera.
        public static float NearPlane = 1.0f;
        public static float FarPlane = 800.0f;
        public static float TanHalfFovY = 1.0f;
        public static float AspectRatio = 16f / 9f;
        public static Matrix View = Matrix.Identity;
        public static Matrix Projection = Matrix.Identity;
        public static Matrix InvProjection = Matrix.Identity;
        public static Vector2 ProjScale = Vector2.One;

        // LearnOpenGL SSAO data, generated once: a 64-sample hemisphere kernel (tangent space) and a 4x4
        // noise-rotation texture tiled across the screen.
        private static Vector3[] _Kernel;
        private static Texture2D _NoiseTex;

        private static void EnsureSSAOData(GraphicsDevice gd)
        {
            if (_Kernel != null && _NoiseTex != null && !_NoiseTex.IsDisposed) return;
            var rng = new Random(12345); // fixed seed -> deterministic kernel/noise

            _Kernel = new Vector3[64];
            for (int i = 0; i < 64; i++)
            {
                // Hemisphere sample (z in [0,1] = along the surface normal), random length, then biased
                // toward the origin with an accelerating curve so most samples sit near the surface.
                var s = new Vector3(
                    (float)(rng.NextDouble() * 2.0 - 1.0),
                    (float)(rng.NextDouble() * 2.0 - 1.0),
                    (float)rng.NextDouble());
                s = Vector3.Normalize(s) * (float)rng.NextDouble();
                float scale = i / 64f;
                scale = MathHelper.Lerp(0.1f, 1.0f, scale * scale);
                _Kernel[i] = s * scale;
            }

            // 4x4 rotation vectors in the tangent plane (z = 0).
            var noise = new Vector4[16];
            for (int i = 0; i < 16; i++)
                noise[i] = new Vector4((float)(rng.NextDouble() * 2.0 - 1.0), (float)(rng.NextDouble() * 2.0 - 1.0), 0f, 0f);
            _NoiseTex?.Dispose();
            _NoiseTex = new Texture2D(gd, 4, 4, false, SurfaceFormat.Vector4);
            _NoiseTex.SetData(noise);
        }

        public static void Draw(GraphicsDevice gd, RenderTarget2D src)
        {
            var effect = WorldContent.GTAO;
            var velRT = PPXDepthEngine.GetVelocityTarget();
            var normalRT = PPXDepthEngine.GetNormalTarget();
            var ao = PPXDepthEngine.GetAOTarget();
            var ao2 = PPXDepthEngine.GetAOTarget2();
            if (effect == null || velRT == null || normalRT == null || ao == null || ao2 == null)
            {
                // Missing dependencies -> pass scene through unchanged.
                gd.BlendState = BlendState.Opaque;
                using (var sb = new SpriteBatch(gd))
                {
                    sb.Begin(blendState: BlendState.Opaque);
                    sb.Draw(src, new Rectangle(0, 0, gd.Viewport.Width, gd.Viewport.Height), Color.White);
                    sb.End();
                }
                return;
            }

            var cfg = WorldConfig.Current;
            var dst = gd.GetRenderTargets();
            var verts = WorldContent.GetTextureVerts(gd);
            gd.BlendState = BlendState.Opaque;

            EnsureSSAOData(gd);

            // Common uniforms.
            effect.Parameters["InvScreenSize"]?.SetValue(new Vector2(1f / ao.Width, 1f / ao.Height));
            effect.Parameters["FarPlane"]?.SetValue(FarPlane);
            effect.Parameters["Radius"]?.SetValue(cfg.AORadius);
            effect.Parameters["Intensity"]?.SetValue(cfg.AOIntensity);
            effect.Parameters["AOBias"]?.SetValue(0.025f);
            effect.Parameters["NoiseScale"]?.SetValue(new Vector2(ao.Width / 4f, ao.Height / 4f));
            effect.Parameters["Samples"]?.SetValue(_Kernel);
            effect.Parameters["View"]?.SetValue(View);
            effect.Parameters["Projection"]?.SetValue(Projection);
            effect.Parameters["InvProjection"]?.SetValue(InvProjection);
            effect.Parameters["velocityTex"]?.SetValue(velRT);
            effect.Parameters["normalTex"]?.SetValue(normalRT);
            effect.Parameters["noiseTex"]?.SetValue(_NoiseTex);

            // DIAGNOSTIC modes 2/3: visualize a G-buffer input directly (don't need the AO computed).
            if (DebugMode == 2)
            {
                gd.SetRenderTargets(dst);
                ApplyDraw(gd, effect, "NormalDebug", verts);
                return;
            }
            if (DebugMode == 3)
            {
                gd.SetRenderTargets(dst);
                ApplyDraw(gd, effect, "DepthDebug", verts);
                return;
            }

            // Pass 1: noisy single-frame AO -> ao
            gd.SetRenderTarget(ao);
            ApplyDraw(gd, effect, "GTAO", verts);

            // Pass 2: 4x4 box blur ao -> ao2 (cancels the noise tiling)
            gd.SetRenderTarget(ao2);
            effect.Parameters["aoTex"]?.SetValue(ao);
            ApplyDraw(gd, effect, "Blur", verts);

            // DIAGNOSTIC mode 1: show the blurred AO (pre-temporal) as grayscale.
            if (DebugMode == 1)
            {
                gd.SetRenderTargets(dst);
                effect.Parameters["aoTex"]?.SetValue(ao2);
                ApplyDraw(gd, effect, "CompositeDebug", verts);
                return;
            }

            // Pass 3: temporal accumulation (reproject prev history via velocity, blend).
            var historyPrev = PPXDepthEngine.GetAOHistoryPrev();
            var historyCurr = PPXDepthEngine.GetAOHistoryCurr();
            gd.SetRenderTarget(historyCurr);
            effect.Parameters["aoTex"]?.SetValue(ao2);
            effect.Parameters["aoHistoryTex"]?.SetValue(historyPrev);
            ApplyDraw(gd, effect, "Temporal", verts);

            // Pass 4: composite scene * AO -> chain destination (or grayscale AO when debugging).
            gd.SetRenderTargets(dst);
            effect.Parameters["colorTex"]?.SetValue(src);
            effect.Parameters["aoTex"]?.SetValue(historyCurr);
            ApplyDraw(gd, effect, "Composite", verts);

            PPXDepthEngine.SwapAOHistory();
        }

        /// <summary>
        /// Snapshot the active camera's projection params for this frame so the GTAO shader can linearize
        /// depth + reconstruct view-space positions. Called from World.PreDraw on the active 3D camera.
        /// </summary>
        public static void SetCamera(BasicCamera cam, float viewportAspect)
        {
            if (cam == null) return;
            NearPlane = cam.NearPlane;
            FarPlane = cam.FarPlane;
            TanHalfFovY = (float)Math.Tan(cam.FOV * 0.5f);
            AspectRatio = viewportAspect;
            View = cam.View;
            Projection = cam.Projection;
            InvProjection = Matrix.Invert(cam.Projection);
            // (Projection.M11, Projection.M22) = view->ndc XY scale (includes Zoom + aspect). Used to
            // size the world-space sampling Radius into UV space at each pixel's depth.
            ProjScale = new Vector2(cam.Projection.M11, cam.Projection.M22);
        }

        private static void ApplyDraw(GraphicsDevice gd, Effect effect, string technique, VertexBuffer verts)
        {
            var tech = effect.Techniques[technique];
            if (tech == null) return;
            effect.CurrentTechnique = tech;
            effect.CurrentTechnique.Passes[0].Apply();
            gd.SetVertexBuffer(verts);
            gd.DrawPrimitives(PrimitiveType.TriangleStrip, 0, 2);
        }
    }
}
