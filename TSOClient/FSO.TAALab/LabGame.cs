using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System.Globalization;
using System.Text;
using NVector2 = System.Numerics.Vector2;

namespace FSO.TAALab
{
    /// <summary>
    /// Interactive TAA tuning lab. Renders a synthetic animated scene at render resolution (output *
    /// renderScale), builds an exact per-object velocity buffer, then runs the game's REAL compiled TAA
    /// resolve (Content/DX/Effects/TAA.xnb, technique "TAA" = TAA_Core; the SM4 build, so ALL 16 Tune*
    /// uniforms incl. the #if SM4 RawSoften*/Ring* ones are live) on it exactly the way
    /// TAAResolve.Draw does — history/meta ping-pong at OUTPUT res (Cosmic TAAU when renderScale < 1) —
    /// with every promoted Tune* uniform live-adjustable from the keyboard.
    ///
    /// Conventions mirrored from the game (single sources of truth cited inline):
    ///  * Jitter: cycled Halton(2,3), +/-0.5 RENDER px, cycle 8*ceil((1/scale)^2) — R2Jitter.cs.
    ///  * Content shift for jitter j px: (+j.x, -j.y) in y-down pixel space; SampleJitterUV is its
    ///    negation in UV — World.PreDraw + TAAResolve.Draw sign derivation.
    ///  * Velocity buffer is JITTER-FREE (game velocity passes subtract JitterNDC), so JitterDelta = 0 —
    ///    World.cs line "the velocity buffer is jitter-free ... so zero it".
    ///  * velocity.rg = UV/frame motion (y-down UV), .b = normalized linear depth (0 near..1 far),
    ///    .a = 1 valid; components clamped +/-0.5 like the game writers. Velocity is per-PIXEL honest:
    ///    each object draws an explicit quad through LabVelocity's Current/Previous WVP matrix pair
    ///    (the game's convention — SkyVelocity.fx / RCObject.fx), so the rotating rect writes a true
    ///    spatially-varying rotation field. Both WVPs carry the SAME current-frame jitter, so the
    ///    jitter translation cancels exactly in the shader's NDC delta (buffer stays jitter-free
    ///    while coverage stays jittered like the scene).
    /// </summary>
    public class LabGame : Game
    {
        private const int OutW = 1280;
        private const int OutH = 720;

        private readonly GraphicsDeviceManager Gdm;
        private SpriteBatch SB;
        private Effect TAA;
        private Effect LabVel;
        private PixelFont Font;

        private Texture2D White;
        private Texture2D Checker;
        private Texture2D Gradient;

        private RenderTarget2D SceneRT;   // render res, Color
        private RenderTarget2D VelRT;     // render res, HalfVector4
        private RenderTarget2D[] Hist = new RenderTarget2D[2]; // output res, HalfVector4 (fp16 history)
        private RenderTarget2D[] Meta = new RenderTarget2D[2]; // output res, Color
        private int HistCurr; // index of this frame's WRITE target; 1-HistCurr is "prev"
        private VertexBuffer FSQuad;
        private VertexBuffer UnitQuad; // 0..1 unit quad for the per-object velocity pass

        // --- resolution / sim state ---
        private static readonly float[] ScalePresets = { 1f, 0.75f, 0.5f, 1f / 3f };
        private int ScaleIdx = 3; // default 1/3 — the TAAU stress case the user is tuning
        private float RenderScale => ScalePresets[ScaleIdx];
        private int RW => Math.Max(1, (int)Math.Round(OutW * RenderScale));
        private int RH => Math.Max(1, (int)Math.Round(OutH * RenderScale));

        private int FrameIdx;       // jitter phase counter (runs even when paused — rest-state test)
        private bool Paused;
        private bool TAAEnabled = true;
        private float SpeedMul = 1f;
        private bool NeedHistoryReset = true;

        // --- scene objects (positions in OUTPUT pixel space; velocity = per-frame delta / output size) ---
        private Vector2 CheckerPos = new Vector2(120, 120);
        private Vector2 CheckerPrev;
        private static readonly Vector2 CheckerVelBase = new Vector2(2.5f, 1.0f); // px/frame at speed 1
        private const int CheckerSize = 160;

        private float RotAngle;
        private float RotAnglePrev;
        private const float RotSpeedBase = 0.010f; // rad/frame at speed 1
        private static readonly Vector2 RotCenter = new Vector2(950, 210);
        private static readonly Vector2 RotSize = new Vector2(260, 64);

        private float LineDriftX = 340;
        private float LineDriftPrevX;
        private const float LineDriftBase = 0.4f; // px/frame — slow sub-pixel-ish drift

        private Vector2 PairAPos = new Vector2(150, 470);
        private Vector2 PairAPrev;
        private Vector2 PairBPos = new Vector2(700, 500);
        private Vector2 PairBPrev;
        private static readonly Vector2 PairAVelBase = new Vector2(1.8f, 0f);
        private static readonly Vector2 PairBVelBase = new Vector2(-1.2f, 0f);
        private static readonly Point PairSize = new Point(220, 150);
        private static readonly Color PairColA = new Color(0.42f, 0.45f, 0.52f);
        private static readonly Color PairColB = new Color(0.47f, 0.50f, 0.56f); // similar-color ghost test

        // --- real game 3D object meshes (.fsom remesh files, loaded standalone — see LabMesh.cs) ---
        private class MeshSceneObject
        {
            public LabMesh Mesh;
            public Vector3 Pos;        // world position (ground point)
            public float Scale = 1f;
            public int Mode;           // 0 = turntable spin, 1 = rocking (self-reveal), 2 = slow spin
            public float Angle, AnglePrev, Phase;
            public Matrix Model(float angle)
            {
                var b = Mesh.Bounds;
                var c = (b.Min + b.Max) * 0.5f;
                // recenter on the vertical axis + drop to ground, then spin about Y
                return Matrix.CreateTranslation(-c.X, -b.Min.Y, -c.Z)
                     * Matrix.CreateScale(Scale)
                     * Matrix.CreateRotationY(angle)
                     * Matrix.CreateTranslation(Pos);
            }
        }
        private readonly List<MeshSceneObject> MeshObjs = new List<MeshSceneObject>();
        private bool MeshesAvailable;
        private bool MeshesEnabled = true;
        private float MeshSpeed = 1f;    // rotation-speed multiplier (ImGui slider)
        private AlphaTestEffect MeshEffect; // unlit textured cutout draw for the color pass
        private Texture2D GrayTex;          // fallback for parts that reference in-game DGRP sprites

