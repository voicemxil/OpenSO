using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FSO.LotView.Utils
{
    /// <summary>
    /// FSR 1.0 RCAS (Robust Contrast-Adaptive Sharpening) pass. Draws a source texture (the resolved
    /// frame) to the currently bound render target through the FSR effect's RCAS technique, driven by
    /// WorldConfig.Current.SharpenAmount. Modeled on <see cref="SSAADownsample"/>.
    /// </summary>
    public static class RCASSharpen
    {
        public static void Draw(GraphicsDevice gd, Texture2D src)
        {
            var effect = WorldContent.FSR;
            if (effect == null) return; //shader missing for this content profile; caller should not have enabled us

            gd.BlendState = BlendState.Opaque;
            effect.CurrentTechnique = effect.Techniques["RCAS"];
            effect.Parameters["tex"].SetValue(src);
            effect.Parameters["SourceSize"].SetValue(new Vector2(1f / src.Width, 1f / src.Height));
            effect.Parameters["Sharpness"].SetValue(WorldConfig.Current.SharpenAmount);
            effect.CurrentTechnique.Passes[0].Apply();

            gd.SetVertexBuffer(WorldContent.GetTextureVerts(gd));
            gd.DrawPrimitives(PrimitiveType.TriangleStrip, 0, 2);
        }
    }
}
