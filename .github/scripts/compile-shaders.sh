#!/usr/bin/env bash
# Compile the desktop effect shaders (ContentSrc/Effects/*.fx) fresh from source into BOTH platform content
# dirs — Content/DX (Windows client) and Content/OGL (DesktopGL: linux/osx clients).
#
# Why this exists: shaders ship as committed .xnb and the in-build MonoGame content task is disabled, so a
# .fx edit does NOT rebuild its .xnb — the committed artifact silently drifts from source. That drift shipped
# real bugs (TAA source changes that never took effect; a Vitaboy .xnb missing a technique the C# indexed,
# crashing non-Windows clients). Recompiling in CI keeps shipped shaders matching source.
#
# MUST run on Windows: MonoGame's effect compiler shells out to Wine to run the HLSL compiler, and the
# Linux/macOS CI runners don't have Wine (its type initializer throws). Windows compiles BOTH the DX and the
# DesktopGL targets natively, so this one Windows job produces both; a downstream job hands the OGL result to
# the Linux/macOS client builds as an artifact.
#
# Excludes:
#   *iOS*.fx        — iOS-only variants (GLVer==2 path, iOS/Android only; desktop's WorldContent.EffectSuffix
#                     is "" so it always loads the non-iOS effects — confirmed, they're never loaded on
#                     desktop). Their .fx SOURCE stays for the iOS target (TSOClientContentiOS.mgcb) but they
#                     don't compile for the desktop profile (X5426), so this glob-based build must skip them.
#                     Their dead desktop .xnb + desktop .mgcb entries have been removed.
#   LightingCommon.fx — an #include, not a standalone effect (no technique); never in the .mgcb build lists.
set -euo pipefail

cd "$(dirname "$0")/../../TSOClient/tso.content"
dotnet tool restore

build() {  # <outdir> <platform>
  # Separate `local` statements: on one line (`local a=$1 b=${a}`) bash expands every argument BEFORE
  # running the assignments, so ${outdir} would still be unbound and `set -u` would abort ("unbound variable").
  local outdir="$1"
  local platform="$2"
  local resp="ContentSrc/_ci_${outdir}.mgcb"
  {
    echo "/outputDir:../Content/$outdir"
    echo "/intermediateDir:obj/ci_$outdir"
    echo "/platform:$platform"
    echo "/profile:Reach"
    echo "/compress:False"
    for fx in ContentSrc/Effects/*.fx; do
      b="$(basename "$fx")"
      case "$b" in *iOS*.fx | LightingCommon.fx) continue ;; esac
      name="Effects/$b"
      printf '#begin %s\n/importer:EffectImporter\n/processor:EffectProcessor\n/processorParam:DebugMode=Auto\n/build:%s\n' "$name" "$name"
    done
  } > "$resp"
  echo "== Compiling desktop effects for $platform -> Content/$outdir =="
  ( cd ContentSrc && dotnet mgcb "/@:$(basename "$resp")" )
  rm -f "$resp"
}

build DX Windows
build OGL DesktopGL
