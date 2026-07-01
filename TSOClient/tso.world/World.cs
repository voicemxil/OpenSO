using FSO.Common;
using FSO.Common.Model;
using FSO.Common.Rendering.Framework;
using FSO.Common.Rendering.Framework.Model;
using FSO.Common.Utils;
using FSO.LotView.Components;
using FSO.LotView.LMap;
using FSO.LotView.Model;
using FSO.LotView.Platform;
using FSO.LotView.RC;
using FSO.LotView.Utils;
using FSO.LotView.Utils.Camera;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace FSO.LotView
{
    /// <summary>
    /// Represents world (I.E lots in the game.)
    /// </summary>
    public class World : _3DScene
    {
        /// <summary>
        /// Creates a new World instance.
        /// </summary>
        /// <param name="Device">A GraphicsDevice instance.</param>
        public World(GraphicsDevice Device)
            : base(Device)
        {
            Effect e = WorldContent.Grad2DEffect;
            e = WorldContent.GrassEffect;
            e = WorldContent.Light2DEffect;
            e = WorldContent.ParticleEffect;
            e = WorldContent.RCObject;
            e = WorldContent.SSAA;
            e = WorldContent._2DWorldBatchEffect;
        }

        /** How many pixels from each edge of the screen before we start scrolling the view **/
        public int ScrollBounds = 20;
        public uint FrameCounter = 0;
        public uint LastCacheClear = 0;
        public static bool DirectX = false;
        public float Opacity = 1f;
        public float BackbufferScale = 1f;
        public bool ForceAdvLight;
        public bool LimitScroll = true;
        public IRCSurroundings Surroundings;

        public float SmoothZoomTimer = -1;
        public float SmoothZoomFrom = 1f;

        public WorldState State;
        public bool UseBackbuffer = true;
        protected bool HasInitGPU;
        protected bool HasInitBlueprint;
        protected bool HasInit;
        
        public WorldStatic Static;
        public WorldArchitecture Architecture;
        public WorldEntities Entities;
        public IWorldPlatform Platform;

        public bool CanSwitchCameras = true;

        protected LMapBatch Light;
        protected Blueprint Blueprint;

        public event Action OnFullZoomOut;

        public sbyte Stories
        {
            get
            {
                return Blueprint.Stories;
            }
        }

        /// <summary>
        /// Setup anything that needs a GraphicsDevice
        /// </summary>
        /// <param name="layer"></param>
        public override void Initialize(_3DLayer layer)
        {
            base.Initialize(layer);

            /**
             * Setup world state, this object acts as a facade
             * to world objects as well as providing various
             * state settings for the world and helper functions
             */
            State = new WorldState(layer.Device, layer.Device.Viewport.Width, layer.Device.Viewport.Height, this);

            State._2D = new _2DWorldBatch(layer.Device, _2DWorldBatch.NUM_2D_BUFFERS,
                _2DWorldBatch.BUFFER_SURFACE_FORMATS, _2DWorldBatch.FORMAT_ALWAYS_DEPTHSTENCIL, _2DWorldBatch.SCROLL_BUFFER);

            Static = new WorldStatic(this);

            State.OutsidePx = new Texture2D(layer.Device, 1, 1);

            ChangedWorldConfig(layer.Device);

            PPXDepthEngine.InitGD(layer.Device);
            PPXDepthEngine.InitScreenTargets();

            base.Camera = State.Camera;

            HasInitGPU = true;
            HasInit = HasInitGPU & HasInitBlueprint;
            GraphicsModeControl.ModeChanged += SetGraphicsMode;
        }

        public void GameResized()
        {
            PPXDepthEngine.InitScreenTargets();
            var newSize = PPXDepthEngine.GetWidthHeight();
            var ssaa = new Point((int)Math.Round(newSize.X / PPXDepthEngine.SSAA), (int)Math.Round(newSize.Y / PPXDepthEngine.SSAA));
            State._2D.GenBuffers(ssaa.X, ssaa.Y);
            State.SetDimensions(ssaa.ToVector2());

            Blueprint?.Changes?.SetFlag(BlueprintGlobalChanges.ZOOM);

            // InitScreenTargets disposed the velocity buffer (size changed). Re-apply the AA/motion config
            // so it's re-allocated at the new screen size; otherwise motion blur stays disabled until the
            // options dialog is reopened.
            if (State?.Device != null) ChangeAAMode(State.Device);
        }

        // Previous frame's NDC jitter, for computing the per-frame jitter delta handed to the TAA resolve.
        private Vector2 _PrevTAAJitterNDC;

        // Halton sequence — base-N radical-inverse of integer i. Halton(2,3) gives well-distributed 2D
        // samples without clustering, which is exactly what TAA needs for sub-pixel jittering.
        private static float HaltonValue(int i, int b)
        {
            float result = 0f;
            float f = 1f / b;
            int idx = i;
            while (idx > 0)
            {
                result += f * (idx % b);
                idx /= b;
                f /= b;
            }
            return result;
        }

        public virtual void InitDefaultGraphicsMode()
        {
            if (Platform == null)
            {
                SetGraphicsMode(GraphicsModeControl.Mode, true);
            }
            else
            {
                ReinitGraphicsMode();
            }
        }

        public virtual void InitBlueprint(Blueprint blueprint)
        {
            this.Blueprint = blueprint;
            Platform?.Dispose();
            InitDefaultGraphicsMode();
            State.ProjectTilePos = EstTileAtPosWithScrollHeight;

            Entities = new WorldEntities(blueprint);
            Architecture = new WorldArchitecture(blueprint);
            Static?.InitBlueprint(blueprint);
            
            State.Changes = blueprint.Changes;
            GameThread.InUpdate(() =>
            {
                Light?.Init(blueprint);
                State.Rooms.Init(blueprint);
            });

            HasInitBlueprint = true;
            HasInit = HasInitGPU & HasInitBlueprint;
        }

        public void InvalidateZoom()
        {
            if (Blueprint == null) { return; }

            foreach (var item in Blueprint.Objects){
                item.OnZoomChanged(State);
            }
            foreach (var sub in Blueprint.SubWorlds) sub.State.Zoom = State.Zoom;
            Blueprint.Changes.SetFlag(BlueprintGlobalChanges.ZOOM);

            State._2D?.ClearTextureCache();
        }

        public void InvalidatePreciseZoom()
        {
            if (Blueprint == null) { return; }
            Blueprint.Changes.SetFlag(BlueprintGlobalChanges.PRECISE_ZOOM);
        }

        public void InvalidateRotation()
        {
            if (Blueprint == null) { return; }

            foreach (var item in Blueprint.Objects)
            {
                item.OnRotationChanged(State);
            }
            foreach (var sub in Blueprint.SubWorlds) sub.State.Rotation = State.Rotation;
            Blueprint.Changes.SetFlag(BlueprintGlobalChanges.ROTATE);

            State._2D?.ClearTextureCache();
        }

        public void InvalidateScroll()
        {
            if (Blueprint == null) { return; }
            Blueprint.Changes.SetFlag(BlueprintGlobalChanges.SCROLL);
        }

        public void InvalidateFloor()
        {
            if (Blueprint == null) { return; }
            Blueprint.Changes.SetFlag(BlueprintGlobalChanges.LEVEL_CHANGED);
        }

        public bool TestScroll(UpdateState state)
        {
            var mouse = state.MouseState;

            if (State == null) { return false; }

            var screenWidth = State.WorldSpace.WorldPxWidth;
            var screenHeight = State.WorldSpace.WorldPxHeight;

            /** Corners **/
            var xBound = screenWidth - ScrollBounds;
            var yBound = screenHeight - ScrollBounds;

            var cursor = CursorType.Normal;
            var scrollVector = new Vector2(0, 0);
            if (mouse.X > 0 && mouse.Y > 0 && mouse.X < screenWidth && mouse.Y < screenHeight)
            {
                if (mouse.Y <= ScrollBounds)
                {
                    if (mouse.X <= ScrollBounds)
                    {
                        /** Scroll top left **/
                        cursor = CursorType.ArrowUpLeft;
                        scrollVector = new Vector2(-1, -1);
                    }
                    else if (mouse.X >= xBound)
                    {
                        /** Scroll top right **/
                        cursor = CursorType.ArrowUpRight;
                        scrollVector = new Vector2(1, -1);
                    }
                    else
                    {
                        /** Scroll up **/
                        cursor = CursorType.ArrowUp;
                        scrollVector = new Vector2(0, -1);
                    }
                }
                else if (mouse.Y <= yBound)
                {
                    if (mouse.X <= ScrollBounds)
                    {
                        /** Left **/
                        cursor = CursorType.ArrowLeft;
                        scrollVector = new Vector2(-1, 0);
                    }
                    else if (mouse.X >= xBound)
                    {
                        /** Right **/
                        cursor = CursorType.ArrowRight;
                        scrollVector = new Vector2(1, -1);
                    }
                }
                else
                {
                    if (mouse.X <= ScrollBounds)
                    {
                        /** Scroll bottom left **/
                        cursor = CursorType.ArrowDownLeft;
                        scrollVector = new Vector2(-1, 1);
                    }
                    else if (mouse.X >= xBound)
                    {
                        /** Scroll bottom right **/
                        cursor = CursorType.ArrowDownRight;
                        scrollVector = new Vector2(1, 1);
                    }
                    else
                    {
                        /** Scroll down **/
                        cursor = CursorType.ArrowDown;
                        scrollVector = new Vector2(0, 1);
                    }
                }
            }

            if (cursor != CursorType.Normal)
            {
                /**
                 * Calculate scroll vector based on rotation & scroll type
                 */
                scrollVector = new Vector2();

                var basis = GetScrollBasis(true);

                switch (cursor)
                {
                    case CursorType.ArrowDown:
                        scrollVector = basis[1];
                        break;

                    case CursorType.ArrowUp:
                        scrollVector = -basis[1];
                        break;

                    case CursorType.ArrowLeft:
                        scrollVector = -basis[0];
                        break;

                    case CursorType.ArrowRight:
                        scrollVector = basis[0];
                        break;

                    case CursorType.ArrowUpLeft:
                        scrollVector = -basis[1] - basis[0];
                        scrollVector *= new Vector2(1, 0.5f);
                        break;

                    case CursorType.ArrowUpRight:
                        scrollVector = basis[0] - basis[1];
                        scrollVector *= new Vector2(1, 0.5f);
                        break;

                    case CursorType.ArrowDownLeft:
                        scrollVector = basis[1] - basis[0];
                        scrollVector *= new Vector2(1, 0.5f);
                        break;

                    case CursorType.ArrowDownRight:
                        scrollVector = basis[1] + basis[0];
                        scrollVector *= new Vector2(1, 0.5f);
                        break;

                }

                /** We need to scroll **/
                if (scrollVector != Vector2.Zero)
                {
                    State.CenterTile += scrollVector * 0.0625f * (60f * FSOEnvironment.DeltaTime);
                    State.ScrollAnchor = null;
                }
            }

            if (cursor != CursorType.Normal)
            {
                CursorManager.INSTANCE.SetCursor(cursor);
                return true; //we scrolled, return true and set cursor
            }
            return false;
        }


        public virtual void Scroll (Vector2 dir, bool multiplied)
        {
            var basis = GetScrollBasis(multiplied);
            State.CenterTile += dir.X*basis[0] + dir.Y*basis[1];
        }

        public Vector2 Transform(Vector2 dir)
        {
            var basis = GetScrollBasis(false);
            return dir.X * basis[0] + dir.Y * basis[1];
        }

        public void Scroll(Vector2 dir)
        {
            Scroll(dir, true);
        }

        public void SetGraphicsMode(GlobalGraphicsMode mode)
        {
            SetGraphicsMode(mode, false);
        }

        public void ReinitGraphicsMode()
        {
            Platform.SwapBlueprint(Blueprint);
        }

        public void SetGraphicsMode(GlobalGraphicsMode mode, bool instant)
        {
            BackbufferScale = 1;
            var transTime = instant ? 0 : -1;

            switch (mode)
            {
                case GlobalGraphicsMode.Full2D:
                case GlobalGraphicsMode.Hybrid2D:
                    State.SetCameraType(this, CameraControllerType._2D, transTime);
                    Platform = new WorldPlatform2D(Blueprint);
                    break;
                case GlobalGraphicsMode.Full3D:
                    State.SetCameraType(this, CameraControllerType._3D, transTime);
                    Platform = new WorldPlatform3D(Blueprint);
                    State.Zoom = WorldZoom.Near;
                    break;
            }
            ChangeAAMode(m_Device);
            State.Platform = Platform;
        }

        public Tuple<float, float> Get3DTTHeights()
        {
            if (Blueprint == null) { return new Tuple<float, float>(0, 0); }
            var terrainHeight = (Blueprint.InterpAltitudeWithSubworlds(new Vector3(State.CenterTile, 0))) * 3;

            float targHeight;

            if (State.ScrollAnchor?.MyMario != null)
            {
                targHeight = State.ScrollAnchor.MyMario.GetMarioPosition().Z * 3;
            }
            else
            {
                targHeight = Math.Max((Blueprint.InterpAltitudeWithSubworlds(new Vector3(State.Camera.Position.X, State.Camera.Position.Z, 0) / 3) + (State.Level - 1) * 2.95f) * 3, terrainHeight);
            }

            return new Tuple<float, float>(terrainHeight, targHeight);
        }

        public virtual Vector2[] GetScrollBasis(bool multiplied)
        {
            if (State.CameraMode == CameraRenderMode._3D)
            {
                var cam = State.Cameras.ActiveCamera as CameraController3D;
                var mat = Matrix.CreateRotationZ(-(cam?.RotationX ?? 0));
                var z = multiplied ? ((1 + (float)Math.Sqrt(cam?.Zoom3D ?? 1)) / 2) : 1;
                return new Vector2[] {
                    Vector2.Transform(new Vector2(0, -1), mat) * z,
                    Vector2.Transform(new Vector2(1, 0), mat) * z
                };
            }
            else
            {
                var cam = State.Cameras.ActiveCamera as CameraController2D;
                var rcam = cam.Camera;
                var rot = (float)DirectionUtils.PosMod((-0.5 + (int)rcam.Rotation - rcam.RotateOff / 90) * (-Math.PI / 2), Math.PI * 2);

                var mat = Matrix.CreateRotationZ(rot);
                int z = (multiplied) ? (((1 << (3 - (int)State.Zoom)) * 3) / 2) : 1;
                return new Vector2[] {
                    Vector2.Transform(new Vector2(0, -1), mat) * z,
                    Vector2.Transform(new Vector2(1, 0), mat) * z * 2
                };
            }
            /*
            else
            {
                Vector2[] output = new Vector2[2];
                switch (State.Rotation)
                {
                    case WorldRotation.TopLeft:
                        output[1] = new Vector2(2, 2);
                        output[0] = new Vector2(1, -1);
                        break;
                    case WorldRotation.TopRight:
                        output[1] = new Vector2(2, -2);
                        output[0] = new Vector2(-1, -1);
                        break;
                    case WorldRotation.BottomRight:
                        output[1] = new Vector2(-2, -2);
                        output[0] = new Vector2(-1, 1);
                        break;
                    case WorldRotation.BottomLeft:
                        output[1] = new Vector2(-2, 2);
                        output[0] = new Vector2(1, 1);
                        break;
                }
                if (multiplied)
                {
                    int multiplier = ((1 << (3 - (int)State.Zoom)) * 3) / 2;
                    output[0] *= multiplier;
                    output[1] *= multiplier;
                }
                return output;
            }
            */
        }

        public void InitiateSmoothZoom(WorldZoom zoom)
        {
            //TODO: disable in 3d
            if (!WorldConfig.Current.SmoothZoom)
            {
                return;
            }
            SmoothZoomTimer = 0;
            var curScale = (1 << (3 - (int)State.Zoom));
            var zoomScale = (1 << (3 - (int)zoom));

            SmoothZoomFrom = (float)zoomScale / curScale;
            State.PreciseZoom = SmoothZoomFrom;
        }

        public void CenterTo(EntityComponent comp)
        {
            if (comp.Room == 0 || comp.Room == 65531) return; //don't center if the target is out of bounds

            bool isFirstPerson = State.Cameras.ActiveType == CameraControllerType.Direct;
            sbyte level = comp.Level;

            Vector3 pelvisCenter;

            if (comp is AvatarComponent)
            {
                pelvisCenter = isFirstPerson ? ((AvatarComponent)comp).GetHeadlinePos() + comp.Position : ((AvatarComponent)comp).GetPelvisPosition();

                if (((AvatarComponent)comp).MyMario != null)
                {
                    level = ((AvatarComponent)comp).MyMario.DetermineLevel(false);
                }
            }
            else
            {
                pelvisCenter = comp.Position;
            }

            if (State.CameraMode < CameraRenderMode._3D)
            {
                State.Cameras.WithTransitionsDisabled(() =>
                {
                    State.CenterTile = State.Project2DCenterTile(pelvisCenter);
                    State.Camera2D.RotationAnchor = pelvisCenter;
                });
            }
            else
            {
                if (!isFirstPerson)
                {
                    State.CenterTile = new Vector2(pelvisCenter.X, pelvisCenter.Y);
                }
                else
                {
                    State.Cameras.CameraDirect.PreDraw(this);
                }

                State.Cameras.CameraDirect.FirstPersonAvatar = isFirstPerson ? comp as AvatarComponent : null;
                if (isFirstPerson && State.Cameras.ActiveType == CameraControllerType.Direct)
                {
                    level = 5;
                    State.DrawRoofs = true;
                }
            }

            if (State.Level != level) State.Level = level;
        }

        public void RestoreTerrainToCenterTile()
        {
            //center tiles center the lot on a tile at the base level of 0 elevation.
            var pos = Blueprint.InterpAltitude(new Vector3(State.CenterTile, 0)) + (State.Level - 1) * 2.95f;
            State.CenterTile -= (pos / 2.95f) * State.WorldSpace.GetTileFromScreen(new Vector2(0, 230)) / (1 << (3 - (int)State.Zoom));
        }

        public void ToggleFirstPerson(CameraControllerType type)
        {
            if (State.Cameras.ActiveType == type)
            {
                // If switching direct control mode, do that instead of switching back to the normal graphics mode.

                SetGraphicsMode(GraphicsModeControl.Mode, false);
            }
            else
            {
                BackbufferScale = 1;

                if (Platform is WorldPlatform2D)
                {
                    // In first person mode, always use the 3D world platform.
                    SetGraphicsMode(GlobalGraphicsMode.Full3D, true);
                }

                State.SetCameraType(this, type, 0);
                ChangeAAMode(m_Device);
            }
        }

        public override void Update(UpdateState state)
        {
            base.Update(state);
            State.FramesSinceLastDraw++;

            if (Blueprint != null)
            {
                if (!Content.Content.Get().TS1) Blueprint.Weather?.Update();
                var partiCopy = new List<ParticleComponent>(Blueprint.Particles);
                foreach (var particle in partiCopy)
                {
                    particle.Update(null, State);
                }

                partiCopy = new List<ParticleComponent>(Blueprint.ObjectParticles);
                foreach (var particle in partiCopy)
                {
                    particle.Update(null, State);
                }

                Blueprint.SM64?.Update(null, State, Visible);
            }

            if (State.ScrollAnchor != null)
            {
                CenterTo(State.ScrollAnchor);
            }

            State.Cameras.Update(state, this);
            if (SmoothZoomTimer > -1)
            {
                SmoothZoomTimer += 60f * FSOEnvironment.DeltaTime;
                if (SmoothZoomTimer >= 15)
                {
                    State.PreciseZoom = 1f;
                    SmoothZoomTimer = -1;
                }
                else
                {
                    var p = Math.Sin((SmoothZoomTimer / 30.0) * Math.PI);
                    State.PreciseZoom = (float)((p) + (1 - p) * SmoothZoomFrom);
                }
            }

            if (state.WindowFocused && Visible)
            {
                if (FSOEnvironment.Enable3D && CanSwitchCameras)
                {
                    if (state.NewKeys.Contains(Microsoft.Xna.Framework.Input.Keys.Tab) && state.InputManager.GetFocus() == null)
                    {
                        ToggleFirstPerson(CameraControllerType.FirstPerson);
                    }
                }
            }
        }

        protected void BoundView()
        {
            if (!LimitScroll) return;
            //bound the scroll so we can't see gray space.
            float boundfactor = 0.5f;
            switch (State.Zoom)
            {
                case WorldZoom.Near:
                    boundfactor = 1.20f; break;
                case WorldZoom.Medium:
                    boundfactor = 1.05f; break;
            }
            boundfactor *= Blueprint?.Width ?? 64;
            var off = 0.5f * (Blueprint?.Width ?? 64);
            var tile = State.CenterTile;
            tile = new Vector2(Math.Min(boundfactor + off, Math.Max(off - boundfactor, tile.X)), Math.Min(boundfactor + off, Math.Max(off - boundfactor, tile.Y)));
            if (tile != State.CenterTile) State.CenterTile = tile;
        }

        /// <summary>
        /// Pre-Draw
        /// </summary>
        /// <param name="device"></param>
        public override void PreDraw(GraphicsDevice device)
        {
            base.PreDraw(device);
            if (HasInit == false) { return; }
            // Marks "first PrepareCulling call this frame can update PreviousViewProjection". Without this,
            // the 5+ PrepareCulling calls per frame each overwrite PreviousViewProjection -> velocity = 0.
            State.BeginFrameForVelocity();
            // Compute TAA sub-pixel jitter from a Halton(2,3) sequence ONLY when TAA is fully operational
            // (setting on + 3D + history buffers + shader present). Gating it this way means jitter only
            // runs when TAA's accumulation is actually present to resolve it (a half-enabled jitter without
            // accumulation reads as constant shake — the "way too strong" the user saw before the gate).
            // JITTER_PIXELS=0.5 gives the reference ±0.5px range (samples spread across the full pixel
            // footprint), which is what properly resolves stairstepping on high-contrast edges. The earlier
            // 0.25 under-sampled the pixel and left some edges aliased.
            const float JITTER_PIXELS = 0.5f;
            bool taaJitterReady = WorldConfig.Current.TAA
                && State.CameraMode == CameraRenderMode._3D
                && WorldContent.TAA != null
                && FSO.Common.Utils.PPXDepthEngine.GetHistoryPrev() != null;
            if (taaJitterReady)
            {
                int i = (State.TAAFrameIndex++ & 0xF) + 1; // 1..16, Halton starts at sample 1
                float hx = HaltonValue(i, 2) - 0.5f; // [-0.5, +0.5)
                float hy = HaltonValue(i, 3) - 0.5f;
                var bb = FSO.Common.Utils.PPXDepthEngine.GetBackbuffer();
                int w = bb?.Width ?? device.Viewport.Width;
                int h = bb?.Height ?? device.Viewport.Height;
                // Sub-pixel offset in PIXELS, range ±JITTER_PIXELS (reference ±0.5px footprint at 0.5).
                float jpxX = hx * (2f * JITTER_PIXELS);
                float jpxY = hy * (2f * JITTER_PIXELS);
                // Camera is PERSPECTIVE — jitter is applied as an NDC translation via Projection.M31/M32
                // (depth-independent for perspective). NDC<->pixel: ndc = 2*px/dim, hence the 2x (this is
                // the standard pixel->NDC conversion, NOT a doubling of the jitter amount).
                var ndcJitter = new Vector2(2f * jpxX / w, 2f * jpxY / h);
                State.TAAJitter = ndcJitter;
                // Publish for the sky dome (no WorldState there). The velocity pass now subtracts this jitter
                // itself (JitterNDC uniform), so the velocity buffer is jitter-free — TAA reprojection no
                // longer needs the JitterDelta cancellation (leaving it would double-correct), so zero it.
                FSO.Common.Utils.PPXDepthEngine.TAAJitterNDC = ndcJitter;
                FSO.LotView.Utils.TAAResolve.JitterDeltaUV = Vector2.Zero;
                _PrevTAAJitterNDC = ndcJitter;
            }
            else
            {
                State.TAAJitter = Vector2.Zero;
                FSO.Common.Utils.PPXDepthEngine.TAAJitterNDC = Vector2.Zero;
                FSO.LotView.Utils.TAAResolve.JitterDeltaUV = Vector2.Zero;
                _PrevTAAJitterNDC = Vector2.Zero;
            }
            State.Cameras.PreDraw(this);
            // Snapshot the active 3D camera's projection params for GTAO's depth linearization.
            // Falls through when no 3D camera (2D modes don't write velocity, so AO is gated off anyway).
            var active3D = (State.Cameras.ActiveCamera as FSO.LotView.Utils.Camera.CameraController3D)?.Camera;
            if (active3D != null && device != null)
            {
                float aspect = (device.Viewport.Height > 0) ? ((float)device.Viewport.Width / device.Viewport.Height) : 1f;
                FSO.LotView.Utils.AOPass.SetCamera(active3D, aspect);
            }
            BoundView();
            State._2D.PreciseZoom = State.PreciseZoom;
            State.OutsideColor = Blueprint.OutsideColor;
            FSO.Common.Rendering.Framework.GameScreen.ClearColor = new Color(new Color(0x72, 0x72, 0x72).ToVector4() * State.OutsideColor.ToVector4());
            foreach (var sub in Blueprint.SubWorlds) sub.PreDraw(device, State);
            State.UpdateInterpolation();
            if (Blueprint != null)
            {
                foreach (var ent in Blueprint.Objects)
                {
                    ent.Update(null, State);
                }
            }

            //For all the tiles in the dirty list, re-render them
            //PPXDepthEngine.SetPPXTarget(null, null, true);
            State.PrepareLighting();
            State._2D.Begin(this.State.Camera2D);
            Blueprint.Changes.PreDraw(device, State);
            Static?.PreDraw(device, State);

            if (UseBackbuffer && Visible)
            {
                PPXDepthEngine.SetPPXTarget(null, null, true);
                InternalDraw(device);
                device.SetRenderTarget(null);
            }

            return;
        }

        /// <summary>
        /// We will just take over the whole rendering of this scene :)
        /// </summary>
        /// <param name="device"></param>
        public override void Draw(GraphicsDevice device){
            if (HasInit == false) { return; }

            FrameCounter++;
            if (FrameCounter < LastCacheClear + 60*60)
            {
                State._2D.ClearTextureCache();
            }
            if (!UseBackbuffer)
                InternalDraw(device);
            else
            {
                PPXDepthEngine.WithOpacity = State.CameraMode < CameraRenderMode._3D;
                PPXDepthEngine.DrawBackbuffer(Opacity, BackbufferScale);
            }
            return;
        }

        protected virtual void InternalDraw(GraphicsDevice device)
        {
            device.RasterizerState = RasterizerState.CullNone;
            if (State.CameraMode == CameraRenderMode._3D) device.Clear(State.OutsideColor);
            State.PrepareLighting();
            State._2D.OutputDepth = true;
            
            State._2D.Begin(this.State.Camera2D);

            //State._2D.PreciseZoom = State.PreciseZoom;
            State._2D.ResetMatrices(device.Viewport.Width, device.Viewport.Height);

            device.DepthStencilState = DepthStencilState.Default;
            if (State.CameraMode == CameraRenderMode._3D) Static?.DrawBg(State.Device, State, SkyBounds, false);
            Architecture.Draw2D(device, State);
            Static?.Draw(State);
            State.PrepareCamera();
            Entities.DrawAvatars(device, State);
            Entities.Draw(device, State);
            Entities.DrawAvatarTransparency(device, State);

            Blueprint?.SM64?.Draw(device, State);

            State._2D.OutputDepth = false;
        }

        public void Force2DPredraw(GraphicsDevice device)
        {
            Blueprint.Changes.PreDraw(device, State);
        }

        public float? BoxRC2(Ray ray, float tileSize)
        {
            var px = (ray.Direction.X > 0);
            var py = (ray.Direction.Z > 0);
            //find current tile
            int x = (!px) ? (int)Math.Ceiling(ray.Position.X / tileSize) :
                           (int)(ray.Position.X / tileSize);
            int y = (!py) ? (int)Math.Ceiling(ray.Position.Z / tileSize) :
                           (int)(ray.Position.Z / tileSize);

            //find next tile boundary
            float nx = ((px) ? (x + 1) : (x - 1)) * 3;
            float ny = ((py) ? (y + 1) : (y - 1)) * 3;

            const float Epsilon = 1e-6f;
            float? min = null;
            if (Math.Abs(ray.Direction.X) > Epsilon)
            {
                min = (nx - ray.Position.X) / ray.Direction.X;
            }

            if (Math.Abs(ray.Direction.Z) > Epsilon)
            {
                var min2 = (ny - ray.Position.Z) / ray.Direction.Z;
                if (min == null || min.Value > min2) min = min2;
            }
            return min;
        }

        public float? BoxRC(Ray ray, BoundingBox box)
        {
            const float Epsilon = 1e-6f;

            float? tMin = null, tMax = null;

            if (Math.Abs(ray.Direction.X) < Epsilon)
            {
                if (ray.Position.X < box.Min.X || ray.Position.X > box.Max.X)
                    return null;
            }
            else
            {
                tMin = (box.Min.X - ray.Position.X) / ray.Direction.X;
                tMax = (box.Max.X - ray.Position.X) / ray.Direction.X;

                if (tMin > tMax)
                {
                    var temp = tMin;
                    tMin = tMax;
                    tMax = temp;
                }
                if (tMin <= 0 || (ray.Direction.X >= 0 && tMin == 0)) tMin = tMax;
            }

            if (Math.Abs(ray.Direction.Z) < Epsilon)
            {
                if (ray.Position.Z < box.Min.Z || ray.Position.Z > box.Max.Z)
                    return null;
            }
            else
            {
                var tMinZ = (box.Min.Z - ray.Position.Z) / ray.Direction.Z;
                var tMaxZ = (box.Max.Z - ray.Position.Z) / ray.Direction.Z;

                if (tMinZ > tMaxZ)
                {
                    var temp = tMinZ;
                    tMinZ = tMaxZ;
                    tMaxZ = temp;
                }
                if (tMinZ < 0 || (ray.Direction.Z >= 0 && tMinZ == 0)) tMinZ = tMaxZ;

                if (!tMin.HasValue || tMin > tMinZ) tMin = tMinZ;
                if (!tMax.HasValue || tMaxZ > tMax) tMax = tMaxZ;
            }

            // a negative tMin means that the intersection point is behind the ray's origin
            // we discard these as not hitting the AABB
            if (tMin < 0) return null;

            return tMin;
        }

        public Vector2 EstTileAtPosWithScroll(Vector2 pos, sbyte level = -1)
        {
            if (level == -1) level = State.Level;
            var ray = State.CameraRayAtScreenPos(pos, level);

            return EstTileAtPosWithScroll(ray, level).Value;
        }

        public Vector2? EstTileAtPosWithScroll(Ray ray, sbyte level, bool canFail = false)
        {
            Ray baseRay = ray;
            var baseBox = new BoundingBox(new Vector3(0, -5000, 0), new Vector3(Blueprint.Width * 3, 5000, Blueprint.Height * 3));
            if (baseBox.Contains(ray.Position) != ContainmentType.Contains)
            {
                //move ray start inside box
                var i = baseBox.Intersects(ray);
                if (i != null)
                {
                    ray.Position += ray.Direction * (i.Value + 0.01f);
                }
            }

            var mx = (int)ray.Position.X / 3;
            var my = (int)ray.Position.Z / 3;

            var px = (ray.Direction.X > 0);
            var py = (ray.Direction.Z > 0);

            var canProj = Blueprint?.Altitude != null;

            int iteration = 0;
            while (mx >= 0 && mx < Blueprint.Width && my >= 0 && my < Blueprint.Width && canProj)
            {
                //test triangle 1. (centre of tile down xz, we lean towards positive x)
                var plane = new Plane(
                    new Vector3(mx * 3, Blueprint.GetAltPoint(mx, my) * Blueprint.TerrainFactor * 3, my * 3),
                    new Vector3(mx * 3 + 3, Blueprint.GetAltPoint(mx + 1, my) * Blueprint.TerrainFactor * 3, my * 3),
                    new Vector3(mx * 3 + 3, Blueprint.GetAltPoint(mx + 1, my + 1) * Blueprint.TerrainFactor * 3, my * 3 + 3)
                    );
                var tBounds = new BoundingBox(new Vector3(mx * 3, -5000, my * 3), new Vector3(mx * 3 + 3, 5000, my * 3 + 3));

                var t1 = ray.Intersects(plane);
                var t2 = BoxRC2(ray, 3);
                //var t2 = BoxRC(ray, tBounds);
                //if (plane.DotCoordinate(ray.Position) > 0) t1 = 0;
                if (t1 != null && t2 != null && t1.Value < t2.Value)
                {
                    //hit the ground...
                    var tentative = ray.Position + ray.Direction * (t1.Value + 0.00001f);

                    //did it hit the correct side of the triangle?
                    var mySide = ((tentative.X / 3) % 1) - ((tentative.Z / 3) % 1);
                    if (mySide >= 0)
                    {
                        return new Vector2(tentative.X / 3, tentative.Z / 3);
                    }
                    else
                    {
                        //test the other side (positive z)
                        plane = new Plane(
                            new Vector3(mx * 3, Blueprint.GetAltPoint(mx, my) * Blueprint.TerrainFactor * 3, my * 3),
                            new Vector3(mx * 3, Blueprint.GetAltPoint(mx, my + 1) * Blueprint.TerrainFactor * 3, my * 3 + 3),
                            new Vector3(mx * 3 + 3, Blueprint.GetAltPoint(mx + 1, my + 1) * Blueprint.TerrainFactor * 3, my * 3 + 3)
                            );
                        t1 = ray.Intersects(plane);
                        if (t1 != null && t2 != null && t1.Value < t2.Value)
                        {
                            //hit the other side
                            tentative = ray.Position + ray.Direction * (t1.Value + 0.00001f);
                            return new Vector2(tentative.X / 3, tentative.Z / 3);
                        }
                    }
                }
                if (t2 == null) break;
                ray.Position += ray.Direction * (t2.Value + 0.00001f);

                mx = (!px) ? ((int)Math.Ceiling(ray.Position.X / 3) - 1) :
                               (int)(ray.Position.X / 3);
                my = (!py) ? ((int)Math.Ceiling(ray.Position.Z / 3) - 1) :
                               (int)(ray.Position.Z / 3);

                if (iteration++ > 1000) break;
            }

            // Failed to cast a ray into the main world. If there are subworlds, try there.
            if (Blueprint.SubWorlds.Count > 0)
            {
                foreach (var nextWorld in Blueprint.SubWorlds)
                {
                    Ray newRay = baseRay;
                    newRay.Position -= new Vector3(nextWorld.GlobalPosition.X * -3, nextWorld.Blueprint.BaseAlt * nextWorld.Blueprint.TerrainFactor * -3, nextWorld.GlobalPosition.Y * -3);
                    var subPos = nextWorld.EstTileAtPosWithScroll(newRay, level, true);

                    if (subPos == null)
                    {
                        continue;
                    }

                    return subPos.Value - nextWorld.GlobalPosition;
                }
            }

            if (canFail)
            {
                return null;
            }

            //fall back to base positioning
            var bplane = new Plane(new Vector3(0, 0, 0), new Vector3(Blueprint.Width * 3, 0, 0), new Vector3(0, 0, Blueprint.Height * 3));
            if (ray.Position.Y < 0)
            {
                ray.Direction *= -1;
            }

            var cast = ray.Intersects(bplane);
            if (cast != null)
            {
                ray.Position += ray.Direction * (cast.Value + 0.01f);
                return new Vector2(ray.Position.X / 3, ray.Position.Z / 3);
            }

            return new Vector2(0, 0);
        }

        public Vector3? EstTileAtPosWithScroll3D(Vector2 pos, sbyte startFloor = -1, bool canFail = false)
        {
            var initialRay = State.CameraRayAtScreenPos(pos, 1);

            bool pointingUp = initialRay.Direction.Y > 0;

            if (startFloor == -1) startFloor = State.Level;
            sbyte endFloor = 0;
            sbyte iterator = -1;

            if (pointingUp)
            {
                (startFloor, endFloor) = ((sbyte)(endFloor + 1), (sbyte)(startFloor + 1));
                iterator = 1;
            }

            for (sbyte floor = startFloor; floor != endFloor; floor += iterator)
            {
                var ray = State.CameraRayAtScreenPos(pos, floor);
                var result = EstTileAtPosWithScroll(ray, floor, true);
                if (result.HasValue && (floor == 1 || (Blueprint.TileInbounds(result.Value) && Blueprint.GetFloor((short)result.Value.X, (short)result.Value.Y, floor).Pattern != 0)))
                {
                    return new Vector3(result.Value, floor);
                }
            }

            if (canFail) return null;

            return new Vector3(EstTileAtPosWithScroll(pos), State.Level);
        }

        public Vector3 EstTileAtPosWithScrollHeight(Vector2 pos, sbyte startFloor = -1)
        {
            var result = EstTileAtPosWithScroll3D(pos, startFloor).Value;

            float altitude = Blueprint.InterpAltitudeWithSubworlds(result);

            result.Z = altitude + (result.Z-1) * 2.95f;
            return result;
        }

        /// <summary>
        /// Gets the ID of the object at a given position.
        /// </summary>
        /// <param name="x">X position of object.</param>
        /// <param name="y">Y position of object.</param>
        /// <param name="gd">GraphicsDevice instance.</param>
        /// <returns>ID of object at position if found.</returns>
        public short GetObjectIDAtScreenPos(int x, int y, GraphicsDevice gd)
        {
            State._2D.Begin(this.State.Camera2D);
            return Platform.GetObjectIDAtScreenPos(x, y, gd, State);
        }

         /// <summary>
        /// Gets an object group's thumbnail provided an array of objects.
        /// </summary>
        /// <param name="objects">The object components to draw.</param>
        /// <param name="gd">GraphicsDevice instance.</param>
        /// <param name="state">WorldState instance.</param>
        /// <returns>Object's ID if the object was found at the given position.</returns>
        public Texture2D GetObjectThumb(ObjectComponent[] objects, Vector3[] positions, GraphicsDevice gd)
        {
            State._2D.Begin(this.State.Camera2D);
            return Platform.GetObjectThumb(objects, positions, gd, State);
        }

        public Texture2D GetLotThumb(GraphicsDevice gd, Action<Texture2D> rooflessCallback)
        {
            State._2D.Begin(this.State.Camera2D);
            var thumb = Platform.GetLotThumb(gd, State, rooflessCallback);
            // The 2D thumb path leaves the PPX Backbuffer (a render target) bound via SetPPXTarget(null);
            // restore the real backbuffer so a following Present (this can run outside the draw loop)
            // doesn't throw "Cannot call Present when a render target is active".
            gd.SetRenderTarget(null);
            return thumb;
        }

        public void ChangeAAMode(GraphicsDevice gd)
        {
            var lastm = PPXDepthEngine.MSAA;
            var lasts = PPXDepthEngine.SSAA;
            var cfg = WorldConfig.Current;

            // Decoupled: hardware MSAA and render scale are independent and can combine.
            var msaa = cfg.MSAA;                                         //0/2/4/8
            float scale = (cfg.RenderScale > 0f) ? cfg.RenderScale : 1f; //<1 upscale, 1 native, >1 supersample
            // Back-compat: an older config only set the legacy int SuperSampling; honor it at default RenderScale.
            if (scale == 1f && cfg.SuperSampling > 1) scale = cfg.SuperSampling;

            // Back-compat: an older/stale options UI only sets the legacy WorldConfig.AA preset (0/1/2). If the
            // decoupled fields were left at defaults but AA is set, derive MSAA/render scale from it so AA
            // still works regardless of which UI build is running.
            if (msaa == 0 && scale == 1f && cfg.AA > 0)
            {
                if (cfg.AA == 1) msaa = 4;
                else scale = 2f;
            }

            PPXDepthEngine.MSAA = msaa;
            PPXDepthEngine.SSAA = scale;

            // Render scale is a 3D-only feature. It sizes the shared backbuffer, but the 2D sprite scene is
            // pixel-exact to the viewport (ortho camera), so a non-native backbuffer crops it. Neutralize
            // render scale entirely in 2D modes: supersampling (>1) folds into hardware MSAA (free quality,
            // no resize), and upscaling (<1) is simply disabled so the 2D scene always renders at native
            // resolution (no crop). Separating geometry-buffer resolution from the 2D sprites would be the
            // "true" fix but is a much larger renderer change; 3D-only is the agreed scope for now.
            if (State.CameraMode < CameraRenderMode._3D && PPXDepthEngine.SSAA != 1f)
            {
                if (PPXDepthEngine.SSAA > 1f)
                    PPXDepthEngine.MSAA = System.Math.Max(PPXDepthEngine.MSAA, 8);
                PPXDepthEngine.SSAA = 1f;
            }

            // Resolve pass. The plain box-downsample is the default supersample resolve; FXAA/SMAA/FSR are
            // post-process passes applied via PostProcessFunc once their effects are built (Windows shader
            // build). When a pass's shader is missing the chooser leaves it off, so this is safe everywhere.
            // Supersampling (scale > 1) uses the box downsample; upscaling (scale < 1) uses the FSR bicubic
            // upscale. Falls back to the box resolve if the FSR shader isn't present (e.g. non-HiDef device).
            PPXDepthEngine.SSAAFunc = (PPXDepthEngine.SSAA < 0.999f && WorldContent.FSR != null) ? FSRUpscale.Draw : SSAADownsample.Draw;

            // FXAA is the only post-process pass built so far, so any selected post-AA mode (PostAA > 0,
            // including the SMAA presets) routes through it until SMAA's own shaders land. Sharpen (FSR) is
            // a separate pass and stays off here until its shader exists. null => DrawBackbuffer keeps the
            // plain blit (no behaviour change). DrawBackbuffer only invokes this when SSAA == 1.
            // Spatial post-AA selector (SMAA > FXAA > off). Independent of TAA: the spatial pass runs first,
            // then TAA gets its own stage AFTER it (see DrawBackbuffer / PPXDepthEngine.TAAFunc).
            System.Action<GraphicsDevice, RenderTarget2D> postFn = null;
            if (cfg.PostAA >= 2 && WorldContent.SMAA != null && WorldContent.SMAAAreaTex != null && WorldContent.SMAASearchTex != null)
                postFn = SMAAResolve.Draw;
            else if (cfg.PostAA >= 1 && WorldContent.FXAA != null)
                postFn = PostProcessAA.Draw;
            PPXDepthEngine.PostProcessFunc = postFn;

            // Temporal AA: separate resolve-chain stage applied AFTER the spatial AA above, so FXAA/SMAA and
            // TAA compose (spatial edge smoothing + temporal stabilization) instead of being mutually
            // exclusive. Needs the velocity buffer, so 3D mode + TAA/MotionBlur content present.
            bool taaReady = cfg.TAA && State.CameraMode == CameraRenderMode._3D
                            && WorldContent.TAA != null && WorldContent.MotionBlur != null;
            PPXDepthEngine.TAAFunc = taaReady ? TAAResolve.Draw : null;

            // FSR RCAS sharpening: a final, user-controlled pass over the resolved frame, available at ANY
            // render scale (native, supersampled/downscaled, or upscaled). Note this is separate from the
            // downscale RESOLVE — supersampling resolves with the box/tent (SSAAFunc), never FSR — so RCAS
            // here is just optional sharpening, not "FSR downscaling".
            bool sharpen = cfg.Sharpen > 0 && cfg.SharpenAmount > 0f && WorldContent.FSR != null;
            PPXDepthEngine.SharpenFunc = sharpen ? RCASSharpen.Draw : null;

            if (lastm != PPXDepthEngine.MSAA || lasts != PPXDepthEngine.SSAA) PPXDepthEngine.InitScreenTargets();

            // Velocity buffer for TAA / per-pixel motion blur. Only meaningful in 3D mode (the 2D path is
            // cached sprites; for it MotionBlur=1 uses a cheap camera-delta blur with no velocity buffer).
            // Allocates ~16MB at 1080p; freed when both features are off.
            bool wantVelocity = FSOEnvironment.Enable3D && State.CameraMode == CameraRenderMode._3D
                                && (cfg.TAA || cfg.MotionBlur == 2 || cfg.VelocityDebug);
            PPXDepthEngine.EnableVelocityTarget(wantVelocity);
            // History buffers for TAA's ping-pong. Allocated alongside velocity (same scope as TAA itself).
            PPXDepthEngine.EnableHistoryTargets(cfg.TAA && State.CameraMode == CameraRenderMode._3D);

            // Per-pixel motion blur consumer: reads color + velocity, samples N taps along velocity.
            bool motionBlur3D = wantVelocity && cfg.MotionBlur == 2 && WorldContent.MotionBlur != null && cfg.MotionBlurAmount > 0f;
            PPXDepthEngine.MotionBlurFunc = motionBlur3D ? PerPixelMotionBlur.Draw : null;

            // Bloom: post-process, independent of velocity/3D. Enabled when on with a non-zero intensity
            // and the shader is present.
            bool bloom = cfg.Bloom && cfg.BloomIntensity > 0f && WorldContent.Bloom != null;
            PPXDepthEngine.BloomFunc = bloom ? Utils.BloomPass.Draw : null;

            // GTAO/SSAO: functional but DISABLED for now — the path and its UI are hidden until the grass
            // (and other content) is ready for it. Flip AOEnabled to re-enable; the menu controls in
            // UIGraphicsOptionsDialog must be restored alongside. Kept wired (not deleted) so it's a one-line
            // re-enable.
            const bool AOEnabled = false;
            bool ao = AOEnabled && wantVelocity && cfg.AO && cfg.AOIntensity > 0f && WorldContent.GTAO != null;
            PPXDepthEngine.AOFunc = ao ? Utils.AOPass.Draw : null;
            // AO needs the velocity buffer too — force-enable it even when TAA/motion blur are off.
            if (AOEnabled && cfg.AO && cfg.AOIntensity > 0f && WorldContent.GTAO != null && State.CameraMode == CameraRenderMode._3D)
            {
                PPXDepthEngine.EnableVelocityTarget(true);
                PPXDepthEngine.AOFunc = Utils.AOPass.Draw;
            }

            // Velocity diagnostic visualizer: when on, overrides the entire post chain in DrawBackbuffer
            // so the user sees the raw MRT1 buffer instead of the scene. Surfacing this lets us debug
            // which shaders' DrawWithVelocity techniques are correct without guessing from blur artifacts.
            bool velocityDebug = wantVelocity && cfg.VelocityDebug && WorldContent.VelocityViz != null;
            PPXDepthEngine.VelocityDebugFunc = velocityDebug ? Utils.VelocityVisualizer.Draw : null;
        }

        // --- Post pipeline config for the 3D city/map view -----------------------------------------------
        // The city/map renderer (FSO.LotView is not its owner - it's a separate Terrain scene) has no World
        // or WorldState, so it can't drive ChangeAAMode. This static path configures the shared PPXDepthEngine
        // for it: same WorldConfig.Current source as the lot path (so MSAA / render scale stay consistent),
        // always 3D, and - for now - with the velocity-dependent passes (TAA, per-pixel motion blur, AO,
        // velocity debug) left OFF, because the terrain renderer doesn't emit a velocity buffer yet. The
        // heavy target (re)allocation only runs when the scale / MSAA / viewport actually changes.
        private static int _CityLastMSAA = -1;
        private static float _CityLastSSAA = -1f;
        private static int _CityLastW = -1, _CityLastH = -1;
        public static void ConfigureCityAA(GraphicsDevice gd)
        {
            PPXDepthEngine.InitGD(gd);
            var cfg = WorldConfig.Current;

            var msaa = cfg.MSAA;
            float scale = (cfg.RenderScale > 0f) ? cfg.RenderScale : 1f;
            if (scale == 1f && cfg.SuperSampling > 1) scale = cfg.SuperSampling;
            if (msaa == 0 && scale == 1f && cfg.AA > 0) { if (cfg.AA == 1) msaa = 4; else scale = 2f; }

            PPXDepthEngine.MSAA = msaa;
            PPXDepthEngine.SSAA = scale;

            PPXDepthEngine.SSAAFunc = (scale < 0.999f && WorldContent.FSR != null) ? FSRUpscale.Draw : SSAADownsample.Draw;

            System.Action<GraphicsDevice, RenderTarget2D> postFn = null;
            if (cfg.PostAA >= 2 && WorldContent.SMAA != null && WorldContent.SMAAAreaTex != null && WorldContent.SMAASearchTex != null)
                postFn = SMAAResolve.Draw;
            else if (cfg.PostAA >= 1 && WorldContent.FXAA != null)
                postFn = PostProcessAA.Draw;
            PPXDepthEngine.PostProcessFunc = postFn;

            bool bloom = cfg.Bloom && cfg.BloomIntensity > 0f && WorldContent.Bloom != null;
            PPXDepthEngine.BloomFunc = bloom ? Utils.BloomPass.Draw : null;

            // RCAS sharpen — user-controlled, available at any render scale (the downscale resolve uses the
            // box/tent, not FSR, so this is just optional sharpening).
            bool sharpen = cfg.Sharpen > 0 && cfg.SharpenAmount > 0f && WorldContent.FSR != null;
            PPXDepthEngine.SharpenFunc = sharpen ? RCASSharpen.Draw : null;

            PPXDepthEngine.WithOpacity = false; //3D scene is opaque; no per-pixel opacity blend on resolve.

            // Re-allocate shared targets only on a real size/scale/MSAA change (resize, settings change, or the
            // lot path having left them sized differently). InitScreenTargets is expensive - never per-frame.
            int w = gd.Viewport.Width, h = gd.Viewport.Height;
            var bb = PPXDepthEngine.GetBackbuffer();
            int wantW = System.Math.Max(1, (int)System.Math.Round(scale * w));
            int wantH = System.Math.Max(1, (int)System.Math.Round(scale * h));
            if (bb == null || _CityLastMSAA != msaa || _CityLastSSAA != scale || _CityLastW != w || _CityLastH != h
                || bb.Width != wantW || bb.Height != wantH)
            {
                PPXDepthEngine.InitScreenTargets();
                _CityLastMSAA = msaa; _CityLastSSAA = scale; _CityLastW = w; _CityLastH = h;
            }

            // Velocity buffer for per-pixel motion blur / TAA / velocity debug. The city terrain is static
            // geometry, so velocity is camera-induced - Terrain.Draw tracks the previous BaseMatrix and selects
            // the pass-5 velocity technique. Allocated AFTER InitScreenTargets so it matches the (re)sized
            // backbuffer (InitScreenTargets disposes it).
            bool wantVelocity = cfg.TAA || cfg.MotionBlur == 2 || cfg.VelocityDebug;
            PPXDepthEngine.EnableVelocityTarget(wantVelocity);
            PPXDepthEngine.EnableHistoryTargets(cfg.TAA);

            bool motionBlur3D = wantVelocity && cfg.MotionBlur == 2 && WorldContent.MotionBlur != null && cfg.MotionBlurAmount > 0f;
            PPXDepthEngine.MotionBlurFunc = motionBlur3D ? PerPixelMotionBlur.Draw : null;

            bool velocityDebug = wantVelocity && cfg.VelocityDebug && WorldContent.VelocityViz != null;
            PPXDepthEngine.VelocityDebugFunc = velocityDebug ? Utils.VelocityVisualizer.Draw : null;

            // TAA: resolve-chain stage applied after the spatial AA. Needs the velocity buffer + history
            // (enabled above when cfg.TAA) and the shaders. Terrain.Draw applies the matching sub-pixel
            // projection jitter each frame whenever TAAFunc is set. AO stays disabled (as in ChangeAAMode).
            bool taaReady = cfg.TAA && WorldContent.TAA != null && WorldContent.MotionBlur != null
                            && PPXDepthEngine.GetVelocityTarget() != null && PPXDepthEngine.GetHistoryPrev() != null;
            PPXDepthEngine.TAAFunc = taaReady ? TAAResolve.Draw : null;
            PPXDepthEngine.AOFunc = null;
        }

        public virtual void ChangedWorldConfig(GraphicsDevice gd)
        {
            //destroy any features that are no longer enabled.

            var config = WorldConfig.Current;
            if (ForceAdvLight)
            {
                config.LightingMode = Math.Max(config.LightingMode, 1);
            }
            State.DisableSmoothRotation = !config.EnableTransitions;

            if (config.AdvancedLighting)
            {
                // Idempotent: ChangedWorldConfig runs on EVERY graphics-settings change (even ones unrelated
                // to lighting, e.g. render scale or bloom), not just when AdvancedLighting actually toggles.
                // Only (re)create the lightmap on an actual off->on transition - unconditionally disposing it
                // here made walls/objects lighting flicker off and rebuild on every settings tweak.
                if (Light == null)
                {
                    State.AmbientLight?.Dispose();
                    State.AmbientLight = null;
                    Light = new LMapBatch(gd, 16);
                    if (Blueprint != null)
                    {
                        Light.Init(Blueprint);
                        Blueprint.Changes.SetFlag(BlueprintGlobalChanges.ROOM_CHANGED);
                        Blueprint.Changes.SetFlag(BlueprintGlobalChanges.OUTDOORS_LIGHTING_CHANGED);
                    }
                    State.Light = Light;
                }
            } else
            {
                if (Light != null)
                {
                    Light.Dispose();
                    Light = null;
                    State.Light = null;
                    if (Blueprint != null) Blueprint.Changes.SetFlag(BlueprintGlobalChanges.OUTDOORS_LIGHTING_CHANGED);
                }
                if (State.AmbientLight == null)
                    State.AmbientLight = new Texture2D(gd, 256, 256);
            }

            if (Blueprint != null && !FSOEnvironment.Enable3D)
            {
                var shad3D = (Blueprint.WCRC != null);
                if (true != shad3D)
                {
                    if (true) //config.AdvancedLighting && config.Shadow3D)
                    {
                        Blueprint.WCRC = new RC.WallComponentRC();
                        Blueprint.WCRC.blueprint = Blueprint;
                        Blueprint.WCRC.Generate(gd, State, false);
                    }
                    else
                    {
                        Blueprint.WCRC?.Dispose();
                        Blueprint.WCRC = null;
                    }
                    Blueprint.Changes.SetFlag(BlueprintGlobalChanges.OUTDOORS_LIGHTING_CHANGED);
                }
            }
            ChangeAAMode(gd);
        }

        public virtual ObjectComponent MakeObjectComponent(Content.GameObject obj)
        {
            return new ObjectComponent(obj);
        }

        public virtual SubWorldComponent MakeSubWorld(GraphicsDevice gd, int index)
        {
            return new SubWorldComponent(gd, index);
        }

        public BoundingBox[] SkyBounds;

        public virtual void InitSubWorlds()
        {
            float minAlt = 0;
            foreach (var height in Blueprint.Altitude)
            {
                var alt = height * Blueprint.TerrainFactor - Blueprint.BaseAlt;
                if (alt < minAlt)
                {
                    minAlt = alt;
                }
            }

            BoundingBox overall = new BoundingBox(new Vector3(0, minAlt, 0), new Vector3(Blueprint.Width * 3, 1000, Blueprint.Height * 3));
            foreach (var world in Blueprint.SubWorlds)
            {
                world.UpdateBounds();
                overall = BoundingBox.CreateMerged(overall, world.Bounds);
            }
            //update sky bounding box edge

            SkyBounds = new BoundingBox[4];
            SkyBounds[0] = new BoundingBox(new Vector3(overall.Min.X - 1, overall.Min.Y, overall.Min.Z), new Vector3(overall.Min.X, overall.Max.Y, overall.Max.Z));
            SkyBounds[1] = new BoundingBox(new Vector3(overall.Min.X, overall.Min.Y, overall.Min.Z - 1), new Vector3(overall.Max.X, overall.Max.Y, overall.Min.Z));
            SkyBounds[2] = new BoundingBox(new Vector3(overall.Min.X, overall.Min.Y, overall.Max.Z), new Vector3(overall.Max.X, overall.Max.Y, overall.Max.Z + 1));
            SkyBounds[3] = new BoundingBox(new Vector3(overall.Max.X, overall.Min.Y, overall.Min.Z), new Vector3(overall.Max.X + 1, overall.Max.Y, overall.Max.Z));
        }

        public int PreloadProgress;
        public int PreloadObjProgress;
        private Queue<PreloadCheckpoint> PreloadCheckpoints = new Queue<PreloadCheckpoint>();

        private struct PreloadCheckpoint
        {
            public int Checkpoint;
            public Action Action;

            public PreloadCheckpoint(Action action)
            {
                Checkpoint = AssetStreaming.GetCheckpoint();
                Action = action;
            }

            public bool TryRun()
            {
                if (AssetStreaming.IsCheckpointMet(Checkpoint))
                {
                    Action();
                    return true;
                }

                return false;
            }
        }

        private void ProcessPreloadCheckpoints(Func<bool> shouldReturn)
        {
            while (PreloadCheckpoints.Count > 0 && !shouldReturn() && PreloadCheckpoints.Peek().TryRun())
            {
                PreloadCheckpoints.Dequeue();
            }
        }

        public bool Preload(GraphicsDevice gd)
        {
            var watch = new Stopwatch();
            watch.Start();

            bool shouldReturn()
            {
                if (watch.ElapsedMilliseconds > 16)
                {
                    watch.Stop();
                    return true;
                }

                return false;
            }

            if (PreloadProgress == 0)
            {
                var done = 0;
                for (int i = PreloadObjProgress; i < Blueprint.Objects.Count; i++)
                {
                    var obj = Blueprint.Objects[i];
                    obj.Preload(gd, State);
                    PreloadObjProgress++;
                    if (done++ >= 6 && shouldReturn()) return false;
                }

                for (int i=0; i<Blueprint.Avatars.Count; i++)
                {
                    Blueprint.Avatars[i].Preload(gd, State);
                }

                State._2D.PreciseZoom = State.PreciseZoom;
                State.OutsideColor = Blueprint.OutsideColor;
                State.PrepareLighting();
                State._2D.Begin(this.State.Camera2D);

                Blueprint.Changes.Preload(gd, State);

                PreloadCheckpoints.Enqueue(new PreloadCheckpoint(() =>
                {
                    State.PrepareLighting();
                    Blueprint.Changes.PreDraw(gd, State);
                }));

                PreloadProgress = 1;
                PreloadObjProgress = 0;
            }

            for (int i= PreloadProgress-1; i<Blueprint.SubWorlds.Count; i++)
            {
                ProcessPreloadCheckpoints(shouldReturn);
                var world = Blueprint.SubWorlds[i];
                for (int j = PreloadObjProgress; j < world.Blueprint.Objects.Count; j++)
                {
                    var obj = world.Blueprint.Objects[j];
                    obj.Preload(gd, State);
                    PreloadObjProgress++;
                    if (shouldReturn()) return false;
                }


                world.State._2D = State._2D;
                world.Blueprint.Changes.Preload(gd, world.State);

                PreloadCheckpoints.Enqueue(new PreloadCheckpoint(() =>
                {
                    world.PreDraw(gd, State);
                }));

                PreloadProgress++;
                PreloadObjProgress = 0;
            }

            ProcessPreloadCheckpoints(shouldReturn);

            return PreloadCheckpoints.Count == 0;
        }

        public override void Dispose()
        {
            base.Dispose();
            State.AmbientLight?.Dispose();
            State.OutsidePx.Dispose();
            Light?.Dispose();
            Platform?.Dispose();
            State.Rooms.Dispose();
            if (State._2D != null && !(this is SubWorldComponent)) State._2D.Dispose();
            if (Blueprint != null)
            {
                foreach (var world in Blueprint.SubWorlds)
                {
                    world.Dispose();
                }
                foreach (var obj in Blueprint.Objects)
                {
                    obj.Dispose();
                }
                foreach (var particle in Blueprint.Particles)
                {
                    particle.Dispose();
                }
                foreach (var particle in Blueprint.ObjectParticles)
                {
                    particle.Dispose();
                }
                Blueprint.Terrain?.Dispose();
                Blueprint.RoofComp?.Dispose();
            }
            GraphicsModeControl.ModeChanged -= SetGraphicsMode;
        }
    }
}
