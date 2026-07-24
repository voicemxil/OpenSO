using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FSO.LotView.Utils
{
    /// <summary>
    /// FSR spatial upscale (Catmull-Rom bicubic) for the render-scale &lt; 1 path. Draws the smaller
    /// backbuffer to the full screen through the FSR effect's Bicubic technique. Matches the SSAAFunc
    /// signature so it can stand in for the box resolve when upscaling. Falls back to the box downsample
    /// if the FSR shader isn't present (e.g. non-HiDef device). Modeled on <see cref="SSAADownsample"/>.
    /// </summary>
    public static class FSRUpscale
    {
        public static void Draw(GraphicsDevice gd, RenderTarget2D targ)
        {
            var effect = WorldContent.FSR;
            if (effect == null) { SSAADownsample.Draw(gd, targ); return; }

            gd.BlendState = BlendState.Opaque;
            // Upscaler 2 = sharp bilinear (crisp texel blocks, bilinear seams); anything else on this
            // path uses EASU. Null-guarded so a stale FSR.xnb without the technique still upscales.
            var sharp = (WorldConfig.Current.Upscaler == 2) ? effect.Techniques["SharpBilinear"] : null;
            effect.CurrentTechnique = sharp ?? effect.Techniques["EASU"];
            if (sharp != null) effect.Parameters["SharpScale"]?.SetValue((float)gd.Viewport.Width / targ.Width);
            effect.Parameters["tex"].SetValue(targ);
            effect.Parameters["SourceSize"].SetValue(new Vector2(1f / targ.Width, 1f / targ.Height));
            effect.CurrentTechnique.Passes[0].Apply();

            gd.SetVertexBuffer(WorldContent.GetTextureVerts(gd));
            gd.DrawPrimitives(PrimitiveType.TriangleStrip, 0, 2);
        }
    }
}
