using FSO.Client.GameContent;
using FSO.Client.Network;
using FSO.Client.Regulators;
using FSO.Client.UI;
using FSO.Common;
using FSO.Common.Audio;
using FSO.Common.DataService;
using FSO.Common.Domain;
using FSO.Common.Rendering.Framework;
using FSO.Common.Utils;
using FSO.Files.Formats.IFF;
using FSO.Files.RC;
using FSO.HIT;
using FSO.HIT.Model;
using FSO.LotView;
using FSO.LotView.Model;
using FSO.Server.DataService.Providers.Client;
using FSO.Server.Protocol.Voltron.DataService;
using FSO.UI.Framework;
using FSO.UI.Model;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using MSDFData;
using Ninject;

namespace FSO.Client
{
    /// <summary>
    /// This is the main type for your game
    /// </summary>
    public class TSOGame : FSO.Common.Rendering.Framework.Game
    {
        public UILayer uiLayer;
        public _3DLayer SceneMgr;

        public TSOGame() : base()
        {
            /*
            var test = new Utils.TestFunctions.ProjectionTest();
            test.TestCombo();
            */

            GameFacade.Game = this;
            //if (GameFacade.DirectX) TimedReferenceController.SetMode(CacheType.PERMANENT);
            Content.RootDirectory = FSOEnvironment.GFXContentDir;
            Graphics.SynchronizeWithVerticalRetrace = true;

            if (GraphicsAdapter.DefaultAdapter.IsProfileSupported(GraphicsProfile.HiDef))
            {
                Graphics.GraphicsProfile = GraphicsProfile.HiDef;
            }

            Utils.DPIScaleDetect.Startup(GlobalSettings.Default);
            FSOEnvironment.DPIScaleFactor = GlobalSettings.Default.DPIScaleFactor;
            // macOS: never exclusive-fullscreen. Mode switching black-screens on Retina GL surfaces
            // (MonoGame#4802) and macOS emulates modes anyway; borderless sizes the backbuffer to the
            // display and composes correctly with the native-Retina override (ApplyMacRetina).
            if (OperatingSystem.IsMacOS()) Graphics.HardwareModeSwitch = false;
            if (!FSOEnvironment.SoftwareDepth)
            {
                var width = (int)(GlobalSettings.Default.GraphicsWidth * FSOEnvironment.DPIScaleFactor);
                var height = (int)(GlobalSettings.Default.GraphicsHeight * FSOEnvironment.DPIScaleFactor);
                if (GlobalSettings.Default.Windowed)
                {
                    // A saved windowed size that covers the whole display is a stale fullscreen size:
                    // the OS clamps the window, the backbuffer no longer matches the client area, and
                    // the image is stretched with an offset mouse cursor. Shrink it to fit.
                    var display = GraphicsAdapter.DefaultAdapter.CurrentDisplayMode;
                    if (width >= display.Width && height >= display.Height)
                    {
                        width = display.Width * 4 / 5;
                        height = display.Height * 4 / 5;
                        GlobalSettings.Default.GraphicsWidth = (int)(width / FSOEnvironment.DPIScaleFactor);
                        GlobalSettings.Default.GraphicsHeight = (int)(height / FSOEnvironment.DPIScaleFactor);
                    }
                }
                Graphics.PreferredBackBufferWidth = width;
                Graphics.PreferredBackBufferHeight = height;
                //Graphics.PreferMultiSampling = true;
                Graphics.PreferredDepthStencilFormat = DepthFormat.Depth24Stencil8;
                TargetElapsedTime = new TimeSpan(10000000 / GlobalSettings.Default.TargetRefreshRate);
                FSOEnvironment.RefreshRate = GlobalSettings.Default.TargetRefreshRate;
                Graphics.HardwareModeSwitch = false;
                Graphics.ApplyChanges();
            }

            this.Window.AllowUserResizing = true;
            this.Window.ClientSizeChanged += new EventHandler<EventArgs>(Window_ClientSizeChanged);

            try
            {
                GameThread.Game = Thread.CurrentThread;
                Thread.CurrentThread.Name = "Game";
            }
            catch
            {
                //fails on android
            }
        }

