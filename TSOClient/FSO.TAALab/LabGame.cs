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
    /// resolve (Content/DX/Effects/TAA.xnb, technique "TAA" = TAA_Core or "TAALite" — live A/B combo;
    /// the SM4 build, so ALL 22 Tune* uniforms incl. the #if SM4-only ones are live) on it exactly the way
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
        // Resolve technique A/B: 0 = "TAA" (full Cosmic resolve, TAA_Core), 1 = "TAALite". The two write
        // DIFFERENT meta semantics (lite has no lock/evidence machinery), so switching resets history/meta.
        private int TechIdx;
        private static readonly string[] TechLabels = { "Full Cosmic TAA", "TAA Lite" };
        private static readonly string[] TechNames = { "TAA", "TAALite" };

        // --- scene objects (positions in OUTPUT pixel space; velocity = per-frame delta / output size).
        //     DETERMINISM (auto-tuner prerequisite): object state is a PURE function of virtual scene
        //     time — PoseAt(simT, meshT, reveal) — never incrementally mutated, so any frame of the
        //     scripted evaluation sequence replays bit-identically from its integer frame index.
        //     Interactive mode just accumulates SimT/MeshT (speed sliders scale the accumulation). ---
        private static readonly Vector2 CheckerStart = new Vector2(120, 120);
        private static readonly Vector2 CheckerVelBase = new Vector2(2.5f, 1.0f); // px/frame at speed 1
        private const int CheckerSize = 160;

        private const float RotSpeedBase = 0.010f; // rad/frame at speed 1
        private static readonly Vector2 RotCenter = new Vector2(950, 210);
        private static readonly Vector2 RotSize = new Vector2(260, 64);

        private const float LineDriftMin = 340f, LineDriftMax = 620f;
        private const float LineDriftBase = 0.4f; // px/frame — slow sub-pixel-ish drift

        private static readonly Vector2 PairAStart = new Vector2(150, 470);
        private static readonly Vector2 PairBStart = new Vector2(700, 500);
        private static readonly Vector2 PairAVelBase = new Vector2(1.8f, 0f);
        private static readonly Vector2 PairBVelBase = new Vector2(-1.2f, 0f);
        private static readonly Point PairSize = new Point(220, 150);
        private static readonly Color PairColA = new Color(0.42f, 0.45f, 0.52f);
        private static readonly Color PairColB = new Color(0.47f, 0.50f, 0.56f); // similar-color ghost test

        // Abrupt-reveal test (auto-tune phase C): a textured block toggles on AND the checker teleports.
        // Both appear with ZERO velocity (prev pose = current pose on the reveal frame) — the honest
        // "new content, no motion vector" convention — stressing the accumulation/scrub lifecycle.
        private static readonly Vector2 RevealPos = new Vector2(500, 260);
        private static readonly Point RevealSize = new Point(240, 180);
        private static readonly Color RevealCol = new Color(0.88f, 0.58f, 0.46f);
        private const float TeleportDX = 400f, TeleportDY = 150f;

        /// <summary>Full scene state for one frame — computed by PoseAt, never mutated.</summary>
        private struct ScenePose
        {
            public Vector2 Checker;
            public float Rot;
            public float LineX;
            public Vector2 PairA, PairB;
            public bool Reveal;
            public float[] MeshAngles;
        }
        private float SimT, MeshT;          // interactive virtual scene time (frames at speed 1)
        private ScenePose CurPose, PrevPose;

        // --- real game 3D object meshes (.fsom remesh files, loaded standalone — see LabMesh.cs) ---
        private class MeshSceneObject
        {
            public LabMesh Mesh;
            public Vector3 Pos;        // world position (ground point)
            public float Scale = 1f;
            public int Mode;           // 0 = turntable spin, 1 = rocking (self-reveal), 2 = slow spin
            public float BasePhase;    // rocking phase offset — FIXED at load (determinism)
            /// <summary>Pure rotation angle at virtual mesh time t (frames at speed 1).</summary>
            public float AngleAt(float t)
            {
                switch (Mode)
                {
                    case 0: return 0.012f * t;                                     // turntable
                    case 1: return 0.7f * (float)Math.Sin(BasePhase + 0.030f * t); // rocking self-reveal
                    default: return 0.004f * t;                                    // slow spin
                }
            }
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
            // structural constants (2026-07-07 promotion — the full-vs-lite haze/ghost hunt)
            public float DirectClampMix = 0.75f;
            public float KarisFade = 1.0f;
            public float GammaMotionDecay = 0.6f;
            public float ConfFadeN = 20.0f;
            public float GrowOffPhase = 0.3f;
            public float DeepCapBase = 0.992f;
            // TAALite tunables (take effect ONLY under the "TAALite" technique — 2026-07-07 promotion;
            // canonical defaults/comments: TAATuning.cs bottom section)
            public float LiteGamma = 1.5f;
            public float LiteGammaScale = 2.0f;
            public float LiteDeepCap = 0.985f;
            public float LiteRespEnd = 0.68f;
            public float LiteMotionBoost = 0.35f;
            public float LiteConfFloor = 0.14f;
            public float LiteMoveGateLo = 0.6f;
            public float LiteMoveGateHi = 2.0f;
            public float LiteHonestLo = 0.65f;
            public float LiteHonestHi = 0.98f;
        }
        private readonly Tunables Tune = new Tunables();
        // Pristine copy: the field initializers above ARE the TAATuning.cs defaults, so a fresh
        // instance serves as the reset-to-defaults table.
        private static readonly Tunables Defaults = new Tunables();

        // --- 22-parameter vector view of Tunables for the auto-tuner. Order matches the print block;
        //     bounds ARE the ImGui slider ranges (the optimizer reflects+clamps at them). ---
        private static readonly string[] ParamNames =
        {
            "MotionBoostFloor", "MotionBoostMax", "StillGateFloor", "MoveGateLo", "MoveGateHi",
            "RespEnd", "MotionTrustCap", "MotionClampTighten", "RawSoftenOnset", "RawSoftenSlope",
            "RawSoftenMotionSup", "Gamma", "TexDetailFloor", "ConfFloor", "RingLo", "RingHi",
            "DirectClampMix", "KarisFade", "GammaMotionDecay", "ConfFadeN", "GrowOffPhase", "DeepCapBase"
        };
        private static readonly float[] ParamLo =
        {
            0f, 0f, 0f, 0f, 0f,  0f, 0f, 0f, 0f, 0f,  0f, 0.5f, 0f, 0f, 0f, 0f,  0f, 0f, 0f, 1f, 0f, 0.9f
        };
        private static readonly float[] ParamHi =
        {
            1f, 1f, 1f, 6f, 6f,  1f, 1f, 1f, 1f, 5f,  1f, 3f, 1f, 1f, 1f, 1f,  1f, 1f, 1f, 64f, 1f, 0.999f
        };
        private static float[] ToVector(Tunables t) => new[]
        {
            t.MotionBoostFloor, t.MotionBoostMax, t.StillGateFloor, t.MoveGateLo, t.MoveGateHi,
            t.RespEnd, t.MotionTrustCap, t.MotionClampTighten, t.RawSoftenOnset, t.RawSoftenSlope,
            t.RawSoftenMotionSup, t.Gamma, t.TexDetailFloor, t.ConfFloor, t.RingLo, t.RingHi,
            t.DirectClampMix, t.KarisFade, t.GammaMotionDecay, t.ConfFadeN, t.GrowOffPhase, t.DeepCapBase
        };
        private static void ApplyVector(float[] v, Tunables t)
        {
            t.MotionBoostFloor = v[0]; t.MotionBoostMax = v[1]; t.StillGateFloor = v[2];
            t.MoveGateLo = v[3]; t.MoveGateHi = Math.Max(v[4], v[3]); // lo <= hi, like the sliders
            t.RespEnd = v[5]; t.MotionTrustCap = v[6]; t.MotionClampTighten = v[7];
            t.RawSoftenOnset = v[8]; t.RawSoftenSlope = v[9]; t.RawSoftenMotionSup = v[10];
            t.Gamma = v[11]; t.TexDetailFloor = v[12]; t.ConfFloor = v[13];
            t.RingLo = v[14]; t.RingHi = Math.Max(v[15], v[14]); // lo <= hi
            t.DirectClampMix = v[16]; t.KarisFade = v[17]; t.GammaMotionDecay = v[18];
            t.ConfFadeN = v[19]; t.GrowOffPhase = v[20]; t.DeepCapBase = v[21];
        }
        private static Tunables FromVector(float[] v)
        {
            var t = new Tunables();
            ApplyVector(v, t);
            return t;
        }

        // --- 10-parameter Lite* vector view (TAALite technique). Same treatment: order matches the
        //     print block, bounds are the slider ranges, paired gates keep lo <= hi. ---
        private static readonly string[] LiteParamNames =
        {
            "LiteGamma", "LiteGammaScale", "LiteDeepCap", "LiteRespEnd", "LiteMotionBoost",
            "LiteConfFloor", "LiteMoveGateLo", "LiteMoveGateHi", "LiteHonestLo", "LiteHonestHi"
        };
        private static readonly float[] LiteParamLo = { 0.5f, 1f, 0.9f, 0.4f, 0f, 0f, 0f, 0.5f, 0.2f, 0.5f };
        private static readonly float[] LiteParamHi = { 3f, 3f, 0.999f, 0.9f, 1f, 1f, 4f, 6f, 0.9f, 1f };
        private static float[] ToLiteVector(Tunables t) => new[]
        {
            t.LiteGamma, t.LiteGammaScale, t.LiteDeepCap, t.LiteRespEnd, t.LiteMotionBoost,
            t.LiteConfFloor, t.LiteMoveGateLo, t.LiteMoveGateHi, t.LiteHonestLo, t.LiteHonestHi
        };
        private static void ApplyLiteVector(float[] v, Tunables t)
        {
            t.LiteGamma = v[0]; t.LiteGammaScale = v[1]; t.LiteDeepCap = v[2]; t.LiteRespEnd = v[3];
            t.LiteMotionBoost = v[4]; t.LiteConfFloor = v[5];
            t.LiteMoveGateLo = v[6]; t.LiteMoveGateHi = Math.Max(v[7], v[6]); // lo <= hi, like the sliders
            t.LiteHonestLo = v[8]; t.LiteHonestHi = Math.Max(v[9], v[8]);     // lo <= hi
        }

        // --- technique-aware search space: the tuner optimizes the param set the SELECTED technique
        //     actually reads (22 Tune* under "TAA", 10 Lite* under "TAALite"). RunLite is captured at
        //     StartTuning and describes BestVec until the next run. ---
        private bool RunLite;
        private string[] ActiveNames => RunLite ? LiteParamNames : ParamNames;
        private float[] ActiveLo => RunLite ? LiteParamLo : ParamLo;
        private float[] ActiveHi => RunLite ? LiteParamHi : ParamHi;
        private float[] ActiveDefaults => RunLite ? ToLiteVector(Defaults) : ToVector(Defaults);
        private void ActiveApply(float[] v, Tunables t) { if (RunLite) ApplyLiteVector(v, t); else ApplyVector(v, t); }
        private Tunables ActiveFromVector(float[] v)
        {
            var t = new Tunables();
            ActiveApply(v, t);
            return t;
        }

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
            CurPose = PrevPose = PoseAt(0f, 0f, false);

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
                        BasePhase = MeshObjs.Count * 1.7f
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
        /// Startup sanity check: report which of the 32 tunable uniforms (22 Tune* + 10 Lite*) resolved
        /// in the loaded TAA.xnb. On the DX/SM4 build all 32 must bind; the OGL build strips the
        /// #if SM4-only ones (RawSoften*/Ring* plus TuneDirectClampMix — its only reference is the SM4
        /// rectified branch).
        /// </summary>
        private void LogParamBindings()
        {
            string[] names =
            {
                "TuneMotionBoostFloor", "TuneMotionBoostMax", "TuneStillGateFloor", "TuneMoveGateLo",
                "TuneMoveGateHi", "TuneRespEnd", "TuneMotionTrustCap", "TuneMotionClampTighten",
                "TuneRawSoftenOnset", "TuneRawSoftenSlope", "TuneRawSoftenMotionSup", "TuneGamma",
                "TuneTexDetailFloor", "TuneConfFloor", "TuneRingLo", "TuneRingHi",
                "TuneDirectClampMix", "TuneKarisFade", "TuneGammaMotionDecay", "TuneConfFadeN",
                "TuneGrowOffPhase", "TuneDeepCapBase",
                // TAALite tunables (2026-07-07 promotion, commit c2979599)
                "LiteGamma", "LiteGammaScale", "LiteDeepCap", "LiteRespEnd", "LiteMotionBoost",
                "LiteConfFloor", "LiteMoveGateLo", "LiteMoveGateHi", "LiteHonestLo", "LiteHonestHi"
            };
            var bound = new List<string>();
            var missing = new List<string>();
            foreach (var n in names) (TAA.Parameters[n] != null ? bound : missing).Add(n);
            Console.WriteLine($"[TAALab] TAA.xnb Tune*/Lite* uniform binding: {bound.Count}/{names.Length} bound.");
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
                if (TState == TuneState.Idle) // scene hotkeys are inert while the auto-tuner runs
                {
                    if (Pressed(Keys.Space)) Paused = !Paused;
                    if (Pressed(Keys.R)) NeedHistoryReset = true;
                    if (Pressed(Keys.T)) { TAAEnabled = !TAAEnabled; if (TAAEnabled) NeedHistoryReset = true; }
                    if (Pressed(Keys.P)) PrintTuningBlock();
                }
            }

            PrevKB = kb;
            base.Update(gameTime);
        }

        /// <summary>Wrap v into [lo, hi) — the pure-function equivalent of the old incremental wraps.</summary>
        private static float Wrap(float v, float lo, float hi)
        {
            float r = (v - lo) % (hi - lo);
            if (r < 0) r += hi - lo;
            return lo + r;
        }

        /// <summary>
        /// PURE scene-state function (the determinism keystone): identical (simT, meshT, reveal) inputs
        /// produce an identical pose — no mutable object state anywhere. simT/meshT are virtual scene
        /// times in frames-at-speed-1; reveal toggles the phase-C block + checker teleport.
        /// </summary>
        private ScenePose PoseAt(float simT, float meshT, bool reveal)
        {
            var p = new ScenePose { Reveal = reveal };
            float cx = CheckerStart.X + (reveal ? TeleportDX : 0f) + CheckerVelBase.X * simT;
            float cy = CheckerStart.Y + (reveal ? TeleportDY : 0f) + CheckerVelBase.Y * simT;
            p.Checker = new Vector2(Wrap(cx, -CheckerSize, OutW), Wrap(cy, -CheckerSize, OutH));
            p.Rot = RotSpeedBase * simT;
            p.LineX = Wrap(LineDriftMin + LineDriftBase * simT, LineDriftMin, LineDriftMax);
            p.PairA = new Vector2(Wrap(PairAStart.X + PairAVelBase.X * simT, -PairSize.X, OutW), PairAStart.Y);
            p.PairB = new Vector2(Wrap(PairBStart.X + PairBVelBase.X * simT, -PairSize.X, OutW), PairBStart.Y);
            p.MeshAngles = new float[MeshObjs.Count];
            for (int i = 0; i < MeshObjs.Count; i++) p.MeshAngles[i] = MeshObjs[i].AngleAt(meshT);
            return p;
        }

        private void AdvanceScene()
        {
            PrevPose = CurPose;
            if (!Paused)
            {
                SimT += Math.Max(0f, SpeedMul);
                MeshT += Math.Max(0f, MeshSpeed);
            }
            CurPose = PoseAt(SimT, MeshT, false);
        }

        // ================= Auto-tune scripted evaluation sequence (fixed 240 frames @ virtual 60Hz) =====
        // Phase A [0,60):    rest — everything static (jitter runs) — convergence quality.
        // Phase B [60,120):  motion at standard speed — motion quality/ghosting.
        // Phase C [120,180): abrupt reveal (block toggles on + checker teleports, zero velocity) then
        //                    rest — accumulation/scrub lifecycle.
        // Phase D [180,240): slow motion (0.25x) — the slow-creep regime.
        private const int SeqFrames = 240;
        private const int RevealFrame = 120;

        private static float EvalSimT(int f)
        {
            if (f < 60) return 0f;                       // A: rest
            if (f < 120) return f - 59f;                 // B: standard speed (1 frame/frame)
            if (f < 180) return 60f;                     // C: frozen (reveal + re-accumulation)
            return 60f + 0.25f * (f - 179);              // D: slow creep (0.25x)
        }

        /// <summary>Scene pose for scripted-sequence frame f — pure function of the integer index.</summary>
        private ScenePose EvalPose(int f)
        {
            float t = EvalSimT(f);
            return PoseAt(t, t, f >= RevealFrame);
        }

        protected override void Draw(GameTime gameTime)
        {
            // Smoke mode (TAALAB_SMOKE=1 full / =lite TAALite): capped run on the first frame, exit after.
            if (SmokeMode && !SmokeStarted && TState == TuneState.Idle)
            {
                SmokeStarted = true;
                if (SmokeLite && TechIdx != 1) { TechIdx = 1; NeedHistoryReset = true; }
                Console.WriteLine($"[AutoTune] SMOKE MODE ({TechNames[TechIdx]}): auto-starting capped run (2 determinism evals + {SmokeEvals} optimizer evals), exiting on completion.");
                StartTuning(TuneMode.Smoke);
            }

            if (TState != TuneState.Idle)
            {
                DrawTuning(gameTime);
                base.Draw(gameTime);
                return;
            }

            if (NeedHistoryReset) ResetHistory();
            AdvanceScene();
            bool drawMeshes = MeshesEnabled && MeshesAvailable;

            if (ShowControl)
            {
                // Debug view: the auto-tuner's ground truth (2x2 supersample of the output grid, no
                // jitter, no TAA, box-downsampled). TAA history goes stale meanwhile — reset on return.
                RenderControl(CurPose, drawMeshes);
                GraphicsDevice.SetRenderTarget(null);
                SB.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
                SB.Draw(ControlDownRT, new Rectangle(0, 0, OutW, OutH), Color.White);
                SB.End();
                NeedHistoryReset = true;
            }
            else
            {
                // Jitter (render px, +/-0.5 — JITTER_PIXELS 0.5, jscale 1 for scale <= 1: World.PreDraw).
                // Content pixel shift (+j.x, -j.y); SampleJitterUV = -contentShift in UV. Phase advances
                // even when the scene is paused so rest-state convergence stays testable.
                var jpx = TAAEnabled ? SampleHalton(FrameIdx++, RenderScale) : Vector2.Zero;
                var sampleJitterUV = new Vector2(-jpx.X / RW, jpx.Y / RH);

                DrawSceneColor(CurPose, SceneRT, RenderScale, jpx, drawMeshes);
                DrawVelocity(CurPose, PrevPose, jpx, drawMeshes);

                if (TAAEnabled)
                {
                    RunResolve(Tune, sampleJitterUV);
                    // Blit resolved history to the screen; swap the ping-pong.
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

        /// <summary>
        /// Synthetic scene + game meshes for one pose, into any target: scale maps OUTPUT pixel space to
        /// the target (RenderScale for the TAA input, 2.0 for the supersampled control), jpx is the
        /// content jitter in target px (zero for the control).
        /// </summary>
        private void DrawSceneColor(ScenePose pose, RenderTarget2D target, float scale, Vector2 jpx, bool withMeshes)
        {
            GraphicsDevice.SetRenderTarget(target);
            GraphicsDevice.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.Black, 1f, 0);
            var shift = new Vector2(jpx.X, -jpx.Y);
            var world = Matrix.CreateScale(scale, scale, 1f) * Matrix.CreateTranslation(shift.X, shift.Y, 0f);
            SB.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp, null, null, null, world);
            SB.Draw(Gradient, new Rectangle(-2, -2, OutW + 4, OutH + 4), Color.White); // bg (overscan covers jitter)
            // similar-color pair (B over A where they cross)
            SB.Draw(White, new Rectangle((int)pose.PairA.X, (int)pose.PairA.Y, PairSize.X, PairSize.Y), PairColA);
            SB.Draw(White, new Rectangle((int)pose.PairB.X, (int)pose.PairB.Y, PairSize.X, PairSize.Y), PairColB);
            // checkerboard mover
            SB.Draw(Checker, new Rectangle((int)pose.Checker.X, (int)pose.Checker.Y, CheckerSize, CheckerSize), Color.White);
            // rotating high-contrast rectangle (self-reveal + edge test)
            SB.Draw(White, RotCenter, null, new Color(0.93f, 0.92f, 0.88f), pose.Rot, new Vector2(0.5f, 0.5f),
                RotSize, SpriteEffects.None, 0f);
            // phase-C abrupt-reveal block (textured for re-convergence detail)
            if (pose.Reveal)
                SB.Draw(Checker, new Rectangle((int)RevealPos.X, (int)RevealPos.Y, RevealSize.X, RevealSize.Y), RevealCol);
            // thin bright lines, 1 OUTPUT px thick (sub-render-pixel under upscale): one static, one drifting
            DrawLine(SB, new Vector2(180, 620), MathHelper.ToRadians(-32), 300, new Color(1f, 1f, 0.85f));
            DrawLine(SB, new Vector2(pose.LineX, 655), MathHelper.ToRadians(-70), 240, new Color(0.95f, 1f, 0.9f));
            SB.End();

            // Real game meshes: unlit textured cutout, perspective camera, z-tested, drawn over the 2D
            // shapes (the 2D pass never writes depth). Same jitter as the 2D transform.
            if (withMeshes)
            {
                var meshProj = MeshProj(jpx, target.Width, target.Height);
                GraphicsDevice.DepthStencilState = DepthStencilState.Default;
                GraphicsDevice.RasterizerState = RasterizerState.CullNone; // reconstructed winding varies
                GraphicsDevice.BlendState = BlendState.Opaque;
                GraphicsDevice.SamplerStates[0] = SamplerState.LinearClamp;
                MeshEffect.View = MeshView;
                MeshEffect.Projection = meshProj;
                for (int i = 0; i < MeshObjs.Count; i++)
                {
                    var m = MeshObjs[i];
                    MeshEffect.World = m.Model(pose.MeshAngles[i]);
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
        }

        /// <summary>
        /// Velocity buffer: same shapes, same jittered transforms, via LabVelocity's matrix-pair
        /// convention (SkyVelocity.fx / RCObject.fx): each object draws with its current AND previous
        /// WVP, and the PS derives honest per-pixel velocity from the clip-space delta (so rotation
        /// writes a true spatially-varying field). Both WVPs carry the SAME current-frame jitter, so the
        /// jitter translation cancels — buffer stays jitter-free.
        /// </summary>
        private void DrawVelocity(ScenePose pose, ScenePose prev, Vector2 jpx, bool withMeshes)
        {
            GraphicsDevice.SetRenderTarget(VelRT);
            GraphicsDevice.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.Transparent, 1f, 0);
            GraphicsDevice.DepthStencilState = DepthStencilState.None;
            GraphicsDevice.BlendState = BlendState.Opaque;
            GraphicsDevice.RasterizerState = RasterizerState.CullNone;

            var shift = new Vector2(jpx.X, -jpx.Y);
            var world = Matrix.CreateScale(RenderScale, RenderScale, 1f) * Matrix.CreateTranslation(shift.X, shift.Y, 0f);
            var ortho2D = world * Matrix.CreateOrthographicOffCenter(0, RW, RH, 0, -1, 1);
            LabVel.CurrentTechnique = LabVel.Techniques["Velocity"];
            // Unit quad -> output-pixel-space model matrix pair -> jittered ortho. Depth constant per object.
            void Quad(Matrix now, Matrix prevM, float depth)
            {
                LabVel.Parameters["CurrentWVP"].SetValue(now * ortho2D);
                LabVel.Parameters["PreviousWVP"].SetValue(prevM * ortho2D);
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
            Quad(RectM(pose.PairA, PairSize.ToVector2()), RectM(prev.PairA, PairSize.ToVector2()), 0.60f);
            Quad(RectM(pose.PairB, PairSize.ToVector2()), RectM(prev.PairB, PairSize.ToVector2()), 0.55f);
            Quad(RectM(pose.Checker, new Vector2(CheckerSize)), RectM(prev.Checker, new Vector2(CheckerSize)), 0.50f);
            // Rotating rect: the matrix pair writes its TRUE spatially-varying rotational velocity field.
            Quad(RotM(pose.Rot), RotM(prev.Rot), 0.45f);
            // Reveal block: static; on its appearance frame the caller passes prev == pose (zero velocity).
            if (pose.Reveal)
            {
                var rvM = RectM(RevealPos, RevealSize.ToVector2());
                Quad(rvM, rvM, 0.40f);
            }
            var stLineM = LineM(new Vector2(180, 620), MathHelper.ToRadians(-32), 300);
            Quad(stLineM, stLineM, 0.30f);
            Quad(LineM(new Vector2(pose.LineX, 655), MathHelper.ToRadians(-70), 240),
                 LineM(new Vector2(prev.LineX, 655), MathHelper.ToRadians(-70), 240), 0.35f);

            // Mesh velocities: per-pixel rotational velocity from the model matrix pair, alpha cutout
            // matching the color pass, per-pixel linear depth (saturate(clip.w/800) — the game's
            // PackDepth). Z-tested against a fresh depth buffer like the color pass.
            if (withMeshes)
            {
                var meshProj = MeshProj(jpx, RW, RH);
                GraphicsDevice.DepthStencilState = DepthStencilState.Default;
                LabVel.CurrentTechnique = LabVel.Techniques["VelocityMasked"];
                var vp = MeshView * meshProj;
                for (int i = 0; i < MeshObjs.Count; i++)
                {
                    var m = MeshObjs[i];
                    LabVel.Parameters["CurrentWVP"].SetValue(m.Model(pose.MeshAngles[i]) * vp);
                    LabVel.Parameters["PreviousWVP"].SetValue(m.Model(prev.MeshAngles[i]) * vp);
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
        }

        /// <summary>The real resolve — uniform-for-uniform what TAAResolve.Draw sets — into Hist/Meta[HistCurr].</summary>
        private void RunResolve(Tunables t, Vector2 sampleJitterUV)
        {
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
            TAA.Parameters["TuneMotionBoostFloor"]?.SetValue(t.MotionBoostFloor);
            TAA.Parameters["TuneMotionBoostMax"]?.SetValue(t.MotionBoostMax);
            TAA.Parameters["TuneStillGateFloor"]?.SetValue(t.StillGateFloor);
            TAA.Parameters["TuneMoveGateLo"]?.SetValue(t.MoveGateLo);
            TAA.Parameters["TuneMoveGateHi"]?.SetValue(t.MoveGateHi);
            TAA.Parameters["TuneRespEnd"]?.SetValue(t.RespEnd);
            TAA.Parameters["TuneMotionTrustCap"]?.SetValue(t.MotionTrustCap);
            TAA.Parameters["TuneMotionClampTighten"]?.SetValue(t.MotionClampTighten);
            TAA.Parameters["TuneRawSoftenOnset"]?.SetValue(t.RawSoftenOnset);
            TAA.Parameters["TuneRawSoftenSlope"]?.SetValue(t.RawSoftenSlope);
            TAA.Parameters["TuneRawSoftenMotionSup"]?.SetValue(t.RawSoftenMotionSup);
            TAA.Parameters["TuneGamma"]?.SetValue(t.Gamma);
            TAA.Parameters["TuneTexDetailFloor"]?.SetValue(t.TexDetailFloor);
            TAA.Parameters["TuneConfFloor"]?.SetValue(t.ConfFloor);
            TAA.Parameters["TuneRingLo"]?.SetValue(t.RingLo);
            TAA.Parameters["TuneRingHi"]?.SetValue(t.RingHi);
            TAA.Parameters["TuneDirectClampMix"]?.SetValue(t.DirectClampMix);
            TAA.Parameters["TuneKarisFade"]?.SetValue(t.KarisFade);
            TAA.Parameters["TuneGammaMotionDecay"]?.SetValue(t.GammaMotionDecay);
            TAA.Parameters["TuneConfFadeN"]?.SetValue(t.ConfFadeN);
            TAA.Parameters["TuneGrowOffPhase"]?.SetValue(t.GrowOffPhase);
            TAA.Parameters["TuneDeepCapBase"]?.SetValue(t.DeepCapBase);
            // TAALite tunables (read only by the "TAALite" technique — uploaded every frame like the
            // game's TAAResolve.Draw, harmless under TAA_Core)
            TAA.Parameters["LiteGamma"]?.SetValue(t.LiteGamma);
            TAA.Parameters["LiteGammaScale"]?.SetValue(t.LiteGammaScale);
            TAA.Parameters["LiteDeepCap"]?.SetValue(t.LiteDeepCap);
            TAA.Parameters["LiteRespEnd"]?.SetValue(t.LiteRespEnd);
            TAA.Parameters["LiteMotionBoost"]?.SetValue(t.LiteMotionBoost);
            TAA.Parameters["LiteConfFloor"]?.SetValue(t.LiteConfFloor);
            TAA.Parameters["LiteMoveGateLo"]?.SetValue(t.LiteMoveGateLo);
            TAA.Parameters["LiteMoveGateHi"]?.SetValue(t.LiteMoveGateHi);
            TAA.Parameters["LiteHonestLo"]?.SetValue(t.LiteHonestLo);
            TAA.Parameters["LiteHonestHi"]?.SetValue(t.LiteHonestHi);

            TAA.CurrentTechnique = TAA.Techniques[TechNames[TechIdx]];
            TAA.CurrentTechnique.Passes[0].Apply();
            GraphicsDevice.SetVertexBuffer(FSQuad);
            GraphicsDevice.DrawPrimitives(PrimitiveType.TriangleStrip, 0, 2);
        }

        /// <summary>Thin line as a rotated 1-output-px-thick sprite (sub-render-pixel under upscale).</summary>
        private void DrawLine(SpriteBatch sb, Vector2 start, float angle, float length, Color color)
        {
            sb.Draw(White, start, null, color, angle, new Vector2(0f, 0.5f), new Vector2(length, 1f), SpriteEffects.None, 0f);
        }

        // ==================================== Auto-tuner ====================================
        // Classical (Nelder-Mead) search over the 22 Tune* params, minimizing perceptual difference of
        // the real resolve's output from a 2x2-supersampled no-TAA control over the fixed 240-frame
        // scripted sequence. The control is scale/tunable-independent, so it is pre-rendered ONCE per
        // session (per mesh-visibility setting) into CPU sample arrays; each candidate eval then only
        // re-runs the TAA path. All GPU work stays on the Draw thread, chunked across Draw calls so the
        // UI stays clickable (STOP). Score determinism is checked explicitly by evaluating the defaults
        // twice at the start of every run — bit-identical totals or the tool flags itself unreliable.

        private enum TuneState { Idle, ControlPreRender, Evaluating }
        private enum TuneMode { Quick, Full, Smoke }

        // metric weights (task constants): total = SpatialWeight * MSE + TemporalWeight * TD
        private const double SpatialWeight = 1.0;
        private const double TemporalWeight = 2.0;
        private const int ChunkFramesEval = 80;     // sequence frames processed per Draw call (UI cadence)
        private const int ChunkFramesControl = 48;
        private const int FullRestarts = 3;
        private const int FullEvalsPerRestart = 200;
        private const int QuickEvals = 60;
        private const int SmokeEvals = 8;           // + the 2 determinism-check evals = 10 total
        private const string AutoTuneHeader = "// FSO.TAALab AUTO-TUNED best - paste over the fields in FSO.LotView.Utils.TAATuning:";

        private TuneState TState = TuneState.Idle;
        private TuneMode TMode;
        private bool StopRequested;
        // TAALAB_SMOKE=1 -> smoke run on the current (full) technique; TAALAB_SMOKE=lite -> TAALite.
        private static readonly string SmokeEnv = Environment.GetEnvironmentVariable("TAALAB_SMOKE");
        private readonly bool SmokeMode = !string.IsNullOrEmpty(SmokeEnv) && SmokeEnv != "0";
        private readonly bool SmokeLite = string.Equals(SmokeEnv, "lite", StringComparison.OrdinalIgnoreCase);
        private bool SmokeStarted;
        private bool ShowControl; // debug: present the supersampled control instead of the TAA output

        // control cache (pre-rendered ground truth, sampled every 2nd output pixel)
        private int MetricW => OutW / 2;
        private int MetricH => OutH / 2;
        private Color[][] ControlSamples;
        private bool ControlCacheValid;
        private bool ControlCacheMeshes;
        private int ControlFrame;
        private RenderTarget2D ControlRT, ControlDownRT, MetricRT;

        // per-candidate eval state
        private Tunables EvalTune;
        private float[] CurVec;
        private int EvalFrame;
        private Color[] TaaCur, TaaPrev;
        private double[] RowS, RowT;                 // per-row partial sums (deterministic parallel reduce)
        private readonly double[] PhaseS = new double[4], PhaseT = new double[4];

        // optimizer driver state
        private NelderMeadOptimizer Opt;
        private int DetPhase = 2;                    // 0/1 = determinism-check evals pending, 2 = done
        private EvalScores DetA;
        private bool? DetIdentical;
        private EvalScores DefaultsScores;
        private bool HaveDefaultsScores;
        private int RestartIdx, RestartCount, RestartBudget, RestartEvals;
        private int EvalsDone;
        private float[] BestVec;
        private EvalScores BestScoresV;
        private double BestTotal = double.MaxValue;
        private int BestEvalNum;
        private readonly System.Diagnostics.Stopwatch RunSW = new System.Diagnostics.Stopwatch();
        private readonly System.Diagnostics.Stopwatch EvalSW = new System.Diagnostics.Stopwatch();
        private double EvalMsTotal; private int EvalMsCount;
        private bool PrevFixedTimeStep;

        private void EnsureTuningTargets()
        {
            if (ControlRT != null) return;
            // 2x2 supersample of the OUTPUT grid (2560x1440 for the 720p window) + box-downsample stage
            // + half-res metric-sampling stage. Output-res fixed, so render-scale changes don't touch these.
            ControlRT = new RenderTarget2D(GraphicsDevice, OutW * 2, OutH * 2, false, SurfaceFormat.Color, DepthFormat.Depth24);
            ControlDownRT = new RenderTarget2D(GraphicsDevice, OutW, OutH, false, SurfaceFormat.Color, DepthFormat.None);
            MetricRT = new RenderTarget2D(GraphicsDevice, MetricW, MetricH, false, SurfaceFormat.Color, DepthFormat.None);
        }

        /// <summary>Ground-truth path: pose at 2x output res, NO jitter/TAA/velocity, box-downsampled.</summary>
        private void RenderControl(ScenePose pose, bool withMeshes)
        {
            EnsureTuningTargets();
            DrawSceneColor(pose, ControlRT, 2f, Vector2.Zero, withMeshes);
            // Exact 2x2 box filter: a bilinear fetch lands on the corner of each 2x2 source quad.
            GraphicsDevice.SetRenderTarget(ControlDownRT);
            SB.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
            SB.Draw(ControlRT, new Rectangle(0, 0, OutW, OutH), Color.White);
            SB.End();
        }

        /// <summary>
        /// Point-blit an output-res texture to half res (picks every 2nd pixel — the metric sampling
        /// grid; statistical accuracy, 4x less readback) and read it back to the CPU.
        /// </summary>
        private void ReadMetricSamples(Texture2D tex, Color[] dst)
        {
            GraphicsDevice.SetRenderTarget(MetricRT);
            SB.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
            SB.Draw(tex, new Rectangle(0, 0, MetricW, MetricH), Color.White);
            SB.End();
            GraphicsDevice.SetRenderTarget(null);
            MetricRT.GetData(dst);
        }

        private void StartTuning(TuneMode mode)
        {
            if (TState != TuneState.Idle) return;
            TMode = mode;
            StopRequested = false;
            DetPhase = 0; DetIdentical = null; HaveDefaultsScores = false;
            BestVec = null; BestTotal = double.MaxValue; BestEvalNum = 0;
            EvalsDone = 0; EvalMsTotal = 0; EvalMsCount = 0;
            RestartIdx = 0;
            RestartCount = mode == TuneMode.Full ? FullRestarts : 1;
            RestartBudget = mode == TuneMode.Full ? FullEvalsPerRestart : mode == TuneMode.Quick ? QuickEvals : SmokeEvals;
            Opt = null;
            RunLite = TechIdx == 1; // technique-aware search space, captured for the whole run
            EnsureTuningTargets();
            PrevFixedTimeStep = IsFixedTimeStep;
            IsFixedTimeStep = false; // no fixed-step catch-up spiral while Draw calls take ~0.2s
            RunSW.Restart();

            bool meshes = MeshesEnabled && MeshesAvailable;
            Console.WriteLine($"[AutoTune] run started: mode {mode}, {RestartCount} restart(s) x {RestartBudget} evals, " +
                $"scale {RenderScale.ToString("0.00", CultureInfo.InvariantCulture)}, technique {TechNames[TechIdx]}, " +
                $"optimizing {ActiveNames.Length} params ({(RunLite ? "Lite*" : "Tune*")}), meshes {(meshes ? "on" : "off")}, " +
                $"weights spatial {SpatialWeight} / temporal {TemporalWeight}, metric grid {MetricW}x{MetricH}.");
            if (!ControlCacheValid || ControlCacheMeshes != meshes)
            {
                ControlSamples = new Color[SeqFrames][];
                ControlCacheValid = false;
                ControlCacheMeshes = meshes;
                ControlFrame = 0;
                TState = TuneState.ControlPreRender;
                Console.WriteLine($"[AutoTune] pre-rendering supersampled control ({SeqFrames} frames at {OutW * 2}x{OutH * 2})...");
            }
            else
            {
                Console.WriteLine("[AutoTune] control sequence already cached — reusing.");
                BeginDeterminismCheck();
            }
        }

        private void BeginDeterminismCheck()
        {
            TState = TuneState.Evaluating;
            BeginEval(ActiveDefaults);
        }

        private void BeginEval(float[] vec)
        {
            CurVec = vec;
            EvalTune = ActiveFromVector(vec); // untuned set stays at defaults (inert for this technique)
            for (int i = 0; i < 4; i++) { PhaseS[i] = 0; PhaseT[i] = 0; }
            EvalFrame = 0;
            HistCurr = 0;          // fixed ping-pong start (identical target usage every eval)
            ResetHistory();        // history black + warmup meta clear, exactly like the game
            TaaCur ??= new Color[MetricW * MetricH];
            TaaPrev ??= new Color[MetricW * MetricH];
            RowS ??= new double[MetricH];
            RowT ??= new double[MetricH];
            EvalSW.Restart();
        }

        /// <summary>One control-sequence frame: render 2x, downsample, sample, store.</summary>
        private void StepControlFrame()
        {
            RenderControl(EvalPose(ControlFrame), ControlCacheMeshes);
            var arr = new Color[MetricW * MetricH];
            ReadMetricSamples(ControlDownRT, arr);
            ControlSamples[ControlFrame] = arr;
            if (++ControlFrame >= SeqFrames)
            {
                ControlCacheValid = true;
                Console.WriteLine("[AutoTune] control sequence cached (session-persistent).");
                BeginDeterminismCheck();
            }
        }

        /// <summary>One TAA-path frame of the current candidate eval.</summary>
        private void StepEvalFrame()
        {
            int f = EvalFrame;
            var jpx = SampleHalton(f, RenderScale);
            var sampleJitterUV = new Vector2(-jpx.X / RW, jpx.Y / RH);
            var pose = EvalPose(f);
            // Reveal frame: new content appears with ZERO velocity (prev == current), the honest convention.
            var prev = f == RevealFrame ? pose : EvalPose(f - 1);

            DrawSceneColor(pose, SceneRT, RenderScale, jpx, ControlCacheMeshes);
            DrawVelocity(pose, prev, jpx, ControlCacheMeshes);
            RunResolve(EvalTune, sampleJitterUV);
            ReadMetricSamples(Hist[HistCurr], TaaCur);
            AccumulateMetric(f);
            (TaaCur, TaaPrev) = (TaaPrev, TaaCur);
            HistCurr = 1 - HistCurr;

            if (++EvalFrame >= SeqFrames) OnEvalComplete(ComputeScores());
        }

        /// <summary>
        /// Per-frame metric: spatial MSE (linear RGB) vs control + temporal term
        /// TD = mean |(taa[t]-taa[t-1]) - (control[t]-control[t-1])| (penalizes ghosting AND fizzle).
        /// Row-parallel with per-row partial sums summed in fixed order — bit-deterministic.
        /// </summary>
        private void AccumulateMetric(int f)
        {
            int w = MetricW, h = MetricH;
            var taa = TaaCur; var taaPrev = TaaPrev;
            var ctrl = ControlSamples[f];
            var ctrlPrev = f > 0 ? ControlSamples[f - 1] : null;
            var rowS = RowS; var rowT = RowT;
            bool hasPrev = f > 0;
            Parallel.For(0, h, y =>
            {
                double s = 0, td = 0;
                int o = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = o + x;
                    Color a = taa[i], c = ctrl[i];
                    double dr = (a.R - c.R) * (1.0 / 255.0);
                    double dg = (a.G - c.G) * (1.0 / 255.0);
                    double db = (a.B - c.B) * (1.0 / 255.0);
                    s += (dr * dr + dg * dg + db * db) * (1.0 / 3.0);
                    if (hasPrev)
                    {
                        Color ap = taaPrev[i], cp = ctrlPrev[i];
                        double tr = ((a.R - ap.R) - (c.R - cp.R)) * (1.0 / 255.0);
                        double tg = ((a.G - ap.G) - (c.G - cp.G)) * (1.0 / 255.0);
                        double tb = ((a.B - ap.B) - (c.B - cp.B)) * (1.0 / 255.0);
                        td += (Math.Abs(tr) + Math.Abs(tg) + Math.Abs(tb)) * (1.0 / 3.0);
                    }
                }
                rowS[y] = s; rowT[y] = td;
            });
            double fs = 0, ft = 0;
            for (int y = 0; y < h; y++) { fs += rowS[y]; ft += rowT[y]; }
            int phase = f < 60 ? 0 : f < 120 ? 1 : f < 180 ? 2 : 3;
            double n = w * h;
            PhaseS[phase] += fs / n;
            PhaseT[phase] += ft / n;
        }

        private EvalScores ComputeScores()
        {
            var r = new EvalScores();
            double s = 0, t = 0;
            var ps = new double[4];
            for (int i = 0; i < 4; i++)
            {
                s += PhaseS[i]; t += PhaseT[i];
                ps[i] = (SpatialWeight * PhaseS[i] + TemporalWeight * PhaseT[i]) / 60.0; // 60 frames/phase
            }
            r.Rest = ps[0]; r.Motion = ps[1]; r.Reveal = ps[2]; r.Slow = ps[3];
            r.SpatialMean = s / SeqFrames;
            r.TemporalMean = t / SeqFrames;
            r.Total = (SpatialWeight * s + TemporalWeight * t) / SeqFrames;
            return r;
        }

        private static string Fmt(EvalScores s) => string.Format(CultureInfo.InvariantCulture,
            "total {0:0.000000} | rest {1:0.000000} motion {2:0.000000} reveal {3:0.000000} slow {4:0.000000} | spatial {5:0.000000} temporal {6:0.000000}",
            s.Total, s.Rest, s.Motion, s.Reveal, s.Slow, s.SpatialMean, s.TemporalMean);

        private void Consider(float[] vec, EvalScores sc)
        {
            if (sc.Total >= BestTotal) return;
            BestTotal = sc.Total;
            BestVec = (float[])vec.Clone();
            BestScoresV = sc;
            BestEvalNum = EvalsDone;
            Console.WriteLine($"[AutoTune] eval #{EvalsDone}: new best — {Fmt(sc)}");
        }

        private float[] RestartStart(int idx)
        {
            var d = ActiveDefaults;
            if (idx == 0) return d; // restart 1 = exact defaults
            var rng = new Random(7777 * idx + 1); // FIXED seed per restart — runs stay reproducible
            for (int i = 0; i < d.Length; i++)
            {
                d[i] += (float)((rng.NextDouble() * 2 - 1) * 0.10) * (ActiveHi[i] - ActiveLo[i]);
                d[i] = Math.Clamp(d[i], ActiveLo[i], ActiveHi[i]);
            }
            return d;
        }

        private void StartRestart()
        {
            RestartEvals = 0;
            Opt = new NelderMeadOptimizer(RestartStart(RestartIdx), ActiveLo, ActiveHi);
            Console.WriteLine($"[AutoTune] restart {RestartIdx + 1}/{RestartCount} " +
                $"({(RestartIdx == 0 ? "defaults start" : "defaults +/-10% jitter start")}) — budget {RestartBudget} evals.");
            BeginEval(Opt.Ask());
        }

        private void OnEvalComplete(EvalScores sc)
        {
            EvalsDone++;
            EvalSW.Stop();
            EvalMsTotal += EvalSW.Elapsed.TotalMilliseconds;
            EvalMsCount++;

            if (DetPhase < 2)
            {
                if (DetPhase == 0)
                {
                    DetPhase = 1;
                    DetA = sc;
                    DefaultsScores = sc; HaveDefaultsScores = true;
                    Console.WriteLine($"[AutoTune] defaults baseline ({(RunLite ? "Lite*" : "Tune*")}): {Fmt(sc)}");
                    BeginEval(ActiveDefaults); // second identical-params eval — THE determinism check
                }
                else
                {
                    DetPhase = 2;
                    bool identical = sc.Total == DetA.Total && sc.Rest == DetA.Rest && sc.Motion == DetA.Motion
                                  && sc.Reveal == DetA.Reveal && sc.Slow == DetA.Slow;
                    DetIdentical = identical;
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "[AutoTune] determinism check (identical params, two evals): eval1 total={0:R} eval2 total={1:R} -> {2}",
                        DetA.Total, sc.Total, identical ? "IDENTICAL (PASS)" : "MISMATCH (FAIL) — scores are not trustworthy!"));
                    Consider(ActiveDefaults, sc); // defaults are the baseline best
                    StartRestart();
                }
                return;
            }

            Opt.Tell(sc.Total);
            Consider(CurVec, sc);
            RestartEvals++;
            if (StopRequested) { FinishRun("stopped"); return; }

            float[] next = RestartEvals >= RestartBudget ? null : Opt.Ask();
            if (next == null)
            {
                if (RestartEvals < RestartBudget)
                    Console.WriteLine($"[AutoTune] restart {RestartIdx + 1} converged after {RestartEvals} evals.");
                RestartIdx++;
                if (RestartIdx >= RestartCount) { FinishRun("complete"); return; }
                StartRestart();
            }
            else
            {
                BeginEval(next);
            }
        }

        private void FinishRun(string reason)
        {
            RunSW.Stop();
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "[AutoTune] {0} after {1} evals in {2:0.0}s (avg {3:0} ms/eval).",
                reason, EvalsDone, RunSW.Elapsed.TotalSeconds, EvalMsCount > 0 ? EvalMsTotal / EvalMsCount : 0));
            if (DetIdentical.HasValue)
                Console.WriteLine($"[AutoTune] determinism: {(DetIdentical.Value ? "PASS (two identical-params evals scored bit-identically)" : "FAIL")}");
            if (HaveDefaultsScores) Console.WriteLine($"[AutoTune] defaults: {Fmt(DefaultsScores)}");
            if (BestVec != null)
            {
                Console.WriteLine($"[AutoTune] best:     {Fmt(BestScoresV)} (eval #{BestEvalNum}, {(RunLite ? "Lite*" : "Tune*")} space)");
                PrintTuningBlock(ActiveFromVector(BestVec), AutoTuneHeader);
            }
            TState = TuneState.Idle;
            IsFixedTimeStep = PrevFixedTimeStep;
            NeedHistoryReset = true; // interactive resumes from a clean history
            if (SmokeMode) Exit();
        }

        private void CancelDuringControl()
        {
            Console.WriteLine("[AutoTune] stopped during control pre-render — no results (cache incomplete).");
            ControlCacheValid = false;
            TState = TuneState.Idle;
            IsFixedTimeStep = PrevFixedTimeStep;
            NeedHistoryReset = true;
            if (SmokeMode) Exit();
        }

        /// <summary>Tuning-mode Draw: chunk of headless sequence frames, then a cheap progress present.</summary>
        private void DrawTuning(GameTime gameTime)
        {
            int budget = TState == TuneState.ControlPreRender ? ChunkFramesControl : ChunkFramesEval;
            while (budget-- > 0 && TState != TuneState.Idle)
            {
                if (StopRequested)
                {
                    if (TState == TuneState.ControlPreRender) CancelDuringControl();
                    else FinishRun("stopped");
                    break;
                }
                if (TState == TuneState.ControlPreRender) StepControlFrame();
                else StepEvalFrame();
            }

            // Cheap progress present: latest control / resolved frame + a banner + the ImGui panel.
            GraphicsDevice.SetRenderTarget(null);
            GraphicsDevice.Clear(new Color(16, 16, 20));
            Texture2D show = TState == TuneState.ControlPreRender ? ControlDownRT : (Texture2D)Hist[1 - HistCurr];
            if (show != null)
            {
                SB.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
                SB.Draw(show, new Rectangle(0, 0, OutW, OutH), Color.White);
                SB.End();
            }
            SB.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            SB.Draw(White, new Rectangle(4, 4, 700, 26), Color.Black * 0.62f);
            string msg = TState == TuneState.ControlPreRender
                ? string.Format(CultureInfo.InvariantCulture, "AUTO-TUNE: CONTROL {0}/{1}", ControlFrame, SeqFrames)
                : string.Format(CultureInfo.InvariantCulture, "AUTO-TUNE: EVAL {0} FRAME {1}/{2} BEST {3}",
                    EvalsDone, EvalFrame, SeqFrames, BestVec != null ? BestTotal.ToString("0.000000", CultureInfo.InvariantCulture) : "-");
            Font.Draw(SB, msg, new Vector2(10, 8), Color.Yellow, 2f);
            SB.End();

            Gui.BeforeLayout(gameTime);
            DrawGui();
            Gui.AfterLayout();
        }

        /// <summary>Minimal pixel-font readout (ImGui is the primary UI now).</summary>
        private void DrawOverlay()
        {
            SB.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            const float sc = 2f;
            SB.Draw(White, new Rectangle(4, 4, 470, 26), Color.Black * 0.62f);
            string status = string.Format(CultureInfo.InvariantCulture,
                "TAA {0}  {1}  SCALE {2:0.00}  {3}X{4}  {5:0} FPS",
                TAAEnabled ? (TechIdx == 1 ? "LITE" : "FULL") : "OFF",
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
            bool idle = TState == TuneState.Idle;

            if (ImGui.CollapsingHeader("Scene", ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (!idle) ImGui.BeginDisabled(); // scene/scale/technique are frozen during a tuning run
                int idx = ScaleIdx;
                if (ImGui.Combo("Render scale", ref idx, ScaleLabels, ScaleLabels.Length) && idx != ScaleIdx)
                {
                    ScaleIdx = idx;
                    RecreateTargets();
                }
                // Live full-vs-lite A/B. The two techniques write DIFFERENT meta semantics (lite has no
                // lock/evidence machinery), so a switch goes through the history-reset path.
                int tech = TechIdx;
                if (ImGui.Combo("Technique", ref tech, TechLabels, TechLabels.Length) && tech != TechIdx)
                {
                    TechIdx = tech;
                    NeedHistoryReset = true;
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
                if (!idle) ImGui.EndDisabled();
            }

            DrawAutoTuneGui(idle);

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

            if (ImGui.CollapsingHeader("Structural", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.SliderFloat("DirectClampMix", ref Tune.DirectClampMix, 0f, 1f, "%.3f");
                ImGui.SliderFloat("KarisFade", ref Tune.KarisFade, 0f, 1f, "%.3f");
                ImGui.SliderFloat("GammaMotionDecay", ref Tune.GammaMotionDecay, 0f, 1f, "%.3f");
                ImGui.SliderFloat("ConfFadeN", ref Tune.ConfFadeN, 1f, 64f, "%.1f");
                ImGui.SliderFloat("GrowOffPhase", ref Tune.GrowOffPhase, 0f, 1f, "%.3f");
                ImGui.SliderFloat("DeepCapBase", ref Tune.DeepCapBase, 0.9f, 0.999f, "%.4f");
                if (ImGui.Button("Reset to defaults##structural"))
                {
                    Tune.DirectClampMix = Defaults.DirectClampMix; Tune.KarisFade = Defaults.KarisFade;
                    Tune.GammaMotionDecay = Defaults.GammaMotionDecay; Tune.ConfFadeN = Defaults.ConfFadeN;
                    Tune.GrowOffPhase = Defaults.GrowOffPhase; Tune.DeepCapBase = Defaults.DeepCapBase;
                }
            }

            if (ImGui.CollapsingHeader("Lite (TAALite technique only)", ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (TechIdx != 1)
                    ImGui.TextDisabled("inert now — select the 'TAA Lite' technique to see these take effect");
                ImGui.SliderFloat("LiteGamma", ref Tune.LiteGamma, 0.5f, 3f, "%.3f");
                ImGui.SliderFloat("LiteGammaScale", ref Tune.LiteGammaScale, 1f, 3f, "%.3f");
                ImGui.SliderFloat("LiteDeepCap", ref Tune.LiteDeepCap, 0.9f, 0.999f, "%.4f");
                ImGui.SliderFloat("LiteRespEnd", ref Tune.LiteRespEnd, 0.4f, 0.9f, "%.3f");
                ImGui.SliderFloat("LiteMotionBoost", ref Tune.LiteMotionBoost, 0f, 1f, "%.3f");
                ImGui.SliderFloat("LiteConfFloor", ref Tune.LiteConfFloor, 0f, 1f, "%.3f");
                ImGui.SliderFloat("LiteMoveGateLo", ref Tune.LiteMoveGateLo, 0f, 4f, "%.3f");
                ImGui.SliderFloat("LiteMoveGateHi", ref Tune.LiteMoveGateHi, 0.5f, 6f, "%.3f");
                Tune.LiteMoveGateHi = Math.Max(Tune.LiteMoveGateHi, Tune.LiteMoveGateLo); // keep lo <= hi
                ImGui.SliderFloat("LiteHonestLo", ref Tune.LiteHonestLo, 0.2f, 0.9f, "%.3f");
                ImGui.SliderFloat("LiteHonestHi", ref Tune.LiteHonestHi, 0.5f, 1f, "%.3f");
                Tune.LiteHonestHi = Math.Max(Tune.LiteHonestHi, Tune.LiteHonestLo); // keep lo <= hi
                if (ImGui.Button("Reset to defaults##lite"))
                {
                    Tune.LiteGamma = Defaults.LiteGamma; Tune.LiteGammaScale = Defaults.LiteGammaScale;
                    Tune.LiteDeepCap = Defaults.LiteDeepCap; Tune.LiteRespEnd = Defaults.LiteRespEnd;
                    Tune.LiteMotionBoost = Defaults.LiteMotionBoost; Tune.LiteConfFloor = Defaults.LiteConfFloor;
                    Tune.LiteMoveGateLo = Defaults.LiteMoveGateLo; Tune.LiteMoveGateHi = Defaults.LiteMoveGateHi;
                    Tune.LiteHonestLo = Defaults.LiteHonestLo; Tune.LiteHonestHi = Defaults.LiteHonestHi;
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
                Tune.DirectClampMix = Defaults.DirectClampMix; Tune.KarisFade = Defaults.KarisFade;
                Tune.GammaMotionDecay = Defaults.GammaMotionDecay; Tune.ConfFadeN = Defaults.ConfFadeN;
                Tune.GrowOffPhase = Defaults.GrowOffPhase; Tune.DeepCapBase = Defaults.DeepCapBase;
                Tune.LiteGamma = Defaults.LiteGamma; Tune.LiteGammaScale = Defaults.LiteGammaScale;
                Tune.LiteDeepCap = Defaults.LiteDeepCap; Tune.LiteRespEnd = Defaults.LiteRespEnd;
                Tune.LiteMotionBoost = Defaults.LiteMotionBoost; Tune.LiteConfFloor = Defaults.LiteConfFloor;
                Tune.LiteMoveGateLo = Defaults.LiteMoveGateLo; Tune.LiteMoveGateHi = Defaults.LiteMoveGateHi;
                Tune.LiteHonestLo = Defaults.LiteHonestLo; Tune.LiteHonestHi = Defaults.LiteHonestHi;
            }
            ImGui.TextDisabled("Space pause  R reset hist  T TAA A/B  P print  Esc quit");

            ImGui.End();
        }

        /// <summary>The "Auto-tune" ImGui section: start/stop, progress, best-so-far, load/print best.</summary>
        private void DrawAutoTuneGui(bool idle)
        {
            if (!ImGui.CollapsingHeader("Auto-tune", ImGuiTreeNodeFlags.DefaultOpen)) return;

            ImGui.TextDisabled(string.Format(CultureInfo.InvariantCulture,
                "score = {0:0.0} x spatial MSE + {1:0.0} x temporal diff, vs 2x supersampled control",
                SpatialWeight, TemporalWeight));
            ImGui.TextDisabled("sequence: 60 rest / 60 motion / 60 reveal+rest / 60 slow (240 @ virtual 60Hz)");

            ImGui.TextDisabled($"search space follows the technique combo: {(TechIdx == 1 ? "10 Lite* params" : "22 Tune* params")}");

            if (idle)
            {
                if (ImGui.Button("Start (3 restarts)")) StartTuning(TuneMode.Full);
                ImGui.SameLine();
                if (ImGui.Button("Start quick")) StartTuning(TuneMode.Quick);
                ImGui.SameLine();
                ImGui.TextDisabled(ControlCacheValid ? "control: cached" : "control: not cached");
            }
            else
            {
                if (ImGui.Button("STOP")) StopRequested = true;
                ImGui.SameLine();
                ImGui.Text(TState == TuneState.ControlPreRender
                    ? $"pre-rendering control {ControlFrame}/{SeqFrames}"
                    : $"restart {Math.Min(RestartIdx + 1, RestartCount)}/{RestartCount}  eval #{EvalsDone + 1}  frame {EvalFrame}/{SeqFrames}"
                      + (DetPhase < 2 ? "  (determinism check)" : ""));
            }

            if (EvalMsCount > 0)
                ImGui.Text(string.Format(CultureInfo.InvariantCulture, "evals: {0}   avg {1:0} ms/eval", EvalsDone, EvalMsTotal / EvalMsCount));
            if (DetIdentical.HasValue)
            {
                if (DetIdentical.Value) ImGui.TextColored(new System.Numerics.Vector4(0.4f, 1f, 0.4f, 1f), "determinism check: PASS (bit-identical)");
                else ImGui.TextColored(new System.Numerics.Vector4(1f, 0.35f, 0.35f, 1f), "determinism check: FAIL — scores unreliable!");
            }
            if (HaveDefaultsScores)
                ImGui.Text(string.Format(CultureInfo.InvariantCulture,
                    "defaults {0:0.000000}  (R {1:0.0000} M {2:0.0000} V {3:0.0000} S {4:0.0000})",
                    DefaultsScores.Total, DefaultsScores.Rest, DefaultsScores.Motion, DefaultsScores.Reveal, DefaultsScores.Slow));
            if (BestVec != null)
            {
                ImGui.Text(string.Format(CultureInfo.InvariantCulture,
                    "best     {0:0.000000}  (R {1:0.0000} M {2:0.0000} V {3:0.0000} S {4:0.0000})  eval #{5} [{6}]",
                    BestScoresV.Total, BestScoresV.Rest, BestScoresV.Motion, BestScoresV.Reveal, BestScoresV.Slow,
                    BestEvalNum, RunLite ? "Lite*" : "Tune*"));
                if (idle)
                {
                    if (ImGui.Button("Load best into sliders"))
                    {
                        ActiveApply(BestVec, Tune); // into the param set the run actually tuned
                        NeedHistoryReset = true;
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Print best C# block")) PrintTuningBlock(ActiveFromVector(BestVec), AutoTuneHeader);
                }
                if (ImGui.TreeNode("Best vs defaults (deltas)"))
                {
                    var def = ActiveDefaults;
                    var names = ActiveNames;
                    bool any = false;
                    for (int i = 0; i < names.Length; i++)
                    {
                        float dv = BestVec[i] - def[i];
                        if (Math.Abs(dv) < 1e-4f) continue;
                        any = true;
                        ImGui.Text(string.Format(CultureInfo.InvariantCulture,
                            "{0}: {1:0.####} -> {2:0.####}  ({3}{4:0.####})",
                            names[i], def[i], BestVec[i], dv >= 0 ? "+" : "", dv));
                    }
                    if (!any) ImGui.TextDisabled("(best == defaults)");
                    ImGui.TreePop();
                }
            }
            if (idle && ImGui.Checkbox("Debug view: supersampled control (ground truth)", ref ShowControl) && !ShowControl)
                NeedHistoryReset = true;
        }

        private void PrintCheatsheet()
        {
            Console.WriteLine("==================== OpenSO TAA Lab ====================");
            Console.WriteLine("Runs the game's REAL compiled TAA resolve (DX/SM4 TAA.xnb, technique TAA or");
            Console.WriteLine("TAALite — live A/B combo) on a synthetic scene. History/meta at output res;");
            Console.WriteLine("TAAU when scale < 1. All 32 tunable uniforms are live (22 Tune* incl. the");
            Console.WriteLine("SM4-only set + 10 Lite* for the TAALite technique).");
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
            Console.WriteLine();
            Console.WriteLine("Auto-tune (ImGui section): Nelder-Mead vs a 2x supersampled no-TAA control on");
            Console.WriteLine("a fixed 240-frame rest/motion/reveal/slow script. TECHNIQUE-AWARE: tunes the");
            Console.WriteLine("22 Tune* params under Full Cosmic TAA, the 10 Lite* params under TAA Lite.");
            Console.WriteLine("Every run evaluates the defaults twice first (bit-identical or it flags FAIL).");
            Console.WriteLine("Env TAALAB_SMOKE=1 (full) / =lite (TAALite) runs a capped 10-eval pass + exits.");
            Console.WriteLine("=========================================================");
        }

        private void PrintTuningBlock() =>
            PrintTuningBlock(Tune, "// FSO.TAALab snapshot - paste over the fields in FSO.LotView.Utils.TAATuning:");

        private static void PrintTuningBlock(Tunables t, string header)
        {
            var sb = new StringBuilder();
            sb.AppendLine(header);
            void Section(string[] names, float[] vec)
            {
                for (int i = 0; i < names.Length; i++)
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "        public static float {0} = {1}f;", names[i], Math.Round(vec[i], 4)));
            }
            Section(ParamNames, ToVector(t));
            sb.AppendLine("        // TAALite tunables:");
            Section(LiteParamNames, ToLiteVector(t));
            Console.WriteLine(sb.ToString());
        }
    }
}
