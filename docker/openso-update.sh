#!/usr/bin/env bash
# OpenSO server image auto-update.
#
# Pulls the latest `:release` server image and recreates the server ONLY if the image actually changed.
# The WARNED nightly restart itself is handled in-server by FreeSO's `shutdown` task (config.json tasks
# schedule, 0 9 * * * UTC) — it broadcasts a 15-minute countdown to players and saves all lots before the
# server exits, and `restart: unless-stopped` brings it back. This script (nightly systemd timer, ~09:30
# UTC, just after that restart) only swaps in a NEW release image. The box tracks `:release`, which moves
# ONLY when a dev-#/alpha-#/beta-# release is cut (release.yml) — main-branch (`:edge`) builds never reach
# production. On a normal night `up -d` is a no-op (no restart); on a release night it's a brief swap on
# an already-emptied server. See docker/DEPLOY.md "Updates".
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
