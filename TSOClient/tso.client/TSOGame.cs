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

            FSOEnvironment.DPIScaleFactor = GlobalSettings.Default.DPIScaleFactor;
            if (!FSOEnvironment.SoftwareDepth)
            {
                Graphics.PreferredBackBufferWidth = (int)(GlobalSettings.Default.GraphicsWidth * FSOEnvironment.DPIScaleFactor);
                Graphics.PreferredBackBufferHeight = (int)(GlobalSettings.Default.GraphicsHeight * FSOEnvironment.DPIScaleFactor);
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
            if (newChange || !GlobalSettings.Default.Windowed) return;
            if (Window.ClientBounds.Width == 0 || Window.ClientBounds.Height == 0) return;
            newChange = true;
            var width = Math.Max(1, Window.ClientBounds.Width);
            var height = Math.Max(1, Window.ClientBounds.Height);
            Graphics.PreferredBackBufferWidth = width;
            Graphics.PreferredBackBufferHeight = height;
            Graphics.ApplyChanges();

            GlobalSettings.Default.GraphicsWidth = width;
            GlobalSettings.Default.GraphicsHeight = height;

            newChange = false;
            if (uiLayer?.CurrentUIScreen == null) return;

            uiLayer.SpriteBatch.ResizeBuffer(GlobalSettings.Default.GraphicsWidth, GlobalSettings.Default.GraphicsHeight);
            GlobalSettings.Default.GraphicsWidth = (int)(width / FSOEnvironment.DPIScaleFactor);
            GlobalSettings.Default.GraphicsHeight = (int)(height / FSOEnvironment.DPIScaleFactor);
            uiLayer.CurrentUIScreen.GameResized();
        }

        /// <summary>
        /// Allows the game to perform any initialization it needs to before starting to run.
        /// This is where it can query for any required services and load any non-graphic
        /// related content.  Calling base.Initialize will enumerate through any components
        /// and initialize them as well.
        /// </summary>
        /// <summary>
        /// The macOS .app sets the process working directory to the bundle's Contents/MacOS, but the game
        /// loads many assets through paths relative to the working dir (e.g. "Content/UI/hints/...", EOD
        /// textures, splash screens). Point the cwd at the install dir — the folder holding the real Content
        /// next to the .app — so those relative loads resolve. FSO.Unix sets an absolute ContentDir; on
        /// Windows/Linux ContentDir is relative and the cwd is already the install dir, so this is a no-op.
        /// Called both before and after base.Initialize() because MonoGame can reset the cwd during init.
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
            catch { /* best effort — absolute ContentDir still covers the core content scan */ }
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
                MotionBlur = settings.MotionBlur,
                MotionBlurAmount = settings.MotionBlurAmount,
                Bloom = settings.Bloom,
                BloomThreshold = settings.BloomThreshold,
                BloomIntensity = settings.BloomIntensity,
                AO = settings.AO,
                AORadius = settings.AORadius,
                AOIntensity = settings.AOIntensity,
                VelocityDebug = settings.VelocityDebug,
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
            EnsureWorkingDir(); // base.Initialize() (MonoGame/SDL) can reset the cwd to the bundle — restore it

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
            // Decoupled render timing: false + VSync (set above) lets Draw run at the display's true refresh
            // rate instead of a fixed 60. The SimAntics sim stays at 30Hz because VM.GameTickRate tracks the
            // measured fps published as FSOEnvironment.RefreshRate (see the Draw override below). So the
            // interpolated frame rate follows the monitor while game-logic speed is unchanged.
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
            // Real frame delta for framerate-independent animation. IsFixedTimeStep=false -> ElapsedGameTime is
            // wall-clock; clamp so a hitch/alt-tab can't produce a huge step.
            FSOEnvironment.DeltaTime = System.Math.Min(0.25f, System.Math.Max(1e-5f, (float)gameTime.ElapsedGameTime.TotalSeconds));
            GameThread.UpdateExecuting = true;
            DiscordRpcEngine.Update();

            if (HITVM.Get() != null) HITVM.Get().Tick();

            base.Update(gameTime);
            GameThread.UpdateExecuting = false;
        }

        // --- Decoupled render-rate measurement ---------------------------------------------------------
        // With IsFixedTimeStep=false + VSync, Draw is paced by the display's true refresh rate. Measure the
        // achieved frame period and publish it as FSOEnvironment.RefreshRate; the SimAntics VM reads that each
        // tick (GameTickRate) to hold the sim at 30Hz, so a faster render rate never speeds the game up. This
        // is display-agnostic (works regardless of which monitor / virtual display the game ends up on).
        private System.Diagnostics.Stopwatch _frameTimer;
        private double _smoothedFrameMs = 1000.0 / 60.0;

        protected override void Draw(GameTime gameTime)
        {
            base.Draw(gameTime);

            if (_frameTimer == null) { _frameTimer = System.Diagnostics.Stopwatch.StartNew(); return; }
            double ms = _frameTimer.Elapsed.TotalMilliseconds;
            _frameTimer.Restart();
            if (ms > 0.1 && ms < 1000.0) // ignore pauses/hitches; 0.1ms..1s is a sane per-frame range
            {
                _smoothedFrameMs = _smoothedFrameMs * 0.9 + ms * 0.1; // EMA, ~10-frame settle
                int fps = System.Math.Max(10, System.Math.Min(360, (int)System.Math.Round(1000.0 / _smoothedFrameMs)));
                // Publish RefreshRate only on a meaningful, sustained change. The VM derives BOTH its fixed
                // 30Hz tick cadence and the interpolation Fraction from RefreshRate; rewriting it every frame
                // makes GameTickRate wobble, so the networked sim ticks + interpolates irregularly -> visible
                // stutter as the client reconciles with the server. A hysteresis band keeps it pinned while
                // fps is steady (VSync at the display rate), and still tracks a real sustained change.
                int cur = FSOEnvironment.RefreshRate;
                if (System.Math.Abs(fps - cur) > System.Math.Max(3, cur / 12)) // ~8% band, min 3 Hz
                    FSOEnvironment.RefreshRate = fps;
            }
        }

        protected override void EndDraw()
        {
            // Safety net before Present. Some lot/thumbnail/transition render paths bind a render target
            // and finish by rebinding the PPX Backbuffer (still a render target) rather than the real
            // screen. When that runs outside the world draw (e.g. a city->lot load) and IsFixedTimeStep is
            // off, a Present can land before the world draw resets the target, throwing "Cannot call Present
            // when a render target is active". Force the real backbuffer so the present is always valid.
            GraphicsDevice.SetRenderTarget(null);
            base.EndDraw();
        }
    }
}
