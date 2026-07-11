using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System.Globalization;
using System.Text;
using FSO.LotView.Utils; // TAATuning — the canonical tuning preset, source-linked (see csproj)
using NVector2 = System.Numerics.Vector2;

namespace FSO.TAALab
{
    /// <summary>
    /// Interactive TAA tuning lab. Renders a synthetic animated scene at render resolution (output *
    /// renderScale), builds an exact per-object velocity buffer, then runs the game's REAL compiled TAA
    /// resolve (Content/DX/Effects/TAA.xnb, technique "TAA" = TAA_Core or "TAALite" — live A/B combo;
    /// the SM4 build, so ALL 17 Tune* uniforms incl. the #if SM4-only ones are live) on it exactly the way
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
        private Effect LabDown; // lab-only ground-truth downsampler (linear-light + Gaussian reference)
        private PixelFont Font;

        private Texture2D White;
        private Texture2D Checker;
        private Texture2D Gradient;
        private Texture2D Noise;   // fine-noise patch (fixed-seed; stipple/texture-crunch venue)

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

        // Bright-highlight cluster (2026-07-10 — the ringing artifact's venue was missing from the
        // scene): near-white sub-pixel dots the negative-lobe kernels can ring against and the Karis
        // weighting must keep from sparkling. Three static + one slow ORBITER (sub-pixel tangential
        // creep ~0.2 px/frame — also exercises the slow phase).
        private static readonly Vector2 GlintOrbitCenter = new Vector2(870, 100);
        private const float GlintOrbitRadius = 4f;
        private const float GlintOrbitSpeed = 0.05f; // rad/frame at speed 1
        // Fine-noise texture patch (sand analogue — the stipple-at-full-accumulation venue; finer
        // than a render pixel under upscale). Static, top-right, clear of the rotator's swept reach.
        private static readonly Rectangle NoisePatchRect = new Rectangle(1150, 60, 96, 96);
        // VELOCITY-LESS mover: drawn in the COLOR pass only — its region keeps the background's
        // zero-velocity/0.95-depth write, so it moves while the velocity buffer says still. This is
        // the game's animated-texture / velocity-MRT-skipping overlay case: the resolve must catch
        // it with the variance clamp + luma feedback alone (no reprojection, no depth reject).
        private static readonly Vector2 NoVelStart = new Vector2(60, 24);
        private const float NoVelSpeed = 1.5f; // px/frame at speed 1
        private static readonly Point NoVelSize = new Point(90, 56);
        private static readonly Color NoVelCol = new Color(0.55f, 0.40f, 0.35f);

        /// <summary>Full scene state for one frame — computed by PoseAt, never mutated.</summary>
        private struct ScenePose
        {
            public Vector2 Checker;
            public float Rot;
            public float LineX;
            public Vector2 PairA, PairB;
            public bool Reveal;
            public float GlintAngle;
            public Vector2 NoVelMover;
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

        // --- live tuning values: initialized FROM the canonical preset (TAATuning.cs, source-linked
        //     into this project — see the csproj). The lab can no longer drift from shipped defaults:
        //     a fresh Tunables IS the shipped tuning. Press P for a ready-to-paste TAATuning block. ---
        private class Tunables
        {
            public float MotionBoostFloor = TAATuning.MotionBoostFloor;
            public float MotionBoostMax = TAATuning.MotionBoostMax;
            public float StillGateFloor = TAATuning.StillGateFloor;
            public float MoveGateLo = TAATuning.MoveGateLo;
            public float MoveGateHi = TAATuning.MoveGateHi;
            public float RespEnd = TAATuning.RespEnd;
            public float MotionTrustCap = TAATuning.MotionTrustCap;
            public float MotionClampTighten = TAATuning.MotionClampTighten;
            // gamma schedule endpoints — a FIXED reference schedule, NOT in the optimizer vector
            public float GammaNative = TAATuning.GammaNative;
            public float GammaUpscale = TAATuning.GammaUpscale;
            public float ConfFloor = TAATuning.ConfFloor;
            public float RingLo = TAATuning.RingLo;
            public float RingHi = TAATuning.RingHi;
            // structural constants (2026-07-07 promotion; 2026-07-10 reference-alignment prune removed
            // RawSoften*/TexDetailFloor/KarisFade/GammaMotionDecay — see TAATuning.cs)
            public float DirectClampMix = TAATuning.DirectClampMix;
            public float ConfFadeN = TAATuning.ConfFadeN;
            public float GrowOffPhase = TAATuning.GrowOffPhase;
            public float DeepCapBase = TAATuning.DeepCapBase;
            // TAALite tunables (take effect ONLY under the "TAALite" technique — 2026-07-07 promotion;
            // canonical defaults/comments: TAATuning.cs bottom section)
            public float LiteGamma = TAATuning.LiteGamma;
            public float LiteGammaScale = TAATuning.LiteGammaScale;
            public float LiteDeepCap = TAATuning.LiteDeepCap;
            public float LiteRespEnd = TAATuning.LiteRespEnd;
            public float LiteMotionBoost = TAATuning.LiteMotionBoost;
            public float LiteConfFloor = TAATuning.LiteConfFloor;
            public float LiteMoveGateLo = TAATuning.LiteMoveGateLo;
            public float LiteMoveGateHi = TAATuning.LiteMoveGateHi;
            public float LiteHonestLo = TAATuning.LiteHonestLo;
            public float LiteHonestHi = TAATuning.LiteHonestHi;
        }
        private readonly Tunables Tune = new Tunables();
        // Pristine copy: the field initializers above read TAATuning (the canonical preset), so a
        // fresh instance serves as the reset-to-SHIPPED-defaults table.
        private static readonly Tunables Defaults = new Tunables();

        // --- A/B slot: B starts as the SHIPPED defaults, snapshottable from the sliders. Key 'B'
        //     (or the checkbox) swaps which set feeds the interactive resolve; "Score A vs B" runs
        //     both through the auto-tuner's 240-frame metric for a numbers-backed verdict. ---
        private readonly Tunables TuneB = new Tunables();
        private bool UseB;
        /// <summary>Full copy: the 15 Tune* + 10 Lite* optimizer vectors cover 25 of the 27 fields;
        /// the 2 gamma-schedule fields (non-optimized) are copied explicitly.</summary>
        private static Tunables CloneTunables(Tunables s)
        {
            var t = new Tunables();
            CopyTunables(s, t);
            return t;
        }
        private static void CopyTunables(Tunables src, Tunables dst)
        {
            ApplyVector(ToVector(src), dst);
            ApplyLiteVector(ToLiteVector(src), dst);
            dst.GammaNative = src.GammaNative;   // non-optimized: explicit copy (not in any vector)
            dst.GammaUpscale = src.GammaUpscale;
        }

        // --- 22-parameter vector view of Tunables for the auto-tuner. Order matches the print block;
        //     bounds ARE the ImGui slider ranges (the optimizer reflects+clamps at them). ---
        // Lo+GAP reparameterization (tuner-v2 fix) for the paired bounds: optimizing (Lo, Hi) with a
        // Hi = max(Hi, Lo) repair creates a degenerate Lo == Hi manifold the search kept collapsing
        // into; optimizing (Lo, Gap >= 0) makes every point of the space valid by construction.
        // The Tunables/print block stay in Lo/Hi terms — only the optimizer's vector view changes.
        // (15 optimizer params since 2026-07-10: the reference-alignment prune removed RawSoften*/
        // TexDetailFloor/KarisFade/GammaMotionDecay, then Gamma/GammaScale were pulled OUT of the
        // search space — gamma is now a fixed reference SCHEDULE (GammaNative->GammaUpscale), still
        // uniforms/sliders but never optimized. Print block below keeps all 17 TAATuning fields.)
        private static readonly string[] ParamNames =
        {
            "MotionBoostFloor", "MotionBoostMax", "StillGateFloor", "MoveGateLo", "MoveGateGap",
            "RespEnd", "MotionTrustCap", "MotionClampTighten",
            "ConfFloor", "RingLo", "RingGap",
            "DirectClampMix", "ConfFadeN", "GrowOffPhase", "DeepCapBase"
        };
        // GrowOffPhase floor 0.25: meta N is RGBA8-quantized (1 LSB ~ 0.5 N), so per-frame growth
        // increments below ~0.25 N round to the SAME code point — the optimizer exploring below the
        // floor is silently tuning "no growth at all", not a slower rate.
        private static readonly float[] ParamLo =
        {
            0f, 0f, 0f, 0f, 0f,  0f, 0f, 0f,  0f, 0f, 0f,  0f, 1f, 0.25f, 0.9f
        };
        private static readonly float[] ParamHi =
        {
            1f, 1f, 1f, 6f, 6f,  1f, 1f, 1f,  1f, 1f, 1f,  1f, 64f, 1f, 0.999f
        };
        private static float[] ToVector(Tunables t) => new[]
        {
            t.MotionBoostFloor, t.MotionBoostMax, t.StillGateFloor, t.MoveGateLo, t.MoveGateHi - t.MoveGateLo,
            t.RespEnd, t.MotionTrustCap, t.MotionClampTighten,
            t.ConfFloor, t.RingLo, t.RingHi - t.RingLo,
            t.DirectClampMix, t.ConfFadeN, t.GrowOffPhase, t.DeepCapBase
        };
        private static void ApplyVector(float[] v, Tunables t)
        {
            t.MotionBoostFloor = v[0]; t.MotionBoostMax = v[1]; t.StillGateFloor = v[2];
            t.MoveGateLo = v[3]; t.MoveGateHi = Math.Min(v[3] + v[4], 6f); // gap-encoded, capped at the slider max
            t.RespEnd = v[5]; t.MotionTrustCap = v[6]; t.MotionClampTighten = v[7];
            t.ConfFloor = v[8];
            t.RingLo = v[9]; t.RingHi = Math.Min(v[9] + v[10], 1f); // gap-encoded
            t.DirectClampMix = v[11];
            t.ConfFadeN = v[12]; t.GrowOffPhase = v[13]; t.DeepCapBase = v[14];
            // (gamma schedule is NOT in the vector — left at t's existing values)
        }
        // TAATuning field names/values for the print block: all 17 fields (true Lo/Hi, NOT the
        // gap-encoded vector; includes the non-tuned gamma schedule so the paste-over is complete).
        private static readonly string[] PrintNames =
        {
            "MotionBoostFloor", "MotionBoostMax", "StillGateFloor", "MoveGateLo", "MoveGateHi",
            "RespEnd", "MotionTrustCap", "MotionClampTighten", "GammaNative", "GammaUpscale",
            "ConfFloor", "RingLo", "RingHi",
            "DirectClampMix", "ConfFadeN", "GrowOffPhase", "DeepCapBase"
        };
        private static float[] PrintVector(Tunables t) => new[]
        {
            t.MotionBoostFloor, t.MotionBoostMax, t.StillGateFloor, t.MoveGateLo, t.MoveGateHi,
            t.RespEnd, t.MotionTrustCap, t.MotionClampTighten, t.GammaNative, t.GammaUpscale,
            t.ConfFloor, t.RingLo, t.RingHi,
            t.DirectClampMix, t.ConfFadeN, t.GrowOffPhase, t.DeepCapBase
        };
        private static readonly string[] LitePrintNames =
        {
            "LiteGamma", "LiteGammaScale", "LiteDeepCap", "LiteRespEnd", "LiteMotionBoost",
            "LiteConfFloor", "LiteMoveGateLo", "LiteMoveGateHi", "LiteHonestLo", "LiteHonestHi"
        };
        private static float[] LitePrintVector(Tunables t) => new[]
        {
            t.LiteGamma, t.LiteGammaScale, t.LiteDeepCap, t.LiteRespEnd, t.LiteMotionBoost,
            t.LiteConfFloor, t.LiteMoveGateLo, t.LiteMoveGateHi, t.LiteHonestLo, t.LiteHonestHi
        };
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
            "LiteConfFloor", "LiteMoveGateLo", "LiteMoveGateGap", "LiteHonestLo", "LiteHonestGap"
        };
        private static readonly float[] LiteParamLo = { 0.5f, 1f, 0.9f, 0.4f, 0f, 0f, 0f, 0f, 0.2f, 0f };
        private static readonly float[] LiteParamHi = { 3f, 3f, 0.999f, 0.9f, 1f, 1f, 4f, 5.5f, 0.9f, 0.8f };
        private static float[] ToLiteVector(Tunables t) => new[]
        {
            t.LiteGamma, t.LiteGammaScale, t.LiteDeepCap, t.LiteRespEnd, t.LiteMotionBoost,
            t.LiteConfFloor, t.LiteMoveGateLo, t.LiteMoveGateHi - t.LiteMoveGateLo,
            t.LiteHonestLo, t.LiteHonestHi - t.LiteHonestLo
        };
        private static void ApplyLiteVector(float[] v, Tunables t)
        {
            t.LiteGamma = v[0]; t.LiteGammaScale = v[1]; t.LiteDeepCap = v[2]; t.LiteRespEnd = v[3];
            t.LiteMotionBoost = v[4]; t.LiteConfFloor = v[5];
            // gap-encoded pairs, Hi clamped into its slider range
            t.LiteMoveGateLo = v[6]; t.LiteMoveGateHi = Math.Clamp(v[6] + v[7], 0.5f, 6f);
            t.LiteHonestLo = v[8]; t.LiteHonestHi = Math.Clamp(v[8] + v[9], 0.5f, 1f);
        }

