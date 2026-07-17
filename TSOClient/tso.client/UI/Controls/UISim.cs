using System;
using FSO.Client.UI.Framework;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using FSO.Client.Utils;
using FSO.Common.Rendering.Framework.Model;
using FSO.Common.Rendering.Framework;
using FSO.Common.Rendering.Framework.IO;
using FSO.Common.Rendering.Framework.Camera;
using Microsoft.Xna.Framework.Input;
using FSO.Vitaboy;
using FSO.LotView.Utils;
using FSO.LotView;
using FSO.Client.UI.Framework.Parser;
using FSO.Common;

namespace FSO.Client.UI.Controls
{
    /// <summary>
    /// Renders a sim in the UI, this class just helps translate the UI world
    /// into the 3D world for sim rendering. Two modes: the default orthographic thumbnail,
    /// and an opt-in perspective CAS viewport (SetPerspective) with orbit/dolly controls.
    /// </summary>
    public class UISim : UIElement
    {
        private _3DTargetScene Scene;
        private WorldCamera Camera;     // orthographic (legacy thumbnail)
        private BasicCamera PCamera;    // perspective (modern CAS viewport)
        public AdultVitaboyModel Avatar { get; internal set; }

        /** 45 degrees in either direction **/
        public float RotationRange = 45;
        public float RotationStartAngle = 45;
        public float RotationSpeed = new TimeSpan(0, 0, 10).Ticks;
        public bool AutoRotate = true;
        public float TimeOffset;

        // Interactive viewport (CAS): drag to rotate/orbit, scroll to zoom/dolly. Opt-in.
        public bool Interactive = false;
        public float MinZoom = 0.6f, MaxZoom = 2.5f;
        public float RotateSensitivity = 0.01f;
        public float ZoomSensitivity = 0.15f;
        private UIMouseEventRef _MouseEvt;
        private bool _MouseOver, _Dragging, _UserControlled, _ScrollInit;
        private int _LastMouseX, _LastMouseWheel;
        private float _ManualRotation;

        public bool Perspective { get; private set; }
        private float _Azimuth = 0f;                       // radians; 0 = sim facing the camera
        private float _Distance = 11f;                     // camera distance from the focus point
        private Vector3 _Focus = new Vector3(0, 3.0f, 0);  // look-at (body centre); head focus retargets this later
        public float MinDistance = 2.4f, MaxDistance = 22f;
        private int _TargetW = 140, _TargetH = 200;        // render-target logical size

        private Vector3 _FocusTarget = new Vector3(0, 3.0f, 0);
        private float _DistanceTarget = 11f;
        private bool _SmoothFocus = false;
        public float FocusLerp = 0.16f;
        public float PanFraction = 0.28f;  // >0 renders the sim left of centre, leaving room for menus on the right

        // Studio lighting (CAS): the directional-lighting technique is normally fed by a lot's light
        // maps; with no lot we feed a synthetic constant light (1x1 textures + zeroed map transform).
        public bool StudioLighting = false;
        public Vector3 KeyDirection = new Vector3(-0.5f, 0.72f, 0.62f); // direction TO the key (camera-relative; +X=screen-right, +Y=up, +Z=toward camera)
        // pctAmbient = Fill/(Key+Fill) sets the shadow floor (FillStrength is the shadow-depth knob)
        public float KeyStrength = 1.15f, FillStrength = 0.30f;
        public Vector3 KeyColor = new Vector3(1.30f, 1.22f, 1.06f);       // warm key (LightingAdjust)
        public Vector4 ShadowColor = new Vector4(0.16f, 0.20f, 0.30f, 1f); // deep cool shadow (OutsideDark)
        private Texture2D _StudioWhite, _StudioDir;
        private int _DirTechnique = -1; // resolved by name so a shader reorder can't silently select the wrong technique

        protected string m_Timestamp;
        public float HeadXPos = 0.0f, HeadYPos = 0.0f;

        private WorldZoom Zoom = WorldZoom.Near;

        // Perspective (CAS) targets are full-window; DPIScaleFactor is user-configurable up to 3x, so a naive
        // windowSize*DPIScaleFactor render target can balloon to multi-thousand-pixel dimensions on a 4K/high-DPI
        // setup. Clamp each axis to a sane preview size instead — this is a background avatar viewport, not the
        // main scene. The orthographic thumbnail path (140x200 logical) never gets remotely this large.
        private const int MaxPerspectiveDim = 2048;

