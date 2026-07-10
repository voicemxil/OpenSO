using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FSO.Common.Utils
{
    /// <summary>
    /// The temporal resolve's persistent state: the history/meta ping-pong targets plus an explicit
    /// record of the LAYOUT that state was written with. Extracted from PPXDepthEngine statics so that
    /// every way history can become stale is handled in ONE place, as data, instead of relying on the
    /// shader's rejection heuristics to eat incompatible bytes.
    ///
    /// Layout semantics differ per resolve tier: TAA_Core writes meta = (N, dilated velocity encode,
    /// oscillation pack), TAADebug repurposes GB+A for diagnostics, and TAALite keeps its own lighter
    /// meta contract (see TAA.fx). The lot world and the 3D city view also share these targets while
    /// rendering entirely different scenes. Both were previously "handled" implicitly: a tier/debug/
    /// owner switch fed the previous mode's bytes into the new mode's decode for at least one frame.
    /// Now: TAAResolve declares its layout via BeginResolve each frame, and any mismatch with the
    /// stored signature clears history first (a reset reason is kept for diagnostics/logging).
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
        private bool _AIsPrev;
        public bool IsFP16 { get; private set; }

        // the layout the current history contents were written with
        private object _Owner;          // which presenter wrote it (lot World instance / city token)
        private ResolveTier _Tier;
        private bool _Upscale;
        private bool _HasContents;      // false until the first resolve after alloc/clear
        private bool _PendingInvalidate;
        public string LastResetReason { get; private set; } = "initial";

        // meta clear = N=0, prev-velocity ~zero (0.5 bias encode), no oscillation evidence
        private static readonly Color MetaClear = new Color(0, 127, 127, 0);

        public RenderTarget2D Prev => _AIsPrev ? HistoryA : HistoryB;
        public RenderTarget2D Curr => _AIsPrev ? HistoryB : HistoryA;
        public RenderTarget2D MetaPrev => _AIsPrev ? MetaA : MetaB;
        public RenderTarget2D MetaCurr => _AIsPrev ? MetaB : MetaA;
        public void Swap() { _AIsPrev = !_AIsPrev; }

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
        /// Called by the resolve each frame BEFORE binding targets, declaring the layout it is about
        /// to write. Performs any pending or signature-mismatch clear. Returns true if history was
        /// reset (the resolve runs anyway — a cleared history reads as the ordinary first frame:
        /// meta N=0 pins blend to current, exactly the existing warmup path).
        /// </summary>
        public bool BeginResolve(GraphicsDevice gd, ResolveTier tier, bool upscale)
        {
            if (HistoryA == null) return false;
            if (_HasContents && (tier != _Tier || upscale != _Upscale))
            {
                Invalidate($"layout change ({_Tier}{(_Upscale ? "+U" : "")} -> {tier}{(upscale ? "+U" : "")})");
            }
            _Tier = tier;
            _Upscale = upscale;
            bool reset = _PendingInvalidate;
            if (reset) Clear(gd);
            _HasContents = true;
            return reset;
        }

        private void Clear(GraphicsDevice gd)
        {
            _PendingInvalidate = false;
            _HasContents = false;
            if (HistoryA == null || gd == null) return;
            var prevRTs = gd.GetRenderTargets();
            gd.SetRenderTarget(HistoryA); gd.Clear(Color.Transparent);
            gd.SetRenderTarget(HistoryB); gd.Clear(Color.Transparent);
            gd.SetRenderTarget(MetaA); gd.Clear(MetaClear);
            gd.SetRenderTarget(MetaB); gd.Clear(MetaClear);
            if (prevRTs != null && prevRTs.Length > 0) gd.SetRenderTargets(prevRTs); else gd.SetRenderTarget(null);
            System.Diagnostics.Debug.WriteLine($"[TAA] history reset: {LastResetReason}");
        }

        /// <summary>
        /// Allocate (or free) the ping-pong at the given size. Prefers fp16 history (RGBA8 quantized
        /// the packed depth -> false disocclusions, and stalled color accumulation at one 8-bit LSB),
        /// falling back to Color where HalfVector4 isn't renderable; TAAResolve keys the blunted
        /// depth-reject tuning off IsFP16 on that path. A (re)allocation always clears.
        /// </summary>
        public void Ensure(GraphicsDevice gd, bool enable, int w, int h)
        {
            if (!enable)
            {
                if (HistoryA != null) { HistoryA.Dispose(); HistoryA = null; }
                if (HistoryB != null) { HistoryB.Dispose(); HistoryB = null; }
                if (MetaA != null) { MetaA.Dispose(); MetaA = null; }
                if (MetaB != null) { MetaB.Dispose(); MetaB = null; }
                _HasContents = false;
                return;
            }
            if (gd == null) return;
            if (HistoryA != null && HistoryA.Width == w && HistoryA.Height == h) return;
            HistoryA?.Dispose();
            HistoryB?.Dispose();
            MetaA?.Dispose();
            MetaB?.Dispose();
            try
            {
                HistoryA = new RenderTarget2D(gd, w, h, false, SurfaceFormat.HalfVector4, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
                HistoryB = new RenderTarget2D(gd, w, h, false, SurfaceFormat.HalfVector4, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
                IsFP16 = true;
            }
            catch
            {
                HistoryA?.Dispose(); HistoryB?.Dispose();
                HistoryA = new RenderTarget2D(gd, w, h, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
                HistoryB = new RenderTarget2D(gd, w, h, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
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
