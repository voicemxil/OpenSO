using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using FSO.Common.Utils;

namespace FSO.LotView.Utils
{
    /// <summary>
    /// Per-pixel motion blur (3D), McGuire 2012 reconstruction filter. Three passes:
    ///   1. TileMax       — reduce the velocity buffer to KxK tiles (max-magnitude velocity per tile).
    ///   2. NeighborMax   — 3x3 dilation of TileMax so fast streaks reach into neighbouring tiles.
    ///   3. Reconstruction — depth-aware jittered gather along the tile's dominant velocity.
    /// Slots into the resolve chain at PPXDepthEngine.MotionBlurFunc, between scale-resolve and post-AA so
    /// FXAA/SMAA smooth the blurred edges.
    ///
    /// The velocity buffer packs .rg = per-frame UV velocity, .b = linear depth, .a = valid mask. The
    /// reconstruction's depth test is what kills the silhouette ghosting: a background pixel only adopts a
    /// moving foreground's colour when the foreground velocity reaches it AND it is in front.
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
                // Anything missing -> plain blit so the frame still renders.
                gd.BlendState = BlendState.Opaque;
                using (var sb = new SpriteBatch(gd))
                {
                    sb.Begin(blendState: BlendState.Opaque);
                    sb.Draw(src, new Rectangle(0, 0, gd.Viewport.Width, gd.Viewport.Height), Color.White);
                    sb.End();
                }
                return;
            }

            // The resolve chain bound our destination (screen or a ping-pong RT) before calling us. Save it
            // so we can restore it for the final reconstruction pass after the two tile passes.
            var dst = gd.GetRenderTargets();
            var verts = WorldContent.GetTextureVerts(gd);
            gd.BlendState = BlendState.Opaque;

            // --- Pass 1: TileMax (velocity -> tiles) ---
            gd.SetRenderTarget(tileMax);
            effect.Parameters["velocityTex"]?.SetValue(velocity);
            effect.Parameters["SourceTexel"]?.SetValue(new Vector2(1f / velocity.Width, 1f / velocity.Height));
            ApplyAndDraw(gd, effect, "TileMax", verts);

            // --- Pass 2: NeighborMax (3x3 tile dilation) ---
            gd.SetRenderTarget(neighborMax);
            effect.Parameters["tileMaxTex"]?.SetValue(tileMax);
            effect.Parameters["TileTexel"]?.SetValue(new Vector2(1f / tileMax.Width, 1f / tileMax.Height));
            ApplyAndDraw(gd, effect, "NeighborMax", verts);

            // --- Pass 3: Reconstruction (gather to the chain's destination) ---
            gd.SetRenderTargets(dst);
            effect.Parameters["colorTex"]?.SetValue(src);
            effect.Parameters["velocityTex"]?.SetValue(velocity);
            effect.Parameters["neighborMaxTex"]?.SetValue(neighborMax);
            effect.Parameters["ScreenSizePx"]?.SetValue(new Vector2(gd.Viewport.Width, gd.Viewport.Height));
            // ShutterScale = shutter fraction (slider 0..1). velocity is per-frame, so this is automatically
            // frame-rate-coupled: lower fps -> larger per-frame velocity -> longer blur, like a real shutter.
            effect.Parameters["ShutterScale"]?.SetValue(WorldConfig.Current.MotionBlurAmount);
            ApplyAndDraw(gd, effect, "Reconstruction", verts);
        }

        private static void ApplyAndDraw(GraphicsDevice gd, Effect effect, string technique, VertexBuffer verts)
        {
            var tech = effect.Techniques[technique];
            if (tech == null) return; // shader profile / build problem; skip rather than crash
            effect.CurrentTechnique = tech;
            effect.CurrentTechnique.Passes[0].Apply();
            gd.SetVertexBuffer(verts);
            gd.DrawPrimitives(PrimitiveType.TriangleStrip, 0, 2);
        }
    }
}
