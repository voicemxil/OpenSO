using FSO.Files.RC;
using FSO.LotView.Components;
using FSO.LotView.Effects;
using FSO.LotView.Model;
using FSO.LotView.Utils;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
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
            effect.MipBias = FSO.Common.Utils.PPXDepthEngine.TAAMipBias; // sharper texture mips under TAA at scale<1
            // PreviousViewProjection is captured before subworld ModelTranslation is applied -
            // fold it in so static subworld geometry gets pure camera-motion velocity
            var prevVP = state.PreviousViewProjection;
            if (state.Cameras.ModelTranslation.HasValue)
                prevVP = Matrix.CreateTranslation(-state.Cameras.ModelTranslation.Value) * prevVP;
            effect.PreviousViewProjection = prevVP;

            // in 3D StaticDraw renders live (including subworld objects) - bind the velocity MRT here too
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

            // velocity MRT scoped around the avatar loop. DrawWithVelocity uses a pre-combined
            // ViewProjection so the in-DrawGeometry shadow technique switch still rebinds vsShadow.
            var velocityRT = FSO.Common.Utils.PPXDepthEngine.GetVelocityTarget();
            bool useVelocity = velocityRT != null;
            if (useVelocity)
            {
                FSO.Common.Utils.PPXDepthEngine.BindVelocityMRT(gd, velocityRT);
                // mirror the non-velocity lighting choice: the lightmap-sampling velocity techniques are
                // only valid with Advanced Lighting on; otherwise use the flat DrawWithVelocityFlat
                int velTech = !WorldConfig.Current.AdvancedLighting ? 9 : (advDir ? 8 : 7);
                effect.CurrentTechnique = effect.Techniques[velTech]; //last three techniques in Vitaboy.fx
                effect.Parameters["ViewProjection"]?.SetValue(state.View * state.Projection);
                // fold subworld ModelTranslation into the prev VP (see StaticDraw)
                var prevVP = state.PreviousViewProjection;
                if (state.Cameras.ModelTranslation.HasValue)
                    prevVP = Matrix.CreateTranslation(-state.Cameras.ModelTranslation.Value) * prevVP;
                effect.Parameters["PreviousViewProjection"]?.SetValue(prevVP);
                effect.Parameters["JitterNDC"]?.SetValue(state.TAAJitter); // un-jitter the velocity pass
                effect.Parameters["MipBias"]?.SetValue(FSO.Common.Utils.PPXDepthEngine.TAAMipBias); // sharper mips under TAA
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
                    if (avatar.Level <= state.Level && avatar.VisibleToLocalPlayer(state)) avatar.DrawHeadline3D(gd, state);
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
            effect.MipBias = FSO.Common.Utils.PPXDepthEngine.TAAMipBias; // sharper texture mips under TAA at scale<1
            // subworld ModelTranslation fix - see TerrainComponent.Draw
            var prevVP = state.PreviousViewProjection;
            if (state.Cameras.ModelTranslation.HasValue)
                prevVP = Matrix.CreateTranslation(-state.Cameras.ModelTranslation.Value) * prevVP;
            effect.PreviousViewProjection = prevVP;
            gd.RasterizerState = RasterizerState.CullNone;

            // bind VelocityTarget as MRT1 only around the object loop - non-velocity-aware shaders
            // would write garbage to MRT1
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
            DrawObjectsBatched(gd, state, dyn.ToList());

            if (useVelocity)
            {
                // restore single-RT so subsequent draws don't write MRT1
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

            DrawObjectsBatched(gd, state, staticObj.ToList());

            _2d.EndImmediate();
        }

        // GPU instancing: batch identical objects (same DGRP mesh + sprite flags + level) into single
        // instanced draws (see DGRPRenderer.DrawInstanced, ObjectComponent.CanInstance). Grouping only
        // reorders instances of the same mesh+state relative to each other; everything else draws as before.

        private const int InstanceBatchMinSize = 4;

        private readonly struct InstanceKey : IEquatable<InstanceKey>
        {
            public readonly DGRP3DMesh Mesh;
            public readonly ulong Flags;
            public readonly ulong Flags2;
            public readonly sbyte Level;

            public InstanceKey(DGRP3DMesh mesh, ulong flags, ulong flags2, sbyte level)
            {
                Mesh = mesh;
                Flags = flags;
                Flags2 = flags2;
                Level = level;
            }

            public bool Equals(InstanceKey other) =>
                Mesh == other.Mesh && Flags == other.Flags && Flags2 == other.Flags2 && Level == other.Level;
            public override bool Equals(object obj) => obj is InstanceKey k && Equals(k);
            public override int GetHashCode() => HashCode.Combine(Mesh, Flags, Flags2, Level);
        }

        private static Matrix[] _InstWorldScratch = new Matrix[0];
        private static Matrix[] _InstPrevWorldScratch = new Matrix[0];

        private static void DrawObjectsBatched(GraphicsDevice gd, WorldState state, IList<ObjectComponent> objs)
        {
            if (!WorldConfig.Current.ObjectInstancing || objs.Count < InstanceBatchMinSize)
            {
                for (int i = 0; i < objs.Count; i++) objs[i].Draw(gd, state);
                return;
            }

            var keys = new InstanceKey?[objs.Count];
            Dictionary<InstanceKey, List<int>> groups = null;
            for (int i = 0; i < objs.Count; i++)
            {
                var obj = objs[i];
                if (!obj.CanInstance(state)) continue;
                var mesh = obj.EnsureMesh3D();
                if (mesh == null) continue;

                var key = new InstanceKey(mesh, obj.DynamicSpriteFlags, obj.DynamicSpriteFlags2, obj.Level);
                keys[i] = key;

                groups ??= new Dictionary<InstanceKey, List<int>>();
                if (!groups.TryGetValue(key, out var idxList))
                {
                    idxList = new List<int>();
                    groups[key] = idxList;
                }
                idxList.Add(i);
            }

            if (groups == null)
            {
                for (int i = 0; i < objs.Count; i++) objs[i].Draw(gd, state);
                return;
            }

            var flushed = new bool[objs.Count];
            for (int i = 0; i < objs.Count; i++)
            {
                if (flushed[i]) continue;

                List<int> idxList = null;
                if (keys[i].HasValue) groups.TryGetValue(keys[i].Value, out idxList);

                if (idxList != null && idxList.Count >= InstanceBatchMinSize)
                {
                    FlushInstanceGroup(state, objs, idxList);
                    foreach (var j in idxList) flushed[j] = true;
                }
                else
                {
                    objs[i].Draw(gd, state);
                }
            }
        }

        private static void FlushInstanceGroup(WorldState state, IList<ObjectComponent> objs, List<int> idxList)
        {
            int count = idxList.Count;
            if (_InstWorldScratch.Length < count)
            {
                _InstWorldScratch = new Matrix[count];
                _InstPrevWorldScratch = new Matrix[count];
            }

            // same velocity condition DGRPRenderer.Draw3D uses per-object
            var device = state.Device;
            bool useVelocity = FSO.Common.Utils.PPXDepthEngine.GetVelocityTarget() != null
                && device.GetRenderTargets().Length > 1;

            DGRP3DMesh mesh = null;
            ulong flags = 0, flags2 = 0;
            sbyte level = 0;
            for (int n = 0; n < count; n++)
            {
                var obj = objs[idxList[n]];
                obj.PrepareInstancedDraw(out var world, out var prevWorld);
                _InstWorldScratch[n] = world;
                _InstPrevWorldScratch[n] = prevWorld;
                if (n == 0)
                {
                    mesh = obj.EnsureMesh3D();
                    flags = obj.DynamicSpriteFlags;
                    flags2 = obj.DynamicSpriteFlags2;
                    level = obj.Level;
                }
            }

            DGRPRenderer.DrawInstanced(state, mesh, flags, flags2, level,
                _InstWorldScratch, useVelocity ? _InstPrevWorldScratch : null, count);
        }
    }
}