        // Perspective camera for the mesh corner (the game's 3D lot mode is perspective).
        private static readonly Matrix MeshView =
            Matrix.CreateLookAt(new Vector3(0f, 2.6f, 7.5f), new Vector3(0f, 1.3f, 0f), Vector3.Up);
        private static Matrix MeshProj(Vector2 jpx, int rw, int rh)
        {
            // Jitter as a post-projection clip-space translation: content pixel shift (+j.x, -j.y)
            // in y-down pixels => NDC offset (2*j.x/RW, +2*j.y/RH) (NDC y is up). Row-vector
            // convention: (x,y,z,w)*T adds w*t, i.e. a constant NDC offset after the divide —
            // exactly how the game jitters its 3D projection.
            return Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(45f), OutW / (float)OutH, 0.1f, 100f)
                 * Matrix.CreateTranslation(2f * jpx.X / rw, 2f * jpx.Y / rh, 0f);
        }

        // --- live tuning values: mutable copies of the TAATuning defaults (keep in sync with
        //     TSOClient/tso.world/Utils/TAATuning.cs — press P for a ready-to-paste block). ---
        private class Tunables
        {
            public float MotionBoostFloor = 0.12f;
            public float MotionBoostMax = 0.22f;
            public float StillGateFloor = 0.25f;
            public float MoveGateLo = 0.6f;
            public float MoveGateHi = 2.0f;
            public float RespEnd = 0.60f;
            public float MotionTrustCap = 0.72f;
            public float MotionClampTighten = 0.72f;
            public float RawSoftenOnset = 0.12f;
            public float RawSoftenSlope = 2.2f;
            public float RawSoftenMotionSup = 0.85f;
            public float Gamma = 1.5f;
            public float TexDetailFloor = 0.28f;
            public float ConfFloor = 0.14f;
            public float RingLo = 0.03f;
            public float RingHi = 0.10f;
        }
        private readonly Tunables Tune = new Tunables();
        // Pristine copy: the field initializers above ARE the TAATuning.cs defaults, so a fresh
        // instance serves as the reset-to-defaults table.
        private static readonly Tunables Defaults = new Tunables();

        private ImGuiRenderer Gui;
        private float SmoothedFps;
        private KeyboardState PrevKB;

        public LabGame()
        {
            Gdm = new GraphicsDeviceManager(this)
            {
                PreferredBackBufferWidth = OutW,
                PreferredBackBufferHeight = OutH,
                GraphicsProfile = GraphicsProfile.HiDef, // MRT + HalfVector4 targets
                SynchronizeWithVerticalRetrace = true
            };
            IsFixedTimeStep = true;
            Content.RootDirectory = "Content";
            IsMouseVisible = true;
            Window.Title = "OpenSO TAA Lab";
        }

        protected override void LoadContent()
        {
            SB = new SpriteBatch(GraphicsDevice);
            TAA = Content.Load<Effect>("Effects/TAA");
            LabVel = Content.Load<Effect>("Effects/LabVelocity");
            Font = new PixelFont(GraphicsDevice);

            White = new Texture2D(GraphicsDevice, 1, 1);
            White.SetData(new[] { Color.White });

            // Checkerboard (interior-texture ghosting test): 8px cells, moderate contrast + a tint.
            const int ck = 128, cell = 8;
            var cdata = new Color[ck * ck];
            for (int y = 0; y < ck; y++)
                for (int x = 0; x < ck; x++)
                {
                    bool a = ((x / cell) + (y / cell)) % 2 == 0;
                    cdata[y * ck + x] = a ? new Color(0.72f, 0.62f, 0.45f) : new Color(0.30f, 0.28f, 0.24f);
                }
            Checker = new Texture2D(GraphicsDevice, ck, ck);
            Checker.SetData(cdata);

            // Mid-gray background with a subtle large-scale gradient.
            const int gs = 256;
            var gdata = new Color[gs * gs];
            for (int y = 0; y < gs; y++)
                for (int x = 0; x < gs; x++)
                {
                    float v = 0.46f + 0.07f * (x / (float)gs) + 0.05f * (y / (float)gs);
                    gdata[y * gs + x] = new Color(v, v * 0.99f, v * 1.03f);
                }
            Gradient = new Texture2D(GraphicsDevice, gs, gs);
            Gradient.SetData(gdata);

            // Fullscreen strip — byte-identical layout to WorldContent.GetTextureVerts (the resolve's VS
            // flips Coord.y itself).
            var verts = new[]
            {
                new VertexPositionTexture(new Vector3(-1, -1, 0), new Vector2(0, 0)),
                new VertexPositionTexture(new Vector3(-1,  1, 0), new Vector2(0, 1)),
                new VertexPositionTexture(new Vector3( 1, -1, 0), new Vector2(1, 0)),
                new VertexPositionTexture(new Vector3( 1,  1, 0), new Vector2(1, 1)),
            };
            FSQuad = new VertexBuffer(GraphicsDevice, typeof(VertexPositionTexture), 4, BufferUsage.None);
            FSQuad.SetData(verts);

            // 0..1 unit quad (triangle strip) for the velocity pass: each object supplies its own
            // model matrix pair, so the quad is transformed to output-pixel space per draw.
            var unit = new[]
            {
                new VertexPositionTexture(new Vector3(0, 0, 0), new Vector2(0, 0)),
                new VertexPositionTexture(new Vector3(1, 0, 0), new Vector2(1, 0)),
                new VertexPositionTexture(new Vector3(0, 1, 0), new Vector2(0, 1)),
                new VertexPositionTexture(new Vector3(1, 1, 0), new Vector2(1, 1)),
            };
            UnitQuad = new VertexBuffer(GraphicsDevice, typeof(VertexPositionTexture), 4, BufferUsage.None);
            UnitQuad.SetData(unit);

            Gui = new ImGuiRenderer(this);
            Gui.RebuildFontAtlas();

            LoadGameMeshes();

            RecreateTargets();
            LogParamBindings();
            PrintCheatsheet();
        }

