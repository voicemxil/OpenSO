using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;

namespace FSO.Common.Utils
{
    public class FeatureLevelTest
    {
        public static bool UpdateFeatureLevel(GraphicsDevice gd)
        {
            //if 3d is enabled, check if we support non-power-of-two mipmaps
            if (FSOEnvironment.SoftwareKeyboard && FSOEnvironment.SoftwareDepth)
            {
                FSOEnvironment.EnableNPOTMip = false;
                return true;
            }
            try
            {
                using (var mipTest = new Texture2D(gd, 11, 11, true, SurfaceFormat.Color))
                {
                    var data = new Color[11 * 11];
                    TextureUtils.UploadWithMips(mipTest, gd, data);
                }
            }
            catch (Exception e)
            {
                FSOEnvironment.EnableNPOTMip = false;
            }

            try
            {
                using (var mipTest = new Texture2D(gd, 4, 4, true, SurfaceFormat.Dxt5))
                {
                    var data = new byte[16];
                    mipTest.SetData(data);
                }
            }
            catch (Exception e)
            {
                FSOEnvironment.TexCompressSupport = false;
            }

            // MSAA test. Determine the HIGHEST sample count the GPU can actually render + resolve, not just
            // "is MSAA supported". The old test only tried 8x and required an exact Color.Red: on Apple Silicon
            // GL 8x MSAA resolves to BLACK (the target creates fine but renders wrong), so it reported MSAA
            // entirely unsupported when 4x works — and a UI that then offered 8x produced a black screen.
            // Render to each level (8x -> 4x -> 2x) with the same format the world backbuffer uses, resolve it,
            // and take the highest level that comes back red-ish as the cap. Exposed as FSOEnvironment.MaxMSAA
            // so the settings menu only offers supported tiers and the renderer clamps to it.
            int maxMSAA = 0;
            foreach (var samples in new[] { 8, 4, 2 })
            {
                try
                {
                    using (var msaaTarg = new RenderTarget2D(gd, 4, 4, false, SurfaceFormat.Color, DepthFormat.Depth24Stencil8, samples, RenderTargetUsage.PreserveContents))
                    {
                        gd.SetRenderTarget(msaaTarg);
                        gd.Clear(Color.Red);
                        using (var tex = TextureUtils.CopyAccelerated(gd, msaaTarg))
                        {
                            var px = new Color[tex.Width * tex.Height];
                            tex.GetData(px);
                            gd.SetRenderTarget(null);
                            // Red-ish (not black/garbage) = this level actually renders+resolves. 8x on Apple
                            // Silicon GL creates a target but renders black, so it's correctly rejected here.
                            if (px[0].R > 100 && px[0].G < 100 && px[0].B < 100) { maxMSAA = samples; break; }
                        }
                    }
                }
                catch
                {
                    try { gd.SetRenderTarget(null); } catch { }
                }
            }
            FSOEnvironment.MaxMSAA = maxMSAA;
            FSOEnvironment.MSAASupport = maxMSAA >= 2;

            return true;
        }
    }
}
