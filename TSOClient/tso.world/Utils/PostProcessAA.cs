using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FSO.LotView.Utils
{
    /// <summary>
    /// Post-process anti-aliasing resolve. Draws the rendered backbuffer to the currently bound render
    /// target (the screen) through a full-screen post-process effect. Today this is FXAA; SMAA/FSR will
    /// plug in here as further passes. Modeled on <see cref="SSAADownsample"/>.
    /// </summary>
    public static class PostProcessAA
    {
        public static void Draw(GraphicsDevice gd, RenderTarget2D targ)
        {
            var effect = WorldContent.FXAA;
            if (effect == null) return; //shader missing for this content profile; caller should not have enabled us

            gd.BlendState = BlendState.Opaque;
            effect.CurrentTechnique = effect.Techniques[0];
            effect.Parameters["InvViewportSize"].SetValue(new Vector2(1f / targ.Width, 1f / targ.Height));
            effect.Parameters["tex"].SetValue(targ);
            effect.CurrentTechnique.Passes[0].Apply();

            gd.SetVertexBuffer(WorldContent.GetTextureVerts(gd));
            gd.DrawPrimitives(PrimitiveType.TriangleStrip, 0, 2);
        }
    }
}
