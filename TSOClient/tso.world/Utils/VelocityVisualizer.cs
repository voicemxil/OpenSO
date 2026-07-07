using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using FSO.Common.Utils;

namespace FSO.LotView.Utils
{
    /// <summary>
    /// Debug view of the MRT1 velocity buffer (WorldConfig.VelocityDebug); replaces the post-resolve chain.
    /// Black = unwritten, mid gray = valid+stationary, red = +vx, green = +vy, blue tint = valid flag.
    /// </summary>
    public static class VelocityVisualizer
    {
        // velocities are tiny (~0.005 UV/frame) - amplify into a visible hue
        private const float SCALE = 30f;

        public static void Draw(GraphicsDevice gd, RenderTarget2D src)
        {
            var effect = WorldContent.VelocityViz;
            var velocity = PPXDepthEngine.GetVelocityTarget();
            if (effect == null || velocity == null)
            {
                // shader/buffer missing - plain blit
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
            // depth mode: grayscale of the packed linear depth in v.b instead of velocity hue
            effect.Parameters["DepthMode"]?.SetValue(WorldConfig.Current.VelocityDebugDepth ? 1f : 0f);
            var tech = effect.Techniques["VelocityViz"];
            if (tech == null) return;
            effect.CurrentTechnique = tech;
            effect.CurrentTechnique.Passes[0].Apply();

            gd.SetVertexBuffer(WorldContent.GetTextureVerts(gd));
            gd.DrawPrimitives(PrimitiveType.TriangleStrip, 0, 2);
        }
    }
}
