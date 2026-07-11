using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FSO.Common.Utils
{
    /// <summary>
    /// Everything the temporal resolve consumes for one frame, gathered as one value with its
    /// coherence rules in one place (TAAResolve.BuildContract) instead of spread across
    /// PPXDepthEngine statics read at different times. The contract answers three questions the
    /// resolve used to answer implicitly:
    ///   1. Ready — are all inputs present and mutually consistent (velocity on the color grid,
    ///      history sized to the grid this resolve writes)? If not, the frame passes through
    ///      un-resolved rather than resolving misaligned.
    ///   2. Layout (Tier + Upscale + input grid + jitter cycle + ResolveVersion) — which
    ///      history/meta encoding and accumulation regime this resolve is about to write.
    ///      TemporalHistoryState compares it against what history was COMMITTED with and resets
    ///      when they're incompatible (e.g. a TAAU render-scale change keeps native-sized history
    ///      but changes the input grid and jitter cycle under it).
    ///   3. Continuity (FrameId) — do these inputs immediately follow the frame history holds?
    ///      Frames that bypass the resolve (fade/zoom transitions, missing-target fall-throughs)
    ///      advance PPXDepthEngine.FrameId without a Commit, so one-frame motion vectors are never
    ///      applied to older history: the gap clears history instead.
    /// The FSO.TAALab harness mirrors the resolve driver; if fields are added here, mirror them there.
    /// </summary>
    public struct TemporalFrameContract
    {
        public RenderTarget2D Color;     // this frame's scene color (render-res under TAAU)
        public RenderTarget2D Velocity;  // screen-space velocity MRT (HalfVector4), dilated in-shader
        public TemporalHistoryState History;
        public TemporalHistoryState.ResolveTier Tier;
        public bool Upscale;             // TAAU: history/output on the native grid, color render-res
        public int OutputWidth, OutputHeight;
        public long FrameId;             // PPXDepthEngine.FrameId these inputs belong to; Commit records
                                         // it, BeginResolve requires history to be exactly one frame older
        public int ResolveVersion;       // TAAResolve.RESOLVE_VERSION; part of the history signature so a
                                         // shader/meta layout change can never decode old-layout bytes

        public Vector2 JitterDeltaUV;    // per-frame jitter delta; cancels the jitter baked into velocity
        public Vector2 SampleJitterUV;   // un-jittered offset for the variance-box taps
        public float VelGatePxScale;     // render-px -> native-px scale for the motion gates
        public float BlendFactor;
        public float MaxAccum;           // must match the shader's meta.R decode
        public float JitterPhases;       // jitter cycle length; sizes the accumulation trust window
        public Vector4 DepthRejectParams; // keyed to history format (fp16 sharp / RGBA8 blunted)

        /// <summary>
        /// All inputs present, velocity on the color grid (same rendered frame's MRT pair), and
        /// history sized 1:1 to the surface this resolve writes. Transient mismatches (e.g. the
        /// frame after a scale change) fail this and fall through un-resolved.
        /// </summary>
        public bool Ready
        {
            get
            {
                var prev = History?.Prev;
                if (Color == null || Velocity == null || prev == null
                    || History.Curr == null || History.MetaPrev == null || History.MetaCurr == null) return false;
                if (Velocity.Width != Color.Width || Velocity.Height != Color.Height) return false;
                return Upscale
                    ? (prev.Width == OutputWidth && prev.Height == OutputHeight)
                    : (prev.Width == Color.Width && prev.Height == Color.Height);
            }
        }
    }
}
