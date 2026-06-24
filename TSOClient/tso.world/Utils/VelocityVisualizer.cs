using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using FSO.Common.Utils;

namespace FSO.LotView.Utils
{
    /// <summary>
    /// Diagnostic visualizer for the MRT1 velocity buffer. When `WorldConfig.Current.VelocityDebug` is on
    /// (and a velocity target is allocated), this replaces the entire post-resolve chain so the user sees
    /// raw velocity instead of the scene.
    ///
    /// Encoding (per-pixel):
    ///   alpha == 0          → BLACK            (no velocity written by any shader)
    ///   alpha == 1, v == 0  → mid GRAY         (valid wiring, stationary)
    ///   alpha == 1, +vx     → RED-tinted       (velocity to the right)
    ///   alpha == 1, +vy     → GREEN-tinted     (velocity downward in UV)
    ///   blue tint           → "validity" flag — distinguishes zero-velocity from unwritten.
    ///
    /// Pan-test: every visible surface should turn the SAME hue (the camera-induced direction). If grass
    /// flickers while objects stay solid, that grass shader has wrong velocity. If a surface is BLACK
    /// during a pan, no shader is writing velocity for it.
    /// </summary>
    public static class VelocityVisualizer
    {
        // Velocities are typically tiny (e.g. 0.005 UV/frame). Amplifying by 30 lifts a normal pan into a
        // clearly-distinguishable hue without saturating immediately.
        private const float SCALE = 30f;

        public static void Draw(GraphicsDevice gd, RenderTarget2D src)
        {
            var effect = WorldContent.VelocityViz;
            var velocity = PPXDepthEngine.GetVelocityTarget();
            if (effect == null || velocity == null)
            {
                // Shader / buffer missing → fall through to plain blit so the frame still renders.
                gd.BlendState = BlendState.Opaque;
                using (var sb = new SpriteBatch(gd))
                {
                    sb.Begin(blendState: BlendState.Opaque);
                    sb.Draw(src, new Rectangle(0, 0, gd.Viewport.Width, gd.Viewport.Height), Color.White);
                    sb.End();
                }
                return;
            }

            gd.BlendState = BlendState.Opaque;
            effect.Parameters["velocityTex"]?.SetValue(velocity);
            effect.Parameters["Scale"]?.SetValue(SCALE);
            var tech = effect.Techniques["VelocityViz"];
            if (tech == null) return;
            effect.CurrentTechnique = tech;
            effect.CurrentTechnique.Passes[0].Apply();

            gd.SetVertexBuffer(WorldContent.GetTextureVerts(gd));
            gd.DrawPrimitives(PrimitiveType.TriangleStrip, 0, 2);
        }
    }
}
