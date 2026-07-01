using FSO.LotView.Components;
using FSO.LotView.Effects;
using FSO.LotView.Model;
using FSO.LotView.Utils;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System.Collections.Generic;
using System.Linq;

namespace FSO.LotView
{
    /// <summary>
    /// Draws entities in the world. Drawn after all architecture, before semitransparent architecture and particles.
    /// 
    /// - Objects
    ///   - STATIC: a list of objects that have not changed in a while. drawn to
    ///   - DYNAMIC: a list of objects that have changed recently. Drawn on top of the static buffer
    ///   - 3D: There are no dynamic and static layers in this mode - everything on camera is drawn every frame.
    /// - Avatars
    ///   - 2D: Drawn before all objects. Semitransparent avatars (ghosts) are drawn after all objects.
    ///   - 3D: Z-sorted with objects.
    /// - Particles:
    ///   - 2D: Drawn with the object in dynamic layer.
    ///   - 3D: Drawn somehow
    /// 
    /// (part of lotview 2.0)
    /// </summary>
    public class WorldEntities
    {
        public bool UseStaticBuffer;
        public Blueprint Blueprint;

        //2d rendering mode
        private List<_2DDrawBuffer> StaticObjectsCache = new List<_2DDrawBuffer>();

        public WorldEntities(Blueprint blueprint)
        {
            Blueprint = blueprint;
        }

        private void ClearDrawBuffer(List<_2DDrawBuffer> buf)
        {
            foreach (var b in buf) b.Dispose();
            buf.Clear();
        }

        public void StaticDraw(GraphicsDevice gd, WorldState state, Vector2 pxOffset)
        {
            var changes = Blueprint.Changes;
            //state.PrepareLighting();
            state.PrepareCulling(pxOffset);

            var effect = WorldContent.RCObject;
            gd.BlendState = BlendState.NonPremultiplied;
            effect.ViewProjection = state.ViewProjection;
            effect.JitterNDC = state.TAAJitter; // un-jitter the velocity pass (0 when TAA off)
            // Subworld ModelTranslation fix: in 3D, SubDraw sets Cameras.ModelTranslation so state.View
            // (and the ViewProjection re-derived by the PrepareCulling above) include the subworld offset,
            // but PreviousViewProjection was captured at frame start without it. Apply the same translation
            // so static subworld geometry's velocity collapses to pure camera motion.
            var prevVP = state.PreviousViewProjection;
            if (state.Cameras.ModelTranslation.HasValue)
                prevVP = Matrix.CreateTranslation(-state.Cameras.ModelTranslation.Value) * prevVP;
            effect.PreviousViewProjection = prevVP;

            // In 2D mode StaticDraw renders into a cached sprite buffer (no velocity). But in 3D it renders
            // LIVE to the backbuffer — and this is the path that draws SUBWORLD (surrounding-lot) 3D objects
            // like trees (WorldArchitecture.DrawSub -> SubDraw -> Entities.StaticDraw). So bind the velocity
            // MRT here too; DGRPRenderer auto-selects DrawWithVelocity when a velocity target is present.
            var velocityRT = FSO.Common.Utils.PPXDepthEngine.GetVelocityTarget();
            bool useVelocity = velocityRT != null && state.CameraMode == CameraRenderMode._3D;
            RenderTargetBinding[] savedRTs = null;
            if (useVelocity)
            {
                savedRTs = gd.GetRenderTargets();
                FSO.Common.Utils.PPXDepthEngine.BindVelocityMRT(gd, velocityRT);
                effect.SetTechnique(RCObjectTechniques.DrawWithVelocity);
            }
            else
            {
                effect.SetTechnique(RCObjectTechniques.Draw);
            }

            DrawObjBuf(gd, state, pxOffset);

            if (useVelocity)
            {
                if (savedRTs != null && savedRTs.Length > 0) gd.SetRenderTargets(savedRTs);
                else gd.SetRenderTarget(FSO.Common.Utils.PPXDepthEngine.GetBackbuffer());
            }
            //if (false)
            //{
                //foreach (var sub in Blueprint.SubWorlds) sub.SubDraw(gd, state, (pxOffsetSub) => sub.Entities.StaticDraw(gd, state, pxOffsetSub));
            //}
        }

