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
            string msaaLog = "[msaa] ";
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
                            bool ok = px[0].R > 100 && px[0].G < 100 && px[0].B < 100;
                            msaaLog += $"{samples}x(mc{msaaTarg.MultiSampleCount} px{px[0].R},{px[0].G},{px[0].B} {(ok ? "OK" : "BAD")}) ";
                            if (ok) { maxMSAA = samples; break; }
                        }
                    }
                }
                catch (Exception e)
                {
                    try { gd.SetRenderTarget(null); } catch { }
                    msaaLog += $"{samples}x(EXC:{e.GetType().Name}) ";
                }
            }
            FSOEnvironment.MaxMSAA = maxMSAA;
            FSOEnvironment.MSAASupport = maxMSAA >= 2;
            WriteMsaaLog(msaaLog + $"=> MaxMSAA={maxMSAA}");

            return true;
        }

        // Diagnostic: append the per-level MSAA probe result next to the install dir, so the supported tiers
        // can be confirmed on a given GPU. Best-effort; safe to remove once detection is settled.
        private static void WriteMsaaLog(string line)
        {
            try
            {
                var cd = FSOEnvironment.ContentDir;
                string dir = System.IO.Path.IsPathRooted(cd)
                    ? (System.IO.Path.GetDirectoryName(cd.TrimEnd(System.IO.Path.DirectorySeparatorChar, '/')) ?? ".")
                    : ".";
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "openso-msaa.log"), line + "\n");
            }
            catch { }
        }
    }
}
