using FSO.Common.Utils;
using FSO.LotView.Components;
using FSO.LotView.Model;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Linq;

namespace FSO.LotView
{
    /// <summary>
    /// Handling for the 2D Static layer.
    /// </summary>
    public class WorldStatic
    {
        public static readonly int NUM_2D_BUFFERS = 6;
        public static readonly int BUFFER_STATIC = 0;
        public static readonly int BUFFER_STATIC_DEPTH = 1;
        public static readonly int BUFFER_OBJID = 2;
        public static readonly int BUFFER_THUMB = 3; //used for drawing thumbnails
        public static readonly int BUFFER_THUMB_DEPTH = 4; //used for drawing thumbnails
        public static readonly int BUFFER_LOTTHUMB = 5;
        
        public static readonly int SCROLL_BUFFER = 512; //resolution to add to render size for scroll reasons

        public ScrollBuffer StaticSurface;
        public World World;
        private Blueprint Bp;
        public SkyDomeComponent Dome;

        public WorldStatic(World world)
        {
            World = world;
        }

        public void InitBlueprint(Blueprint bp)
        {
            Bp = bp;
        }

        private Vector2 GetScrollIncrement(Vector2 pxOffset, WorldState state)
        {
            var scrollSize = SCROLL_BUFFER / state.PreciseZoom;
            return new Vector2((float)Math.Floor(pxOffset.X / scrollSize) * scrollSize, (float)Math.Floor(pxOffset.Y / scrollSize) * scrollSize);
        }

        public void PreDraw(GraphicsDevice gd, WorldState state)
        {
            var changes = state.Changes;
            if (changes.DrawImmediate) return;
            if (changes.StaticSurfaceDirty)
            {
                var pxOffset = -state.WorldSpace.GetScreenOffset();
                var newOff = GetScrollIncrement(pxOffset, state);
                var oldCenter = state.CenterTile;
                state.CenterTile += state.WorldSpace.GetTileFromScreen(newOff - pxOffset); //offset the scroll to the position of the scroll buffer.
                var tileOffset = state.CenterTile;

                /** Draw static objects to a texture **/
                Promise<Texture2D> bufferTexture = null;
                Promise<Texture2D> depthTexture = null;
                using (var buffer = state._2D.WithBuffer(BUFFER_STATIC, ref bufferTexture, BUFFER_STATIC_DEPTH, ref depthTexture))
                {
                    while (buffer.NextPass())
                    {
                        World.Architecture.StaticDraw(gd, state, newOff);
                        World.Entities.StaticDraw(gd, state, newOff);
                    }
                }
                StaticSurface = new ScrollBuffer(bufferTexture.Get(), depthTexture.Get(), newOff, new Vector3(tileOffset, 0));
                changes.StaticSurfaceDirty = false; //static surface has been updated!
                state.CenterTile = oldCenter;
            }
            changes.StaticSurface = StaticSurface; //copy so changes can keep track of when we leave this buffer range
        }

        public void Draw(WorldState state)
        {
            var changes = state.Changes;
            if (changes.DrawImmediate)
            {
                return;
            }
            var _2d = state._2D;
            var pxOffset = -state.WorldSpace.GetScreenOffset();
            var tileOffset = state.CenterTile;

            _2d.OffsetPixel(Vector2.Zero);
            _2d.SetScroll(new Vector2());
            _2d.Begin(state.Camera2D);
            state._2D.PreciseZoom = 1f;
            if (StaticSurface != null)
            {
                _2d.DrawScrollBuffer(StaticSurface, pxOffset, new Vector3(tileOffset, 0), state);
                _2d.Pause();
                _2d.Resume();
            }
            state._2D.PreciseZoom = state.PreciseZoom;
        }

        public void DrawBg(GraphicsDevice gd, WorldState state, BoundingBox[] skyBounds, bool forceSurround)
        {
            state.PrepareCamera();
            // The sky bounds are thin walls hugging the lot's perimeter: they detect the background
            // peeking past the lot's EDGES from a camera above the lot. A free camera OUTSIDE the
            // lot bounds can look down at backdrop terrain without its frustum ever crossing those
            // walls — skipping the sky + city backdrop and leaving a void — so treat an
            // out-of-bounds camera as "background visible" unconditionally.
            var camPos = state.Camera.Position;
            var camOutsideLot = camPos.X < 0 || camPos.Z < 0 || camPos.X > Bp.Width * 3 || camPos.Z > Bp.Height * 3;
            // With no surrounding-lot subworlds loaded, the city backdrop is the only geometry
            // covering the ground outside the lot — culling it against the perimeter boxes can
            // only ever produce a void, never save meaningful work.
            var noSurround = Bp.SubWorlds.Count == 0;
            if (forceSurround || noSurround || camOutsideLot || (state.CameraMode == CameraRenderMode._3D && state.Cameras.ExternalTransitionActive()) || skyBounds?.Any(x => x.Intersects(state.Frustum)) != false)
            {
                // The background draws FIRST in the frame, but on preserve-contents targets the
                // depth AND stencil channels still hold LAST frame's lot rendering. The city
                // backdrop draws with a stencil test (draw where stencil != 1) and the lot's own
                // passes write 1s — so wherever the lot covered the screen a frame ago, this
                // frame's backdrop was masked out entirely (a camera pointing straight down inside
                // the lot lost the whole backdrop to the sky dome). Start the background pass with
                // clean depth AND stencil.
                gd.Clear(ClearOptions.DepthBuffer | ClearOptions.Stencil, Color.White, 1, 0);

                if (Dome == null) Dome = new SkyDomeComponent(gd, Bp);
                Dome.BP = Bp;
                Dome.Draw(gd, state);

                World.Surroundings?.DrawSurrounding(gd, state.Camera, Bp.Weather.FogColor, (Bp.SubWorlds.Count > 0) ? 1 : 0, state.TAAJitter);
            }
            gd.Clear(ClearOptions.DepthBuffer, Color.White, 1, 0);

            //if (((WorldCamera3D)state.Camera).FromIntensity > 0) state.CenterTile = state.CenterTile;
        }
    }
}
