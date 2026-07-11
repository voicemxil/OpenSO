namespace FSO.LotView.Utils
{
    /// <summary>
    /// SINGLE SOURCE OF TRUTH for the TAA_Core (full Cosmic TAA/TAAU resolve) tuning constants that were
    /// promoted from shader literals to uniforms (see the "LIVE-TUNING UNIFORMS" block in TAA.fx).
    /// TAAResolve.Draw uploads every one of these each frame via the null-safe Parameters[...]?.SetValue
    /// pattern, so an older TAA.xnb without the uniforms cannot crash (it just keeps its baked defaults,
    /// which are identical to these values). The FSO.TAALab harness keeps its own mutable copy of the same
    /// defaults for interactive tuning; once a set is validated in the lab, paste it here to ship it.
    /// The values below ARE the pre-promotion literals — shipping behavior is bit-identical.
    /// TAALite's own tunables live at the bottom (Lite* — promoted 2026-07-07); the Tune* set above
    /// is TAA_Core-only.
    /// </summary>
    public static class TAATuning
    {
        // motionBoost = saturate(vmag * 20) * MotionBoostMax * lerp(MotionBoostFloor, 1, suspicion)
        public static float MotionBoostFloor = 0.12f;
        public static float MotionBoostMax = 0.22f;
        // stillGate = 1 - smoothstep(0.8, 2.0, velPx * lerp(StillGateFloor, 1, suspicion))
        public static float StillGateFloor = 0.25f;
        // moveGate = smoothstep(MoveGateLo, MoveGateHi, velPx)  (native px/frame)
        public static float MoveGateLo = 0.6f;
        public static float MoveGateHi = 2.0f;
        // historyWeight = lerp(deepEnd, RespEnd, diff) — the full-diff responsive end (TAA_Core only)
        public static float RespEnd = 0.60f; // settled middle (0.55 was the raw-leaning notch; the direct clamp now carries the scrub)
        // historyWeight = min(historyWeight, lerp(1, MotionTrustCap, moveGate * upscale fade))
        // 0.72 -> 0.65 (2026-07-05 final round, paired with the honest-disocclusion knee widening to
        // 0.45..0.85): moving pixels refresh ~35%/frame so mover-edge trails die in 2-3 frames, shown
        // through the crisp Lanczos reconstruction (user law: edges under motion must read as
        // reconstruction, not ghost).
        public static float MotionTrustCap = 0.75f; // re-raised 0.58->0.75 (final direction: edges TREATED during motion, not raw — safe now that motion history is direct-clamped ghost-free; the deep-ish trust is what smooths the edge)
        // gammaEff *= lerp(1, MotionClampTighten, moveGate * 0.8 * upscale fade)
        public static float MotionClampTighten = 0.72f;
        // rawSoften = saturate((blend - Onset) * Slope) * (1 - moveGate * MotionSup)
        public static float RawSoftenOnset = 0.12f;
        public static float RawSoftenSlope = 0.0f; // A/B 2026-07-07: soften OFF — the haze on un-accumulated pixels WAS the filtSoft display swap (predates the Lanczos+hull reconstruction it compensated for; Lite displays reconstruction directly and reads crisp). If speckle returns on reveals, restore ~1.2 with Onset 0.3 (evidence-only band) instead of the old 2.2/0.12.
        public static float RawSoftenMotionSup = 0.65f; // settled middle after the raw(0.85)<->hazy(0.45) oscillation: moving raw-state pixels ~35% softened
        // Variance clamp base width in sigma (TAA_Core's GAMMA; TAALite keeps its own 1.5 literal)
        public static float Gamma = 1.5f;
        // blend = max(blend, texDetail * TexDetailFloor * (1 - oscLock))
        public static float TexDetailFloor = 0.28f;
        // confFloor = lerp(ConfFloor, 0.08, saturate(upscaleRatio - 2)) * coverage
        public static float ConfFloor = 0.14f;
        // ringContam = foreign * smoothstep(RingLo, RingHi, |histOwn - historyRaw|)
        public static float RingLo = 0.03f;
        public static float RingHi = 0.10f;
        // ---- structural constants (2026-07-07 promotion — the full-vs-lite haze/ghost hunt) ----
        // history = lerp(rectified, directCl, DirectClampMix * max(moveGate, storedMove)) — motion direct-clamp share
        public static float DirectClampMix = 0.75f;
        // lumaFade = max(moveGate, storedMove) * KarisFade — scales the Karis anti-flicker motion fade
        public static float KarisFade = 1.0f;
        // GAMMA widening *= (1 - GammaMotionDecay * gammaMotion) — wide-box narrowing strength in motion
        public static float GammaMotionDecay = 0.6f;
        // blend *= lerp(1, confMul, saturate(minN / ConfFadeN)) — evidence depth arming the off-phase throttle
        public static float ConfFadeN = 20.0f;
        // growK = agreeK * lerp(1, lerp(GrowOffPhase, 1, testify), saturate(prevN / 8)) — off-phase growth discount floor
        public static float GrowOffPhase = 0.3f;
        // deepCap = lerp(DeepCapBase, cycleWindow, smoothstep(1.2, 1.8, upscaleRatio)) — deep-end memory cap
        public static float DeepCapBase = 0.992f;
        // thin-line depth-ridge test: ridge fires when both opposite plus-tap depths exceed the center by
        // ThinLineEps * depth (TSR DetectThinGeometry ErrorMultiplier analogue)
        public static float ThinLineEps = 0.02f;

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
