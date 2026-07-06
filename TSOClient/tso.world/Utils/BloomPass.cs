using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using FSO.Common.Utils;

namespace FSO.LotView.Utils
{
    /// <summary>
    /// Bloom post-process (Bloom.fx): bright-pass, dual-filter down/upsample mip chain, composite.
    /// Runs at PPXDepthEngine.BloomFunc (after post-AA, before sharpen).
    /// </summary>
    public static class BloomPass
    {
        public static void Draw(GraphicsDevice gd, RenderTarget2D src)
        {
            var effect = WorldContent.Bloom;
            int mips = PPXDepthEngine.BloomMipCount;
            if (effect == null || mips < 2)
            {
                // missing shader/targets - pass the scene through unchanged
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
            var dst = gd.GetRenderTargets();           // the chain's destination for the composite
            var verts = WorldContent.GetTextureVerts(gd);
            gd.BlendState = BlendState.Opaque;

            effect.Parameters["Threshold"]?.SetValue(cfg.BloomThreshold);
            effect.Parameters["Knee"]?.SetValue(cfg.BloomThreshold * 0.1f + 0.01f);
            effect.Parameters["Intensity"]?.SetValue(cfg.BloomIntensity);
            // per-mip upsample contribution (Karis/COD canonical 0.7)
            effect.Parameters["UpsampleBlend"]?.SetValue(0.70f);

            var mip0 = PPXDepthEngine.GetBloomMip(0);
            gd.SetRenderTarget(mip0);
            effect.Parameters["sourceTex"]?.SetValue(src);
            effect.Parameters["TexelSize"]?.SetValue(new Vector2(1f / src.Width, 1f / src.Height));
            ApplyDraw(gd, effect, "Prefilter", verts);

            for (int i = 1; i < mips; i++)
            {
                var s = PPXDepthEngine.GetBloomMip(i - 1);
                gd.SetRenderTarget(PPXDepthEngine.GetBloomMip(i));
                effect.Parameters["sourceTex"]?.SetValue(s);
                effect.Parameters["TexelSize"]?.SetValue(new Vector2(1f / s.Width, 1f / s.Height));
                ApplyDraw(gd, effect, "Downsample", verts);
            }

            // additive upsample back down to mip0, which then holds the full bloom
            gd.BlendState = BlendState.Additive;
            for (int i = mips - 1; i >= 1; i--)
            {
                var s = PPXDepthEngine.GetBloomMip(i);
                gd.SetRenderTarget(PPXDepthEngine.GetBloomMip(i - 1));
                effect.Parameters["sourceTex"]?.SetValue(s);
                effect.Parameters["TexelSize"]?.SetValue(new Vector2(1f / s.Width, 1f / s.Height));
                ApplyDraw(gd, effect, "Upsample", verts);
            }
            gd.BlendState = BlendState.Opaque;

            gd.SetRenderTargets(dst);
            effect.Parameters["sceneTex"]?.SetValue(src);
            effect.Parameters["sourceTex"]?.SetValue(mip0);
            effect.Parameters["TexelSize"]?.SetValue(new Vector2(1f / mip0.Width, 1f / mip0.Height));
            ApplyDraw(gd, effect, "Composite", verts);
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
