# Sprite → 3D Mesh Reconstruction Overhaul — Technical Plan

> **Update (Phase 1 implemented).** Key correction discovered during implementation: the mesh
> simplifier **already** auto-detects and pins topological boundary vertices at iteration 0
> ([Simplify.cs:291-331](../TSOClient/tso.common/MeshSimplify/Simplify.cs#L291)), overwriting any
> `border` flag set at the call site. So "set the border flag" was a no-op idea — boundaries are
> already pinned. That reframes the real problem: the plant's **scattered triangles are isolated
> AA-fringe specks**, each forming its own tiny boundary loop the simplifier then *pins and refuses to
> remove*; the mailbox **melts because the input depth is noisy/terraced**. The fix is therefore to
> **clean the input** (condition depth + remove specks/tiny islands) rather than touch decimation.
> Alpha-gating was dropped (sprite `PixelData` is nulled after GPU upload, so it isn't reliably
> available during reconstruction); morphological speck + connected-component removal achieves the
> same de-scatter using only the always-available depth buffer. Skirts deferred.

**Outcome (2026-06-24):** Shipping the **per-view path** (reconstruct v14). Phase 1 conditioning + two
later wins — gentler decimation (aggressiveness 2.5, Quality 2) for thin parts, and cross-relief normal
welding (`SmoothNormalsAcrossGeoms`) for consistent shading on surfaces seen by multiple rotations.
**Phase 2 volumetric fusion is shelved** behind `DGRPRCParams.Fusion` (default off): it produced smoother
silhouettes but had persistent top gaps that couldn't be resolved without intermediate-state inspection.
Code retained in `DGRP3DFusion.cs` / `MarchingTets.cs` for a future, instrumented attempt.

**Status:** Phase 1 implemented and verified in-game (2026-06-24). Run with `FreeSO.exe w 1280x720 -3d`.

> **Revision v4 (hole/seam regression fix).** The first cut conditioned the depth *mask* by removing
> pixels — speck removal + connected-component removal split at depth discontinuities. On curved/stepped
> solids that fragmented surfaces and deleted small fragments, opening **holes and seams** (most visible
> off the bake angle, since these are 2.5D single-view shells). Fix: **stop removing mask pixels.** Depth
> conditioning is now a *monotone* bilateral filter (output is always a weighted average of neighbours,
> so it can't push a vertex out far enough to break the mesher's edge test and open a hole). Scatter
> removal moved to **after triangulation**: cull small *disconnected triangle islands* (`CullSmallComponents`
> in DGRP3DMesh) — a solid is one island and stays whole; only floating fringe bits go. `CURRENT_RECONSTRUCT`
> bumped 3→4. Verified: mailbox/post/trash-can solid and hole-free head-on; plant body stays coherent.
> NOTE: residual off-axis thinness is inherent to the single-view 2.5D shell and is a Phase-2 (fusion) /
> skirt concern, not fixed here.

Remaining: tune `DepthDiscontinuity`/`Quality` and the cull thresholds per object class, A/B old-build
triangle-count deltas, evaluate skirts, then Phase 2 fusion.
**Scope (Phase 1):** Rewrite the per-view depth-map → mesh pipeline for better visual quality and triangle efficiency, with no change to texturing, dynamic-sprite toggling, or the `.fsom` cache format.
**Scope (Phase 2, later, gated):** Prototype volumetric multi-view fusion (TSDF + marching cubes) for static objects only.

---

## 1. Goals & hard constraints

**Goals**
- Eliminate the two dominant artifacts: *melted blobs* (e.g. mailbox) and *scattered/disjoint triangles* (e.g. plant).
- Remove depth *terracing* (8-bit quantization stair-steps).
- Produce **fewer, better-placed** triangles (flat areas cheap, silhouettes/detail preserved).
- Better normals → better lighting.

**Constraints that shape the design** (discovered in code, must not break)
1. Geometry is stored as `Geoms[dynamicSpriteId][texture]` — see [DGRP3DMesh.cs:397-404](../TSOClient/tso.files/RC/DGRP3DMesh.cs#L397). Each `DGRP3DGeometry` records its `(PixelDir, PixelSPR)` so UVs map 1:1 onto the **original sprite**. → Reconstruction must stay **per-sprite, per-rotation**.
2. **Dynamic sprites toggle per `dynid` group** (lights on/off, trash full/empty). → We may not fuse geometry across sprites/views in Phase 1.
3. Cache invalidation already exists: `ReconstructVersion < CURRENT_RECONSTRUCT` throws "Reconstruction outdated" and regenerates ([DGRP3DMesh.cs:280](../TSOClient/tso.files/RC/DGRP3DMesh.cs#L280)). → Bump the constant; old caches auto-rebuild.
4. `DGRPRCParams` is serialized in the `FSOR` chunk with a version int ([DGRPRCParams.cs:22](../TSOClient/tso.files/RC/DGRPRCParams.cs#L22)). → New params need version-guarded reads.

---

## 2. Current pipeline (recap of what we're replacing)

In the `QueueWork` block of [DGRP3DMesh.cs:435-720](../TSOClient/tso.files/RC/DGRP3DMesh.cs#L435):

1. **Depth → verts:** each opaque pixel (`d < 0.999`) → one vertex at `vpos + (1-d)*dFactor`. Raw 8-bit depth.
2. **Triangulate:** 2×2 pixel quads → 1–2 tris, dropped if any diagonal/edge exceeds a single global `MaxAllowedSq = 0.065²`.
3. **Decimate:** FastQuadric to `tris/100`, `border` flag **never set**.
4. **Normals:** flat accumulate, averaged across everything.

### Failure modes → fixes (the whole plan in one table)

| Artifact (screenshot) | Root cause | Fix (phase) |
|---|---|---|
| Terraced surfaces | raw 8-bit depth, 256 levels | **A.** edge-aware denoise + dequantize |
| Scattered tris (plant) | global break-threshold leaves holes; AA-fringe pixels meshed | **A.** alpha-gated validity + **B.** adaptive discontinuity test |
| Melted blobs (mailbox) | FastQuadric collapses silhouettes (no border pins) over noisy input | **A.** clean input + **C.** border-pinned, feature-preserving decimation |
| Paper-thin edges / bg bleed on rotate | single 2.5D shell, zero thickness | **B.** optional silhouette skirts |
| Flat lighting / wrong shading | normals from noisy geo, averaged across edges | **C.** area-weighted, discontinuity-split normals |

---

## 3. New pipeline (Phase 1)

Three CPU stages, all inside the existing worker-thread `QueueWork` closure (no GPU round-trip per sprite — unlike the old disabled `DequantizeDepth` path which did `GameThread.InUpdate` + `WaitOne` per sprite).

### Stage A — Depth conditioning

**A1. Validity mask.** A pixel is valid iff `depth < 0.999` **and** `spriteAlpha > threshold` (~0.5). Today only the depth sentinel is used, so anti-aliased fringe pixels (alpha≈0 but with a depth value) become stray vertices — a primary cause of the plant's scatter.
- *Dependency:* add a CPU alpha accessor on `DGRPSprite` analogous to `GetDepth()` ([DGRP.cs:272](../TSOClient/tso.files/Formats/IFF/Chunks/DGRP.cs#L272)), reading the SPR2 frame's pixel data without a GPU read-back.

**A2. Edge-aware denoise + dequantize.** For each valid pixel, fit a local plane to valid neighbors in a small window (5×5), **range-gated** so neighbors across a depth discontinuity are excluded (bilateral weight on depth similarity). Evaluate the plane at the center.
- This simultaneously (a) removes 8-bit terracing by reconstructing the sub-quantization slope and (b) denoises, while (c) **preserving** true steps because gated neighbors don't pull across them.
- This is the CPU equivalent of the existing `derivativeDepth`/`dequantizeDepth` shader ([SpriteEffects.fx:636-671](../TSOClient/tso.content/ContentSrc/Effects/SpriteEffects.fx#L636)) — same idea (local slope estimate + correction), moved into the worker for throughput and to drop the per-sprite GPU stall.
- New file: `tso.files/RC/Utils/DepthConditioner.cs` (pure CPU, testable in isolation).

### Stage B — Discontinuity-aware triangulation

**B1. Adaptive break test.** Replace the single world-space `MaxAllowedSq` with a test relative to the **local surface slope**: an edge between neighbors p,q is continuous iff `|depth(p) − depth(q)| ≤ k · expectedStep`, where `expectedStep` comes from the conditioned gradient field. Steep-but-real surfaces survive; only genuine silhouette jumps break. Keep `MaxAllowedSq` as a hard fallback cap.

**B2. Boundary classification.** Any vertex touching a broken edge (or the validity-mask boundary) is flagged `border = true`. Used by both B3 and Stage C.

**B3. Silhouette skirts (optional, param-gated, default off initially).** Along boundary loops, extrude border vertices backward (−`dFactor` direction) by a small capped amount and stitch a quad strip, giving the shell thickness so small rotations don't reveal a paper edge or background bleed. Evaluate visually before enabling by default; risk is overdraw and back-face artifacts.

### Stage C — Feature-preserving decimation + normals

**C1. Border-pinned FastQuadric.** Set `MSVertex.border = true` on boundary verts ([MSVertex.cs:11](../TSOClient/tso.common/MeshSimplify/MSVertex.cs#L11)) before `simplify_mesh`. The simplifier already refuses collapses across mismatched border flags ([Simplify.cs:84](../TSOClient/tso.common/MeshSimplify/Simplify.cs#L84)) — currently dormant because the flag is never set. This is the single biggest anti-blob lever: silhouettes stop melting.

**C2. Curvature-aware target.** Scale the triangle target by mesh complexity (flat objects decimate harder, detailed ones keep more) instead of a flat `/100`. Exposed as a `Quality` knob.

**C3. Normals.** Area-weighted accumulation (cross-product magnitude already encodes area), with smoothing **split at borders** so silhouette edges stay crisp instead of being averaged into neighbors.

---

## 4. Files touched / added (as built)

| File | Change |
|---|---|
| `tso.files/RC/Utils/DepthConditioner.cs` | **new** — Stage A: validity mask, speck removal, tiny connected-component removal (split at depth steps), edge-aware plane-fit dequantize/denoise. CPU only, no GPU round-trip |
| `tso.files/RC/DGRP3DMesh.cs` | replaced the disabled GPU-dequant branch with `DepthConditioner.Process`; vertex loop now uses the conditioned depth + validity mask; `Quality` scales the decimation target; bumped `CURRENT_RECONSTRUCT` 2→3 |
| `tso.files/RC/DGRPRCParams.cs` | added `DepthConditioning`, `DepthFilterStrength`, `DepthDiscontinuity`, `Quality` + version-guarded (de)serialization |
| `tso.files/Formats/IFF/Chunks/FSOR.cs` | bumped `CURRENT_VERSION` 1→2 |

**Net:** 1 new file + edits to 3. No `MeshReconstructor` extraction (kept the change surgical/in-place), no `DGRP.cs` alpha accessor (speck/island removal replaced alpha-gating), no border-flag plumbing (simplifier already pins boundaries). No changes to `DGRP3DGeometry` storage, the `.fsom` format, the shader, the renderer, or the dynamic-sprite system. Triangulation kept its existing world-space discontinuity test (now reliable thanks to conditioning).

---

## 5. New tuning params (`DGRPRCParams`) — as built

```
bool   DepthConditioning  = true;   // Stage A master on/off
float  DepthFilterStrength = 1.0;   // dequantize plane-fit range tightness (higher = smoother)
float  DepthDiscontinuity  = 0.06;  // depth-space step (0..1) treated as a silhouette break
float  Quality             = 1.0;   // decimation target scale (higher keeps more triangles)
```
Serialization: `FSOR.CURRENT_VERSION` bumped 1→2; `DGRPRCParams(IoBuffer, version)` reads the new fields only when `version >= 2`, else defaults apply. Existing per-IFF overrides in `ParamsByIff` (doors/counters/etc.) keep working unchanged (they construct via the parameterless ctor, which now carries the new defaults). Skirts and the adaptive-triangulation `DiscontinuityFactor` were deferred.

---

## 6. Cache & rollout

- Bumping `CURRENT_RECONSTRUCT` makes every existing `MeshCache/*.fsom` report "outdated" and regenerate on first use — **expected one-time heavier load** after the update. `MeshReplace/` hand-authored meshes are unaffected (they're explicit overrides).
- No migration needed; regeneration is automatic and threaded.

---

## 7. Verification

Build (`FreeSO.exe`, Windows/MonoGame per the build-run memory), launch, and capture before/after screenshots of the three diagnostic objects from the reference shot:
- **Plant** — scatter gone, foliage coherent.
- **Mailbox** — no melt; box edges crisp.
- **Trash can** — lid artifacts reduced; round silhouette clean.
Also spot-check a **counter** (CounterFix path) and a **door/window** (DoorFix/rotation-limited) to confirm special cases still work. Compare triangle counts (efficiency goal) and watch first-load regeneration time.

---

## 8. Risks & mitigations

| Risk | Mitigation |
|---|---|
| Thin features (plant leaves) lost by smoothing | range-gated filter preserves steps; alpha mask keeps thin opaque pixels; tune `DepthFilterStrength` |
| Border pinning leaves too many tris | balance with `Quality`; pins are on *boundary* loops only, interiors still decimate |
| Skirts cause overdraw/back-face artifacts | default off; opt-in per-IFF after visual check |
| Params version bump breaks FSOR read | guarded reads + default fallback; unit-cover the ctor |
| Worker-thread CPU cost up | offset by removing per-sprite GPU stall; result is cached to disk once |

---

## 9. Phase 2 (future, gated) — volumetric fusion sketch

For **static** objects (no dynamic sprites, no animation): back-project all 4 rotation depth maps into a shared voxel grid, integrate a TSDF, extract one watertight mesh via marching cubes, then bake a texture atlas by projecting source sprites. Gated behind a per-IFF flag with the Phase-1 result as fallback. Deferred because it breaks per-sprite UVs and dynamic-sprite toggling — only worth it where those don't apply. Decide after Phase 1 lands and we can A/B real objects.

---

## 10. Sequencing checklist (Phase 1)

1. `DGRPSprite` alpha accessor (CPU).
2. `DepthConditioner` (Stage A) + isolated test on a known sprite.
3. `MeshReconstructor` (Stages B+C); wire into `DGRP3DMesh`, bump reconstruct version.
4. Params + guarded serialization.
5. Build, regenerate cache, screenshot the diagnostic objects, tune `DiscontinuityFactor`/`Quality`.
6. Evaluate skirts on/off; set sensible per-IFF defaults.