        // Mirrors the clamp PPXDepthEngine.InitScreenTargets applies to the main backbuffer: use the user's
        // configured granular MSAALevel (0/2/4/8) instead of a hard-coded tier, and never exceed what
        // FeatureLevelTest determined this GPU can actually resolve.
        private static int TargetMSAA()
        {
            return Math.Min(GlobalSettings.Default.MSAALevel, FSOEnvironment.MaxMSAA);
        }

        private Point TargetSize()
        {
            var scale = FSOEnvironment.DPIScaleFactor;
            var w = _TargetW * scale;
            var h = _TargetH * scale;
            if (Perspective)
            {
                // Clamp BOTH axes by the same factor so the render-target aspect ratio matches the
                // viewport — clamping each axis independently would squash/stretch the avatar.
                var f = Math.Min(1f, MaxPerspectiveDim / Math.Max(w, h));
                w *= f; h *= f;
            }
            return new Point(Math.Max(1, (int)w), Math.Max(1, (int)h));
        }

        /// <summary>
        /// Resize the perspective viewport target without rebuilding the scene/camera, so orbit and
        /// focus state survive a window resize. The camera's projection aspect is recomputed lazily
        /// from the (freshly resized) target viewport.
        /// </summary>
        public void SetPerspectiveSize(int w, int h)
        {
            if (!Perspective || (w == _TargetW && h == _TargetH)) return;
            _TargetW = w;
            _TargetH = h;
            Scene.SetSize(TargetSize());
            PCamera?.ProjectionDirty();
        }

        /// <summary>
        /// When was this character last cached by the client?
        /// </summary>
        public string Timestamp
        {
            get { return m_Timestamp; }
            set { m_Timestamp = value; }
        }

        private void UISimInit()
        {
            Vitaboy.Avatar.DefaultTechnique = (GlobalSettings.Default.Lighting) ? 3 : 0;
            Camera = new WorldCamera(GameFacade.GraphicsDevice);
            Camera.Zoom = Zoom;
            Camera.CenterTile = new Vector3(-1, -1, 0)*FSOEnvironment.DPIScaleFactor;
            Scene = new _3DTargetScene(GameFacade.GraphicsDevice, Camera, TargetSize(), TargetMSAA());
            Scene.ID = "UISim";

            GameFacade.Game.GraphicsDevice.DeviceReset += new EventHandler<EventArgs>(GraphicsDevice_DeviceReset);

            Avatar = new AdultVitaboyModel();
            Avatar.Scene = Scene;
            var scale = FSOEnvironment.DPIScaleFactor;
            Avatar.Scale = new Vector3(scale, scale, scale);

            Scene.Add(Avatar);
        }

        /// <summary>
        /// Switch between the orthographic thumbnail and the perspective CAS render.
        /// Rebuilds the render target + camera; the avatar is preserved.
        /// </summary>
        public void SetPerspective(bool on, int targetW = 0, int targetH = 0)
        {
            if (targetW > 0) _TargetW = targetW;
            if (targetH > 0) _TargetH = targetH;
            Perspective = on;
            // Flat lighting, like the legacy CAS: the synthetic studio key overshoots (1.2x peak on
            // a 1.3x key colour) and clips the prefab heads' near-white baked glasses lenses to a
            // white blob with dark facet wedges. The flat techniques ignore normals entirely.
            StudioLighting = false;
            if (on) Avatar.RotationY = 0f; // orbit the camera, keep the model front-facing
            RebuildScene();
        }

        private void RebuildScene()
        {
            if (Scene != null)
            {
                GameFacade.Scenes.RemoveExternal(Scene);
                Scene.Dispose();
            }
            var size = TargetSize();
            var msaa = TargetMSAA();
            if (Perspective)
            {
                PCamera = new BasicCamera(GameFacade.GraphicsDevice, Vector3.Zero, _Focus, Vector3.Up);
                PCamera.NearPlane = 0.4f;
                PCamera.FarPlane = 200f;
                Avatar.Scale = new Vector3(1f); // frame in unscaled bone space (matches UIPieMenu head cam)
                UpdatePerspectiveCamera();
                Scene = new _3DTargetScene(GameFacade.GraphicsDevice, PCamera, size, msaa);
            }
            else
            {
                Camera = new WorldCamera(GameFacade.GraphicsDevice);
                Camera.Zoom = Zoom;
                Camera.CenterTile = new Vector3(-1, -1, 0) * FSOEnvironment.DPIScaleFactor;
                Scene = new _3DTargetScene(GameFacade.GraphicsDevice, Camera, size, msaa);
                PCamera = null;
            }
            Scene.ID = "UISim";
            Avatar.Scene = Scene;
            Scene.Add(Avatar);
            GameFacade.Scenes.AddExternal(Scene);
        }

