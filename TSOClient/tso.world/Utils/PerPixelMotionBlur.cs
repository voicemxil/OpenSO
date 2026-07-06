using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using FSO.Common.Utils;

namespace FSO.LotView.Utils
{
    /// <summary>
    /// Per-pixel motion blur (McGuire 2012): TileMax -> NeighborMax -> depth-aware reconstruction.
    /// Velocity buffer packs .rg = per-frame UV velocity, .b = linear depth, .a = valid mask.
    /// Runs at PPXDepthEngine.MotionBlurFunc, between scale-resolve and post-AA.
    /// </summary>
    public static class PerPixelMotionBlur
    {
        public static void Draw(GraphicsDevice gd, RenderTarget2D src)
        {
            var effect = WorldContent.MotionBlur;
            var velocity = PPXDepthEngine.GetVelocityTarget();
            var tileMax = PPXDepthEngine.GetMBTileMax();
            var neighborMax = PPXDepthEngine.GetMBNeighborMax();
            if (effect == null || velocity == null || tileMax == null || neighborMax == null)
            {
                // anything missing - plain blit
                gd.BlendState = BlendState.Opaque;
                using (var sb = new SpriteBatch(gd))
                {
                    sb.Begin(blendState: BlendState.Opaque);
                    sb.Draw(src, new Rectangle(0, 0, gd.Viewport.Width, gd.Viewport.Height), Color.White);
                    sb.End();
                }
                return;
            }

            // save the chain's bound destination; the tile passes rebind targets
            var dst = gd.GetRenderTargets();
            var verts = WorldContent.GetTextureVerts(gd);
            gd.BlendState = BlendState.Opaque;

            gd.SetRenderTarget(tileMax);
            effect.Parameters["velocityTex"]?.SetValue(velocity);
            effect.Parameters["SourceTexel"]?.SetValue(new Vector2(1f / velocity.Width, 1f / velocity.Height));
            ApplyAndDraw(gd, effect, "TileMax", verts);

            gd.SetRenderTarget(neighborMax);
            effect.Parameters["tileMaxTex"]?.SetValue(tileMax);
            effect.Parameters["TileTexel"]?.SetValue(new Vector2(1f / tileMax.Width, 1f / tileMax.Height));
            ApplyAndDraw(gd, effect, "NeighborMax", verts);

            gd.SetRenderTargets(dst);
            effect.Parameters["colorTex"]?.SetValue(src);
            effect.Parameters["velocityTex"]?.SetValue(velocity);
            effect.Parameters["neighborMaxTex"]?.SetValue(neighborMax);
            effect.Parameters["ScreenSizePx"]?.SetValue(new Vector2(gd.Viewport.Width, gd.Viewport.Height));
            // shutter fraction 0..1; velocity is per-frame, so blur length tracks frame time
            effect.Parameters["ShutterScale"]?.SetValue(WorldConfig.Current.MotionBlurAmount);
            ApplyAndDraw(gd, effect, "Reconstruction", verts);
        }

        private static void ApplyAndDraw(GraphicsDevice gd, Effect effect, string technique, VertexBuffer verts)
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