        bool newChange = false;
        void Window_ClientSizeChanged(object sender, EventArgs e)
        {
            // Gate on the live fullscreen state, not the persisted Windowed setting: Alt+Enter
            // toggles fullscreen without touching the setting, and a fullscreen-driven resize must
            // never be recorded as the windowed size (it makes the next windowed launch open at
            // full display size, which the OS clamps - stretched image + offset mouse).
            if (newChange || Graphics.IsFullScreen) return;
            if (Window.ClientBounds.Width == 0 || Window.ClientBounds.Height == 0) return;
            newChange = true;
            var width = Math.Max(1, Window.ClientBounds.Width);
            var height = Math.Max(1, Window.ClientBounds.Height);
            if (_macDpi > 0)
            {
                // Retina: the SDL window stays in points; the backbuffer runs at points * dpi via
                // the live PresentationParameters override (see Draw). Pushing pixel sizes through
                // ApplyChanges would grow the real window - skip it and hand the PIXEL size to the
                // game-side resize below (whose /DPIScaleFactor math then lands back on points).
                width *= _macDpi;
                height *= _macDpi;
                // must be current BEFORE GameResized below rebuilds world targets - Draw's per-frame
                // update hasn't run yet, so the old size would be baked in until the next resize
                FSO.Common.Utils.PPXDepthEngine.ForcedBackbufferSize = new Point(width, height);
            }
            else
            {
                Graphics.PreferredBackBufferWidth = width;
                Graphics.PreferredBackBufferHeight = height;
                Graphics.ApplyChanges();
            }
            newChange = false;

            // Auto DPI follows the live window: re-fit the detected scale to the new client size
            // (stepping down a quarter tier while the window is too small for it) and track the
            // monitor the window is on. Skipped under macOS Retina - the scale is the display's
            // backing factor, not an OS setting the window can move between.
            if (GlobalSettings.Default.AutoDPI == 1 && _macDpi == 0)
            {
                var scale = Utils.DPIScaleDetect.GetScaleForWindow(Window.Handle, width, height);
                if (scale != FSOEnvironment.DPIScaleFactor)
                {
                    FSOEnvironment.DPIScaleFactor = scale;
                    GlobalSettings.Default.DPIScaleFactor = scale;
                    if (uiLayer?.CurrentUIScreen != null)
                        uiLayer.CurrentUIScreen.ScaleX = uiLayer.CurrentUIScreen.ScaleY = scale;
                }
            }

            GlobalSettings.Default.GraphicsWidth = (int)(width / FSOEnvironment.DPIScaleFactor);
            GlobalSettings.Default.GraphicsHeight = (int)(height / FSOEnvironment.DPIScaleFactor);

            if (uiLayer?.CurrentUIScreen == null) return;
            uiLayer.SpriteBatch.ResizeBuffer(width, height);
            uiLayer.CurrentUIScreen.GameResized();
        }

        /// <summary>
        /// Allows the game to perform any initialization it needs to before starting to run.
        /// This is where it can query for any required services and load any non-graphic
        /// related content.  Calling base.Initialize will enumerate through any components
        /// and initialize them as well.
        /// </summary>
        /// <summary>
        /// The macOS .app sets the cwd to the bundle's Contents/MacOS, but many assets load via
        /// cwd-relative paths - point the cwd at the install dir (parent of the absolute ContentDir).
        /// No-op on Windows/Linux (relative ContentDir). Called again after base.Initialize(),
        /// which can reset the cwd.
        /// </summary>
        private static void EnsureWorkingDir()
        {
            if (!System.IO.Path.IsPathRooted(FSOEnvironment.ContentDir)) return;
            try
            {
                var installDir = System.IO.Path.GetDirectoryName(
                    FSOEnvironment.ContentDir.TrimEnd(System.IO.Path.DirectorySeparatorChar, '/'));
                if (installDir != null && System.IO.Directory.Exists(installDir))
                    System.IO.Directory.SetCurrentDirectory(installDir);
            }
            catch { /* best effort */ }
        }

        /// <summary>
        /// macOS only: MonoGame's SDL_SetWindowIcon replaces the .app's Liquid Glass Dock icon with
        /// its own logo - reset it back to the bundle icon.
        /// </summary>
        private void RestoreMacDockIcon()
        {
            if (!OperatingSystem.IsMacOS()) return;
            try { Utils.MacRetina.RestoreBundleDockIcon(); }
            catch { /* best effort */ }
        }

