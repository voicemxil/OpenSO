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

            // MSAA test. The old test rendered to an 8x multisampled target, resolved it and required the
            // pixel to equal EXACTLY Color.Red — which false-negatived MSAA on hardware that actually supports
            // it (the resolve can come back slightly off on OpenGL, and 8x / PreserveContents may be rejected
            // by newer MonoGame even when lower counts work). Instead, ask for a multisampled target and trust
            // the render target's actual (driver-clamped) MultiSampleCount: if the driver gives us >= 2 samples
            // and we can render to it without throwing, hardware MSAA is supported. Try descending counts so a
            // driver that rejects 8x but supports 4x/2x is still detected.
            FSOEnvironment.MSAASupport = false;
            foreach (var samples in new[] { 8, 4, 2 })
            {
                try
                {
                    using (var msaaTarg = new RenderTarget2D(gd, 4, 4, false, SurfaceFormat.Color, DepthFormat.None, samples, RenderTargetUsage.DiscardContents))
                    {
                        gd.SetRenderTarget(msaaTarg);
                        gd.Clear(Color.Red);
                        gd.SetRenderTarget(null);
                        if (msaaTarg.MultiSampleCount >= 2)
                        {
                            FSOEnvironment.MSAASupport = true;
                            break;
                        }
                    }
                }
                catch
                {
                    try { gd.SetRenderTarget(null); } catch { }
                }
            }

            return true;
        }
    }
}
