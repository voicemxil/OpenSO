namespace FSO.LotView.Utils
{
    /// <summary>
    /// THE canonical TAA tuning preset — the single source of truth for every promoted resolve constant
    /// (see the "LIVE-TUNING UNIFORMS" block in TAA.fx). Three consumers, none of which may hold their
    /// own copy of a value:
    ///  * The game: TAAResolve.UploadTunables uploads every public static float here each frame via
    ///    reflection ("X" -> uniform "TuneX"; "LiteX" -> "LiteX"), and audits the loaded effect once —
    ///    a missing uniform or a baked shader default that differs from this file is reported instead
    ///    of silently retuning.
    ///  * The lab: FSO.TAALab source-links this file (no tso.* project reference) and initializes its
    ///    live Tunables + reset table from these statics, so lab sessions always start from SHIPPED
    ///    values. Once a set is validated in the lab, paste it here (press P there for a ready block).
    ///  * The shader: TAA.fx float initializers are FALLBACK ONLY (a stale xnb / skipped upload) and
    ///    must be kept equal to this file; the runtime audit above flags any drift.
    /// TAALite's own tunables live at the bottom (Lite* — promoted 2026-07-07); the Tune* set above
    /// is TAA_Core-only.
    /// </summary>
    public static class TAATuning
    {
        // motionBoost = saturate(vmag * 20) * MotionBoostMax * lerp(MotionBoostFloor, 1, suspicion)
        // 2026-07-10: auto-tuned (15-param, gamma-fixed, 8K box ref, strict artifact metric).
        public static float MotionBoostFloor = 0.5174f;
        public static float MotionBoostMax = 0.4307f;
        // stillGate = 1 - smoothstep(0.8, 2.0, velPx * lerp(StillGateFloor, 1, suspicion))
        public static float StillGateFloor = 0.2873f;
        // moveGate = smoothstep(MoveGateLo, MoveGateHi, velPx)  (native px/frame)
        public static float MoveGateLo = 0.0237f;
        public static float MoveGateHi = 0.6035f;
        // historyWeight = lerp(deepEnd, RespEnd, diff) — the full-diff responsive end (TAA_Core only)
        public static float RespEnd = 0.5523f;
        // historyWeight = min(historyWeight, lerp(1, MotionTrustCap, moveGate * upscale fade))
        // 0.72 -> 0.65 (2026-07-05 final round, paired with the honest-disocclusion knee widening to
        // 0.45..0.85): moving pixels refresh ~35%/frame so mover-edge trails die in 2-3 frames, shown
        // through the crisp Lanczos reconstruction (user law: edges under motion must read as
        // reconstruction, not ghost).
        public static float MotionTrustCap = 0.77f; // 2026-07-10 auto-tuned (was 0.75)
        // gammaEff *= lerp(1, MotionClampTighten, moveGate * 0.8) — ALL scales since 2026-07-10
        // (reference alignment: FSR/Intel tighten under motion at native too)
        public static float MotionClampTighten = 0.9816f; // 2026-07-10 auto-tuned (near-1: barely tightens — the strict metric prefers keeping detail under motion)
        // GAMMA = lerp(GammaNative, GammaUpscale, sat((ratio-1)*0.5) * (1-suspicion) * (1-motion)) —
        // a FIXED REFERENCE SCHEDULE (2026-07-10), NOT auto-tuned. Native rest width -> heavy-upscale
        // rest width, the way FSR/TSR do it (FSR3 fixes rest widening at 3.0σ, AABB-bounded). A free
        // base let the optimizer inflate the box to game the synthetic metric (climbed 1.5->1.85);
        // fixing it to the reference schedule takes gamma out of the tuner's hands. The clamp box is
        // also intersected with the true tap min/max AABB (FSR safety rail), widening collapses FULLY
        // with motion, and locks escape via lerp-to-raw-history instead of box inflation.
        // Removed with the 2026-07-10 change set: RawSoftenOnset/Slope/MotionSup (soften path deleted
        // — shipped off), TexDetailFloor (permanent raw-injection floor deleted — no reference
        // analogue, stipple suspect), KarisFade (inverse-luma weighting now always on, per references).
        public static float GammaNative = 1.5f;   // rest sigma at native (reference 1.0-1.5)
        public static float GammaUpscale = 3.0f;  // rest sigma at 0.33x (FSR3's rest widening, AABB-bounded)
        // confFloor = lerp(ConfFloor, 0.08, saturate(upscaleRatio - 2)) * coverage
        public static float ConfFloor = 0.5696f; // 2026-07-10 auto-tuned (up from 0.14: more current-frame injection at upscale — a sharpness/response push)
        // ringContam = foreign * smoothstep(RingLo, RingHi, |histOwn - historyRaw|)
        public static float RingLo = 0.1223f;
        public static float RingHi = 0.4471f;
        // ---- structural constants (2026-07-07 promotion — the full-vs-lite haze/ghost hunt) ----
        // history = lerp(rectified, directCl, DirectClampMix * max(moveGate, storedMove)) — motion direct-clamp share
        public static float DirectClampMix = 0.9809f; // 2026-07-10 auto-tuned (near-full direct clamp under motion)
        // blend *= lerp(1, confMul, saturate(minN / ConfFadeN)) — evidence depth arming the off-phase throttle
        public static float ConfFadeN = 21.8903f;
        // growK = agreeK * lerp(1, lerp(GrowOffPhase, 1, testify), saturate(prevN / 8)) — off-phase growth discount floor.
        // Keep >= 0.25: meta N is RGBA8-quantized (1 LSB ~ 0.5 N), so growth increments below ~0.25/frame
        // round to the same code point — values under the floor mean "no growth", not slower growth.
        public static float GrowOffPhase = 0.9416f; // 2026-07-10 auto-tuned (fast N growth — reaches deep trust quickly)
        // deepCap = lerp(DeepCapBase, cycleWindow, smoothstep(1.2, 1.8, upscaleRatio)) — deep-end memory cap
        public static float DeepCapBase = 0.9671f; // 2026-07-10 auto-tuned (down from 0.992: ~30-frame window vs ~125 — the strict metric cut over-smoothing)

        // ---- TAALite tunables (2026-07-07 promotion). Lite's "raw motion resolve" (the user's
        // Switch-2-DLSS-lite anchor) is the DESIGN CHARACTER — tune to taste, don't converge it toward
        // the full path. Defaults = the previously-shipped literals (bit-identical). ----
        public static float LiteGamma = 1.5f;        // variance box base width (sigma) at native
        public static float LiteGammaScale = 2.0f;   // resolution ramp target (1.5 -> 3.0 at ratio 3)
        public static float LiteDeepCap = 0.985f;    // counter deep-end trust cap
        public static float LiteRespEnd = 0.68f;     // full-diff responsive end
        public static float LiteMotionBoost = 0.35f; // speed-proportional current boost (the raw-motion lever)
        public static float LiteConfFloor = 0.14f;   // off-phase sample-confidence injection floor
        public static float LiteMoveGateLo = 0.6f;   // motion gate lower edge (native px/frame)
        public static float LiteMoveGateHi = 2.0f;   // motion gate upper edge
        public static float LiteHonestLo = 0.65f;    // honest-disocclusion raw knee, lower edge
        public static float LiteHonestHi = 0.98f;    // honest-disocclusion raw knee, upper edge
    }
}