        // --- technique-aware search space: the tuner optimizes the param set the SELECTED technique
        //     actually reads (15 Tune* under "TAA", 10 Lite* under "TAALite"). RunLite is captured at
        //     StartTuning and describes BestVec until the next run. ---
        private bool RunLite;
        // Gamma schedule captured at run start (StartTuning/StartCompare) from the live sliders, so
        // every eval of a run uses the SAME fixed schedule deterministically even if a slider is
        // dragged mid-run. The tuner never optimizes these — it optimizes around them.
        private float RunGammaNative = TAATuning.GammaNative, RunGammaUpscale = TAATuning.GammaUpscale;
        private string[] ActiveNames => RunLite ? LiteParamNames : ParamNames;
        private float[] ActiveLo => RunLite ? LiteParamLo : ParamLo;
        private float[] ActiveHi => RunLite ? LiteParamHi : ParamHi;
        private float[] ActiveDefaults => RunLite ? ToLiteVector(Defaults) : ToVector(Defaults);
        private void ActiveApply(float[] v, Tunables t) { if (RunLite) ApplyLiteVector(v, t); else ApplyVector(v, t); }
        private Tunables ActiveFromVector(float[] v)
        {
            var t = new Tunables();
            ActiveApply(v, t);
            t.GammaNative = RunGammaNative;   // non-optimized fixed schedule (see field comment)
            t.GammaUpscale = RunGammaUpscale;
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
            LabDown = Content.Load<Effect>("Effects/LabDownsample");
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

            // Deterministic fine-noise patch (sand analogue): 1 texel per OUTPUT pixel, so it is
            // sub-render-pixel under upscale — the texture-crunch / stipple-at-full-accumulation venue.
            // FIXED seed: the control cache and every eval must see the identical texture.
            var nrng = new Random(1234);
            var ndata = new Color[NoisePatchRect.Width * NoisePatchRect.Height];
            for (int i = 0; i < ndata.Length; i++)
            {
                float v = 0.30f + 0.55f * (float)nrng.NextDouble();
                ndata[i] = new Color(v, v * 0.96f, v * 0.88f);
            }
            Noise = new Texture2D(GraphicsDevice, NoisePatchRect.Width, NoisePatchRect.Height);
            Noise.SetData(ndata);

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
        /// Startup sanity check: report which of the 27 tunable uniforms (17 Tune* + 10 Lite*) resolved
        /// in the loaded TAA.xnb. On the DX/SM4 build all 27 must bind; the OGL build strips the
        /// #if SM4-only ones (Ring* plus TuneDirectClampMix — its only reference is the SM4
        /// rectified branch).
        /// </summary>
        private void LogParamBindings()
        {
            string[] names =
            {
                "TuneMotionBoostFloor", "TuneMotionBoostMax", "TuneStillGateFloor", "TuneMoveGateLo",
                "TuneMoveGateHi", "TuneRespEnd", "TuneMotionTrustCap", "TuneMotionClampTighten",
                "TuneGammaNative", "TuneGammaUpscale", "TuneConfFloor", "TuneRingLo", "TuneRingHi",
                "TuneDirectClampMix", "TuneConfFadeN",
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
                    // A/B: sliders (A) vs baseline snapshot (B). History resets so each set converges
                    // from scratch — stale accumulation tuned under the other set would contaminate.
                    if (Pressed(Keys.B)) { UseB = !UseB; NeedHistoryReset = true; }
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
            p.GlintAngle = GlintOrbitSpeed * simT;
            p.NoVelMover = new Vector2(Wrap(NoVelStart.X + NoVelSpeed * simT, -NoVelSize.X, OutW), NoVelStart.Y);
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
                if (SmokeMulti) MultiScale = true;
                if (SmokeCont) ContinuousTrain = true;
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
                // Debug view: the auto-tuner's ground truth (ControlSS x supersample of the output grid, no
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
                DrawVelocity(CurPose, PrevPose, jpx, drawMeshes, VelRT, RenderScale, RW, RH);

                if (TAAEnabled)
                {
                    RunResolve(UseB ? TuneB : Tune, sampleJitterUV, SceneRT, VelRT, RW, RH, RenderScale);
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
        /// the target (RenderScale for the TAA input, ControlSS for the supersampled control), jpx is the
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
            // static fine-noise patch (stipple venue; 1 texel per output px)
            SB.Draw(Noise, NoisePatchRect, Color.White);
            // VELOCITY-LESS mover (color pass only — deliberately absent from DrawVelocity)
            SB.Draw(White, new Rectangle((int)pose.NoVelMover.X, (int)pose.NoVelMover.Y, NoVelSize.X, NoVelSize.Y), NoVelCol);
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
            // bright-highlight cluster: three static near-white sub-pixel dots + one slow orbiter
            // (negative-lobe ringing + Karis anti-sparkle venue; drawn over the mid-gray gradient)
            SB.Draw(White, new Rectangle(800, 80, 2, 2), Color.White);
            SB.Draw(White, new Rectangle(830, 100, 1, 1), Color.White);
            SB.Draw(White, new Rectangle(816, 130, 1, 2), Color.White);
            // float position (NOT an int Rectangle): the orbit is sub-pixel by design, and rounding
            // here would desync the color pass from the velocity pass's float matrix.
            var glint = GlintOrbitCenter + GlintOrbitRadius * new Vector2((float)Math.Cos(pose.GlintAngle), (float)Math.Sin(pose.GlintAngle));
            SB.Draw(White, glint, null, Color.White, 0f, Vector2.Zero, new Vector2(2f, 2f), SpriteEffects.None, 0f);
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
        private void DrawVelocity(ScenePose pose, ScenePose prev, Vector2 jpx, bool withMeshes,
            RenderTarget2D velRT, float scale, int rw, int rh)
        {
            GraphicsDevice.SetRenderTarget(velRT);
            GraphicsDevice.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.Transparent, 1f, 0);
            GraphicsDevice.DepthStencilState = DepthStencilState.None;
            GraphicsDevice.BlendState = BlendState.Opaque;
            GraphicsDevice.RasterizerState = RasterizerState.CullNone;

            var shift = new Vector2(jpx.X, -jpx.Y);
            var world = Matrix.CreateScale(scale, scale, 1f) * Matrix.CreateTranslation(shift.X, shift.Y, 0f);
            var ortho2D = world * Matrix.CreateOrthographicOffCenter(0, rw, rh, 0, -1, 1);
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
            // static noise patch (same depth-plane family as other static backdrops)
            var npM = RectM(new Vector2(NoisePatchRect.X, NoisePatchRect.Y), new Vector2(NoisePatchRect.Width, NoisePatchRect.Height));
            Quad(npM, npM, 0.85f);
            // (the VELOCITY-LESS mover is deliberately NOT drawn here — its color-pass motion must
            // arrive with the background's zero velocity, the game's animated-texture/overlay case)
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
            // glint cluster: statics + the sub-pixel orbiter (honest matrix-pair velocity)
            var g1 = RectM(new Vector2(800, 80), new Vector2(2, 2));
            var g2 = RectM(new Vector2(830, 100), new Vector2(1, 1));
            var g3 = RectM(new Vector2(816, 130), new Vector2(1, 2));
            Quad(g1, g1, 0.25f); Quad(g2, g2, 0.25f); Quad(g3, g3, 0.25f);
            Vector2 GlintPos(float a) => GlintOrbitCenter + GlintOrbitRadius * new Vector2((float)Math.Cos(a), (float)Math.Sin(a));
            Quad(RectM(GlintPos(pose.GlintAngle), new Vector2(2, 2)),
                 RectM(GlintPos(prev.GlintAngle), new Vector2(2, 2)), 0.25f);
            // Depths match the color pass's painter order at the crossing (~x400,y465): the DRIFT line
            // draws on top, so it must be the NEARER surface (2026-07-10 audit fix — they were swapped,
            // giving the crossing pixels a depth that contradicted the visible line).
            var stLineM = LineM(new Vector2(180, 620), MathHelper.ToRadians(-32), 300);
            Quad(stLineM, stLineM, 0.35f);
            Quad(LineM(new Vector2(pose.LineX, 655), MathHelper.ToRadians(-70), 240),
                 LineM(new Vector2(prev.LineX, 655), MathHelper.ToRadians(-70), 240), 0.30f);

            // Mesh velocities: per-pixel rotational velocity from the model matrix pair, alpha cutout
            // matching the color pass, per-pixel linear depth (saturate(clip.w/800) — the game's
            // PackDepth). Z-tested against a fresh depth buffer like the color pass.
            if (withMeshes)
            {
                var meshProj = MeshProj(jpx, rw, rh);
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

        /// <summary>The real resolve — uniform-for-uniform what TAAResolve.Draw sets — into Hist/Meta[HistCurr].
        /// The input targets/dimensioning are parameters so the multi-scale objective can resolve at
        /// scales other than the UI-selected one; interactive callers pass the SceneRT/VelRT globals.</summary>
        private void RunResolve(Tunables t, Vector2 sampleJitterUV,
            RenderTarget2D sceneRT, RenderTarget2D velRT, int rw, int rh, float scale)
        {
            var histPrev = Hist[1 - HistCurr];
            var metaPrev = Meta[1 - HistCurr];
            GraphicsDevice.SetRenderTargets(Hist[HistCurr], Meta[HistCurr]);
            GraphicsDevice.BlendState = BlendState.Opaque;

            TAA.Parameters["colorTex"]?.SetValue(sceneRT);
            TAA.Parameters["historyTex"]?.SetValue(histPrev);
            TAA.Parameters["metaHistoryTex"]?.SetValue(metaPrev);
            TAA.Parameters["velocityTex"]?.SetValue(velRT);
            TAA.Parameters["InvScreenSize"]?.SetValue(new Vector2(1f / OutW, 1f / OutH));
            TAA.Parameters["InvColorSize"]?.SetValue(new Vector2(1f / rw, 1f / rh));
            TAA.Parameters["BlendFactor"]?.SetValue(ScaledBlendFactor(scale));
            TAA.Parameters["MaxAccum"]?.SetValue(128f); // TAAResolve.MAX_ACCUM
            TAA.Parameters["JitterDelta"]?.SetValue(Vector2.Zero); // velocity buffer is jitter-free
            // fp16 history variant (the lab's history IS HalfVector4)
            TAA.Parameters["DepthRejectParams"]?.SetValue(new Vector4(0.0015f, 12f, 0f, 0.02f));
            TAA.Parameters["SampleJitterUV"]?.SetValue(sampleJitterUV);
            TAA.Parameters["VelGatePxScale"]?.SetValue(1f); // TAAU/native grids are native-sized
            TAA.Parameters["JitterPhases"]?.SetValue((float)HaltonCycle(scale));
            // live tunables (same null-safe pattern as TAAResolve — SM3-stripped uniforms just skip)
            TAA.Parameters["TuneMotionBoostFloor"]?.SetValue(t.MotionBoostFloor);
            TAA.Parameters["TuneMotionBoostMax"]?.SetValue(t.MotionBoostMax);
            TAA.Parameters["TuneStillGateFloor"]?.SetValue(t.StillGateFloor);
            TAA.Parameters["TuneMoveGateLo"]?.SetValue(t.MoveGateLo);
            TAA.Parameters["TuneMoveGateHi"]?.SetValue(t.MoveGateHi);
            TAA.Parameters["TuneRespEnd"]?.SetValue(t.RespEnd);
            TAA.Parameters["TuneMotionTrustCap"]?.SetValue(t.MotionTrustCap);
            TAA.Parameters["TuneMotionClampTighten"]?.SetValue(t.MotionClampTighten);
            TAA.Parameters["TuneGammaNative"]?.SetValue(t.GammaNative);
            TAA.Parameters["TuneGammaUpscale"]?.SetValue(t.GammaUpscale);
            TAA.Parameters["TuneConfFloor"]?.SetValue(t.ConfFloor);
            TAA.Parameters["TuneRingLo"]?.SetValue(t.RingLo);
            TAA.Parameters["TuneRingHi"]?.SetValue(t.RingHi);
            TAA.Parameters["TuneDirectClampMix"]?.SetValue(t.DirectClampMix);
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
        // Derivative-free search (CMA-ES default, Nelder-Mead selectable) over the active param space,
        // minimizing perceptual difference of the real resolve's output from an 8x-supersampled (8K+) no-TAA
        // control over the fixed 240-frame scripted sequence. The control is scale/tunable-independent,
        // so it is pre-rendered ONCE per session (per mesh-visibility setting) into CPU sample arrays;
        // each candidate eval then only re-runs the TAA path. Warm-startable from shipped defaults,
        // the live sliders, or the session's previous best. Efficiency: monotone partial-score bounds
        // early-prune hopeless candidates, exact-duplicate vectors are served from a score cache, and
        // metric readback is pipelined one frame behind the render. All GPU work stays on the Draw
        // thread, chunked across Draw calls so the UI stays clickable (STOP). Score determinism is
        // checked explicitly by evaluating the defaults twice at the start of every optimize run —
        // bit-identical totals or the tool flags itself unreliable.

        private enum TuneState { Idle, ControlPreRender, Evaluating }
        private enum TuneMode { Quick, Full, Smoke }

        // ---- artifact-class metric terms (2026-07-10 — "stricter and smarter on ghosting, noise,
        //      fizzle/trails"). All CPU-side, deterministic, and MONOTONE-accumulating (the prune
        //      bound stays valid). Detail-weighted like the base terms except Strict (strict is
        //      unweighted by design — a wrong pixel is wrong).
        //  GHOST/TRAIL PERSISTENCE (ritchie metric-v3's PersGain idea): a ghost is a same-signed luma
        //      error that PERSISTS at a pixel across frames — first-difference temporal terms
        //      underweight a slowly-decaying trail (each step is small). Per-slot signed run counter;
        //      error starts scoring once it has held the same sign > 2 frames, ramping to full by 6.
        //      (The rotating metric parity changes the sampled output pixel every 2 frames — fine:
        //      ghosts/trails are regional, so the persistence signal survives the rotation.)
        //  FIZZLE/NOISE: TAA changing where the ground truth did NOT change (|dCtrl| < 1/255 on a
        //      same-parity pair) — the rest-state churn term, scored above the 1/255 dead-band.
        //  STRICT bad-pixel fractions (ritchie strict-metrics idea, folded into the OBJECTIVE):
        //      fraction of pixel-frames whose max-channel error exceeds 2/255 (+4x extra beyond
        //      5/255) — the optimizer can no longer trade a small very-wrong region for mean gains.
        private const double GhostWeight = 4.0;
        private const double FizzleWeight = 3.0;
        private const double StrictWeight = 0.03; // fractions are ~1e-2 magnitude; ~10-20% of total at baseline
        private float[] PersState;                // per-slot signed persistence run (sign = error sign)
        private double[] RowG, RowF, RowB;
        private readonly double[] PhaseG = new double[4], PhaseF = new double[4], PhaseB = new double[4];

        // metric weights (task constants): total = SpatialWeight * MSE + TemporalWeight * TD
        private const double SpatialWeight = 1.0;
        private const double TemporalWeight = 2.0;
        private const int ChunkFramesEval = 80;     // sequence frames processed per Draw call (UI cadence)
        private const int ChunkFramesControl = 6; // 8x-supersampled frames are ~16x the old 2x cost — keep STOP clickable
        private const int FullRestarts = 3;
        private const int FullEvalsPerRestart = 200;
        private const int QuickEvals = 60;
        private const int SmokeEvals = 8;           // + the 2 determinism-check evals = 10 total
        private const string AutoTuneHeader = "// FSO.TAALab AUTO-TUNED best - paste over the fields in FSO.LotView.Utils.TAATuning:";

        private TuneState TState = TuneState.Idle;
        private TuneMode TMode;
        private bool StopRequested;
        // TAALAB_SMOKE=1 -> smoke run on the current (full) technique; =lite -> TAALite;
        // =ms -> full technique with the multi-scale objective.
        private static readonly string SmokeEnv = Environment.GetEnvironmentVariable("TAALAB_SMOKE");
        private readonly bool SmokeMode = !string.IsNullOrEmpty(SmokeEnv) && SmokeEnv != "0";
        private readonly bool SmokeLite = string.Equals(SmokeEnv, "lite", StringComparison.OrdinalIgnoreCase);
        private readonly bool SmokeMulti = string.Equals(SmokeEnv, "ms", StringComparison.OrdinalIgnoreCase);
        // =cont -> verifies continuous self-training: two chained cycles at smoke budget, then exits.
        private readonly bool SmokeCont = string.Equals(SmokeEnv, "cont", StringComparison.OrdinalIgnoreCase);
        private bool SmokeStarted;
        private bool ShowControl; // debug: present the supersampled control instead of the TAA output

        // control cache (pre-rendered ground truth, sampled every 2nd output pixel)
        private int MetricW => OutW / 2;
        private int MetricH => OutH / 2;
        // Reference final-stage filter: BOX by default (crispest honest linear-light average — the
        // tuner must not target Gaussian softness; user call 2026-07-10). The sigma-0.44 Gaussian
        // stays selectable: it is the anti-alias-honest reference (box passes stair-step combing as
        // truth, which resists tuning out stipple). A/B the tuning TARGETS by re-running with each.
        private bool GaussianRef;
        private bool ControlCacheGauss;
        private Color[][] ControlSamples;
        // ---- detail-weighted error (tuner-v2 feature): per-pixel weight from the CONTROL's luma
        //      gradient (never the TAA output — ungameable), w = Floor + (1-Floor)*sat(detail*Gain),
        //      applied to the spatial AND temporal terms pre-reduction and normalized by the frame's
        //      mean weight — small details count ~4x more than flat background. Quantized to a byte
        //      per pixel (deterministic, ~55 MB per 240-frame cache). ----
        private byte[][] ControlWeightQ;
        private double[] ControlMeanW;
        private const float DetailWeightFloor = 0.25f;
        private const float DetailWeightGain = 8f;
        private bool ControlCacheValid;
        private bool ControlCacheMeshes;
        private int ControlFrame;
        // Ground-truth supersample factor: 8x the OUTPUT grid (10240x5760 for the 720p window — 8K+),
        // clamped to the D3D11 texture limit as powers of two so the downsample chain stays an EXACT
        // box filter (each 2:1 bilinear blit averages a 2x2 quad; 8x = three chained blits).
        private const int ControlSSMax = 8;
        private const int MaxTextureDim = 16384; // D3D11 FL11 guarantee
        private int ControlSS
        {
            get
            {
                int ss = ControlSSMax;
                // floor 4: the downsample chain needs at least BoxDecode -> GaussDown (see
                // RenderControl); OutW would have to exceed 4096 to violate the texture limit at 4x.
                while (ss > 4 && (OutW * ss > MaxTextureDim || OutH * ss > MaxTextureDim)) ss >>= 1;
                return ss;
            }
        }
        private RenderTarget2D ControlRT, ControlDownRT, MetricRT, MetricRTB;
        private RenderTarget2D[] ControlChain; // halving blit chain: SS/2, SS/4, ... 2x (1x is ControlDownRT)
        private RenderTarget2D MetricTarget(int frame) => (frame & 1) == 0 ? MetricRT : MetricRTB;

        // per-candidate eval state
        private Tunables EvalTune;
        private float[] CurVec;
        private int EvalFrame;
        private Color[] TaaCur, TaaPrev;
        private double[] RowS, RowT;                 // per-row partial sums (deterministic parallel reduce)
        private readonly double[] PhaseS = new double[4], PhaseT = new double[4];
        private readonly int[] PhaseTN = new int[4]; // temporal samples per phase (odd frames only — parity pairs)

        // optimizer driver state
        private IOptimizer Opt;
        // Auto (default): NM for DISCOVERY (fresh starts + escalation cycles — its early simplex
        // moves are greedier in 22-D than CMA-ES, which spends a full lambda per generation before
        // adapting), CMA-ES for REFINEMENT cycles around a fresh best (superior local model).
        // Field observation that motivated this (2026-07-10): improvement rate is highest at the
        // front of a run and decays as CMA's sigma contracts; a manual stop/restart recovered the
        // early speed. The stall cutoff below automates that restart.
        private static readonly string[] OptimizerLabels = { "Nelder-Mead", "CMA-ES", "Auto (NM discover / CMA refine)" };
        private int OptimizerIdx = 2;
        private bool UseNmNow;                      // optimizer actually chosen for the current restart
        private int RestartLambda;                  // CMA population of the current restart (stall scaling)
        private int RestartSinceImprove;            // evals since the best last improved, this restart
        // end a restart once it stops paying: no best-improvement in this many evals -> roll into
        // the next restart/cycle (fresh sigma + seed) instead of grinding out the full budget
        private int StallLimit => UseNmNow ? 30 : Math.Min(60, Math.Max(30, 3 * RestartLambda));
        private static readonly string[] StartFromLabels = { "shipped defaults", "current sliders", "session best" };
        private int StartFromIdx = 1;               // warm-start from the sliders by default
        private float[] SessionBestFull, SessionBestLite;   // best vector of the last finished run, per space
        private float[] SessionBest => RunLite ? SessionBestLite : SessionBestFull;

        // efficiency: early-prune evals whose monotone partial-score lower bound already exceeds the
        // best total, and never re-run a full eval of an exact-duplicate vector (NM shrink revisits).
        private bool PruneEnabled = true;
        private int PrunedEvals, PrunedFramesSaved, CacheHits;
        private readonly Dictionary<string, double> ScoreCache = new Dictionary<string, double>();
        private (bool lite, int scale, bool meshes, bool gauss) CacheCtx = (false, -1, false, false);
        private static string VecKey(float[] v)
        {
            var sb = new StringBuilder(v.Length * 8);
            foreach (var x in v) sb.Append(BitConverter.SingleToInt32Bits(x).ToString("X8"));
            return sb.ToString();
        }

        // ---- multi-scale objective (returned from the tuner-v2 arc, reimplemented on this driver):
        //      every candidate is evaluated at several render scales and the optimizer minimizes the
        //      weighted sum, so one tuning covers the upscale range instead of overfitting the UI
        //      scale. The ground truth is OUTPUT-res, so the control cache is scale-independent (no
        //      extra pre-renders); history is output-res, so passes share the ping-pong (reset between
        //      passes). Cost: one 240-frame sequence per scale per eval. ----
        private bool MultiScale;
        private static readonly float[] MultiScales = { 1f / 3f, 0.5f, 1f };
        private static readonly double[] MultiScaleWeights = { 0.5, 0.3, 0.2 };
        private float[] EvalScales; private double[] EvalWeights;  // captured per run
        private int ScalePass;                                     // current scale pass of the eval
        private EvalScores[] PassScores;
        private double EvalBoundBase;                              // weighted totals of finished passes (pruning)
        // per-scale eval-res scene/velocity targets (the UI-scale SceneRT/VelRT are presentation-owned)
        private readonly Dictionary<int, (RenderTarget2D scene, RenderTarget2D vel)> EvalRTs =
            new Dictionary<int, (RenderTarget2D, RenderTarget2D)>();
        private (RenderTarget2D scene, RenderTarget2D vel) EvalTargetsFor(float scale)
        {
            int key = (int)Math.Round(scale * 1000f);
            if (!EvalRTs.TryGetValue(key, out var t))
            {
                int rw = Math.Max(1, (int)Math.Round(OutW * scale)), rh = Math.Max(1, (int)Math.Round(OutH * scale));
                t = (new RenderTarget2D(GraphicsDevice, rw, rh, false, SurfaceFormat.Color, DepthFormat.Depth24),
                     new RenderTarget2D(GraphicsDevice, rw, rh, false, SurfaceFormat.HalfVector4, DepthFormat.Depth24));
                EvalRTs[key] = t;
            }
            return t;
        }

        // ---- continuous self-training: when enabled, a finished optimize run chains straight into
        //      the next cycle, warm-started from the carried best (best/score cache/determinism
        //      verdict all survive — no repeated det checks or baseline evals). Improving cycles
        //      refine (small CMA sigma); stagnant cycles escalate IPOP-style (sigma up, population
        //      doubled, fresh seed) to escape the local optimum; after MaxStagnantCycles fruitless
        //      escalations the loop declares convergence and stops. ----
        private bool ContinuousTrain;
        private bool CycleRun;                   // current run is a chained cycle (skip det/baseline)
        private bool CycleEscalated;             // current cycle is a stagnation escalation (Auto -> NM)
        private int CycleIdx, StagnantCycles;
        private double CycleStartBest = double.MaxValue;
        private double CycleSigma = 0.15;
        private int CycleLambda;                 // 0 = CMA-ES default for the dimension
        private const int MaxStagnantCycles = 3;
        private int DefaultLambda => 4 + (int)(3.0 * Math.Log(ActiveNames.Length));

        // scored A/B compare run (uses the same control cache + eval machinery, no optimizer)
        private enum RunKind { Optimize, Compare }
        private RunKind Kind = RunKind.Optimize;
        private Tunables[] CmpSets; private string[] CmpLabels; private int CmpIdx;
        private readonly EvalScores[] CmpScores = new EvalScores[2];
        private bool HaveCmp; private string CmpCtx = "";
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
            // ControlSS x supersample of the OUTPUT grid + the exact box-downsample chain
            // + half-res metric-sampling stage (rotating-parity point grid — see ReadMetricSamples).
            // Output-res fixed, so render-scale changes don't touch these. The 8x target + chain is
            // heavy VRAM (~0.5 GB at 720p) but only exists while a control pre-render (or the
            // ground-truth debug view) needs it — freed once the control cache is CPU-resident.
            int ss = ControlSS;
            ControlRT = new RenderTarget2D(GraphicsDevice, OutW * ss, OutH * ss, false, SurfaceFormat.Color, DepthFormat.Depth24);
            // fp16 intermediates: the chain carries LINEAR-light values (see LabDownsample.fx) and
            // 8-bit storage would band them; bilinear blits on fp16 stay exact linear box averages.
            var chain = new List<RenderTarget2D>();
            for (int f = ss / 2; f >= 2; f >>= 1)
                chain.Add(new RenderTarget2D(GraphicsDevice, OutW * f, OutH * f, false, SurfaceFormat.HalfVector4, DepthFormat.None));
            ControlChain = chain.ToArray();
            ControlDownRT ??= new RenderTarget2D(GraphicsDevice, OutW, OutH, false, SurfaceFormat.Color, DepthFormat.None);
            MetricRT ??= new RenderTarget2D(GraphicsDevice, MetricW, MetricH, false, SurfaceFormat.Color, DepthFormat.None);
            // second metric target: eval frames alternate blit destinations so frame f's readback only
            // has to wait for work issued THROUGH frame f, not the frame f+1 commands already queued.
            MetricRTB ??= new RenderTarget2D(GraphicsDevice, MetricW, MetricH, false, SurfaceFormat.Color, DepthFormat.None);
        }

        /// <summary>Release the supersample target + downsample chain (the VRAM-heavy part; the
        /// metric targets stay). The control cache itself lives in CPU arrays, and ControlDownRT is
        /// kept for the progress present / debug view of the last pre-rendered frame.</summary>
        private void FreeControlTargets()
        {
            ControlRT?.Dispose(); ControlRT = null;
            if (ControlChain != null) foreach (var rt in ControlChain) rt.Dispose();
            ControlChain = null;
        }

        /// <summary>Fullscreen 2:1 pass through LabDownsample (BoxDecode / GaussDown).</summary>
        private void DownsamplePass(string technique, Texture2D src, RenderTarget2D dst)
        {
            GraphicsDevice.SetRenderTarget(dst);
            GraphicsDevice.BlendState = BlendState.Opaque;
            GraphicsDevice.DepthStencilState = DepthStencilState.None;
            LabDown.Parameters["srcTex"].SetValue(src);
            LabDown.Parameters["InvSrcSize"].SetValue(new Vector2(1f / src.Width, 1f / src.Height));
            LabDown.CurrentTechnique = LabDown.Techniques[technique];
            LabDown.CurrentTechnique.Passes[0].Apply();
            GraphicsDevice.SetVertexBuffer(FSQuad);
            GraphicsDevice.DrawPrimitives(PrimitiveType.TriangleStrip, 0, 2);
        }

        /// <summary>Ground-truth path: pose at ControlSS x output res (8K+ at the default 8x), NO
        /// jitter/TAA/velocity, downsampled in LINEAR light with a sigma-0.44-out-px Gaussian
        /// reference filter (see LabDownsample.fx header for why box/gamma were reference bugs):
        /// BoxDecode (gamma -> linear, 2:1) -> bilinear fp16 halvings to 2x -> GaussDown (2:1 +
        /// re-encode). Requires ControlSS >= 4 (always true at sane window sizes).</summary>
        private void RenderControl(ScenePose pose, bool withMeshes)
        {
            EnsureTuningTargets();
            DrawSceneColor(pose, ControlRT, ControlSS, Vector2.Zero, withMeshes);
            DownsamplePass("BoxDecode", ControlRT, ControlChain[0]);
            for (int i = 1; i < ControlChain.Length; i++)
            {
                // bilinear fetch at each 2x2 corner = exact box average, already in linear light
                GraphicsDevice.SetRenderTarget(ControlChain[i]);
                SB.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
                SB.Draw(ControlChain[i - 1], new Rectangle(0, 0, ControlChain[i].Width, ControlChain[i].Height), Color.White);
                SB.End();
            }
            DownsamplePass(GaussianRef ? "GaussDown" : "BoxEncode", ControlChain[ControlChain.Length - 1], ControlDownRT);
        }

        /// <summary>
        /// Point-blit an output-res texture to the half-res metric grid (4x less readback/cache) and
        /// read it back to the CPU. ROTATING PARITY (2026-07-09 fix): the old fixed point-blit sampled
        /// ONE of the four output-pixel parities for the entire run — with this scene's single-pixel
        /// geometry, lines on the other three parities were invisible to the optimizer, spatially AND
        /// temporally, so it could overfit whatever happened to land on the measured parity. The source
        /// offset now rotates through all four parities on a TWO-FRAME cadence (frame pair k samples
        /// parity k &amp; 3), identically for the control pre-render and every eval (both pass the frame
        /// index), keeping control/TAA arrays aligned per frame. The pair cadence is what keeps the
        /// temporal term honest: t vs t-1 is only accumulated on the second frame of a pair, where both
        /// frames sampled the SAME pixels (see AccumulateMetric) — rotating per-frame would compare
        /// different pixels and turn the flicker metric into a parity-difference metric.
        /// </summary>
        private void ReadMetricSamples(Texture2D tex, Color[] dst, int frame)
        {
            BlitMetric(tex, MetricRT, frame);
            MetricRT.GetData(dst);
        }

        /// <summary>The point-blit half of ReadMetricSamples — the eval loop separates it from the
        /// GetData so the readback of frame f can happen just before frame f+1's GPU work is issued
        /// (the CPU-side AccumulateMetric of f-1 overlaps the GPU rendering f).</summary>
        private void BlitMetric(Texture2D tex, RenderTarget2D rt, int frame)
        {
            int parity = (frame >> 1) & 3;
            int ox = -(parity & 1);        //  0 -> odd source columns, -1 -> even
            int oy = -((parity >> 1) & 1); //  0 -> odd source rows,    -1 -> even
            GraphicsDevice.SetRenderTarget(rt);
            SB.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
            // dest pixel x samples source column 2x+1+ox (never negative: min is ox+1 = 0)
            SB.Draw(tex, new Rectangle(0, 0, MetricW, MetricH), new Rectangle(ox, oy, OutW, OutH), Color.White);
            SB.End();
            GraphicsDevice.SetRenderTarget(null);
        }

        private void StartTuning(TuneMode mode, bool cycle = false)
        {
            if (TState != TuneState.Idle) return;
            Kind = RunKind.Optimize;
            TMode = mode;
            CycleRun = cycle;
            StopRequested = false;
            if (cycle)
            {
                // chained self-training cycle: best/defaults/determinism verdict carry over —
                // pruning is strong from the first eval and no det/baseline evals are repeated
                DetPhase = 2;
            }
            else
            {
                DetPhase = 0; DetIdentical = null; HaveDefaultsScores = false;
                BestVec = null; BestTotal = double.MaxValue; BestEvalNum = 0;
                CycleIdx = 0; StagnantCycles = 0; CycleStartBest = double.MaxValue;
                CycleSigma = 0.15; CycleLambda = 0; CycleEscalated = false;
            }
            EvalsDone = 0; EvalMsTotal = 0; EvalMsCount = 0;
            RestartIdx = 0;
            RestartCount = cycle ? 1 : mode == TuneMode.Full ? FullRestarts : 1;
            RestartBudget = mode == TuneMode.Full ? FullEvalsPerRestart : mode == TuneMode.Quick ? QuickEvals : SmokeEvals;
            Opt = null;
            RunLite = TechIdx == 1; // technique-aware search space, captured for the whole run
            RunGammaNative = Tune.GammaNative; RunGammaUpscale = Tune.GammaUpscale; // fixed schedule for the run
            EnsureTuningTargets();
            PrevFixedTimeStep = IsFixedTimeStep;
            IsFixedTimeStep = false; // no fixed-step catch-up spiral while Draw calls take ~0.2s
            RunSW.Restart();

            bool meshes = MeshesEnabled && MeshesAvailable;
            CaptureObjectiveScales();
            // the score cache is only valid for one (space, objective, meshes, reference) context
            var ctx = (RunLite, MultiScale ? -1 : ScaleIdx, meshes, GaussianRef);
            if (ctx != CacheCtx) { ScoreCache.Clear(); CacheCtx = ctx; }
            PrunedEvals = 0; PrunedFramesSaved = 0; CacheHits = 0;
            if (StartFromIdx == 2 && SessionBest == null)
                Console.WriteLine("[AutoTune] no session best for this space yet — starting from shipped defaults instead.");
            Console.WriteLine($"[AutoTune] run started: mode {mode}, optimizer {OptimizerLabels[OptimizerIdx]}, " +
                $"start from {StartFromLabels[StartFromIdx]}, {RestartCount} restart(s) x {RestartBudget} evals, " +
                $"objective {ObjectiveDesc()}, technique {TechNames[TechIdx]}, " +
                $"optimizing {ActiveNames.Length} params ({(RunLite ? "Lite*" : "Tune*")}), meshes {(meshes ? "on" : "off")}, " +
                $"pruning {(PruneEnabled ? "on" : "off")}, " +
                $"weights spatial {SpatialWeight} / temporal {TemporalWeight}, metric grid {MetricW}x{MetricH} (rotating parity, pair cadence).");
            EnsureControl(meshes);
        }

        private void CaptureObjectiveScales()
        {
            EvalScales = MultiScale ? MultiScales : new[] { RenderScale };
            EvalWeights = MultiScale ? MultiScaleWeights : new[] { 1.0 };
        }

        private string ObjectiveDesc()
        {
            if (!MultiScale) return "scale " + RenderScale.ToString("0.00", CultureInfo.InvariantCulture);
            var sb = new StringBuilder("multi-scale ");
            for (int i = 0; i < MultiScales.Length; i++)
                sb.Append(string.Format(CultureInfo.InvariantCulture, "{0}{1:0.00}x*{2:0.0}",
                    i > 0 ? "+" : "", MultiScales[i], MultiScaleWeights[i]));
            return sb.ToString();
        }

        /// <summary>Scored A/B: run the full 240-frame metric on the A (sliders) and B (baseline)
        /// sets under the CURRENT technique and objective (single-scale or the multi-scale set) —
        /// exact scores, no optimizer, no pruning.</summary>
        private void StartCompare()
        {
            if (TState != TuneState.Idle) return;
            Kind = RunKind.Compare;
            StopRequested = false;
            CaptureObjectiveScales();
            CmpSets = new[] { CloneTunables(Tune), CloneTunables(TuneB) };
            CmpLabels = new[] { "A (sliders) ", "B (baseline)" };
            CmpIdx = 0; HaveCmp = false;
            EvalMsTotal = 0; EvalMsCount = 0; EvalsDone = 0;
            EnsureTuningTargets();
            PrevFixedTimeStep = IsFixedTimeStep;
            IsFixedTimeStep = false;
            RunSW.Restart();
            bool meshes = MeshesEnabled && MeshesAvailable;
            CmpCtx = $"technique {TechNames[TechIdx]}, {ObjectiveDesc()}, meshes {(meshes ? "on" : "off")}";
            Console.WriteLine($"[A/B] scoring A (sliders) vs B (baseline): {CmpCtx}");
            EnsureControl(meshes);
        }

        private void EnsureControl(bool meshes)
        {
            if (!ControlCacheValid || ControlCacheMeshes != meshes || ControlCacheGauss != GaussianRef)
            {
                ControlSamples = new Color[SeqFrames][];
                ControlWeightQ = new byte[SeqFrames][];
                ControlMeanW = new double[SeqFrames];
                ControlCacheValid = false;
                ControlCacheMeshes = meshes;
                ControlCacheGauss = GaussianRef;
                ControlFrame = 0;
                TState = TuneState.ControlPreRender;
                Console.WriteLine($"[AutoTune] pre-rendering {ControlSS}x supersampled control ({SeqFrames} frames at {OutW * ControlSS}x{OutH * ControlSS}, " +
                    $"linear-light {(GaussianRef ? "sigma-0.44 Gaussian" : "box")} reference)...");
            }
            else
            {
                Console.WriteLine("[AutoTune] control sequence already cached — reusing.");
                AfterControlReady();
            }
        }

        private void AfterControlReady()
        {
            if (Kind == RunKind.Compare) { TState = TuneState.Evaluating; BeginEvalTunables(CmpSets[0]); }
            else BeginDeterminismCheck();
        }

        private void BeginDeterminismCheck()
        {
            TState = TuneState.Evaluating;
            if (DetPhase == 2) { StartRestart(); return; } // chained cycle: det already verified this session
            BeginEval(ActiveDefaults);
        }

        private void BeginEval(float[] vec)
        {
            CurVec = vec;
            BeginEvalTunables(ActiveFromVector(vec)); // untuned set stays at defaults (inert for this technique)
        }

        private void BeginEvalTunables(Tunables t)
        {
            EvalTune = t;
            ScalePass = 0;
            PassScores = new EvalScores[EvalScales.Length];
            EvalBoundBase = 0;
            TaaCur ??= new Color[MetricW * MetricH];
            TaaPrev ??= new Color[MetricW * MetricH];
            RowS ??= new double[MetricH];
            RowT ??= new double[MetricH];
            RowG ??= new double[MetricH];
            RowF ??= new double[MetricH];
            RowB ??= new double[MetricH];
            PersState ??= new float[MetricW * MetricH];
            BeginScalePass();
            EvalSW.Restart();
        }

        /// <summary>Reset the per-sequence state for the current scale pass of the eval.</summary>
        private void BeginScalePass()
        {
            for (int i = 0; i < 4; i++) { PhaseS[i] = 0; PhaseT[i] = 0; PhaseTN[i] = 0; PhaseG[i] = 0; PhaseF[i] = 0; PhaseB[i] = 0; }
            Array.Clear(PersState, 0, PersState.Length); // persistence must not leak across passes/evals
            EvalFrame = 0;
            HistCurr = 0;          // fixed ping-pong start (identical target usage every pass)
            ResetHistory();        // history black + warmup meta clear, exactly like the game
        }

        /// <summary>One control-sequence frame: render 2x, downsample, sample, store.</summary>
        private void StepControlFrame()
        {
            RenderControl(EvalPose(ControlFrame), ControlCacheMeshes);
            var arr = new Color[MetricW * MetricH];
            ReadMetricSamples(ControlDownRT, arr, ControlFrame);
            ControlSamples[ControlFrame] = arr;
            BuildDetailWeights(ControlFrame);
            if (++ControlFrame >= SeqFrames)
            {
                ControlCacheValid = true;
                FreeControlTargets(); // the cache is CPU-resident now — release the 8K chain's VRAM
                Console.WriteLine("[AutoTune] control sequence cached (session-persistent).");
                AfterControlReady();
            }
        }

        /// <summary>
        /// One TAA-path frame of the current candidate eval. Readback is PIPELINED one frame behind
        /// the render: frame f-1's metric samples are collected (and its CPU accumulate runs) BEFORE
        /// frame f's GPU work is issued, so the GetData only waits on commands through f-1 while the
        /// CPU reduce of the previous frame overlapped the GPU rendering it. Collect may early-PRUNE
        /// the eval (monotone partial-score bound exceeds the best total) — then this eval is already
        /// over and a new one (or the run epilogue) has begun.
        /// </summary>
        private void StepEvalFrame()
        {
            int f = EvalFrame;
            if (f > 0 && !CollectMetricFrame(f - 1)) return; // pruned — eval advanced inside

            float es = EvalScales[ScalePass];
            var (sceneRT, velRT) = EvalTargetsFor(es);
            int erw = sceneRT.Width, erh = sceneRT.Height;
            var jpx = SampleHalton(f, es);
            var sampleJitterUV = new Vector2(-jpx.X / erw, jpx.Y / erh);
            var pose = EvalPose(f);
            // Reveal frame: new content appears with ZERO velocity (prev == current), the honest convention.
            var prev = f == RevealFrame ? pose : EvalPose(f - 1);

            DrawSceneColor(pose, sceneRT, es, jpx, ControlCacheMeshes);
            DrawVelocity(pose, prev, jpx, ControlCacheMeshes, velRT, es, erw, erh);
            RunResolve(EvalTune, sampleJitterUV, sceneRT, velRT, erw, erh, es);
            BlitMetric(Hist[HistCurr], MetricTarget(f), f);
            HistCurr = 1 - HistCurr;

            if (++EvalFrame >= SeqFrames)
            {
                if (!CollectMetricFrame(SeqFrames - 1)) return; // may prune when later scale passes remain
                PassScores[ScalePass] = ComputeScores();
                EvalBoundBase += EvalWeights[ScalePass] * PassScores[ScalePass].Total;
                if (++ScalePass < EvalScales.Length) { BeginScalePass(); return; }
                OnEvalComplete(CombineScores());
            }
        }

        /// <summary>Scale-weighted combination of the per-pass scores (identity for single-scale runs).</summary>
        private EvalScores CombineScores()
        {
            var r = new EvalScores();
            for (int i = 0; i < PassScores.Length; i++)
            {
                double w = EvalWeights[i];
                var s = PassScores[i];
                r.Total += w * s.Total; r.Rest += w * s.Rest; r.Motion += w * s.Motion;
                r.Reveal += w * s.Reveal; r.Slow += w * s.Slow;
                r.SpatialMean += w * s.SpatialMean; r.TemporalMean += w * s.TemporalMean;
                r.GhostMean += w * s.GhostMean; r.FizzleMean += w * s.FizzleMean; r.StrictMean += w * s.StrictMean;
            }
            return r;
        }

        private string FmtPerScale()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < PassScores.Length; i++)
                sb.Append(string.Format(CultureInfo.InvariantCulture, "{0}{1:0.00}x {2:0.000000}",
                    i > 0 ? "  " : "", EvalScales[i], PassScores[i].Total));
            return sb.ToString();
        }

        /// <summary>
        /// Read back + accumulate eval frame g, then apply the early-prune test. The partial score
        /// SW*S_acc/240 + TW*T_acc/120 uses the FINAL normalization denominators over non-negative
        /// per-frame terms, so it is a monotone lower bound of the eval's eventual total — once it
        /// exceeds the best total the candidate cannot win and the remaining frames are skipped
        /// (the optimizer is Tell()ed the bound: optimistic but still ranked worse than best).
        /// Returns false if the eval was pruned (the driver has already moved on).
        /// </summary>
        private bool CollectMetricFrame(int g)
        {
            MetricTarget(g).GetData(TaaCur);
            AccumulateMetric(g);
            (TaaCur, TaaPrev) = (TaaPrev, TaaCur);

            // Prune anywhere except the very last frame of the LAST scale pass (there the bound IS
            // the exact total — that's a normal worse-than-best eval, not a prune). Finished passes
            // contribute their exact weighted totals via EvalBoundBase.
            bool lastFrameOfEval = ScalePass == EvalScales.Length - 1 && g == SeqFrames - 1;
            if (PruneEnabled && !lastFrameOfEval && Kind == RunKind.Optimize && DetPhase == 2
                && BestTotal < double.MaxValue)
            {
                double passLb = SpatialWeight * (PhaseS[0] + PhaseS[1] + PhaseS[2] + PhaseS[3]) / SeqFrames
                              + TemporalWeight * (PhaseT[0] + PhaseT[1] + PhaseT[2] + PhaseT[3]) / (SeqFrames / 2)
                              + GhostWeight * (PhaseG[0] + PhaseG[1] + PhaseG[2] + PhaseG[3]) / SeqFrames
                              + FizzleWeight * (PhaseF[0] + PhaseF[1] + PhaseF[2] + PhaseF[3]) / (SeqFrames / 2)
                              + StrictWeight * (PhaseB[0] + PhaseB[1] + PhaseB[2] + PhaseB[3]) / SeqFrames;
                double lb = EvalBoundBase + EvalWeights[ScalePass] * passLb;
                if (lb > BestTotal)
                {
                    OnEvalPruned(lb, ScalePass * SeqFrames + g + 1);
                    return false;
                }
            }
            return true;
        }

        /// <summary>Quantized detail weights for control frame f (see the field block comment):
        /// luma central-difference gradient magnitude on the metric grid, byte-quantized over
        /// [Floor, 1]. Deterministic; the per-frame mean is the metric normalizer.</summary>
        private void BuildDetailWeights(int f)
        {
            int w = MetricW, h = MetricH;
            var ctrl = ControlSamples[f];
            var q = new byte[w * h];
            double sum = 0;
            for (int y = 0; y < h; y++)
            {
                int o = y * w;
                int oUp = Math.Max(0, y - 1) * w, oDn = Math.Min(h - 1, y + 1) * w;
                for (int x = 0; x < w; x++)
                {
                    float Luma(Color c) => (0.25f * c.R + 0.5f * c.G + 0.25f * c.B) * (1f / 255f);
                    int xl = Math.Max(0, x - 1), xr = Math.Min(w - 1, x + 1);
                    float gx = Math.Abs(Luma(ctrl[o + xr]) - Luma(ctrl[o + xl]));
                    float gy = Math.Abs(Luma(ctrl[oDn + x]) - Luma(ctrl[oUp + x]));
                    float detail = Math.Max(gx, gy);
                    float wt = DetailWeightFloor + (1f - DetailWeightFloor) * Math.Clamp(detail * DetailWeightGain, 0f, 1f);
                    byte qb = (byte)Math.Round((wt - DetailWeightFloor) / (1f - DetailWeightFloor) * 255f);
                    q[o + x] = qb;
                    sum += DetailWeightFloor + (1f - DetailWeightFloor) * (qb * (1f / 255f)); // mean of the QUANTIZED weights
                }
            }
            ControlWeightQ[f] = q;
            ControlMeanW[f] = sum / (w * h);
        }

        /// <summary>
        /// Per-frame metric: spatial MSE (linear RGB) vs control + temporal term
        /// TD = mean |(taa[t]-taa[t-1]) - (control[t]-control[t-1])| (penalizes ghosting AND fizzle).
        /// The temporal term only accumulates on the SECOND frame of each sampling-parity pair (odd f)
        /// so both frames of the diff sampled the same pixels — see ReadMetricSamples. Per-phase
        /// temporal counts (PhaseTN) keep the score normalization exact.
        /// Row-parallel with per-row partial sums summed in fixed order — bit-deterministic.
        /// </summary>
        private void AccumulateMetric(int f)
        {
            int w = MetricW, h = MetricH;
            var taa = TaaCur; var taaPrev = TaaPrev;
            var ctrl = ControlSamples[f];
            var wq = ControlWeightQ[f];
            bool hasPrev = (f & 1) == 1; // same-parity predecessor exists (also implies f > 0)
            var ctrlPrev = hasPrev ? ControlSamples[f - 1] : null;
            var rowS = RowS; var rowT = RowT;
            var rowG = RowG; var rowF = RowF; var rowB = RowB;
            var pers = PersState;
            Parallel.For(0, h, y =>
            {
                double s = 0, td = 0, gp = 0, fz = 0, bp = 0;
                int o = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = o + x;
                    double wt = DetailWeightFloor + (1.0 - DetailWeightFloor) * (wq[i] * (1.0 / 255.0));
                    Color a = taa[i], c = ctrl[i];
                    double dr = (a.R - c.R) * (1.0 / 255.0);
                    double dg = (a.G - c.G) * (1.0 / 255.0);
                    double db = (a.B - c.B) * (1.0 / 255.0);
                    s += wt * (dr * dr + dg * dg + db * db) * (1.0 / 3.0);

                    // GHOST/TRAIL persistence: signed luma-error run counter (see field block)
                    double lumaErr = 0.25 * dr + 0.5 * dg + 0.25 * db;
                    float ps = pers[i];
                    if (Math.Abs(lumaErr) <= 2.0 / 255.0) pers[i] = 0f;
                    else if (lumaErr > 0) pers[i] = ps > 0 ? ps + 1 : 1;
                    else pers[i] = ps < 0 ? ps - 1 : -1;
                    // Ramp IN over runs 3..6 (a trail), ramp OUT over 12..30: a same-signed error that
                    // persists FOREVER is steady-state reconstruction bias (already scored spatially,
                    // every frame), not a ghost — without the fade-out the term scored converged
                    // approximation offset and dominated the total at defaults (52% at 0.33x).
                    float run = Math.Abs(pers[i]);
                    if (run > 2f)
                        gp += wt * Math.Abs(lumaErr) * Math.Min((run - 2f) * 0.25f, 1f)
                            * Math.Max(1f - Math.Max(run - 12f, 0f) / 18f, 0f);

                    // STRICT bad-pixel fractions: unweighted max-channel error (a wrong pixel is wrong)
                    int maxErr = Math.Max(Math.Abs(a.R - c.R), Math.Max(Math.Abs(a.G - c.G), Math.Abs(a.B - c.B)));
                    if (maxErr > 2) bp += maxErr > 5 ? 5.0 : 1.0;

                    if (hasPrev)
                    {
                        Color ap = taaPrev[i], cp = ctrlPrev[i];
                        double tr = ((a.R - ap.R) - (c.R - cp.R)) * (1.0 / 255.0);
                        double tg = ((a.G - ap.G) - (c.G - cp.G)) * (1.0 / 255.0);
                        double tb = ((a.B - ap.B) - (c.B - cp.B)) * (1.0 / 255.0);
                        td += wt * (Math.Abs(tr) + Math.Abs(tg) + Math.Abs(tb)) * (1.0 / 3.0);

                        // FIZZLE: TAA luma changed where the ground truth held still (dead-band 1/255)
                        double ctrlD = Math.Abs((0.25 * (c.R - cp.R) + 0.5 * (c.G - cp.G) + 0.25 * (c.B - cp.B)) * (1.0 / 255.0));
                        if (ctrlD < 1.0 / 255.0)
                        {
                            double taaD = Math.Abs((0.25 * (a.R - ap.R) + 0.5 * (a.G - ap.G) + 0.25 * (a.B - ap.B)) * (1.0 / 255.0));
                            fz += wt * Math.Max(taaD - 1.0 / 255.0, 0.0);
                        }
                    }
                }
                rowS[y] = s; rowT[y] = td; rowG[y] = gp; rowF[y] = fz; rowB[y] = bp;
            });
            double fs = 0, ft = 0, fg = 0, ff = 0, fb = 0;
            for (int y = 0; y < h; y++) { fs += rowS[y]; ft += rowT[y]; fg += rowG[y]; ff += rowF[y]; fb += rowB[y]; }
            int phase = f < 60 ? 0 : f < 120 ? 1 : f < 180 ? 2 : 3;
            // normalize by the frame's mean weight so weighting shifts emphasis, not magnitude
            double n = w * h * ControlMeanW[f];
            PhaseS[phase] += fs / n;
            PhaseG[phase] += fg / n;
            PhaseB[phase] += fb / (w * h); // strict stays unweighted: plain pixel fraction
            if (hasPrev) { PhaseT[phase] += ft / n; PhaseF[phase] += ff / n; PhaseTN[phase]++; }
        }

        private EvalScores ComputeScores()
        {
            var r = new EvalScores();
            double s = 0, t = 0, g = 0, fz = 0, b = 0; int tn = 0;
            var ps = new double[4];
            for (int i = 0; i < 4; i++)
            {
                s += PhaseS[i]; t += PhaseT[i]; tn += PhaseTN[i];
                g += PhaseG[i]; fz += PhaseF[i]; b += PhaseB[i];
                // per-frame terms: 60 frames/phase; pair terms: counted (parity pairs -> 30/phase)
                ps[i] = SpatialWeight * PhaseS[i] / 60.0 + TemporalWeight * PhaseT[i] / Math.Max(1, PhaseTN[i])
                      + GhostWeight * PhaseG[i] / 60.0 + FizzleWeight * PhaseF[i] / Math.Max(1, PhaseTN[i])
                      + StrictWeight * PhaseB[i] / 60.0;
            }
            r.Rest = ps[0]; r.Motion = ps[1]; r.Reveal = ps[2]; r.Slow = ps[3];
            r.SpatialMean = s / SeqFrames;
            r.TemporalMean = t / Math.Max(1, tn);
            r.GhostMean = g / SeqFrames;
            r.FizzleMean = fz / Math.Max(1, tn);
            r.StrictMean = b / SeqFrames;
            r.Total = SpatialWeight * r.SpatialMean + TemporalWeight * r.TemporalMean
                    + GhostWeight * r.GhostMean + FizzleWeight * r.FizzleMean + StrictWeight * r.StrictMean;
            return r;
        }

        private static string Fmt(EvalScores s) => string.Format(CultureInfo.InvariantCulture,
            "total {0:0.000000} | rest {1:0.000000} motion {2:0.000000} reveal {3:0.000000} slow {4:0.000000} | spatial {5:0.000000} temporal {6:0.000000} ghost {7:0.000000} fizzle {8:0.000000} strict {9:0.0000}",
            s.Total, s.Rest, s.Motion, s.Reveal, s.Slow, s.SpatialMean, s.TemporalMean, s.GhostMean, s.FizzleMean, s.StrictMean);

        private bool Consider(float[] vec, EvalScores sc)
        {
            if (sc.Total >= BestTotal) return false;
            BestTotal = sc.Total;
            BestVec = (float[])vec.Clone();
            BestScoresV = sc;
            BestEvalNum = EvalsDone;
            Console.WriteLine($"[AutoTune] eval #{EvalsDone}: new best — {Fmt(sc)}"
                + (EvalScales.Length > 1 ? $"  [per-scale: {FmtPerScale()}]" : ""));
            return true;
        }

        /// <summary>The run's chosen warm-start vector (clamped into bounds), per the GUI combo.</summary>
        private float[] StartVector()
        {
            float[] v;
            switch (StartFromIdx)
            {
                case 1: v = RunLite ? ToLiteVector(Tune) : ToVector(Tune); break;
                case 2 when SessionBest != null: v = (float[])SessionBest.Clone(); break;
                default: v = ActiveDefaults; break;
            }
            for (int i = 0; i < v.Length; i++) v[i] = Math.Clamp(v[i], ActiveLo[i], ActiveHi[i]);
            return v;
        }

        private float[] RestartStart(int idx)
        {
            var d = CycleRun && BestVec != null ? (float[])BestVec.Clone() : StartVector();
            if (idx == 0) return d; // restart 1 = the exact chosen start
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
            RestartSinceImprove = 0;
            var start = RestartStart(RestartIdx);
            // cycles vary the seed so a re-run from the same best explores NEW candidates (identical
            // seed + start would replay the previous cycle straight into the score cache forever)
            int seed = 7777 * (RestartIdx + 1) + 13 + 101 * CycleIdx;
            // Auto: NM discovers (a fresh run's exact-start restart, and escalation cycles — with its
            // step scaled to the escalated sigma), CMA-ES everywhere else (jittered restarts, refine cycles)
            UseNmNow = OptimizerIdx == 0 || (OptimizerIdx == 2 && (CycleRun ? CycleEscalated : RestartIdx == 0));
            RestartLambda = CycleRun && CycleLambda > 0 ? CycleLambda : DefaultLambda;
            Opt = UseNmNow
                ? new NelderMeadOptimizer(start, ActiveLo, ActiveHi,
                    initStepFrac: CycleRun ? (float)CycleSigma : 0.08f)
                : (IOptimizer)new CmaEsOptimizer(start, ActiveLo, ActiveHi, seed,
                    sigma0: CycleRun ? CycleSigma : 0.15, lambda: CycleRun ? CycleLambda : 0);
            Console.WriteLine($"[AutoTune] restart {RestartIdx + 1}/{RestartCount} " +
                $"({(UseNmNow ? "Nelder-Mead" : "CMA-ES")}, {(CycleRun ? FormattableString.Invariant($"cycle {CycleIdx} from best, sigma {CycleSigma:0.00}{(UseNmNow ? "" : $", lambda {RestartLambda}")}") : RestartIdx == 0 ? "exact start" : "start +/-10% jitter")}) — budget {RestartBudget} evals, stall cutoff {StallLimit}.");
            AskNextOrAdvance();
        }

        private void OnEvalComplete(EvalScores sc)
        {
            EvalsDone++;
            EvalSW.Stop();
            EvalMsTotal += EvalSW.Elapsed.TotalMilliseconds;
            EvalMsCount++;

            if (Kind == RunKind.Compare)
            {
                CmpScores[CmpIdx] = sc;
                Console.WriteLine($"[A/B] {CmpLabels[CmpIdx]}: {Fmt(sc)}"
                    + (EvalScales.Length > 1 ? $"  [per-scale: {FmtPerScale()}]" : ""));
                CmpIdx++;
                if (StopRequested || CmpIdx >= CmpSets.Length) FinishCompare(StopRequested);
                else BeginEvalTunables(CmpSets[CmpIdx]);
                return;
            }

            if (DetPhase < 2)
            {
                if (DetPhase == 0)
                {
                    DetPhase = 1;
                    DetA = sc;
                    DefaultsScores = sc; HaveDefaultsScores = true;
                    Console.WriteLine($"[AutoTune] defaults baseline ({(RunLite ? "Lite*" : "Tune*")}): {Fmt(sc)}"
                        + (EvalScales.Length > 1 ? $"  [per-scale: {FmtPerScale()}]" : ""));
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
            bool improved = Consider(CurVec, sc);
            ScoreCache[VecKey(CurVec)] = sc.Total; // full evals only — pruned bounds are not true scores
            RestartEvals++;
            RestartSinceImprove = improved ? 0 : RestartSinceImprove + 1;
            AskNextOrAdvance();
        }

        /// <summary>Pruned-eval epilogue: the optimizer is Tell()ed the partial lower bound (already
        /// worse than best, so ranking stays sound); no Consider, no cache entry.</summary>
        private void OnEvalPruned(double bound, int framesRun)
        {
            EvalsDone++;
            PrunedEvals++;
            PrunedFramesSaved += SeqFrames * EvalScales.Length - framesRun;
            EvalSW.Stop();
            EvalMsTotal += EvalSW.Elapsed.TotalMilliseconds;
            EvalMsCount++;
            Opt.Tell(bound);
            RestartEvals++;
            RestartSinceImprove++; // a pruned eval by definition didn't improve the best
            AskNextOrAdvance();
        }

        /// <summary>Advance the optimizer loop: serve exact-duplicate candidates from the score cache
        /// for free (not counted against the eval budget), start the next real eval, or roll into the
        /// next restart / the run epilogue.</summary>
        private void AskNextOrAdvance()
        {
            int cacheStreak = 0; // safety valve: a degenerate optimizer cycling cached points forever
            while (true)
            {
                if (StopRequested) { FinishRun("stopped"); return; }
                bool stalled = RestartSinceImprove >= StallLimit;
                float[] next = RestartEvals >= RestartBudget || stalled ? null : Opt.Ask();
                if (next == null)
                {
                    if (stalled)
                        Console.WriteLine($"[AutoTune] restart {RestartIdx + 1} STALLED — no best-improvement in {RestartSinceImprove} evals ({RestartEvals} total), rolling on.");
                    else if (RestartEvals < RestartBudget)
                        Console.WriteLine($"[AutoTune] restart {RestartIdx + 1} converged after {RestartEvals} evals.");
                    RestartIdx++;
                    if (RestartIdx >= RestartCount) { FinishRun("complete"); return; }
                    StartRestart();
                    return;
                }
                if (cacheStreak < 1000 && ScoreCache.TryGetValue(VecKey(next), out double cached))
                {
                    CacheHits++; cacheStreak++;
                    Opt.Tell(cached);
                    continue;
                }
                BeginEval(next);
                return;
            }
        }

        private void FinishCompare(bool aborted)
        {
            RunSW.Stop();
            if (!aborted && CmpIdx >= 2)
            {
                HaveCmp = true;
                double dA = CmpScores[0].Total, dB = CmpScores[1].Total;
                string verdict = dA == dB ? "TIE" : dA < dB
                    ? string.Format(CultureInfo.InvariantCulture, "A (sliders) WINS by {0:0.0}% (lower is better)", (dB - dA) / dB * 100.0)
                    : string.Format(CultureInfo.InvariantCulture, "B (baseline) WINS by {0:0.0}% (lower is better)", (dA - dB) / dA * 100.0);
                Console.WriteLine($"[A/B] {CmpCtx} -> {verdict}");
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "[A/B] phase deltas (A - B, negative = A better): rest {0:+0.000000;-0.000000;0} motion {1:+0.000000;-0.000000;0} reveal {2:+0.000000;-0.000000;0} slow {3:+0.000000;-0.000000;0}",
                    CmpScores[0].Rest - CmpScores[1].Rest, CmpScores[0].Motion - CmpScores[1].Motion,
                    CmpScores[0].Reveal - CmpScores[1].Reveal, CmpScores[0].Slow - CmpScores[1].Slow));
            }
            else Console.WriteLine("[A/B] compare stopped — incomplete, no verdict.");
            TState = TuneState.Idle;
            IsFixedTimeStep = PrevFixedTimeStep;
            NeedHistoryReset = true;
        }

        private void FinishRun(string reason)
        {
            RunSW.Stop();
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "[AutoTune] {0} after {1} evals in {2:0.0}s (avg {3:0} ms/eval).",
                reason, EvalsDone, RunSW.Elapsed.TotalSeconds, EvalMsCount > 0 ? EvalMsTotal / EvalMsCount : 0));
            if (PrunedEvals > 0 || CacheHits > 0)
                Console.WriteLine($"[AutoTune] efficiency: {PrunedEvals} evals early-pruned " +
                    $"({PrunedFramesSaved} of {EvalsDone * SeqFrames * EvalScales.Length} frames skipped), {CacheHits} duplicate candidates served from cache.");
            if (DetIdentical.HasValue)
                Console.WriteLine($"[AutoTune] determinism: {(DetIdentical.Value ? "PASS (two identical-params evals scored bit-identically)" : "FAIL")}");
            if (HaveDefaultsScores) Console.WriteLine($"[AutoTune] defaults: {Fmt(DefaultsScores)}");
            if (BestVec != null)
            {
                Console.WriteLine($"[AutoTune] best:     {Fmt(BestScoresV)} (eval #{BestEvalNum}, {(RunLite ? "Lite*" : "Tune*")} space)");
                PrintTuningBlock(ActiveFromVector(BestVec), AutoTuneHeader);
                // remember per-space session best so the next run can warm-start from it
                if (RunLite) SessionBestLite = (float[])BestVec.Clone();
                else SessionBestFull = (float[])BestVec.Clone();
            }
            TState = TuneState.Idle;
            IsFixedTimeStep = PrevFixedTimeStep;
            NeedHistoryReset = true; // interactive resumes from a clean history
            // smoke exits here — except the 'cont' variant, which verifies two chained cycles first
            if (SmokeMode && !(SmokeCont && ContinuousTrain && reason == "complete" && CycleIdx < 2)) { Exit(); return; }

            // continuous self-training: chain the next cycle (see the field block comment)
            if (ContinuousTrain && reason == "complete" && BestVec != null)
            {
                bool improved = BestTotal < CycleStartBest * (1.0 - 1e-4);
                if (improved)
                {
                    StagnantCycles = 0;
                    CycleSigma = 0.08;      // refine around the new best
                    CycleLambda = 0;        // default population
                    CycleEscalated = false; // Auto mode: CMA-ES refine
                }
                else
                {
                    StagnantCycles++;
                    if (StagnantCycles >= MaxStagnantCycles)
                    {
                        Console.WriteLine($"[AutoTune] continuous training: {MaxStagnantCycles} stagnant cycles — converged, stopping. " +
                            $"Final best {BestTotal.ToString("0.000000", CultureInfo.InvariantCulture)} (cycle {CycleIdx}).");
                        return;
                    }
                    CycleSigma = Math.Min(0.3, CycleSigma * 1.8);                       // IPOP-style escalation
                    CycleLambda = Math.Min(64, (CycleLambda > 0 ? CycleLambda : DefaultLambda) * 2);
                    CycleEscalated = true; // Auto mode: NM discovery with the escalated step
                    Console.WriteLine(FormattableString.Invariant(
                        $"[AutoTune] continuous training: stagnant cycle ({StagnantCycles}/{MaxStagnantCycles}) — escalating: sigma {CycleSigma:0.00}, lambda {CycleLambda}."));
                }
                CycleIdx++;
                CycleStartBest = BestTotal;
                Console.WriteLine($"[AutoTune] continuous training: starting cycle {CycleIdx} from best.");
                StartTuning(TMode, cycle: true); // chain with the originating mode's budget
            }
        }