        // macOS native-Retina scale (0 = inactive). The NSView is flipped to a best-resolution GL
        // surface and the backbuffer is re-asserted to currentWindowPoints * _macDpi each frame in
        // Draw: MonoGame reverts PresentationParameters to the window's point size on SDL resize
        // events, and deriving from live ClientBounds makes the native render track window resizing.
        // GraphicsDevice.Reset must NOT be used for this - it recreates the GL context, which clears
        // the best-resolution flag and couples the backbuffer to the window size.
        private int _macDpi;

        /// <summary>
        /// macOS only: render at native Retina resolution. Stock MonoGame creates its SDL window
        /// without ALLOW_HIGHDPI, so the GL drawable is point-sized and macOS pixel-doubles it
        /// (pixel-art sprites read as nearest-scaled). Flip the NSView to a best-resolution surface,
        /// size the backbuffer to native pixels, and route the scale through the existing DPI system:
        /// UI scale via DPIScaleFactor, mouse points-to-pixels via WindowPixelRatio (FNA's model).
        /// </summary>
        private void ApplyMacRetina()
        {
            if (!OperatingSystem.IsMacOS()) return;
            try
            {
                // per-window backing scale (falls back to the main display's for safety)
                float backing = Utils.MacRetina.WindowBackingScale(Window.Handle);
                if (backing <= 0f) backing = Utils.MacRetina.MainDisplayBackingScale();
                if (backing < 1.1f) return; //non-Retina display - stock rendering
                int dpi = Math.Clamp((int)Math.Round(backing), 1, 4);
                var prevDpiScale = FSOEnvironment.DPIScaleFactor;

                // set before the surface flip: its window-size nudge fires resize events that must
                // already route through the Retina branch of Window_ClientSizeChanged
                _macDpi = dpi;
                FSOEnvironment.DPIScaleFactor = dpi;
                FSOEnvironment.WindowPixelRatio = dpi;

                Utils.MacRetina.EnableBestResolutionSurface(Window.Handle);
                // Verify via Cocoa (the NSView flag), NOT SDL_GL_GetDrawableSize: SDL reports the
                // point size forever for windows created without ALLOW_HIGHDPI, even when the flip
                // took - gating on it turned a working Retina surface into quarter-screen rendering.
                if (!Utils.MacRetina.SurfaceIsBestResolution(Window.Handle))
                {
                    //surface refused - roll back to stock point-resolution rendering
                    _macDpi = 0;
                    FSOEnvironment.DPIScaleFactor = prevDpiScale;
                    FSOEnvironment.WindowPixelRatio = 1f;
                    return;
                }

                // apply the native size immediately (Draw re-asserts each frame from here on)
                var cb = Window.ClientBounds;
                var pp = GraphicsDevice.PresentationParameters;
                pp.BackBufferWidth = Math.Max(1, cb.Width) * dpi;
                pp.BackBufferHeight = Math.Max(1, cb.Height) * dpi;
                FSO.Common.Utils.PPXDepthEngine.ForcedBackbufferSize = new Point(pp.BackBufferWidth, pp.BackBufferHeight);
                uiLayer?.SpriteBatch.ResizeBuffer(pp.BackBufferWidth, pp.BackBufferHeight);
                if (uiLayer?.CurrentUIScreen != null)
                {
                    uiLayer.CurrentUIScreen.ScaleX = uiLayer.CurrentUIScreen.ScaleY = dpi;
                    uiLayer.CurrentUIScreen.GameResized();
                }
            }
            catch { /* best effort - any failure leaves stock point-resolution rendering */ }
        }

