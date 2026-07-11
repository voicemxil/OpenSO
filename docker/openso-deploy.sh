#!/usr/bin/env bash
# OpenSO admin-triggered on-demand deploy.
#
# Fired by the openso-deploy.path systemd unit when the openso-server container writes
# deploy-trigger/deploy.request — which it does at the end of an admin "Update" shutdown (after the
# in-server countdown has warned players and all lots have been saved, ToolRunServer.WriteDeployRequest).
# This is the Docker replacement for FreeSO's dead watchdog/server-zip self-update: instead of swapping
# files in place, we pull the latest :release image and let docker recreate the (already-drained) server
# onto the new build.
#
# Install once on the box:  see docker/DEPLOY.md "Admin-driven deploy".
set -euo pipefail

# Where docker-compose.yml lives. Override with OPENSO_DIR if you cloned elsewhere.
COMPOSE_DIR="${OPENSO_DIR:-/root/OpenSO/docker}"
# Compose service name of the game server. Override with OPENSO_SERVICE only if you renamed it in an
# override; the tracked docker-compose.yml calls it openso-server.
SERVICE="${OPENSO_SERVICE:-openso-server}"
cd "$COMPOSE_DIR"

FLAG="$COMPOSE_DIR/deploy-trigger/deploy.request"
# Consume the request up-front: remove the flag so this run handles exactly this request and the path-unit
# is free to fire again for a future one. (If the pull/up fails below, the admin can simply click again.)
rm -f "$FLAG"

echo "[openso-deploy] $(date -u +%FT%TZ) admin deploy requested; pulling ghcr.io/voicemxil/openso-server:release …"
docker compose pull "$SERVICE"

# Plain `up -d` recreates the server ONLY if the pulled image differs from the running one (mariadb and
# caddy are untouched). So if :release actually moved we swap onto it; if it didn't, this is a no-op and the
# restart:unless-stopped policy has already brought the drained server back on the same image.
docker compose up -d "$SERVICE"

# Reclaim space from the now-dangling previous image.
docker image prune -f >/dev/null 2>&1 || true

echo "[openso-deploy] done."
