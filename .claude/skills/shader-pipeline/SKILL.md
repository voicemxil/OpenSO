---
name: shader-pipeline
description: Editing MonoGame effect shaders (.fx) in OpenSO — the committed-.xnb drift trap, compiling for DX and OGL, and SM3 (ps_3_0) register limits. Use whenever a change touches TSOClient/tso.content/ContentSrc/Effects/*.fx, any Content/DX|OGL/Effects/*.xnb, TAA/TAAU, lighting, or any rendering change that needs a shader edit.
---

# Shader pipeline (OpenSO)

## The one fact that prevents most shader bugs

The game does NOT compile shaders at build time. It loads the **committed** compiled effects:

- `TSOClient/tso.content/Content/DX/Effects/*.xnb` — Windows client (DirectX)
- `TSOClient/tso.content/Content/OGL/Effects/*.xnb` — Linux/macOS clients (DesktopGL)

Source lives in `TSOClient/tso.content/ContentSrc/Effects/*.fx`. Editing a `.fx` changes **nothing**
at runtime until its `.xnb` is recompiled. This drift has shipped real bugs before (TAA changes that
never took effect; a Vitaboy .xnb missing a technique the C# indexed, crashing non-Windows clients).

CI is the safety net: on every push, `.github/workflows/dotnet.yml` job `compile-shaders` recompiles
ALL desktop effects fresh from source on a Windows runner (`.github/scripts/compile-shaders.sh`) and
overlays them into the published clients. So:

- **CI/release builds always match .fx source.** If your .fx change compiles in CI, it ships.
- **Committed .xnb only matter for local runs.** Still recompile+commit them when you can (needs
  Windows), and say so in the PR if you can't — CI covers the shipped artifacts either way.
- **Never hand-edit or copy stale .xnb files.**

## Compiling locally (Windows only)

MonoGame's effect compiler shells out to the HLSL compiler and needs Wine on Linux/macOS (CI runners
don't have it; the type initializer throws). On Windows:

```bash
cd TSOClient/tso.content
dotnet tool restore          # pins dotnet-mgcb 3.8.5-preview.2
bash ../../.github/scripts/compile-shaders.sh   # from git-bash; builds BOTH DX and OGL
```

The script excludes `*iOS*.fx` (iOS-only variants, not loadable on desktop) and `LightingCommon.fx`
(an `#include`, not a standalone effect — it has no technique). Follow the same rule if you add a
shared-include file: name it so the glob skips it, or it will break the batch compile.

## Both targets must compile — SM3 vs SM4

Effects compile under MonoGame's **Reach profile**. The OGL target is effectively **shader model
3.0** (ps_3_0/vs_3_0): hard limits on registers, instruction count, and no integer ops. The DX build
can pass while the OGL build fails with errors like **X4505 (maximum temp register index exceeded)**
— this exact failure happened with TAA resolve features.

Rules:
- Register/ALU-heavy features must be **gated to SM4**: follow the existing pattern in `TAA.fx` /
  `LightingCommon.fx` (preprocessor gates) and check `FeatureLevelTest.cs` (`tso.common/Utils/`) for
  the runtime capability check that selects techniques.
- After any .fx edit, the definition of "compiles" is: **both** `build DX Windows` and
  `build OGL DesktopGL` succeed. Locally run the script; otherwise wait for the CI
  `compile-shaders` job before trusting the change.
- New techniques indexed from C# must exist in **both** compiled sets, or non-Windows clients crash
  at load.

## Where the C# side lives

- Effect wrappers: `tso.world/Effects/` (e.g. `RCObjectEffect.cs`, `GrassEffect.cs`).
- TAA/TAAU resolve orchestration: `tso.world/Utils/TAAResolve.cs`, driven from `tso.world/World.cs`
  and `tso.common/Utils/PPXDepthEngine.cs`.
- Graphics options UI: `tso.client/UI/Panels/UIGraphicsOptionsDialog.cs`.
- Effect parameter names are matched by string at runtime — renaming a uniform in .fx requires
  updating every C# `Parameters["name"]` reference, and vice versa. Grep before renaming.

## Checklist for any shader change

1. Edit the `.fx` under `ContentSrc/Effects/` (never only the `.xnb`).
2. Keep new heavy code SM4-gated; verify the SM3 path still fits.
3. Compile both DX and OGL (locally on Windows, or via CI) — treat OGL failure as a full failure.
4. If C# references new techniques/parameters, update both sides together.
5. Commit recompiled `.xnb` for both platforms when built locally; otherwise note that CI overlays
   fresh shaders into shipped builds.