        protected override void Initialize()
        {
            EnsureWorkingDir();

            var kernel = new StandardKernel(
                new RegulatorsModule(),
                new NetworkModule(),
                new CacheModule()
            );
            FSOFacade.Kernel = kernel;

            var settings = GlobalSettings.Default;
            if (FSOEnvironment.SoftwareDepth)
            {
                settings.GraphicsWidth = (int)(GraphicsDevice.Viewport.Width / FSOEnvironment.DPIScaleFactor);
                settings.GraphicsHeight = (int)(GraphicsDevice.Viewport.Height / FSOEnvironment.DPIScaleFactor);
            }

            //manage settings
            if (settings.LightingMode == -1)
            {
                if (settings.Lighting)
                {
                    if (settings.Shadows3D)
                        settings.LightingMode = 2;
                    else
                        settings.LightingMode = 1;
                }
                else
                    settings.LightingMode = 0;
                settings.Save();
            }

            var initialMode = (GlobalGraphicsMode)settings.GlobalGraphicsMode;
            if (FSOEnvironment.Enable3D)
            {
                if (initialMode == GlobalGraphicsMode.Full2D) initialMode = GlobalGraphicsMode.Full3D;
            }
            else
            {
                initialMode = GlobalGraphicsMode.Full2D;
            }
            GraphicsModeControl.ChangeMode(initialMode);
            GraphicsModeControl.ModeChanged += SaveGraphicsModePreference;

            FeatureLevelTest.UpdateFeatureLevel(GraphicsDevice);
            if (!FSOEnvironment.MSAASupport)
            {
                settings.AntiAlias = 0;
                settings.MSAALevel = 0; //supersampling and post-process AA don't require MSAA support
            }
            else if (settings.MSAALevel > FSOEnvironment.MaxMSAA)
            {
                // a saved level above the GPU max (e.g. 8x on Apple Silicon) renders black - clamp it
                settings.MSAALevel = FSOEnvironment.MaxMSAA;
            }

            LotView.WorldConfig.Current = new LotView.WorldConfig()
            {
                LightingMode = settings.LightingMode,
                SmoothZoom = settings.SmoothZoom,
                SurroundingLots = settings.SurroundingLotMode,
                AA = settings.AntiAlias,
                MSAA = settings.MSAALevel,
                SuperSampling = settings.SuperSampling,
                RenderScale = settings.RenderScale,
                PostAA = settings.PostAA,
                TAA = settings.TAA,
                TAALite = settings.TAALite,
                MotionBlur = settings.MotionBlur,
                MotionBlurAmount = settings.MotionBlurAmount,
                Bloom = settings.Bloom,
                BloomThreshold = settings.BloomThreshold,
                BloomIntensity = settings.BloomIntensity,
                AO = settings.AO,
                AORadius = settings.AORadius,
                AOIntensity = settings.AOIntensity,
                VelocityDebug = settings.VelocityDebug,
                VelocityDebugDepth = settings.VelocityDebugDepth,
                TAADebug = settings.TAADebug,
                Upscaler = settings.Upscaler,
                Sharpen = settings.Sharpen,
                SharpenAmount = settings.SharpenAmount,
                Weather = settings.Weather,
                Directional = settings.DirectionalLight3D,
                Complex = settings.ComplexShaders,
                EnableTransitions = settings.EnableTransitions
            };

            if (!FSOEnvironment.TexCompressSupport) settings.TexCompression = 0;
            else if ((settings.TexCompression & 2) == 0)
            {
                settings.TexCompression = 1;
            }
            FSOEnvironment.TexCompress = (!IffFile.RETAIN_CHUNK_DATA) && (settings.TexCompression & 1) > 0;
            //end settings management

            OperatingSystem os = Environment.OSVersion;
            PlatformID pid = os.Platform;
            GameFacade.Linux = (pid == PlatformID.MacOSX || pid == PlatformID.Unix);

            FSO.Content.Content.TS1Hybrid = GlobalSettings.Default.TS1HybridEnable;
            FSO.Content.Content.TS1HybridBasePath = GlobalSettings.Default.TS1HybridPath;
            FSO.Content.Content.InitBasic(GlobalSettings.Default.StartupPath, GraphicsDevice);
            FSO.SimAntics.VMAvatar.MissingIconProvider = FSO.Client.UI.Model.UIIconCache.GetObject;
            FSO.SimAntics.VM.TestBinding = "Value";
            //VMContext.InitVMConfig();
            base.Initialize();
            EnsureWorkingDir(); // base.Initialize() (MonoGame/SDL) can reset the cwd - restore it
            RestoreMacDockIcon();

            GameFacade.GameThread = Thread.CurrentThread;

            SceneMgr = new _3DLayer();
            SceneMgr.Initialize(GraphicsDevice);

            FSOFacade.Controller = kernel.Get<GameController>();
            FSOFacade.Hints = new UI.Hints.UIHintManager();
            GameFacade.Screens = uiLayer;
            GameFacade.Scenes = SceneMgr;
            GameFacade.GraphicsDevice = GraphicsDevice;
            GameFacade.GraphicsDeviceManager = Graphics;
            GameFacade.Emojis = new Common.Rendering.Emoji.EmojiProvider(GraphicsDevice);
            CurLoader.BmpLoaderFunc = Files.ImageLoader.FromStream;
            GameFacade.Cursor = new CursorManager(GraphicsDevice);
            if (!GameFacade.Linux) GameFacade.Cursor.Init(FSO.Content.Content.Get().GetPath(""), false);

            /** Init any computed values **/
            GameFacade.Init();

            //init audio now
            HITVM.Init();
            var hit = HITVM.Get();
            hit.SetMasterVolume(HITVolumeGroup.FX, GlobalSettings.Default.FXVolume / 10f);
            hit.SetMasterVolume(HITVolumeGroup.MUSIC, GlobalSettings.Default.MusicVolume / 10f);
            hit.SetMasterVolume(HITVolumeGroup.VOX, GlobalSettings.Default.VoxVolume / 10f);
            hit.SetMasterVolume(HITVolumeGroup.AMBIENCE, GlobalSettings.Default.AmbienceVolume / 10f);

            GameFacade.Strings = new ContentStrings();
            FSOFacade.Controller.Start();

            GraphicsDevice.RasterizerState = new RasterizerState() { CullMode = CullMode.None };

            try
            {
                var audioTest = new SoundEffect(new byte[2], 44100, AudioChannels.Mono); //initialises XAudio.
                audioTest.CreateInstance().Play();
            }
            catch (Exception e)
            {
                FSOProgram.ShowDialog("Failed to initialize audio: \r\n\r\n" + e.StackTrace);
            }

            this.IsMouseVisible = true;
            // false + VSync lets Draw run at the display's true refresh rate; the sim stays at 30Hz
            // because VM.GameTickRate tracks FSOEnvironment.RefreshRate (measured in Draw below).
            this.IsFixedTimeStep = false;

            WorldContent.Init(this.Services, Content.RootDirectory);
            DGRP3DMesh.InitRCWorkers();
            if (!(FSOEnvironment.SoftwareKeyboard && FSOEnvironment.SoftwareDepth)) AddTextInput();
            base.Screen.Layers.Add(SceneMgr);
            base.Screen.Layers.Add(uiLayer);
            GameFacade.LastUpdateState = base.Screen.State;
            //Bind ninject objects
            kernel.Bind<FSO.Content.Content>().ToConstant(FSO.Content.Content.Get());
            kernel.Load(new ClientDomainModule());

            //Have to be eager with this, it sets a singleton instance on itself to avoid packets having
            //to be created using Ninject for performance reasons
            kernel.Get<cTSOSerializer>();
            var ds = kernel.Get<DataService>();
            ds.AddProvider(new ClientAvatarProvider());

            this.Window.Title = "OpenSO";
            DiscordRpcEngine.Init();

            if (!GlobalSettings.Default.Windowed && !GameFacade.GraphicsDeviceManager.IsFullScreen)
            {
                GameFacade.GraphicsDeviceManager.ToggleFullScreen();
            }
            else if (!FSOEnvironment.SoftwareDepth && !GameFacade.GraphicsDeviceManager.IsFullScreen &&
                (Window.ClientBounds.Width != Graphics.PreferredBackBufferWidth ||
                 Window.ClientBounds.Height != Graphics.PreferredBackBufferHeight))
            {
                // The OS can clamp an oversized window during creation without raising
                // ClientSizeChanged, leaving the backbuffer desynced from the client area
                // (the artifact users worked around by toggling fullscreen). Resync now.
                Window_ClientSizeChanged(null, EventArgs.Empty);
            }

            // after the window reaches its final startup state (fullscreen toggle / clamp resync)
            ApplyMacRetina();

            if (GameFacade.Linux) MP3Player.NewMode = false;

            //(new Utils.PalMapper()).DoIt();
        }