        /// <summary>
        /// Load a few real game 3D object meshes (.fsom remesh files committed under
        /// tso.content/Content/MeshReplace, copied to Content/MeshReplace by the csproj) via the
        /// standalone parser in LabMesh.cs. Chosen for TAA-test characteristics:
        ///  * christmastree — dense alpha-cutout foliage (fizzle/ghosting stress), turntable spin
        ///  * cursors (Sim plumbob pedestal) — thin tapered geometry, rocking (the self-reveal test)
        ///  * bannerlamp — thin lamp post + banner (banner textured; the SPR-textured body parts
        ///    fall back to flat gray — full game-content init would be needed for those sprites)
        /// Graceful fallback: if the content is missing the lab just keeps its synthetic shapes.
        /// </summary>
        private void LoadGameMeshes()
        {
            GrayTex = new Texture2D(GraphicsDevice, 1, 1);
            GrayTex.SetData(new[] { new Color(0.55f, 0.53f, 0.50f) });

            var dir = Path.Combine(AppContext.BaseDirectory, "Content", "MeshReplace");
            if (!Directory.Exists(dir))
            {
                Console.WriteLine($"[TAALab] WARNING: game mesh content not found at {dir} — 3D mesh objects disabled, synthetic shapes only.");
                MeshesAvailable = false;
                return;
            }

            var texCache = new Dictionary<string, Texture2D>();
            var wanted = new (string file, int[] groups, Vector3 pos, float scale, int mode)[]
            {
                ("christmastree_iff_100.fsom", new[] { 0 },    new Vector3(-2.4f, 0, 0), 0.85f, 0), // turntable
                ("cursors_iff_302.fsom",       new[] { 0 },    new Vector3(0f, 0, 0.4f), 0.9f,  1), // rocking
                ("bannerlamp_iff_100.fsom",    new[] { 0, 2 }, new Vector3(2.4f, 0, 0),  0.75f, 2), // slow spin
            };
            foreach (var w in wanted)
            {
                var path = Path.Combine(dir, w.file);
                try
                {
                    if (!File.Exists(path)) throw new FileNotFoundException("missing", path);
                    var mesh = LabFsomLoader.Load(path, w.groups, GraphicsDevice, GrayTex, texCache);
                    if (mesh.Parts.Count == 0) throw new InvalidDataException("no renderable parts");
                    MeshObjs.Add(new MeshSceneObject
                    {
                        Mesh = mesh, Pos = w.pos, Scale = w.scale, Mode = w.mode,
                        Phase = MeshObjs.Count * 1.7f
                    });
                    Console.WriteLine($"[TAALab] loaded game mesh {mesh.Name}: {mesh.Parts.Count} parts, " +
                        $"{mesh.Parts.Sum(p => p.PrimCount)} tris.");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[TAALab] WARNING: failed to load game mesh {w.file}: {e.Message}");
                }
            }

            MeshesAvailable = MeshObjs.Count > 0;
            if (MeshesAvailable)
            {
                MeshEffect = new AlphaTestEffect(GraphicsDevice)
                {
                    AlphaFunction = CompareFunction.Greater,
                    ReferenceAlpha = 128, // match the velocity pass's clip(a - 0.5)
                    VertexColorEnabled = false
                };
            }
            else
            {
                Console.WriteLine("[TAALab] WARNING: no game meshes loaded — 3D mesh objects disabled, synthetic shapes only.");
            }
        }

        /// <summary>
        /// Startup sanity check: report which of the 16 Tune* uniforms resolved in the loaded TAA.xnb.
        /// On the DX/SM4 build all 16 must bind; the OGL build strips the 5 #if SM4 ones.
        /// </summary>
        private void LogParamBindings()
        {
            string[] names =
            {
                "TuneMotionBoostFloor", "TuneMotionBoostMax", "TuneStillGateFloor", "TuneMoveGateLo",
                "TuneMoveGateHi", "TuneRespEnd", "TuneMotionTrustCap", "TuneMotionClampTighten",
                "TuneRawSoftenOnset", "TuneRawSoftenSlope", "TuneRawSoftenMotionSup", "TuneGamma",
                "TuneTexDetailFloor", "TuneConfFloor", "TuneRingLo", "TuneRingHi"
            };
            var bound = new List<string>();
            var missing = new List<string>();
            foreach (var n in names) (TAA.Parameters[n] != null ? bound : missing).Add(n);
            Console.WriteLine($"[TAALab] TAA.xnb Tune* uniform binding: {bound.Count}/{names.Length} bound.");
            Console.WriteLine("[TAALab]   bound:   " + string.Join(", ", bound));
            Console.WriteLine(missing.Count == 0
                ? "[TAALab]   missing: (none — DX/SM4 build confirmed)"
                : "[TAALab]   MISSING: " + string.Join(", ", missing) + "  <-- these sliders will be inert!");

            // LabVelocity matrix-pair contract: all must bind or the velocity buffer is garbage.
            string[] velNames = { "CurrentWVP", "PreviousWVP", "Depth", "MeshTex" };
            var velMissing = velNames.Where(n => LabVel.Parameters[n] == null).ToList();
            Console.WriteLine(velMissing.Count == 0
                ? "[TAALab] LabVelocity.xnb params: CurrentWVP, PreviousWVP, Depth, MeshTex all bound."
                : "[TAALab] LabVelocity.xnb MISSING params: " + string.Join(", ", velMissing) + "  <-- velocity pass broken!");
        }

