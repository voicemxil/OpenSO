using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using FSO.Common.Utils;

namespace FSO.LotView.Utils
{
    /// <summary>
    /// Bloom post-process (Bloom.fx). Threshold bright-pass into mip0, Kawase dual-filter downsample chain,
    /// additive upsample back up, then composite scene+bloom to the chain's bound target. Slots into the
    /// resolve chain at PPXDepthEngine.BloomFunc (after post-AA, before sharpen).
    /// </summary>
    public static class BloomPass
    {
        public static void Draw(GraphicsDevice gd, RenderTarget2D src)
        {
            var effect = WorldContent.Bloom;
            int mips = PPXDepthEngine.BloomMipCount;
            if (effect == null || mips < 2)
            {
                // Missing shader/targets -> pass the scene through unchanged so the frame still renders.
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
            // User-facing 0..1 intensity passes straight through. (LDR caveat: the scene caps at 1.0, so
            // proper bloom needs HDR backbuffer + tonemap to fully feel right; with LDR this is as close
            // as we get.)
            effect.Parameters["Intensity"]?.SetValue(cfg.BloomIntensity);
            // Per-mip upsample contribution: 0.7 (Karis/COD canonical). The earlier 0.45 was killing the
            // wide-radius mips that GIVE bloom its characteristic spread/halo around bright objects, so
            // the bloom felt too local. The "screen-wide haze" at low thresholds isn't from the spread —
            // it's from LDR clamping when too much of the image passes the bright-pass. Right tradeoff is
            // a proper bright-pass threshold, not killing the radius.
            // Cascaded effective contributions: mip0=1, mip1=0.70, mip2=0.49, mip3=0.34, mip4=0.24 (total ~2.77).
            effect.Parameters["UpsampleBlend"]?.SetValue(0.70f);

            // Prefilter: scene -> mip0 (bright-pass, downsampled by the half-res target + bilinear read).
            var mip0 = PPXDepthEngine.GetBloomMip(0);
            gd.SetRenderTarget(mip0);
            effect.Parameters["sourceTex"]?.SetValue(src);
            effect.Parameters["TexelSize"]?.SetValue(new Vector2(1f / src.Width, 1f / src.Height));
            ApplyDraw(gd, effect, "Prefilter", verts);

            // Downsample mip0 -> mip1 -> ... -> mip(n-1)
            for (int i = 1; i < mips; i++)
            {
                var s = PPXDepthEngine.GetBloomMip(i - 1);
                gd.SetRenderTarget(PPXDepthEngine.GetBloomMip(i));
                effect.Parameters["sourceTex"]?.SetValue(s);
                effect.Parameters["TexelSize"]?.SetValue(new Vector2(1f / s.Width, 1f / s.Height));
                ApplyDraw(gd, effect, "Downsample", verts);
            }

            // Additive upsample back up: mip(n-1) onto mip(n-2), ..., onto mip0 -> mip0 holds the full bloom.
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

            // Composite scene + bloom -> the chain's destination.
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
