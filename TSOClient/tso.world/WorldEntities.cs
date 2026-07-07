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

            DrawObjBuf(gd, state, pxOffset, useVelocity);

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
                // Respect Directional Lighting here too - DrawWithVelocity (non-directional lightProcess)
                // used to be hardcoded regardless of `advDir`, so enabling TAA/motion-blur (which forces the
                // velocity technique) silently dropped avatars back to flat/ambient shading even with
                // Directional Lighting on, which read as the light direction itself changing.
                // Also respect Advanced Lighting being OFF entirely: DrawWithVelocity/DrawWithVelocityDirection
                // both shade via the lightmap-sampling lightProcess()/lightProcessDirection() path, which is
                // only valid when Advanced Lighting is on (mirrors psVitaboyAdv/psVitaboyDir below). With it
                // off, that lightmap isn't maintained, so those techniques crushed sims to near-black the
                // moment TAA/motion-blur forced a velocity technique - use the flat AmbientLight-only
                // DrawWithVelocityFlat (mirrors the non-velocity path's psVitaboyNoSSAA) instead.
                int velTech = !WorldConfig.Current.AdvancedLighting ? 9 : (advDir ? 8 : 7);
                effect.CurrentTechnique = effect.Techniques[velTech]; //DrawWithVelocityFlat / DrawWithVelocityDirection / DrawWithVelocity, last three techniques in Vitaboy.fx
                effect.Parameters["ViewProjection"]?.SetValue(state.View * state.Projection);
                // Subworld ModelTranslation fix: state.View already has the translation, but
                // PreviousViewProjection was captured pre-translation -> apply same translation here.
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
            //reuse a scratch list instead of ToList()'ing a fresh one every frame (same pattern as
            //the InstanceScratch buffers below).
            _DynamicDrawScratch.Clear();
            _DynamicDrawScratch.AddRange(dyn);
            DrawObjectsBatched(gd, state, _DynamicDrawScratch, useVelocity);

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

        private void DrawObjBuf(GraphicsDevice gd, WorldState state, Vector2 pxOffset, bool useVelocity)
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

            //reuse a scratch list instead of ToList()'ing a fresh one every frame.
            _StaticDrawScratch.Clear();
            _StaticDrawScratch.AddRange(staticObj);
            DrawObjectsBatched(gd, state, _StaticDrawScratch, useVelocity);

            _2d.EndImmediate();
        }

        // ---------------------------------------------------------------------------- GPU instancing
        // Batches repeated identical objects (same DGRP mesh + same dynamic-sprite-flag state + same
        // floor level) into single DrawInstancedPrimitives calls instead of one draw call per object. See
        // DGRPRenderer.DrawInstanced and ObjectComponent.CanInstance/PrepareInstancedDraw. Purely additive:
        // objects that aren't part of a big-enough group still go through the exact same obj.Draw() path
        // as before, at the same relative position in the (already depth-ordered) draw list. Grouping only
        // reorders instances of the SAME mesh+state relative to each other (never relative to a different
        // group or a non-batched object), which is why this only applies to non-2D-sprite object draws -
        // see the transparency/ordering trade-off noted where WorldConfig.ObjectInstancing is declared.

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

        // Reused across frames for the dynamic/static object draw lists, instead of LINQ .ToList()'ing a
        // brand new List<> (+ backing array) every single frame - Clear() keeps the backing array so
        // steady-state frames (similar object counts) settle into zero extra allocation here.
        private static readonly List<ObjectComponent> _DynamicDrawScratch = new List<ObjectComponent>();
        private static readonly List<ObjectComponent> _StaticDrawScratch = new List<ObjectComponent>();

        private static void DrawObjectsBatched(GraphicsDevice gd, WorldState state, IList<ObjectComponent> objs, bool useVelocity)
        {
            // Let DGRPRenderer.Draw3D know whether the velocity MRT is bound for this whole batch, so it
            // doesn't have to call GetRenderTargets() (a fresh array alloc) once per object - the caller
            // above already knows the answer for the whole loop. Save/restore (not just null on exit) in
            // case a future reentrant draw (e.g. a subworld/portal) nests another DrawObjectsBatched call.
            var prevHint = DGRPRenderer.VelocityHint;
            DGRPRenderer.VelocityHint = useVelocity;
            try
            {
                // Instancing requires true 3D depth-sorted geometry - ObjectComponent.CanInstance() always
                // returns false in 2D mode, so the grouping pass below would just burn allocations for
                // nothing. Skip it entirely and go straight to the per-object path.
                if (!WorldConfig.Current.ObjectInstancing || objs.Count < InstanceBatchMinSize || state.CameraMode == CameraRenderMode._2D)
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
                        FlushInstanceGroup(state, objs, idxList, useVelocity);
                        foreach (var j in idxList) flushed[j] = true;
                    }
                    else
                    {
                        objs[i].Draw(gd, state);
                    }
                }
            }
            finally
            {
                DGRPRenderer.VelocityHint = prevHint;
            }
        }

        private static void FlushInstanceGroup(WorldState state, IList<ObjectComponent> objs, List<int> idxList, bool useVelocity)
        {
            int count = idxList.Count;
            if (_InstWorldScratch.Length < count)
            {
                _InstWorldScratch = new Matrix[count];
                _InstPrevWorldScratch = new Matrix[count];
            }

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