        private void RecreateTargets()
        {
            SceneRT?.Dispose(); VelRT?.Dispose();
            for (int i = 0; i < 2; i++) { Hist[i]?.Dispose(); Meta[i]?.Dispose(); }
            // Depth24 (was None): the 3D mesh passes need z-testing; the 2D quads draw with depth off.
            SceneRT = new RenderTarget2D(GraphicsDevice, RW, RH, false, SurfaceFormat.Color, DepthFormat.Depth24);
            VelRT = new RenderTarget2D(GraphicsDevice, RW, RH, false, SurfaceFormat.HalfVector4, DepthFormat.Depth24);
            for (int i = 0; i < 2; i++)
            {
                Hist[i] = new RenderTarget2D(GraphicsDevice, OutW, OutH, false, SurfaceFormat.HalfVector4, DepthFormat.None);
                Meta[i] = new RenderTarget2D(GraphicsDevice, OutW, OutH, false, SurfaceFormat.Color, DepthFormat.None);
            }
            NeedHistoryReset = true;
        }

        private void ResetHistory()
        {
            // Mirrors the game's history/meta warmup clears: history black (warmup ramp seeds from the
            // current frame via N=0), meta = N 0 / zero-velocity encode 0.5,0.5 / osc 0.
            for (int i = 0; i < 2; i++)
            {
                GraphicsDevice.SetRenderTarget(Hist[i]);
                GraphicsDevice.Clear(Color.Transparent);
                GraphicsDevice.SetRenderTarget(Meta[i]);
                GraphicsDevice.Clear(new Color(0, 127, 127, 0));
            }
            GraphicsDevice.SetRenderTarget(null);
            FrameIdx = 0;
            NeedHistoryReset = false;
        }

        // ================= Halton jitter — mirrored VERBATIM from tso.common/Utils/R2Jitter.cs =================
        private static int HaltonCycle(float renderScale)
        {
            float upscale = (renderScale > 0f && renderScale < 1f) ? (1f / renderScale) : 1f;
            return 8 * Math.Max(1, (int)Math.Ceiling(upscale * upscale));
        }
        private static float HaltonValue(int index, int b)
        {
            float f = 1f, r = 0f;
            while (index > 0) { f /= b; r += f * (index % b); index /= b; }
            return r;
        }
        private static Vector2 SampleHalton(int n, float renderScale)
        {
            int cycle = HaltonCycle(renderScale);
            int i = (n % cycle) + 1;
            return new Vector2(HaltonValue(i, 2) - 0.5f, HaltonValue(i, 3) - 0.5f);
        }
        // ========================================================================================================

        // ScaledBlendFactor — mirrored from TAAResolve.cs.
        private static float ScaledBlendFactor(float scale)
        {
            const float BLEND_FACTOR = 0.06f;
            if (scale >= 1f) return BLEND_FACTOR;
            return MathHelper.Clamp(BLEND_FACTOR * scale, 0.03f, BLEND_FACTOR);
        }

        protected override void Update(GameTime gameTime)
        {
            var kb = Keyboard.GetState();
            bool Pressed(Keys k) => kb.IsKeyDown(k) && PrevKB.IsKeyUp(k);

            // Input passthrough: when ImGui wants the keyboard (e.g. Ctrl+click text entry on a
            // slider), the scene shortcuts stay quiet. ImGui's io is valid after LoadContent.
            bool guiWantsKeys = Gui != null && ImGui.GetIO().WantCaptureKeyboard;
            if (!guiWantsKeys)
            {
                if (kb.IsKeyDown(Keys.Escape)) Exit();
                if (Pressed(Keys.Space)) Paused = !Paused;
                if (Pressed(Keys.R)) NeedHistoryReset = true;
                if (Pressed(Keys.T)) { TAAEnabled = !TAAEnabled; if (TAAEnabled) NeedHistoryReset = true; }
                if (Pressed(Keys.P)) PrintTuningBlock();
            }

            PrevKB = kb;
            base.Update(gameTime);
        }

        private void AdvanceScene()
        {
            CheckerPrev = CheckerPos;
            RotAnglePrev = RotAngle;
            LineDriftPrevX = LineDriftX;
            PairAPrev = PairAPos;
            PairBPrev = PairBPos;
            foreach (var m in MeshObjs) m.AnglePrev = m.Angle;

            if (!Paused && MeshSpeed > 0)
            {
                foreach (var m in MeshObjs)
                {
                    switch (m.Mode)
                    {
                        case 0: m.Angle += 0.012f * MeshSpeed; break;                        // turntable
                        case 1: m.Phase += 0.030f * MeshSpeed;                               // rocking:
                            m.Angle = 0.7f * (float)Math.Sin(m.Phase); break;                // self-reveal
                        case 2: m.Angle += 0.004f * MeshSpeed; break;                        // slow spin
                    }
                }
            }

            if (Paused || SpeedMul <= 0) return;

            CheckerPos += CheckerVelBase * SpeedMul;
            if (CheckerPos.X > OutW) CheckerPos.X -= OutW + CheckerSize;
            if (CheckerPos.Y > OutH) CheckerPos.Y -= OutH + CheckerSize;

            RotAngle += RotSpeedBase * SpeedMul;

            LineDriftX += LineDriftBase * SpeedMul;
            if (LineDriftX > 620) LineDriftX = 340;

            PairAPos += PairAVelBase * SpeedMul;
            if (PairAPos.X > OutW) PairAPos.X -= OutW + PairSize.X;
            PairBPos += PairBVelBase * SpeedMul;
            if (PairBPos.X < -PairSize.X) PairBPos.X += OutW + PairSize.X;
        }

