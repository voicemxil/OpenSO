using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FSO.Common.Utils
{
    /// <summary>
    /// Everything the temporal resolve consumes for one frame, gathered as one value with its
    /// coherence rules in one place (TAAResolve.BuildContract) instead of spread across
    /// PPXDepthEngine statics read at different times. The contract answers two questions the
    /// resolve used to answer implicitly:
    ///   1. Ready — are all inputs present and mutually consistent (history sized to the grid this
    ///      resolve writes)? If not, the frame passes through un-resolved rather than resolving
    ///      misaligned.
    ///   2. Layout (Tier + Upscale) — which history/meta encoding this resolve is about to write.
    ///      TemporalHistoryState compares it against what history currently holds and resets when
    ///      they're incompatible.
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

        public Vector2 JitterDeltaUV;    // per-frame jitter delta; cancels the jitter baked into velocity
        public Vector2 SampleJitterUV;   // un-jittered offset for the variance-box taps
        public float VelGatePxScale;     // render-px -> native-px scale for the motion gates
        public float BlendFactor;
        public float MaxAccum;           // must match the shader's meta.R decode
        public float JitterPhases;       // jitter cycle length; sizes the accumulation trust window
        public Vector4 DepthRejectParams; // keyed to history format (fp16 sharp / RGBA8 blunted)

        /// <summary>
        /// All inputs present and history sized 1:1 to the surface this resolve writes. Transient
        /// mismatches (e.g. the frame after a scale change) fail this and fall through un-resolved.
        /// </summary>
        public bool Ready
        {
            get
            {
                var prev = History?.Prev;
                if (Color == null || Velocity == null || prev == null
                    || History.Curr == null || History.MetaPrev == null || History.MetaCurr == null) return false;
                return Upscale
                    ? (prev.Width == OutputWidth && prev.Height == OutputHeight)
                    : (prev.Width == Color.Width && prev.Height == Color.Height);
            }
        }
    }
}