        private void UpdatePerspectiveCamera()
        {
            if (PCamera == null) return;
            var x = (float)Math.Sin(_Azimuth) * _Distance;
            var z = (float)Math.Cos(_Azimuth) * _Distance;
            var orbit = new Vector3(x, 0, z);
            // Truck along the camera's own right axis: shifting position+target by the same
            // camera-space vector frames the sim off-centre without moving the orbit pivot.
            var forward = -Vector3.Normalize(orbit);
            var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.Up));
            var camPan = right * (PanFraction * _Distance);
            PCamera.Position = _Focus + orbit + camPan;
            PCamera.Target = _Focus + camPan;
        }

        /// <summary>Smoothly fly the camera to a head close-up (used when editing the head).</summary>
        public void FocusHead()
        {
            if (!Perspective) return;
            var head = Avatar?.Skeleton?.GetBone("HEAD");
            _FocusTarget = (head != null) ? head.AbsolutePosition : new Vector3(0, 5.2f, 0);
            _DistanceTarget = 3.2f;
            _SmoothFocus = true;
            _UserControlled = true;
        }

        /// <summary>Smoothly pull the camera back to a full-body shot (used when editing the body).</summary>
        public void FocusBody()
        {
            if (!Perspective) return;
            _FocusTarget = new Vector3(0, 3.0f, 0);
            _DistanceTarget = 11f;
            _SmoothFocus = true;
            _UserControlled = true;
        }

        private Vector2 _Size;
        private Vector2 _SimScale;
        [UIAttribute("size")]
        public override Vector2 Size
        {
            get
            {
                return _Size;
            }

            set
            {
                _Size = value;
                _SimScale = new Vector2(1, 1) * (value.Y / 200f);
                // the interactive hit region must track the control (the CAS viewport resizes with the window)
                if (_MouseEvt != null) _MouseEvt.Region = new Rectangle(0, 0, (int)value.X, (int)value.Y);
            }
        }

        public void SetZoom(WorldZoom zoom)
        {
            Zoom = zoom;
            if (Camera != null) Camera.Zoom = zoom;
        }

        public UISim() : this(true)
        {
        }

        public override void GameResized()
        {
            base.GameResized();
            var scale = FSOEnvironment.DPIScaleFactor;
            Scene.SetSize(TargetSize());
            if (Perspective)
            {
                Avatar.Scale = new Vector3(1f);
                PCamera.ProjectionDirty();
            }
            else
            {
                Avatar.Scale = new Vector3(scale, scale, scale);
                Camera.CenterTile = new Vector3(-1, -1, 0) * scale;
                Camera.ProjectionDirty();
            }
        }

        public override void Removed()
        {
            GameFacade.Scenes.RemoveExternal(Scene);
            Scene.Dispose();
            if (StudioLighting) Vitaboy.Avatar.DefaultTechnique = (GlobalSettings.Default.Lighting) ? 3 : 0; // restore global technique
            _StudioWhite?.Dispose();
            _StudioDir?.Dispose();
        }

        public UISim(bool AddScene)
        {
            UISimInit();
            if (AddScene)
                GameFacade.Scenes.AddExternal(Scene);
        }

        private void GraphicsDevice_DeviceReset(object sender, EventArgs e)
        {
            Scene.DeviceReset(GameFacade.GraphicsDevice);
            // 1x1 studio maps are lost on a device reset; null them so they're rebuilt next frame
            _StudioWhite?.Dispose(); _StudioWhite = null;
            _StudioDir?.Dispose(); _StudioDir = null;
        }

        public override void Update(UpdateState state)
        {
            base.Update(state);

            if (Interactive)
            {
                if (_MouseEvt == null && _Size.Y > 0)
                    _MouseEvt = ListenForMouse(new Rectangle(0, 0, (int)_Size.X, (int)_Size.Y), OnMouse);

                if (_Dragging && state.MouseState.LeftButton == ButtonState.Released)
                    _Dragging = false;

                var wheel = state.MouseState.ScrollWheelValue;
                if (!_ScrollInit) { _LastMouseWheel = wheel; _ScrollInit = true; }
                var wheelDelta = (_MouseOver && wheel != _LastMouseWheel) ? (wheel - _LastMouseWheel) / 120f : 0f;
                _LastMouseWheel = wheel;

                if (Perspective)
                {
                    if (_SmoothFocus)
                    {
                        _Focus = Vector3.Lerp(_Focus, _FocusTarget, FocusLerp);
                        _Distance = MathHelper.Lerp(_Distance, _DistanceTarget, FocusLerp);
                        UpdatePerspectiveCamera();
                        if ((_Focus - _FocusTarget).LengthSquared() < 0.002f && Math.Abs(_Distance - _DistanceTarget) < 0.02f) _SmoothFocus = false;
                    }
                    if (_Dragging)
                    {
                        var dx = state.MouseState.X - _LastMouseX;
                        _LastMouseX = state.MouseState.X;
                        _Azimuth -= dx * RotateSensitivity;
                        UpdatePerspectiveCamera();
                    }
                    if (wheelDelta != 0f)
                    {
                        _UserControlled = true;
                        _SmoothFocus = false;
                        _Distance = MathHelper.Clamp(_Distance - wheelDelta * 1.5f, MinDistance, MaxDistance);
                        UpdatePerspectiveCamera();
                    }
                }
                else
                {
                    // legacy: drag rotates the model; scroll = precise-zoom
                    if (_Dragging)
                    {
                        var dx = state.MouseState.X - _LastMouseX;
                        _LastMouseX = state.MouseState.X;
                        _ManualRotation += dx * RotateSensitivity;
                        Avatar.RotationY = _ManualRotation;
                    }
                    if (wheelDelta != 0f)
                    {
                        _UserControlled = true;
                        Camera.PreciseZoom = MathHelper.Clamp(Camera.PreciseZoom + wheelDelta * ZoomSensitivity, MinZoom, MaxZoom);
                    }
                }
            }

            if (AutoRotate && !_UserControlled)
            {
                var time = state.Time.TotalGameTime.Ticks + TimeOffset;
                var phase = (time % RotationSpeed) / RotationSpeed;
                var multiplier = Math.Sin((Math.PI * 2) * phase);
                if (Perspective)
                {
                    _Azimuth = (float)MathUtils.DegreeToRadian(RotationRange * multiplier);
                    UpdatePerspectiveCamera();
                }
                else
                {
                    var newAngle = RotationStartAngle + (RotationRange * multiplier);
                    Avatar.RotationY = (float)MathUtils.DegreeToRadian(newAngle);
                }
            }
        }

        private void OnMouse(UIMouseEventType type, UpdateState state)
        {
            switch (type)
            {
                case UIMouseEventType.MouseOver:
                    _MouseOver = true; break;
                case UIMouseEventType.MouseOut:
                    _MouseOver = false; break;
                case UIMouseEventType.MouseDown:
                    _Dragging = true;
                    _UserControlled = true;
                    if (!Perspective) _ManualRotation = Avatar.RotationY; // continue from current angle (no jump)
                    _LastMouseX = state.MouseState.X;
                    break;
                case UIMouseEventType.MouseUp:
                    _Dragging = false; break;
            }
        }

        /// <summary>
        /// Feed the directional-lighting technique one synthetic camera-relative studio light:
        /// 1x1 constant maps + a zeroed world->light transform, so every fragment samples the
        /// same light and the normal is the only per-pixel variation.
        /// </summary>
        private void ApplyStudioLighting()
        {
            var fx = Vitaboy.Avatar.Effect;
            if (fx == null) return;
            var gd = GameFacade.GraphicsDevice;

            if (_StudioWhite == null)
            {
                _StudioWhite = new Texture2D(gd, 1, 1, false, SurfaceFormat.Color);
                _StudioWhite.SetData(new[] { Color.White });
            }
            if (_StudioDir == null)
                _StudioDir = new Texture2D(gd, 1, 1, false, SurfaceFormat.Vector4);

            // keep the key camera-relative: rotate the to-light vector by the orbit azimuth
            var toLight = Vector3.Transform(Vector3.Normalize(KeyDirection), Microsoft.Xna.Framework.Matrix.CreateRotationY(_Azimuth));
            // the shader reads -direction.xyz as the to-light vector; |xyz| = directional strength, .w = total
            _StudioDir.SetData(new[] { new Vector4(-toLight * KeyStrength, KeyStrength + FillStrength) });

            if (_DirTechnique < 0)
            {
                _DirTechnique = 5; // fallback to known source order if the name isn't found
                for (int i = 0; i < fx.Techniques.Count; i++)
                    if (fx.Techniques[i].Name == "AdvancedLightingDirection") { _DirTechnique = i; break; }
            }
            Vitaboy.Avatar.DefaultTechnique = _DirTechnique;
            Vitaboy.Avatar.SuppressHeadObjectSpec = true; // our synthetic key would blow the glasses spec to a white blob
            fx.Parameters["WorldToLightFactor"]?.SetValue(Vector3.Zero); // every fragment samples the same texel
            fx.Parameters["LightOffset"]?.SetValue(Vector2.Zero);
            fx.Parameters["MapLayout"]?.SetValue(Vector2.One);
            fx.Parameters["Level"]?.SetValue(0f);
            fx.Parameters["MinAvg"]?.SetValue(new Vector2(0f, 1f));
            fx.Parameters["LightingAdjust"]?.SetValue(KeyColor);
            fx.Parameters["OutsideDark"]?.SetValue(ShadowColor);
            fx.Parameters["advancedLight"]?.SetValue(_StudioWhite);
            fx.Parameters["advancedDirection"]?.SetValue(_StudioDir);
        }

        public override void PreDraw(UISpriteBatch batch)
        {
            if (!Visible) return;
            base.PreDraw(batch);
            if (!UISpriteBatch.Invalidated)
            {
                if (!_3DScene.IsInvalidated)
                {
                    // DefaultTechnique is a static shared by every avatar - scope it around this draw
                    // so the studio technique can't mis-light other avatars this frame.
                    var prevTech = Vitaboy.Avatar.DefaultTechnique;
                    var prevSpec = Vitaboy.Avatar.SuppressHeadObjectSpec;
                    if (Perspective && StudioLighting) ApplyStudioLighting();
                    batch.Pause();
                    // Pause() ends the sprite batch but leaves its device state behind — notably
                    // RasterizerState.CullNone, so the glasses' inner lens faces drew too. Flat
                    // lighting shaded both faces identically (invisible doubling); the directional
                    // studio light shades each by its own normal and the stacked translucent layers
                    // compound into a white blob. Mirror DrawAvatars' states (CullCCW + AlphaBlend).
                    var gd = GameFacade.GraphicsDevice;
                    var prevBlend = gd.BlendState;
                    var prevRast = gd.RasterizerState;
                    gd.BlendState = BlendState.AlphaBlend;
                    gd.RasterizerState = RasterizerState.CullCounterClockwise;
                    Scene.Draw(gd);
                    gd.BlendState = prevBlend;
                    gd.RasterizerState = prevRast;
                    batch.Resume();
                    Vitaboy.Avatar.DefaultTechnique = prevTech;
                    Vitaboy.Avatar.SuppressHeadObjectSpec = prevSpec;
                    DrawLocalTexture(batch, Scene.Target, new Vector2());
                }
            }
        }

        public override void Draw(UISpriteBatch batch)
        {
            if (!Visible) return;
            if (Perspective)
            {
                // Viewport fills the control 1:1. Derive the blit scale from the ACTUAL target size (not a
                // flat 1/DPIScaleFactor) so a target clamped by MaxPerspectiveDim (see TargetSize()) still
                // stretches to fill the control instead of leaving a blank gap on oversized high-DPI windows.
                var sx = (Scene.Target.Width > 0) ? _TargetW / (float)Scene.Target.Width : 1f;
                var sy = (Scene.Target.Height > 0) ? _TargetH / (float)Scene.Target.Height : 1f;
                DrawLocalTexture(batch, Scene.Target, null, new Vector2(), new Vector2(sx, sy));
            }
            else
            {
                DrawLocalTexture(batch, Scene.Target, null, new Vector2((_Size.X - _TargetW * _SimScale.X) / 2, 0), new Vector2(1f / FSOEnvironment.DPIScaleFactor, 1f / FSOEnvironment.DPIScaleFactor) * _SimScale);
            }
        }
    }
}
