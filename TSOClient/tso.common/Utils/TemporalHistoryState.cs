using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FSO.Common.Utils
{
    /// <summary>
    /// The temporal resolve's persistent state: the history/meta ping-pong targets plus an explicit
    /// record of the LAYOUT and FRAME that state was written with. Extracted from PPXDepthEngine
    /// statics so that every way history can become stale is handled in ONE place, as data, instead
    /// of relying on the shader's rejection heuristics to eat incompatible bytes.
    ///
    /// Layout semantics differ per resolve tier: TAA_Core writes meta = (N, dilated velocity encode,
    /// oscillation pack), TAADebug repurposes GB+A for diagnostics, and TAALite keeps its own lighter
    /// meta contract (see TAA.fx). The lot world and the 3D city view also share these targets while
    /// rendering entirely different scenes. Both were previously "handled" implicitly: a tier/debug/
    /// owner switch fed the previous mode's bytes into the new mode's decode for at least one frame.
    ///
    /// The lifecycle is TRANSACTIONAL:
    ///   BeginResolve(contract) — declares what the resolve is ABOUT to write, verifies it against
    ///     what history was last COMMITTED with (layout signature + frame continuity), and clears
    ///     first on any incompatibility. Does NOT mark contents valid.
    ///   Commit(frameId)        — after the draw was actually issued: records the declared layout +
    ///     frame id as the committed state and swaps the ping-pong.
    ///   (no Commit)            — a resolve that begins but never commits (missing technique, early
    ///     exit) leaves the committed record describing what Prev genuinely holds; the next frame's
    ///     continuity check (FrameId == committed + 1) then clears, because the scene advanced past
    ///     the history without the resolve seeing it.
    /// </summary>
    public class TemporalHistoryState
    {
        // resolve tiers whose history/meta encodings are mutually incompatible
        public enum ResolveTier : byte
        {
            Full = 0,  // TAA_Core: meta = N / velocity encode / oscillation pack
            Lite = 1,  // TAALite: lighter meta contract
            Debug = 2, // TAADebug: meta GB+A repurposed for diagnostics
        }

        private RenderTarget2D HistoryA, HistoryB; // RGB color, A packed depth
        private RenderTarget2D MetaA, MetaB;
        // Aux state (RGBA8): R = recent-nearest depth (decayed-min occluder memory — multi-frame
        // disocclusion evidence instead of the one-frame history-alpha window), GBA = the last three
        // frames of input luma (period-2..4 flicker recurrence detection, FSR2.2's luma instability).
        private RenderTarget2D AuxA, AuxB;
        private bool _AIsPrev;
        public bool IsFP16 { get; private set; }

        // Everything the current history contents were COMMITTED with. Only Commit writes these, so
        // an aborted resolve can't leave them describing bytes that were never produced.
        private object _Owner;          // which presenter wrote it (lot World instance / city token)
        private ResolveTier _Tier;
        private bool _Upscale;
        private int _InputW, _InputH;   // color/velocity grid consumed (render-res under TAAU: changes
                                        // on a render-scale switch even though history stays native)
        private int _Phases;            // jitter cycle length the accumulation was built under
        private int _Version;           // TAAResolve.RESOLVE_VERSION at commit
        private long _HistoryFrameId;   // PPXDepthEngine.FrameId of the frame that produced Prev
        private bool _HasContents;      // false until the first Commit after alloc/clear

        // the layout declared by the in-flight BeginResolve, promoted to committed by Commit
        private ResolveTier _PendingTier;
        private bool _PendingUpscale;
        private int _PendingInputW, _PendingInputH, _PendingPhases, _PendingVersion;

        private bool _PendingInvalidate;
        public string LastResetReason { get; private set; } = "initial";

        /// <summary>
        /// Bumped on every Clear. The jitter drivers (World.PreDraw, city Terrain) restart their
        /// Halton phase index when they observe a new serial, so fresh history always accumulates
        /// from phase 0 and a scale change can't leave the index mid-way through the OLD cycle.
        /// </summary>
        public int ResetSerial { get; private set; }

        // meta clear = N=0, prev-velocity ~zero (0.5 bias encode), no oscillation evidence
        private static readonly Color MetaClear = new Color(0, 127, 127, 0);

        public RenderTarget2D Prev => _AIsPrev ? HistoryA : HistoryB;
        public RenderTarget2D Curr => _AIsPrev ? HistoryB : HistoryA;
        public RenderTarget2D MetaPrev => _AIsPrev ? MetaA : MetaB;
        public RenderTarget2D MetaCurr => _AIsPrev ? MetaB : MetaA;
        public RenderTarget2D AuxPrev => _AIsPrev ? AuxA : AuxB;
        public RenderTarget2D AuxCurr => _AIsPrev ? AuxB : AuxA;
        private void Swap() { _AIsPrev = !_AIsPrev; }

        /// <summary>
        /// Request a history reset without needing the GraphicsDevice: the clear runs at the next
        /// BeginResolve. Call for any event that makes reprojection meaningless (camera cut/teleport,
        /// world switch) or any state change the layout signature can't see.
        /// </summary>
        public void Invalidate(string reason)
        {
            if (_PendingInvalidate) return;
            _PendingInvalidate = true;
            LastResetReason = reason;
        }

        /// <summary>
        /// The presenter that will feed the resolve (lot World instance, or the city's static token).
        /// An owner change means the scene content changed wholesale — reprojection would ghost the
        /// previous presenter's image into the new one.
        /// </summary>
        public void DeclareOwner(object owner)
        {
            if (_Owner == owner) return;
            _Owner = owner;
            if (_HasContents) Invalidate("presenter switch (lot/city)");
        }

        /// <summary>
        /// Called by the resolve each frame BEFORE binding targets, declaring (via the frame
        /// contract) the layout it is about to write and the frame its inputs belong to. Verifies
        /// against the COMMITTED state and performs any pending or mismatch clear. Returns true if
        /// history was reset (the resolve runs anyway — a cleared history reads as the ordinary
        /// first frame: meta N=0 pins blend to current, exactly the existing warmup path).
        /// Contents stay unclaimed until Commit.
        /// </summary>
        public bool BeginResolve(GraphicsDevice gd, in TemporalFrameContract c)
        {
            if (HistoryA == null) return false;
            if (_HasContents)
            {
                if (c.Tier != _Tier || c.Upscale != _Upscale)
                    Invalidate($"layout change ({_Tier}{(_Upscale ? "+U" : "")} -> {c.Tier}{(c.Upscale ? "+U" : "")})");
                else if (c.Color.Width != _InputW || c.Color.Height != _InputH || (int)c.JitterPhases != _Phases)
                    // the TAAU render-scale case: history stays native-sized, but the input grid,
                    // reconstruction footprint and jitter cycle all changed underneath it
                    Invalidate($"input grid change ({_InputW}x{_InputH}/{_Phases}ph -> {c.Color.Width}x{c.Color.Height}/{(int)c.JitterPhases}ph)");
                else if (c.ResolveVersion != _Version)
                    Invalidate($"resolve version ({_Version} -> {c.ResolveVersion})");
                else if (c.FrameId != _HistoryFrameId + 1)
                    // frames were presented without a committed resolve (fade/zoom transition,
                    // fall-through, aborted draw): one-frame motion can't reproject across the gap
                    Invalidate($"frame gap (history frame {_HistoryFrameId} -> current {c.FrameId})");
            }
            _PendingTier = c.Tier;
            _PendingUpscale = c.Upscale;
            _PendingInputW = c.Color.Width;
            _PendingInputH = c.Color.Height;
            _PendingPhases = (int)c.JitterPhases;
            _PendingVersion = c.ResolveVersion;
            bool reset = _PendingInvalidate;
            if (reset) Clear(gd);
            return reset;
        }

        /// <summary>
        /// Called by the resolve AFTER the draw was issued: promotes the declared layout to the
        /// committed record, stamps the frame id, marks contents valid, and rotates the ping-pong.
        /// Every path that skips this leaves the committed state untouched — the frame-gap check in
        /// BeginResolve then handles the miss.
        /// </summary>
        public void Commit(long frameId)
        {
            _Tier = _PendingTier;
            _Upscale = _PendingUpscale;
            _InputW = _PendingInputW;
            _InputH = _PendingInputH;
            _Phases = _PendingPhases;
            _Version = _PendingVersion;
            _HistoryFrameId = frameId;
            _HasContents = true;
            Swap();
        }

        private void Clear(GraphicsDevice gd)
        {
            _PendingInvalidate = false;
            _HasContents = false;
            ResetSerial++;
            if (HistoryA == null || gd == null) return;
            var prevRTs = gd.GetRenderTargets();
            gd.SetRenderTarget(HistoryA); gd.Clear(Color.Transparent);
            gd.SetRenderTarget(HistoryB); gd.Clear(Color.Transparent);
            gd.SetRenderTarget(MetaA); gd.Clear(MetaClear);
            gd.SetRenderTarget(MetaB); gd.Clear(MetaClear);
            // Aux clear: occluder memory = far (no remembered occluder), luma ring = 0 (matches are
            // additionally amplitude-gated in the resolve, so a zeroed ring cannot fake recurrence).
            var auxClear = new Color(255, 0, 0, 0);
            if (AuxA != null) { gd.SetRenderTarget(AuxA); gd.Clear(auxClear); }
            if (AuxB != null) { gd.SetRenderTarget(AuxB); gd.Clear(auxClear); }
            if (prevRTs != null && prevRTs.Length > 0) gd.SetRenderTargets(prevRTs); else gd.SetRenderTarget(null);
            System.Diagnostics.Debug.WriteLine($"[TAA] history reset: {LastResetReason}");
        }

        /// <summary>
        /// Allocate (or free) the ping-pong at the given size. Prefers fp16 history (RGBA8 quantized
        /// the packed depth -> false disocclusions, and stalled color accumulation at one 8-bit LSB),
        /// falling back to Color where HalfVector4 isn't renderable; TAAResolve keys the blunted
        /// depth-reject tuning off IsFP16 on that path. A (re)allocation always clears, so a size or
        /// format change can never leave committed contents behind.
        /// </summary>
        public void Ensure(GraphicsDevice gd, bool enable, int w, int h)
        {
            if (!enable)
            {
                if (HistoryA != null) { HistoryA.Dispose(); HistoryA = null; }
                if (HistoryB != null) { HistoryB.Dispose(); HistoryB = null; }
                if (MetaA != null) { MetaA.Dispose(); MetaA = null; }
                if (MetaB != null) { MetaB.Dispose(); MetaB = null; }
                if (AuxA != null) { AuxA.Dispose(); AuxA = null; }
                if (AuxB != null) { AuxB.Dispose(); AuxB = null; }
                _HasContents = false;
                return;
            }
            if (gd == null) return;
            if (HistoryA != null && HistoryA.Width == w && HistoryA.Height == h) return;
            HistoryA?.Dispose();
            HistoryB?.Dispose();
            MetaA?.Dispose();
            MetaB?.Dispose();
            AuxA?.Dispose();
            AuxB?.Dispose();
            // Aux shares the history's format choice: the occluder-memory depth needs sub-LSB precision
            // an RGBA8 channel doesn't have (1/255 of the depth range is the size of a typical
            // mover-to-floor gap); on the Color fallback the memory just gets coarser, and the resolve's
            // color-proportional shields absorb the extra quantization noise.
            try
            {
                HistoryA = new RenderTarget2D(gd, w, h, false, SurfaceFormat.HalfVector4, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
                HistoryB = new RenderTarget2D(gd, w, h, false, SurfaceFormat.HalfVector4, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
                AuxA = new RenderTarget2D(gd, w, h, false, SurfaceFormat.HalfVector4, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
                AuxB = new RenderTarget2D(gd, w, h, false, SurfaceFormat.HalfVector4, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
                IsFP16 = true;
            }
            catch
            {
                HistoryA?.Dispose(); HistoryB?.Dispose();
                AuxA?.Dispose(); AuxB?.Dispose();
                HistoryA = new RenderTarget2D(gd, w, h, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
                HistoryB = new RenderTarget2D(gd, w, h, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
                AuxA = new RenderTarget2D(gd, w, h, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
                AuxB = new RenderTarget2D(gd, w, h, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
                IsFP16 = false;
            }
            MetaA = new RenderTarget2D(gd, w, h, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            MetaB = new RenderTarget2D(gd, w, h, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            _AIsPrev = true;
            LastResetReason = "history (re)allocated";
            Clear(gd); // defined contents so the first TAA frame can't read garbage
        }
    }
}
