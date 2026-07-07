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
    /// TAALite is untouched by all of these (it keeps its own literals in TAA.fx).
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
        public static float RespEnd = 0.60f;
        // historyWeight = min(historyWeight, lerp(1, MotionTrustCap, moveGate * upscale fade))
        public static float MotionTrustCap = 0.72f;
        // gammaEff *= lerp(1, MotionClampTighten, moveGate * 0.8 * upscale fade)
        public static float MotionClampTighten = 0.72f;
        // rawSoften = saturate((blend - Onset) * Slope) * (1 - moveGate * MotionSup)
        public static float RawSoftenOnset = 0.12f;
        public static float RawSoftenSlope = 2.2f;
        public static float RawSoftenMotionSup = 0.85f;
        // Variance clamp base width in sigma (TAA_Core's GAMMA; TAALite keeps its own 1.5 literal)
        public static float Gamma = 1.5f;
        // blend = max(blend, texDetail * TexDetailFloor * (1 - oscLock))
        public static float TexDetailFloor = 0.28f;
        // confFloor = lerp(ConfFloor, 0.08, saturate(upscaleRatio - 2)) * coverage
        public static float ConfFloor = 0.14f;
        // ringContam = foreign * smoothstep(RingLo, RingHi, |histOwn - historyRaw|)
        public static float RingLo = 0.03f;
        public static float RingHi = 0.10f;
    }
}
