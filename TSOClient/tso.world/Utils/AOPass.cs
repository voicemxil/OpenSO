using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using FSO.Common.Utils;
using FSO.Common.Rendering.Framework.Camera;

namespace FSO.LotView.Utils
{
    /// <summary>
    /// SSAO post-process driving SSAO.fx — a port of Scalable Ambient Obscurance (McGuire et al.,
    /// HPG 2012): spiral-tap AO estimator, separable depth-key bilateral blur, temporal accumulation,
    /// composite. Runs at PPXDepthEngine.AOFunc, before bloom (AO darkens before bloom adds highlights).
    /// </summary>
    public static class AOPass
    {
        // per-frame spiral rotation index; the temporal pass integrates successive rotations
        private static int _Frame;
        // golden angle: successive frame rotations land maximally far apart
        private const float FRAME_ROTATION = 2.39996323f;
        // Maps the UI intensity slider (0..2) onto the estimator's useful range: at this world's unit
        // scale the raw SAO sum saturates far below slider 1.0, so the slider is pre-scaled down.
        private const float INTENSITY_SCALE = 0.3f;
        // view-space self-shadowing bias, proportional to the sample radius so retuning the radius
        // slider doesn't reintroduce flat-surface shadowing
        private const float BIAS_PER_RADIUS = 0.04f;
        // history weight in the temporal pass
        private const float TEMPORAL_BLEND = 0.9f;

        // debug: 0 = normal, 1 = blurred AO grayscale, 2 = normals, 3 = depth
        public static int DebugMode;

        // camera projection params, set by World.PreDraw each frame
        public static float NearPlane = 1.0f;
        public static float FarPlane = 800.0f;
        public static Matrix View = Matrix.Identity;
        public static Matrix InvProjection = Matrix.Identity;
        public static Vector2 ProjScale = Vector2.One;

        // 4x4 tiled noise: .x = random spiral phase [0,1)
        private static Texture2D _NoiseTex;

        private static void EnsureNoise(GraphicsDevice gd)
        {
            if (_NoiseTex != null && !_NoiseTex.IsDisposed) return;
            var rng = new Random(12345); // fixed seed: deterministic pattern, temporal pass does the dithering
            var noise = new Vector4[16];
            for (int i = 0; i < 16; i++)
                noise[i] = new Vector4((float)rng.NextDouble(), (float)rng.NextDouble(), 0f, 0f);
            _NoiseTex?.Dispose();
            _NoiseTex = new Texture2D(gd, 4, 4, false, SurfaceFormat.Vector4);
            _NoiseTex.SetData(noise);
        }

        public static void Draw(GraphicsDevice gd, RenderTarget2D src)
        {
            var effect = WorldContent.SSAO;
            var velRT = PPXDepthEngine.GetVelocityTarget();
            var normalRT = PPXDepthEngine.GetNormalTarget();
            var ao = PPXDepthEngine.GetAOTarget();
            var ao2 = PPXDepthEngine.GetAOTarget2();
            if (effect == null || velRT == null || normalRT == null || ao == null || ao2 == null)
            {
                // missing dependencies - pass scene through unchanged
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

            EnsureNoise(gd);

            float radius = Math.Max(cfg.AORadius, 0.01f);
            float r2 = radius * radius;
            effect.Parameters["InvScreenSize"]?.SetValue(new Vector2(1f / ao.Width, 1f / ao.Height));
            effect.Parameters["FarPlane"]?.SetValue(FarPlane);
            effect.Parameters["Radius"]?.SetValue(radius);
            effect.Parameters["Bias"]?.SetValue(BIAS_PER_RADIUS * radius);
            // SAO estimator gain: Intensity / Radius^6 (the f^3 falloff term carries r^6)
            effect.Parameters["IntensityDivR6"]?.SetValue(cfg.AOIntensity * INTENSITY_SCALE / (r2 * r2 * r2));
            // pixels per view-space unit at z = -1 on the AO grid; the shader divides by depth
            effect.Parameters["ProjScalePx"]?.SetValue(0.5f * ProjScale.Y * ao.Height);
            effect.Parameters["FrameAngle"]?.SetValue((_Frame++ % 64) * FRAME_ROTATION);
            effect.Parameters["NoiseScale"]?.SetValue(new Vector2(ao.Width / 4f, ao.Height / 4f));
            effect.Parameters["TemporalBlend"]?.SetValue(TEMPORAL_BLEND);
            effect.Parameters["View"]?.SetValue(View);
            effect.Parameters["InvProjection"]?.SetValue(InvProjection);
            effect.Parameters["velocityTex"]?.SetValue(velRT);
            effect.Parameters["normalTex"]?.SetValue(normalRT);
            effect.Parameters["noiseTex"]?.SetValue(_NoiseTex);

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

            // raw AO (+ packed depth key in .gb)
            gd.SetRenderTarget(ao);
            ApplyDraw(gd, effect, "SAO", verts);

            // separable bilateral blur: horizontal into ao2, vertical back into ao
            gd.SetRenderTarget(ao2);
            effect.Parameters["aoTex"]?.SetValue(ao);
            effect.Parameters["BlurAxis"]?.SetValue(new Vector2(1f, 0f));
            ApplyDraw(gd, effect, "Blur", verts);

            gd.SetRenderTarget(ao);
            effect.Parameters["aoTex"]?.SetValue(ao2);
            effect.Parameters["BlurAxis"]?.SetValue(new Vector2(0f, 1f));
            ApplyDraw(gd, effect, "Blur", verts);

            if (DebugMode == 1)
            {
                gd.SetRenderTargets(dst);
                effect.Parameters["aoTex"]?.SetValue(ao);
                ApplyDraw(gd, effect, "CompositeDebug", verts);
                return;
            }

            var historyPrev = PPXDepthEngine.GetAOHistoryPrev();
            var historyCurr = PPXDepthEngine.GetAOHistoryCurr();
            gd.SetRenderTarget(historyCurr);
            effect.Parameters["aoTex"]?.SetValue(ao);
            effect.Parameters["aoHistoryTex"]?.SetValue(historyPrev);
            ApplyDraw(gd, effect, "Temporal", verts);

            gd.SetRenderTargets(dst);
            effect.Parameters["colorTex"]?.SetValue(src);
            effect.Parameters["aoTex"]?.SetValue(historyCurr);
            ApplyDraw(gd, effect, "Composite", verts);

            PPXDepthEngine.SwapAOHistory();
        }

        /// <summary>
        /// Snapshot the active camera's projection params for the SSAO shader. Called from World.PreDraw.
        /// </summary>
        public static void SetCamera(BasicCamera cam, float viewportAspect)
        {
            if (cam == null) return;
            NearPlane = cam.NearPlane;
            FarPlane = cam.FarPlane;
            View = cam.View;
            InvProjection = Matrix.Invert(cam.Projection);
            // (M11, M22) = view->ndc XY scale; sizes the world-space Radius into screen pixels per depth
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