        public void DrawAvatars(GraphicsDevice gd, WorldState state)
        {
            gd.DepthStencilState = DepthStencilState.Default;
            gd.BlendState = BlendState.AlphaBlend;
            gd.RasterizerState = RasterizerState.CullCounterClockwise;

            var advDir = (WorldConfig.Current.Directional && WorldConfig.Current.AdvancedLighting);
            var pass = advDir ? 5 : WorldConfig.Current.PassOffset * 2;

            var effect = WorldContent.AvatarEffect;

            // Velocity output: scope MRT1 binding around the avatar loop. Vitaboy's DrawWithVelocity is
            // built at the same level_9_1 profile as the rest of Vitaboy.fx using a pre-combined
            // ViewProjection (single mat-mul) — keeps state caching consistent so the in-DrawGeometry
            // shadow-pass technique switch correctly rebinds vsShadow.
            var velocityRT = FSO.Common.Utils.PPXDepthEngine.GetVelocityTarget();
            bool useVelocity = velocityRT != null;
            if (useVelocity)
            {
                FSO.Common.Utils.PPXDepthEngine.BindVelocityMRT(gd, velocityRT);
                effect.CurrentTechnique = effect.Techniques[7]; //DrawWithVelocity, last technique in Vitaboy.fx
                effect.Parameters["ViewProjection"]?.SetValue(state.View * state.Projection);
                // Subworld ModelTranslation fix: state.View already has the translation, but
                // PreviousViewProjection was captured pre-translation -> apply same translation here.
                var prevVP = state.PreviousViewProjection;
                if (state.Cameras.ModelTranslation.HasValue)
                    prevVP = Matrix.CreateTranslation(-state.Cameras.ModelTranslation.Value) * prevVP;
                effect.Parameters["PreviousViewProjection"]?.SetValue(prevVP);
                effect.Parameters["JitterNDC"]?.SetValue(state.TAAJitter); // un-jitter the velocity pass
            }
            else
            {
                effect.CurrentTechnique = WorldContent.AvatarEffect.Techniques[pass];
            }

            effect.Parameters["View"].SetValue(state.View);
            effect.Parameters["Projection"].SetValue(state.Projection);

            var _2d = state._2D;
            var pxOffset = -state.WorldSpace.GetScreenOffset();
            var tileOffset = state.CenterTile;

            _2d.SetScroll(pxOffset);
            _2d.OffsetPixel(new Vector2());
            _2d.OffsetTile(new Vector3());
            _2d.PrepareImmediate(Effects.WorldBatchTechniques.drawZSpriteDepthChannel);

            foreach (var avatar in Blueprint.Avatars)
            {
                if (avatar.Level <= state.Level) avatar.Draw(gd, state);
            }

            if (useVelocity)
            {
                gd.SetRenderTarget(FSO.Common.Utils.PPXDepthEngine.GetBackbuffer());
            }

            gd.RasterizerState = RasterizerState.CullNone;
        }

        public void DrawAvatarTransparency(GraphicsDevice gd, WorldState state)
        {
            if (!state.Cameras.Safe2D)
            {
                foreach (var avatar in Blueprint.Avatars)
                {
                    if (avatar.Level <= state.Level) avatar.DrawHeadline3D(gd, state);
                }
            }
        }

