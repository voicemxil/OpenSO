#!/usr/bin/env bash
# OpenSO server auto-update.
#
# Pulls the latest `:release` server image and restarts the server only if the image actually changed.
# Installed as a nightly systemd timer (see docker/systemd/ + docker/DEPLOY.md "Auto-update"), mirroring
# FreeSO's early-morning server restart. The box tracks `:release`, which moves ONLY when a dev-#/alpha-#/
# beta-# release is cut (release.yml) — so main-branch (`:edge`) builds never reach production.
set -euo pipefail

# Where docker-compose.yml lives. Override with OPENSO_DIR if you cloned elsewhere.
COMPOSE_DIR="${OPENSO_DIR:-/root/OpenSO/docker}"
cd "$COMPOSE_DIR"

echo "[openso-update] $(date -u +%FT%TZ) pulling ghcr.io/voicemxil/openso-server:release …"
docker compose pull freeso-server

# `up -d` recreates freeso-server only if the pulled image differs from the running one; mariadb and caddy
# are pinned images and are left untouched. So on a night with no new release this is a no-op (no restart).
docker compose up -d

# Reclaim space from the now-dangling previous image.
docker image prune -f >/dev/null 2>&1 || true

echo "[openso-update] done."
