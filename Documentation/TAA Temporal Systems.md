# TAA — how OpenSO smooths the picture

OpenSO offers a custom **temporal anti-aliasing (TAA)** implementation: instead of smoothing each
frame on its own, the renderer nudges the camera by a fraction of a pixel every frame and blends
the last several frames together. Edges stop looking jagged, thin details stop shimmering, and it costs far less than
rendering at a higher resolution. The same machinery also powers **TAA upscaling (TAAU)**: the game
can render at a lower resolution and reconstruct a sharp native-resolution image, with a mild
auto-sharpen pass applied on top to compensate for the softness blending introduces.

There are two quality tiers, selectable in graphics settings:

| Tier | What it is |
|---|---|
| **Full** | The complete algorithm — best image quality. |
| **Lite** | A cheaper version for slower GPUs — fewer texture reads per pixel, slightly rawer image. |

## How the code is organized

Blending previous frames is only safe if the renderer is careful about *which* frames it blends —
stale history shows up as ghosting and smearing. Two small classes own that problem:

| File | Role |
|---|---|
| `tso.common/Utils/TemporalFrameContract.cs` | Gathers everything the TAA shader needs for one frame (images, motion data, jitter, settings) into a single validated bundle. New shader inputs are added here, never read ad-hoc. |
| `tso.common/Utils/TemporalHistoryState.cs` | Remembers the blended history between frames and knows when it must be thrown away — quality-tier switches, resolution or upscale changes, moving between a lot and the city, skipped frames. Every reset is deliberate and logged, which is what keeps mode switches ghost-free. |
| `tso.world/Utils/TAAResolve.cs` | Drives the shader each frame: builds the contract, uploads tuning values, runs the resolve. |
| `tso.world/Utils/TAATuning.cs` | The single source of truth for every tunable value. |
| `tso.content/ContentSrc/Effects/TAA.fx` | The shader itself (both tiers). |

## How motion blur connects

Motion blur has two modes. **Camera** blur is a simple 2D effect derived from camera movement and
has nothing to do with the temporal systems. **Per-pixel** blur shares their plumbing: it reads the
same per-frame motion-vector ("velocity") buffer that TAA uses to track where each pixel moved.
The engine renders that buffer whenever either effect asks for it, so the two can run together or
independently — TAA never depends on the motion-blur effect itself.

## Tuning

All tuning constants live in `TAATuning.cs` and are uploaded to the shader automatically each
frame. At startup the game audits the loaded shader and reports any uniform that is missing or
whose baked-in default drifted from `TAATuning.cs`, so the two can't silently disagree. Adding a
tunable is a two-file change: a static float in `TAATuning.cs` plus a matching uniform in `TAA.fx`.

Note: shader binaries (`.xnb`) are compiled **only in CI** (Windows job,
`.github/scripts/compile-shaders.sh`). After editing `TAA.fx`, the committed binary is stale until
CI rebuilds it — the runtime binding audit will flag exactly this state.

## Experimenting

[`TSOClient/FSO.TAALab`](../TSOClient/FSO.TAALab/README.md) is an **experimental, Windows-only**
harness for playing with TAA tuning against a synthetic scene, including an automatic tuner. It is
a sandbox, not a production reference: changes to the real renderer are validated in the game
itself.