        public void Draw(GraphicsDevice gd, WorldState state)
        {
            var changes = Blueprint.Changes;
            var _2d = state._2D;

            var effect = WorldContent.RCObject;
            gd.BlendState = BlendState.NonPremultiplied;
            effect.ViewProjection = state.ViewProjection;
            effect.JitterNDC = state.TAAJitter; // un-jitter the velocity pass (0 when TAA off)
            // Subworld ModelTranslation fix — see TerrainComponent.Draw / WallComponentRC.Draw.
            var prevVP = state.PreviousViewProjection;
            if (state.Cameras.ModelTranslation.HasValue)
                prevVP = Matrix.CreateTranslation(-state.Cameras.ModelTranslation.Value) * prevVP;
            effect.PreviousViewProjection = prevVP;
            gd.RasterizerState = RasterizerState.CullNone;

            // Per-pixel motion blur / TAA: bind VelocityTarget as MRT1 *only* around the object loop below
            // so non-velocity-aware shaders (terrain, sky, walls, avatars) can't write garbage to MRT1.
            // (Earlier global MRT bind in SetPPXTarget caused alpha=1 + garbage velocity on every pixel.)
            var velocityRT = FSO.Common.Utils.PPXDepthEngine.GetVelocityTarget();
            bool useVelocity = velocityRT != null;
            if (useVelocity)
            {
                FSO.Common.Utils.PPXDepthEngine.BindVelocityMRT(gd, velocityRT);
                effect.SetTechnique(RCObjectTechniques.DrawWithVelocity);
            }
            else
            {
                effect.SetTechnique(RCObjectTechniques.Draw);
            }

            //Draw dynamic objects.
            
            _2d.OffsetPixel(new Vector2());
            _2d.OffsetTile(new Vector3());
            _2d.PrepareImmediate(Effects.WorldBatchTechniques.drawZSpriteDepthChannel);

            //if we're not using static, draw all the objects here instead
            //TODO: in-place re-order the dynamic objects list to shorten sort time? might not matter for lists this short, and would make it harder to use a hashset
            IEnumerable<ObjectComponent> dyn;
            if (changes.DrawImmediate) dyn = Blueprint.Objects;
            else dyn = changes.DynamicObjects;

            gd.BlendState = BlendState.NonPremultiplied;
            dyn = dyn.Where(x => (x.Level <= state.Level) && x.DoDraw(state));
            if (state.CameraMode == CameraRenderMode._3D) //only use for full 3d - the draw order for 2d rotation is a completely different coordinate space.
            {
                foreach (var obj in dyn) obj.UpdateDrawOrder(state);
            }
            dyn = dyn.OrderBy(x => x.DrawOrder);

            gd.BlendState = BlendState.NonPremultiplied;
            foreach (var obj in dyn)
            {
                obj.Draw(gd, state);
            }

            if (useVelocity)
            {
                // Restore single-RT so the subsequent particle/debug/etc. draws don't write MRT1.
                gd.SetRenderTarget(FSO.Common.Utils.PPXDepthEngine.GetBackbuffer());
            }

            _2d.EndImmediate();

            //object particles are always dynamic
            if (!state.TransitioningToCity)
            {
                foreach (var op in Blueprint.ObjectParticles)
                {
                    if (op.Level <= state.Level && op.Owner.Visible && (op.Owner.Position.X > -2043 || op.Owner.Position.Y > -2043))
                        op.Draw(gd, state);
                }

                foreach (var p in Blueprint.Particles)
                {
                    p.Draw(gd, state);
                }
            }

            foreach (DebugLinesComponent debug in Blueprint.DebugLines)
            {
                debug.Draw(gd, state);
            }

            //foreach (var sub in Blueprint.SubWorlds) sub.SubDraw(gd, state, (pxOffsetSub) => sub.Entities.StaticDraw(gd, state, pxOffsetSub));
        }

        private void DrawObjBuf(GraphicsDevice gd, WorldState state, Vector2 pxOffset)
        {
            var _2d = state._2D;

            //foreach (var sub in Blueprint.SubWorlds) sub.DrawObjects(gd, state);

            if (state.CameraMode == CameraRenderMode._2D)
            {
                _2d.SetScroll(pxOffset);
                _2d.OffsetPixel(new Vector2());
                _2d.OffsetTile(new Vector3());
                _2d.PrepareImmediate(Effects.WorldBatchTechniques.drawZSpriteDepthChannel);
            }
            
            var size = new Vector2(state._2D.LastWidth, state._2D.LastHeight);
            var mainBd = state.WorldSpace.GetScreenFromTile(state.CenterTile);
            var diff = pxOffset - mainBd;
            state.WorldRectangle = new Rectangle((pxOffset).ToPoint(), size.ToPoint());
            state.PrepareCulling(pxOffset);

            IEnumerable<ObjectComponent> staticObj;
            if (Blueprint.Changes.Subworld) staticObj = Blueprint.Objects;
            else staticObj = Blueprint.Changes.StaticObjects;
            staticObj = staticObj.Where(x => (x.Level <= state.Level) && x.DoDraw(state)).OrderBy(x => x.DrawOrder);

            foreach (var obj in staticObj)
            {
                obj.Draw(gd, state);
            }

            _2d.EndImmediate();
        }
    }
}
