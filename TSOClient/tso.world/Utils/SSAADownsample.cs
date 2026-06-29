using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FSO.LotView.Utils
{
    public static class SSAADownsample
    {
        public static void Draw(GraphicsDevice gd, RenderTarget2D targ)
        {
            var effect = WorldContent.SSAA;
            gd.BlendState = BlendState.Opaque;

            // The exact 4-sample 2x2 box (DrawSSAA4) is optimal for integer 2x supersampling. For non-integer
            // ratios it under-covers the source footprint, so use the footprint-aware tent (DrawSSAAFootprint)
            // there. SSAA is the live render scale (source/dest).
            float scale = FSO.Common.Utils.PPXDepthEngine.SSAA;
            bool integer2x = System.Math.Abs(scale - 2f) < 0.01f;
            var footprint = (!integer2x) ? effect.Techniques["DrawSSAAFootprint"] : null;
            effect.CurrentTechnique = footprint ?? effect.Techniques[0]; // [0] = DrawSSAA4

            effect.Parameters["SSAASize"].SetValue(new Vector2(1f / targ.Width, 1f / targ.Height));
            var scaleParam = effect.Parameters["SSAAScale"];
            if (scaleParam != null) scaleParam.SetValue(scale);
            effect.Parameters["tex"].SetValue(targ);
            effect.CurrentTechnique.Passes[0].Apply();

            gd.SetVertexBuffer(WorldContent.GetTextureVerts(gd));
            gd.DrawPrimitives(PrimitiveType.TriangleStrip, 0, 2);
        }
    }
}