        private void SaveGraphicsModePreference(GlobalGraphicsMode obj)
        {
            GlobalSettings.Default.GlobalGraphicsMode = (int)obj;
            GlobalSettings.Default.Save();
        }

        /// <summary>
        /// Run this instance with GameRunBehavior forced as Synchronous.
        /// </summary>
        public new void Run()
        {
            Run(GameRunBehavior.Synchronous);
        }

        /// <summary>
        /// Only used on desktop targets. Use extensive reflection to AVOID linking on iOS!
        /// </summary>
        void AddTextInput()
        {
            this.Window.GetType().GetEvent("TextInput")?.AddEventHandler(this.Window, (EventHandler<TextInputEventArgs>)GameScreen.TextInput);
        }

        void RegainFocus(object sender, EventArgs e)
        {
            GameFacade.Focus = true;
        }

        void LostFocus(object sender, EventArgs e)
        {
            GameFacade.Focus = false;
        }

        protected override void OnExiting(object sender, ExitingEventArgs args)
        {
            base.OnExiting(sender, args);
            var kernel = FSOFacade.Kernel;
            if (kernel != null)
            {
                kernel.Get<LotConnectionRegulator>()?.Disconnect();
                kernel.Get<CityConnectionRegulator>()?.Disconnect();
            }
            GameThread.SetKilled();

            args.Cancel = !(FSOFacade.Controller?.CloseAttempt() ?? true);

            // CompatState below target normally means "crashed before a lot rendered", which the
            // next boot answers by flooring all graphics settings and warning the user. Reaching a
            // clean exit (closing from login/CAS without ever entering a lot) proves the current
            // configuration boots fine — mark it verified so quitting early isn't treated as a crash.
            if (!args.Cancel && GlobalSettings.Default.CompatState >= 0 &&
                GlobalSettings.Default.CompatState < GlobalSettings.TARGET_COMPAT_STATE)
            {
                GlobalSettings.Default.CompatState = GlobalSettings.TARGET_COMPAT_STATE;
                GlobalSettings.Default.Save();
            }
        }