        private void CancelDuringControl()
        {
            Console.WriteLine("[AutoTune] stopped during control pre-render — no results (cache incomplete).");
            ControlCacheValid = false;
            FreeControlTargets();
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
                    else if (Kind == RunKind.Compare) FinishCompare(true);
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
                : Kind == RunKind.Compare
                ? string.Format(CultureInfo.InvariantCulture, "A/B SCORING: {0} {1}FRAME {2}/{3}",
                    CmpIdx < CmpLabels.Length ? CmpLabels[CmpIdx].Trim() : "?",
                    EvalScales.Length > 1 ? FormattableString.Invariant($"SCALE {EvalScales[ScalePass]:0.00} ") : "",
                    EvalFrame, SeqFrames)
                : string.Format(CultureInfo.InvariantCulture, "AUTO-TUNE: EVAL {0} {1}FRAME {2}/{3} BEST {4}",
                    EvalsDone,
                    EvalScales.Length > 1 ? FormattableString.Invariant($"SCALE {EvalScales[ScalePass]:0.00} ") : "",
                    EvalFrame, SeqFrames, BestVec != null ? BestTotal.ToString("0.000000", CultureInfo.InvariantCulture) : "-");
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
                "TAA {0}  SET {1}  {2}  SCALE {3:0.00}  {4}X{5}  {6:0} FPS",
                TAAEnabled ? (TechIdx == 1 ? "LITE" : "FULL") : "OFF",
                UseB ? "B" : "A",
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
            DrawABGui(idle);

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
                ImGui.SliderFloat("GammaNative (rest sigma @ native)", ref Tune.GammaNative, 1f, 2f, "%.3f");
                ImGui.SliderFloat("GammaUpscale (rest sigma @ 0.33x)", ref Tune.GammaUpscale, 1.5f, 3.5f, "%.3f");
                ImGui.TextDisabled("gamma = FIXED reference schedule (native->upscale), NOT auto-tuned");
                ImGui.SliderFloat("ConfFloor", ref Tune.ConfFloor, 0f, 1f, "%.3f");
                if (ImGui.Button("Reset to defaults##trust"))
                {
                    Tune.RespEnd = Defaults.RespEnd; Tune.MotionTrustCap = Defaults.MotionTrustCap;
                    Tune.MotionClampTighten = Defaults.MotionClampTighten;
                    Tune.GammaNative = Defaults.GammaNative; Tune.GammaUpscale = Defaults.GammaUpscale;
                    Tune.ConfFloor = Defaults.ConfFloor;
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
                ImGui.SliderFloat("ConfFadeN", ref Tune.ConfFadeN, 1f, 64f, "%.1f");
                ImGui.SliderFloat("GrowOffPhase", ref Tune.GrowOffPhase, 0.25f, 1f, "%.3f"); // <0.25 quantizes to zero growth (RGBA8 meta N), see ParamLo
                ImGui.SliderFloat("DeepCapBase", ref Tune.DeepCapBase, 0.9f, 0.999f, "%.4f");
                if (ImGui.Button("Reset to defaults##structural"))
                {
                    Tune.DirectClampMix = Defaults.DirectClampMix; Tune.ConfFadeN = Defaults.ConfFadeN;
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
                Tune.GammaNative = Defaults.GammaNative; Tune.GammaUpscale = Defaults.GammaUpscale;
                Tune.ConfFloor = Defaults.ConfFloor;
                Tune.RingLo = Defaults.RingLo; Tune.RingHi = Defaults.RingHi;
                Tune.DirectClampMix = Defaults.DirectClampMix; Tune.ConfFadeN = Defaults.ConfFadeN;
                Tune.GrowOffPhase = Defaults.GrowOffPhase; Tune.DeepCapBase = Defaults.DeepCapBase;
                Tune.LiteGamma = Defaults.LiteGamma; Tune.LiteGammaScale = Defaults.LiteGammaScale;
                Tune.LiteDeepCap = Defaults.LiteDeepCap; Tune.LiteRespEnd = Defaults.LiteRespEnd;
                Tune.LiteMotionBoost = Defaults.LiteMotionBoost; Tune.LiteConfFloor = Defaults.LiteConfFloor;
                Tune.LiteMoveGateLo = Defaults.LiteMoveGateLo; Tune.LiteMoveGateHi = Defaults.LiteMoveGateHi;
                Tune.LiteHonestLo = Defaults.LiteHonestLo; Tune.LiteHonestHi = Defaults.LiteHonestHi;
            }
            ImGui.TextDisabled("Space pause  R reset hist  T TAA on/off  B set A/B  P print  Esc quit");

            ImGui.End();
        }

        /// <summary>
        /// The "A/B" ImGui section: B is a full-tuning baseline snapshot (shipped defaults at launch).
        /// Eyeball A/B with the 'B' key / the checkbox (history resets so each set converges honestly),
        /// or score both sets on the auto-tuner's 240-frame metric for a numeric verdict.
        /// </summary>
        private void DrawABGui(bool idle)
        {
            if (!ImGui.CollapsingHeader("A/B (sliders vs baseline)", ImGuiTreeNodeFlags.DefaultOpen)) return;

            ImGui.TextDisabled("A = live sliders. B = baseline snapshot (starts as shipped defaults).");
            bool useB = UseB;
            if (ImGui.Checkbox("View B (baseline)   [key: B]", ref useB) && useB != UseB)
            {
                UseB = useB;
                NeedHistoryReset = true;
            }
            if (!idle) ImGui.BeginDisabled();
            if (ImGui.Button("Snapshot sliders -> B")) CopyTunables(Tune, TuneB);
            ImGui.SameLine();
            if (ImGui.Button("Load B -> sliders")) { CopyTunables(TuneB, Tune); NeedHistoryReset = true; }
            ImGui.SameLine();
            if (ImGui.Button("Reset B to shipped")) CopyTunables(Defaults, TuneB);
            if (ImGui.Button(MultiScale ? "Score A vs B (multi-scale metric)" : "Score A vs B (2 x 240-frame metric)"))
                StartCompare();
            if (!idle) ImGui.EndDisabled();
            if (HaveCmp)
            {
                double dA = CmpScores[0].Total, dB = CmpScores[1].Total;
                var col = dA < dB ? new System.Numerics.Vector4(0.4f, 1f, 0.4f, 1f)
                        : dA > dB ? new System.Numerics.Vector4(1f, 0.75f, 0.35f, 1f)
                        : new System.Numerics.Vector4(0.8f, 0.8f, 0.8f, 1f);
                ImGui.TextColored(col, string.Format(CultureInfo.InvariantCulture,
                    dA == dB ? "TIE at {0:0.000000}" : dA < dB ? "A wins: {0:0.000000} vs {1:0.000000} ({2:0.0}% better)"
                                                               : "B wins: {1:0.000000} vs {0:0.000000} ({2:0.0}% better)",
                    dA, dB, Math.Abs(dA - dB) / Math.Max(dA, dB) * 100.0));
                ImGui.TextDisabled($"({CmpCtx})");
                ImGui.Text(string.Format(CultureInfo.InvariantCulture,
                    "A: R {0:0.0000} M {1:0.0000} V {2:0.0000} S {3:0.0000}",
                    CmpScores[0].Rest, CmpScores[0].Motion, CmpScores[0].Reveal, CmpScores[0].Slow));
                ImGui.Text(string.Format(CultureInfo.InvariantCulture,
                    "B: R {0:0.0000} M {1:0.0000} V {2:0.0000} S {3:0.0000}",
                    CmpScores[1].Rest, CmpScores[1].Motion, CmpScores[1].Reveal, CmpScores[1].Slow));
            }
        }

        /// <summary>The "Auto-tune" ImGui section: start/stop, progress, best-so-far, load/print best.</summary>
        private void DrawAutoTuneGui(bool idle)
        {
            if (!ImGui.CollapsingHeader("Auto-tune", ImGuiTreeNodeFlags.DefaultOpen)) return;

            ImGui.TextDisabled(string.Format(CultureInfo.InvariantCulture,
                "score = {0:0.0}xMSE + {1:0.0}xTD + {2:0.0}xghost + {3:0.0}xfizzle + {4:0.00}xstrict, vs {5}x control",
                SpatialWeight, TemporalWeight, GhostWeight, FizzleWeight, StrictWeight, ControlSS));
            ImGui.TextDisabled($"reference: linear-light {(GaussianRef ? "sigma-0.44 Gaussian" : "box")}; error detail-weighted (~4x on fine detail)");
            ImGui.TextDisabled("sequence: 60 rest / 60 motion / 60 reveal+rest / 60 slow (240 @ virtual 60Hz)");

            ImGui.TextDisabled($"search space follows the technique combo: {(TechIdx == 1 ? "10 Lite* params" : "15 Tune* params")}");

            if (idle)
            {
                ImGui.Combo("Optimizer", ref OptimizerIdx, OptimizerLabels, OptimizerLabels.Length);
                ImGui.Combo("Start from", ref StartFromIdx, StartFromLabels, StartFromLabels.Length);
                if (StartFromIdx == 2 && SessionBestFull == null && SessionBestLite == null)
                    ImGui.TextDisabled("(no session best yet — will fall back to shipped defaults)");
                ImGui.Checkbox("Early pruning (skip evals that can't beat best)", ref PruneEnabled);
                ImGui.Checkbox("Multi-scale objective (0.33*0.5 + 0.50*0.3 + 1.00*0.2)", ref MultiScale);
                if (MultiScale) ImGui.TextDisabled("one tuning for the whole upscale range; ~3x eval cost");
                if (ImGui.Checkbox("Gaussian reference (anti-alias-honest, reads softer)", ref GaussianRef))
                    ControlCacheValid = false; // reference changed -> control cache must re-render
                ImGui.Checkbox("Continuous self-training (chain cycles from best)", ref ContinuousTrain);
                if (ContinuousTrain) ImGui.TextDisabled("refines while improving, escalates sigma/population when stuck,\nstops after 3 stagnant cycles. CMA-ES recommended. Uncheck or STOP to end.");
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
                if (ContinuousTrain || CycleRun)
                {
                    ImGui.Checkbox("chain##cont", ref ContinuousTrain); // uncheck mid-run to stop after this cycle
                    ImGui.SameLine();
                    ImGui.Text($"cycle {CycleIdx}  stagnant {StagnantCycles}/{MaxStagnantCycles}");
                    ImGui.SameLine();
                }
                ImGui.Text(TState == TuneState.ControlPreRender
                    ? $"pre-rendering control {ControlFrame}/{SeqFrames}"
                    : $"restart {Math.Min(RestartIdx + 1, RestartCount)}/{RestartCount}  eval #{EvalsDone + 1}  frame {EvalFrame}/{SeqFrames}"
                      + (EvalScales.Length > 1 ? FormattableString.Invariant($"  scale {EvalScales[ScalePass]:0.00}") : "")
                      + (DetPhase < 2 ? "  (determinism check)" : ""));
            }

            if (EvalMsCount > 0)
                ImGui.Text(string.Format(CultureInfo.InvariantCulture, "evals: {0}   avg {1:0} ms/eval   pruned {2}   cache hits {3}",
                    EvalsDone, EvalMsTotal / EvalMsCount, PrunedEvals, CacheHits));
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
            {
                NeedHistoryReset = true;
                FreeControlTargets(); // don't hold the 8K chain while the debug view is off
            }
        }

        private void PrintCheatsheet()
        {
            Console.WriteLine("==================== OpenSO TAA Lab ====================");
            Console.WriteLine("Runs the game's REAL compiled TAA resolve (DX/SM4 TAA.xnb, technique TAA or");
            Console.WriteLine("TAALite — live A/B combo) on a synthetic scene. History/meta at output res;");
            Console.WriteLine("TAAU when scale < 1. All 27 tunable uniforms are live (17 Tune* incl. the");
            Console.WriteLine("SM4-only set + 10 Lite* for the TAALite technique).");
            Console.WriteLine();
            Console.WriteLine("Primary UI: the ImGui 'TAA Tuning' window (sliders, per-group + global");
            Console.WriteLine("defaults reset, scene controls, Print C# block).");
            Console.WriteLine("  Space       pause/resume scene motion (jitter keeps running)");
            Console.WriteLine("  R           reset history (black + warmup meta clear)");
            Console.WriteLine("  T           toggle TAA off/on (raw upscaled A/B)");
            Console.WriteLine("  B           toggle tuning set A (sliders) / B (baseline snapshot)");
            Console.WriteLine("  P           print current values as a C# TAATuning block");
            Console.WriteLine("  Esc         quit");
            Console.WriteLine();
            Console.WriteLine("Scene: gradient bg / checkerboard mover (interior-texture ghosting) /");
            Console.WriteLine("rotating rect (honest per-pixel rotational velocity) / thin 1-output-px lines");
            Console.WriteLine("(static + drifting; sub-render-pixel fizzle) / similar-color sliding pair /");
            Console.WriteLine("bright-glint cluster (static + sub-px orbiter; highlight ringing + Karis) /");
            Console.WriteLine("fine-noise patch (1 texel/output px; stipple + texture crunch) / VELOCITY-LESS");
            Console.WriteLine("mover (color-only; clamp must catch it, the animated-texture case) /");
            Console.WriteLine("real game 3D meshes (christmastree spin, plumbob pedestal rocking self-reveal,");
            Console.WriteLine("bannerlamp slow spin) with per-pixel matrix-pair velocity + clip.w/800 depth.");
            Console.WriteLine();
            Console.WriteLine("Auto-tune (ImGui section): CMA-ES (default) or Nelder-Mead vs an 8x supersampled");
            Console.WriteLine("(8K+) no-TAA control (LINEAR-LIGHT averaged; BOX reference by default, sigma-0.44");
            Console.WriteLine("GAUSSIAN toggle; DETAIL-WEIGHTED error) on a fixed 240-frame rest/motion/reveal/slow");
            Console.WriteLine("script. TECHNIQUE-");
            Console.WriteLine("AWARE: tunes the 15 Tune* params under Full Cosmic TAA, the 10 Lite* under TAA");
            Console.WriteLine("Lite. Warm-startable (defaults / sliders / session best); optional MULTI-SCALE");
            Console.WriteLine("objective (0.33/0.5/1.0 weighted 0.5/0.3/0.2 — one tuning for the upscale");
            Console.WriteLine("range); optional CONTINUOUS SELF-TRAINING (chained cycles from best, IPOP-style");
            Console.WriteLine("escalation when stagnant); early pruning + dup-candidate cache + pipelined");
            Console.WriteLine("readback keep evals cheap. A/B section scores the sliders against a baseline");
            Console.WriteLine("snapshot on the same metric (honors the multi-scale objective).");
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
            // TAATuning field names/values — NOT the optimizer vector (which gap-encodes the paired
            // bounds; printing it would emit "MoveGateGap" etc., invalid TAATuning fields).
            Section(PrintNames, PrintVector(t));
            sb.AppendLine("        // TAALite tunables:");
            Section(LitePrintNames, LitePrintVector(t));
            Console.WriteLine(sb.ToString());
        }
    }
}