        protected override void Draw(GameTime gameTime)
        {
            if (NeedHistoryReset) ResetHistory();
            AdvanceScene();

            // Jitter (render px, +/-0.5 — JITTER_PIXELS 0.5, jscale 1 for scale <= 1: World.PreDraw).
            // Content pixel shift (+j.x, -j.y); SampleJitterUV = -contentShift in UV. Phase advances even
            // when the scene is paused so rest-state convergence stays testable.
            var jpx = TAAEnabled ? SampleHalton(FrameIdx++, RenderScale) : Vector2.Zero;
            var shift = new Vector2(jpx.X, -jpx.Y);
            var sampleJitterUV = new Vector2(-jpx.X / RW, jpx.Y / RH);

            var world = Matrix.CreateScale(RenderScale, RenderScale, 1f) * Matrix.CreateTranslation(shift.X, shift.Y, 0f);

            // ---- 1. Synthetic scene at render res (jittered like the game's projection shift) ----
            GraphicsDevice.SetRenderTarget(SceneRT);
            GraphicsDevice.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.Black, 1f, 0);
            SB.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp, null, null, null, world);
            SB.Draw(Gradient, new Rectangle(-2, -2, OutW + 4, OutH + 4), Color.White); // bg (overscan covers jitter)
            // similar-color pair (B over A where they cross)
            SB.Draw(White, new Rectangle((int)PairAPos.X, (int)PairAPos.Y, PairSize.X, PairSize.Y), PairColA);
            SB.Draw(White, new Rectangle((int)PairBPos.X, (int)PairBPos.Y, PairSize.X, PairSize.Y), PairColB);
            // checkerboard mover
            SB.Draw(Checker, new Rectangle((int)CheckerPos.X, (int)CheckerPos.Y, CheckerSize, CheckerSize), Color.White);
            // rotating high-contrast rectangle (self-reveal + edge test)
            SB.Draw(White, RotCenter, null, new Color(0.93f, 0.92f, 0.88f), RotAngle, new Vector2(0.5f, 0.5f),
                RotSize, SpriteEffects.None, 0f);
            // thin bright lines, 1 OUTPUT px thick (sub-render-pixel under upscale): one static, one drifting
            DrawLine(SB, new Vector2(180, 620), MathHelper.ToRadians(-32), 300, new Color(1f, 1f, 0.85f));
            DrawLine(SB, new Vector2(LineDriftX, 655), MathHelper.ToRadians(-70), 240, new Color(0.95f, 1f, 0.9f));
            SB.End();

            bool drawMeshes = MeshesEnabled && MeshesAvailable;
            var meshProj = MeshProj(jpx, RW, RH);

            // ---- 1b. Real game meshes: unlit textured cutout, perspective camera, z-tested, drawn over
            //          the 2D shapes (the 2D pass never writes depth). Same jitter as the 2D transform.
            if (drawMeshes)
            {
                GraphicsDevice.DepthStencilState = DepthStencilState.Default;
                GraphicsDevice.RasterizerState = RasterizerState.CullNone; // reconstructed winding varies
                GraphicsDevice.BlendState = BlendState.Opaque;
                GraphicsDevice.SamplerStates[0] = SamplerState.LinearClamp;
                MeshEffect.View = MeshView;
                MeshEffect.Projection = meshProj;
                foreach (var m in MeshObjs)
                {
                    MeshEffect.World = m.Model(m.Angle);
                    foreach (var part in m.Mesh.Parts)
                    {
                        MeshEffect.Texture = part.Texture;
                        MeshEffect.CurrentTechnique.Passes[0].Apply();
                        GraphicsDevice.SetVertexBuffer(part.Verts);
                        GraphicsDevice.Indices = part.Indices;
                        GraphicsDevice.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, part.PrimCount);
                    }
                }
                GraphicsDevice.DepthStencilState = DepthStencilState.None;
            }

            // ---- 2. Velocity buffer: same shapes, same jittered transforms, via LabVelocity's matrix-pair
            //         convention (SkyVelocity.fx / RCObject.fx): each object draws with its current AND
            //         previous WVP, and the PS derives honest per-pixel velocity from the clip-space delta
            //         (so rotation writes a true spatially-varying field). Both WVPs carry the SAME
            //         current-frame jitter, so the jitter translation cancels — buffer stays jitter-free.
            GraphicsDevice.SetRenderTarget(VelRT);
            GraphicsDevice.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.Transparent, 1f, 0);
            GraphicsDevice.DepthStencilState = DepthStencilState.None;
            GraphicsDevice.BlendState = BlendState.Opaque;
            GraphicsDevice.RasterizerState = RasterizerState.CullNone;

            var ortho2D = world * Matrix.CreateOrthographicOffCenter(0, RW, RH, 0, -1, 1);
            LabVel.CurrentTechnique = LabVel.Techniques["Velocity"];
            // Unit quad -> output-pixel-space model matrix pair -> jittered ortho. Depth constant per object.
            void Quad(Matrix now, Matrix prev, float depth)
            {
                LabVel.Parameters["CurrentWVP"].SetValue(now * ortho2D);
                LabVel.Parameters["PreviousWVP"].SetValue(prev * ortho2D);
                LabVel.Parameters["Depth"].SetValue(depth);
                LabVel.CurrentTechnique.Passes[0].Apply();
                GraphicsDevice.SetVertexBuffer(UnitQuad);
                GraphicsDevice.DrawPrimitives(PrimitiveType.TriangleStrip, 0, 2);
            }
            Matrix RectM(Vector2 pos, Vector2 size) =>
                Matrix.CreateScale(size.X, size.Y, 1f) * Matrix.CreateTranslation(pos.X, pos.Y, 0f);
            Matrix RotM(float angle) =>
                Matrix.CreateTranslation(-0.5f, -0.5f, 0f) * Matrix.CreateScale(RotSize.X, RotSize.Y, 1f) *
                Matrix.CreateRotationZ(angle) * Matrix.CreateTranslation(RotCenter.X, RotCenter.Y, 0f);
            Matrix LineM(Vector2 start, float angle, float length) =>
                Matrix.CreateTranslation(0f, -0.5f, 0f) * Matrix.CreateScale(length, 1f, 1f) *
                Matrix.CreateRotationZ(angle) * Matrix.CreateTranslation(start.X, start.Y, 0f);

            var bgM = RectM(new Vector2(-2, -2), new Vector2(OutW + 4, OutH + 4));
            Quad(bgM, bgM, 0.95f);
            Quad(RectM(PairAPos, PairSize.ToVector2()), RectM(PairAPrev, PairSize.ToVector2()), 0.60f);
            Quad(RectM(PairBPos, PairSize.ToVector2()), RectM(PairBPrev, PairSize.ToVector2()), 0.55f);
            Quad(RectM(CheckerPos, new Vector2(CheckerSize)), RectM(CheckerPrev, new Vector2(CheckerSize)), 0.50f);
            // Rotating rect: the matrix pair writes its TRUE spatially-varying rotational velocity field.
            Quad(RotM(RotAngle), RotM(RotAnglePrev), 0.45f);
            var stLineM = LineM(new Vector2(180, 620), MathHelper.ToRadians(-32), 300);
            Quad(stLineM, stLineM, 0.30f);
            Quad(LineM(new Vector2(LineDriftX, 655), MathHelper.ToRadians(-70), 240),
                 LineM(new Vector2(LineDriftPrevX, 655), MathHelper.ToRadians(-70), 240), 0.35f);

            // ---- 2b. Mesh velocities: per-pixel rotational velocity from the model matrix pair, alpha
            //          cutout matching the color pass, per-pixel linear depth (saturate(clip.w/800) — the
            //          game's PackDepth). Z-tested against a fresh depth buffer like the color pass.
            if (drawMeshes)
            {
                GraphicsDevice.DepthStencilState = DepthStencilState.Default;
                LabVel.CurrentTechnique = LabVel.Techniques["VelocityMasked"];
                var vp = MeshView * meshProj;
                foreach (var m in MeshObjs)
                {
                    LabVel.Parameters["CurrentWVP"].SetValue(m.Model(m.Angle) * vp);
                    LabVel.Parameters["PreviousWVP"].SetValue(m.Model(m.AnglePrev) * vp);
                    foreach (var part in m.Mesh.Parts)
                    {
                        LabVel.Parameters["MeshTex"].SetValue(part.Texture);
                        LabVel.CurrentTechnique.Passes[0].Apply();
                        GraphicsDevice.SetVertexBuffer(part.Verts);
                        GraphicsDevice.Indices = part.Indices;
                        GraphicsDevice.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, part.PrimCount);
                    }
                }
                GraphicsDevice.DepthStencilState = DepthStencilState.None;
            }

            if (TAAEnabled)
            {
                // ---- 3. The real resolve — uniform-for-uniform what TAAResolve.Draw sets ----
                var histPrev = Hist[1 - HistCurr];
                var metaPrev = Meta[1 - HistCurr];
                GraphicsDevice.SetRenderTargets(Hist[HistCurr], Meta[HistCurr]);
                GraphicsDevice.BlendState = BlendState.Opaque;

                TAA.Parameters["colorTex"]?.SetValue(SceneRT);
                TAA.Parameters["historyTex"]?.SetValue(histPrev);
                TAA.Parameters["metaHistoryTex"]?.SetValue(metaPrev);
                TAA.Parameters["velocityTex"]?.SetValue(VelRT);
                TAA.Parameters["InvScreenSize"]?.SetValue(new Vector2(1f / OutW, 1f / OutH));
                TAA.Parameters["InvColorSize"]?.SetValue(new Vector2(1f / RW, 1f / RH));
                TAA.Parameters["BlendFactor"]?.SetValue(ScaledBlendFactor(RenderScale));
                TAA.Parameters["MaxAccum"]?.SetValue(128f); // TAAResolve.MAX_ACCUM
                TAA.Parameters["JitterDelta"]?.SetValue(Vector2.Zero); // velocity buffer is jitter-free
                // fp16 history variant (the lab's history IS HalfVector4)
                TAA.Parameters["DepthRejectParams"]?.SetValue(new Vector4(0.0015f, 12f, 0f, 0.02f));
                TAA.Parameters["SampleJitterUV"]?.SetValue(sampleJitterUV);
                TAA.Parameters["VelGatePxScale"]?.SetValue(1f); // TAAU/native grids are native-sized
                TAA.Parameters["JitterPhases"]?.SetValue((float)HaltonCycle(RenderScale));
                // live tunables (same null-safe pattern as TAAResolve — SM3-stripped uniforms just skip)
                TAA.Parameters["TuneMotionBoostFloor"]?.SetValue(Tune.MotionBoostFloor);
                TAA.Parameters["TuneMotionBoostMax"]?.SetValue(Tune.MotionBoostMax);
                TAA.Parameters["TuneStillGateFloor"]?.SetValue(Tune.StillGateFloor);
                TAA.Parameters["TuneMoveGateLo"]?.SetValue(Tune.MoveGateLo);
                TAA.Parameters["TuneMoveGateHi"]?.SetValue(Tune.MoveGateHi);
                TAA.Parameters["TuneRespEnd"]?.SetValue(Tune.RespEnd);
                TAA.Parameters["TuneMotionTrustCap"]?.SetValue(Tune.MotionTrustCap);
                TAA.Parameters["TuneMotionClampTighten"]?.SetValue(Tune.MotionClampTighten);
                TAA.Parameters["TuneRawSoftenOnset"]?.SetValue(Tune.RawSoftenOnset);
                TAA.Parameters["TuneRawSoftenSlope"]?.SetValue(Tune.RawSoftenSlope);
                TAA.Parameters["TuneRawSoftenMotionSup"]?.SetValue(Tune.RawSoftenMotionSup);
                TAA.Parameters["TuneGamma"]?.SetValue(Tune.Gamma);
                TAA.Parameters["TuneTexDetailFloor"]?.SetValue(Tune.TexDetailFloor);
                TAA.Parameters["TuneConfFloor"]?.SetValue(Tune.ConfFloor);
                TAA.Parameters["TuneRingLo"]?.SetValue(Tune.RingLo);
                TAA.Parameters["TuneRingHi"]?.SetValue(Tune.RingHi);

                TAA.CurrentTechnique = TAA.Techniques["TAA"];
                TAA.CurrentTechnique.Passes[0].Apply();
                GraphicsDevice.SetVertexBuffer(FSQuad);
                GraphicsDevice.DrawPrimitives(PrimitiveType.TriangleStrip, 0, 2);

                // ---- 4. Blit resolved history to the screen; swap the ping-pong ----
                GraphicsDevice.SetRenderTarget(null);
                SB.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
                SB.Draw(Hist[HistCurr], new Rectangle(0, 0, OutW, OutH), Color.White);
                SB.End();
                HistCurr = 1 - HistCurr;
            }
            else
            {
                // A/B: raw upscaled current frame, no temporal work.
                GraphicsDevice.SetRenderTarget(null);
                SB.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
                SB.Draw(SceneRT, new Rectangle(0, 0, OutW, OutH), Color.White);
                SB.End();
            }

            float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
            if (dt > 0) SmoothedFps = SmoothedFps <= 0 ? 1f / dt : MathHelper.Lerp(SmoothedFps, 1f / dt, 0.05f);

            DrawOverlay();

            // ImGui last, over everything (primary UI).
            Gui.BeforeLayout(gameTime);
            DrawGui();
            Gui.AfterLayout();

            base.Draw(gameTime);
        }

        /// <summary>Thin line as a rotated 1-output-px-thick sprite (sub-render-pixel under upscale).</summary>
        private void DrawLine(SpriteBatch sb, Vector2 start, float angle, float length, Color color)
        {
            sb.Draw(White, start, null, color, angle, new Vector2(0f, 0.5f), new Vector2(length, 1f), SpriteEffects.None, 0f);
        }

        /// <summary>Minimal pixel-font readout (ImGui is the primary UI now).</summary>
        private void DrawOverlay()
        {
            SB.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            const float sc = 2f;
            SB.Draw(White, new Rectangle(4, 4, 470, 26), Color.Black * 0.62f);
            string status = string.Format(CultureInfo.InvariantCulture,
                "TAA {0}  {1}  SCALE {2:0.00}  {3}X{4}  {5:0} FPS", TAAEnabled ? "ON" : "OFF",
                Paused ? "PAUSED" : "RUN", RenderScale, RW, RH, SmoothedFps);
            Font.Draw(SB, status, new Vector2(10, 8), Color.Yellow, sc);
            SB.End();
        }

        // ==================================== ImGui UI ====================================

        private static readonly string[] ScaleLabels = { "1.00", "0.75", "0.50", "0.33" };

        private void DrawGui()
        {
            ImGui.SetNextWindowPos(new NVector2(OutW - 440, 10), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(new NVector2(430, 700), ImGuiCond.FirstUseEver);
            ImGui.Begin("TAA Tuning");

            if (ImGui.CollapsingHeader("Scene", ImGuiTreeNodeFlags.DefaultOpen))
            {
                int idx = ScaleIdx;
                if (ImGui.Combo("Render scale", ref idx, ScaleLabels, ScaleLabels.Length) && idx != ScaleIdx)
                {
                    ScaleIdx = idx;
                    RecreateTargets();
                }
                bool raw = !TAAEnabled;
                if (ImGui.Checkbox("TAA off (raw)", ref raw))
                {
                    TAAEnabled = !raw;
                    if (TAAEnabled) NeedHistoryReset = true;
                }
                ImGui.SameLine();
                ImGui.Checkbox("Pause", ref Paused);
                ImGui.SliderFloat("Object speed", ref SpeedMul, 0f, 6f, "%.2f");
                if (MeshesAvailable)
                {
                    ImGui.Checkbox("3D game meshes", ref MeshesEnabled);
                    ImGui.SliderFloat("Mesh spin speed", ref MeshSpeed, 0f, 4f, "%.2f");
                }
                else
                {
                    ImGui.TextDisabled("3D game meshes unavailable (Content/MeshReplace missing)");
                }
                if (ImGui.Button("Reset history")) NeedHistoryReset = true;
                ImGui.SameLine();
                if (ImGui.Button("Print C# block")) PrintTuningBlock();
            }

            if (ImGui.CollapsingHeader("Motion gates", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.SliderFloat("MoveGateLo (px)", ref Tune.MoveGateLo, 0f, 6f, "%.3f");
                ImGui.SliderFloat("MoveGateHi (px)", ref Tune.MoveGateHi, 0f, 6f, "%.3f");
                Tune.MoveGateHi = Math.Max(Tune.MoveGateHi, Tune.MoveGateLo); // keep lo <= hi
                ImGui.SliderFloat("StillGateFloor", ref Tune.StillGateFloor, 0f, 1f, "%.3f");
                ImGui.SliderFloat("MotionBoostFloor", ref Tune.MotionBoostFloor, 0f, 1f, "%.3f");
                ImGui.SliderFloat("MotionBoostMax", ref Tune.MotionBoostMax, 0f, 1f, "%.3f");
                if (ImGui.Button("Reset to defaults##gates"))
                {
                    Tune.MoveGateLo = Defaults.MoveGateLo; Tune.MoveGateHi = Defaults.MoveGateHi;
                    Tune.StillGateFloor = Defaults.StillGateFloor;
                    Tune.MotionBoostFloor = Defaults.MotionBoostFloor; Tune.MotionBoostMax = Defaults.MotionBoostMax;
                }
            }

            if (ImGui.CollapsingHeader("Trust / scrub", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.SliderFloat("RespEnd", ref Tune.RespEnd, 0f, 1f, "%.3f");
                ImGui.SliderFloat("MotionTrustCap", ref Tune.MotionTrustCap, 0f, 1f, "%.3f");
                ImGui.SliderFloat("MotionClampTighten", ref Tune.MotionClampTighten, 0f, 1f, "%.3f");
                ImGui.SliderFloat("Gamma (clamp sigma)", ref Tune.Gamma, 0.5f, 3f, "%.3f");
                if (ImGui.Button("Reset to defaults##trust"))
                {
                    Tune.RespEnd = Defaults.RespEnd; Tune.MotionTrustCap = Defaults.MotionTrustCap;
                    Tune.MotionClampTighten = Defaults.MotionClampTighten; Tune.Gamma = Defaults.Gamma;
                }
            }

            if (ImGui.CollapsingHeader("Display", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.SliderFloat("RawSoftenOnset", ref Tune.RawSoftenOnset, 0f, 1f, "%.3f");
                ImGui.SliderFloat("RawSoftenSlope", ref Tune.RawSoftenSlope, 0f, 5f, "%.3f");
                ImGui.SliderFloat("RawSoftenMotionSup", ref Tune.RawSoftenMotionSup, 0f, 1f, "%.3f");
                ImGui.SliderFloat("ConfFloor", ref Tune.ConfFloor, 0f, 1f, "%.3f");
                ImGui.SliderFloat("TexDetailFloor", ref Tune.TexDetailFloor, 0f, 1f, "%.3f");
                if (ImGui.Button("Reset to defaults##display"))
                {
                    Tune.RawSoftenOnset = Defaults.RawSoftenOnset; Tune.RawSoftenSlope = Defaults.RawSoftenSlope;
                    Tune.RawSoftenMotionSup = Defaults.RawSoftenMotionSup;
                    Tune.ConfFloor = Defaults.ConfFloor; Tune.TexDetailFloor = Defaults.TexDetailFloor;
                }
            }

            if (ImGui.CollapsingHeader("Edge/ring", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.SliderFloat("RingLo", ref Tune.RingLo, 0f, 1f, "%.3f");
                ImGui.SliderFloat("RingHi", ref Tune.RingHi, 0f, 1f, "%.3f");
                Tune.RingHi = Math.Max(Tune.RingHi, Tune.RingLo); // keep lo <= hi
                if (ImGui.Button("Reset to defaults##ring"))
                {
                    Tune.RingLo = Defaults.RingLo; Tune.RingHi = Defaults.RingHi;
                }
            }

            ImGui.Separator();
            if (ImGui.Button("Reset ALL to defaults"))
            {
                Tune.MotionBoostFloor = Defaults.MotionBoostFloor; Tune.MotionBoostMax = Defaults.MotionBoostMax;
                Tune.StillGateFloor = Defaults.StillGateFloor;
                Tune.MoveGateLo = Defaults.MoveGateLo; Tune.MoveGateHi = Defaults.MoveGateHi;
                Tune.RespEnd = Defaults.RespEnd; Tune.MotionTrustCap = Defaults.MotionTrustCap;
                Tune.MotionClampTighten = Defaults.MotionClampTighten;
                Tune.RawSoftenOnset = Defaults.RawSoftenOnset; Tune.RawSoftenSlope = Defaults.RawSoftenSlope;
                Tune.RawSoftenMotionSup = Defaults.RawSoftenMotionSup;
                Tune.Gamma = Defaults.Gamma; Tune.TexDetailFloor = Defaults.TexDetailFloor;
                Tune.ConfFloor = Defaults.ConfFloor;
                Tune.RingLo = Defaults.RingLo; Tune.RingHi = Defaults.RingHi;
            }
            ImGui.TextDisabled("Space pause  R reset hist  T TAA A/B  P print  Esc quit");

            ImGui.End();
        }

        private void PrintCheatsheet()
        {
            Console.WriteLine("==================== OpenSO TAA Lab ====================");
            Console.WriteLine("Runs the game's REAL compiled TAA resolve (DX/SM4 TAA.xnb, technique TAA)");
            Console.WriteLine("on a synthetic scene. History/meta at output res; TAAU when scale < 1.");
            Console.WriteLine("All 16 Tune* uniforms are live (incl. the SM4-only RawSoften*/Ring* set).");
            Console.WriteLine();
            Console.WriteLine("Primary UI: the ImGui 'TAA Tuning' window (sliders, per-group + global");
            Console.WriteLine("defaults reset, scene controls, Print C# block).");
            Console.WriteLine("  Space       pause/resume scene motion (jitter keeps running)");
            Console.WriteLine("  R           reset history (black + warmup meta clear)");
            Console.WriteLine("  T           toggle TAA off/on (raw upscaled A/B)");
            Console.WriteLine("  P           print current values as a C# TAATuning block");
            Console.WriteLine("  Esc         quit");
            Console.WriteLine();
            Console.WriteLine("Scene: gradient bg / checkerboard mover (interior-texture ghosting) /");
            Console.WriteLine("rotating rect (honest per-pixel rotational velocity) / thin 1-output-px lines");
            Console.WriteLine("(static + drifting; sub-render-pixel fizzle) / similar-color sliding pair /");
            Console.WriteLine("real game 3D meshes (christmastree spin, plumbob pedestal rocking self-reveal,");
            Console.WriteLine("bannerlamp slow spin) with per-pixel matrix-pair velocity + clip.w/800 depth.");
            Console.WriteLine("=========================================================");
        }

        private void PrintTuningBlock()
        {
            var sb = new StringBuilder();
            sb.AppendLine("// FSO.TAALab snapshot - paste over the fields in FSO.LotView.Utils.TAATuning:");
            void F(string n, float v) => sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "        public static float {0} = {1}f;", n, Math.Round(v, 4)));
            F("MotionBoostFloor", Tune.MotionBoostFloor);
            F("MotionBoostMax", Tune.MotionBoostMax);
            F("StillGateFloor", Tune.StillGateFloor);
            F("MoveGateLo", Tune.MoveGateLo);
            F("MoveGateHi", Tune.MoveGateHi);
            F("RespEnd", Tune.RespEnd);
            F("MotionTrustCap", Tune.MotionTrustCap);
            F("MotionClampTighten", Tune.MotionClampTighten);
            F("RawSoftenOnset", Tune.RawSoftenOnset);
            F("RawSoftenSlope", Tune.RawSoftenSlope);
            F("RawSoftenMotionSup", Tune.RawSoftenMotionSup);
            F("Gamma", Tune.Gamma);
            F("TexDetailFloor", Tune.TexDetailFloor);
            F("ConfFloor", Tune.ConfFloor);
            F("RingLo", Tune.RingLo);
            F("RingHi", Tune.RingHi);
            Console.WriteLine(sb.ToString());
        }
    }
}