        /// <summary>
        /// LoadContent will be called once per game and is the place to load
        /// all of your content.
        /// </summary>
        protected override void LoadContent()
        {
            Effect vitaboyEffect = null;
            try
            {
                /*
                GameFacade.MainFont = new FSO.Client.UI.Framework.Font();
                GameFacade.MainFont.AddSize(10, Content.Load<SpriteFont>("Fonts/FreeSO_10px"));
                GameFacade.MainFont.AddSize(12, Content.Load<SpriteFont>("Fonts/FreeSO_12px"));
                GameFacade.MainFont.AddSize(14, Content.Load<SpriteFont>("Fonts/FreeSO_14px"));
                GameFacade.MainFont.AddSize(16, Content.Load<SpriteFont>("Fonts/FreeSO_16px"));

                GameFacade.EdithFont = new FSO.Client.UI.Framework.Font();
                GameFacade.EdithFont.AddSize(12, Content.Load<SpriteFont>("Fonts/Trebuchet_12px"));
                GameFacade.EdithFont.AddSize(14, Content.Load<SpriteFont>("Fonts/Trebuchet_14px"));
                */

                GameFacade.VectorFont = new MSDFFont(Content.Load<FieldFont>("../Fonts/simdialogue"));

                GameFacade.EdithVectorFont = new MSDFFont(Content.Load<FieldFont>("../Fonts/trebuchet"));
                GameFacade.EdithVectorFont.VectorScale = 0.366f;
                GameFacade.EdithVectorFont.Height = 15;
                GameFacade.EdithVectorFont.YOff = 11;
                MSDFFont.MSDFEffect = Content.Load<Effect>("Effects/MSDFFont");

                vitaboyEffect = Content.Load<Effect>((FSOEnvironment.GLVer == 2) ? "Effects/VitaboyiOS" : "Effects/Vitaboy");
                uiLayer = new UILayer(this);
            }
            catch (Exception e)
            {
                FSOProgram.ShowDialog("Content could not be loaded. Make sure that the OpenSO content has been compiled! (ContentSrc/TSOClientContent.mgcb) \r\n\r\n" + e.ToString());
                Exit();
                Environment.Exit(0);
            }

            FSO.Vitaboy.Avatar.setVitaboyEffect(vitaboyEffect);
        }

        /// <summary>
        /// UnloadContent will be called once per game and is the place to unload
        /// all content.
        /// </summary>
        protected override void UnloadContent()
        {
            // TODO: Unload any non ContentManager content here
        }

        /// <summary>
        /// Allows the game to run logic such as updating the world,
        /// checking for collisions, gathering input, and playing audio.
        /// </summary>
        /// <param name="gameTime">Provides a snapshot of timing values.</param>
        protected override void Update(GameTime gameTime)
        {
            // wall-clock frame delta (IsFixedTimeStep=false); clamped so a hitch can't step huge
            FSOEnvironment.DeltaTime = System.Math.Min(0.25f, System.Math.Max(1e-5f, (float)gameTime.ElapsedGameTime.TotalSeconds));
            GameThread.UpdateExecuting = true;
            DiscordRpcEngine.Update();

            if (HITVM.Get() != null) HITVM.Get().Tick();

            base.Update(gameTime);
            GameThread.UpdateExecuting = false;
        }

        // Measure the achieved frame period and publish it as FSOEnvironment.RefreshRate; the VM reads
        // it each tick (GameTickRate) to hold the sim at 30Hz regardless of render rate.
        private System.Diagnostics.Stopwatch _frameTimer;
        private double _smoothedFrameMs = 1000.0 / 60.0;

        protected override void Draw(GameTime gameTime)
        {
            // macOS native Retina: keep the backbuffer at native pixels = currentWindowPoints * _macDpi.
            // MonoGame reverts PresentationParameters to the window's point size on resize events, so
            // re-assert before the frame binds the backbuffer (SetRenderTarget(null) reads it live).
            if (_macDpi > 0)
            {
                var cb = Window.ClientBounds;
                int tw = Math.Max(1, cb.Width) * _macDpi;
                int th = Math.Max(1, cb.Height) * _macDpi;
                var pp = GraphicsDevice.PresentationParameters;
                if (pp.BackBufferWidth != tw || pp.BackBufferHeight != th)
                {
                    pp.BackBufferWidth = tw;
                    pp.BackBufferHeight = th;
                }
                FSO.Common.Utils.PPXDepthEngine.ForcedBackbufferSize = new Point(tw, th);
                // Fix the LIVE viewport too: MonoGame's resize handling sets it to the point size
                // directly, and SetRenderTarget(null) (which re-derives it from the PP) may not run
                // for many frames on UI-only screens - the startup "quarter render" until a lot loads.
                // At the top of Draw no render target is bound, so this always targets the backbuffer.
                var vp = GraphicsDevice.Viewport;
                if (vp.Width != tw || vp.Height != th)
                    GraphicsDevice.Viewport = new Viewport(0, 0, tw, th);
            }
            base.Draw(gameTime);

            if (_frameTimer == null) { _frameTimer = System.Diagnostics.Stopwatch.StartNew(); return; }
            double ms = _frameTimer.Elapsed.TotalMilliseconds;
            _frameTimer.Restart();
            if (ms > 0.1 && ms < 1000.0) // ignore pauses/hitches
            {
                _smoothedFrameMs = _smoothedFrameMs * 0.9 + ms * 0.1; // EMA, ~10-frame settle
                int fps = System.Math.Max(10, System.Math.Min(360, (int)System.Math.Round(1000.0 / _smoothedFrameMs)));
                // Only publish on a sustained change: the VM derives both tick cadence and interpolation
                // Fraction from RefreshRate, so per-frame rewrites make the sim tick/interpolate
                // irregularly (visible stutter).
                int cur = FSOEnvironment.RefreshRate;
                if (System.Math.Abs(fps - cur) > System.Math.Max(3, cur / 12)) // ~8% band, min 3 Hz
                    FSOEnvironment.RefreshRate = fps;
            }
        }

        protected override void EndDraw()
        {
            // Some render paths finish with the PPX Backbuffer (a render target) still bound; with
            // IsFixedTimeStep off a Present can land before the world draw resets it ("Cannot call
            // Present when a render target is active"). Force the real backbuffer first.
            GraphicsDevice.SetRenderTarget(null);
            base.EndDraw();
        }
    }
}
