# OpenSO Server Docker Setup

Runs the OpenSO server and MariaDB database via Docker Compose. This is the quick/local-dev version —
for the full from-zero public deployment (DNS, HTTPS, email verification, auto-update), see
[`docker/DEPLOY.md`](DEPLOY.md).

## Usage

Run from the repository root:

```bash
docker compose -f docker/docker-compose.yml pull   # fetch the prebuilt server image + mariadb + caddy
docker compose -f docker/docker-compose.yml up -d
```

The server image is built by CI and published to GHCR (`ghcr.io/voicemxil/openso-server`) — the compose
file pulls it rather than building locally, so there's no `--build` step.

Stop the server:

```bash
docker compose -f docker/docker-compose.yml down
```

## Configuration

Box-local config is **kept out of git** so `git pull` on a running box never collides with local secrets
or tweaks. The tracked files are *examples/defaults*; you copy or override them once, and your real values
live only on the box (they're in `.gitignore`).

**1. Server config** — copy the example, then edit your copy (never the example):

```bash
cp docker/config.example.json docker/config.json   # docker/config.json is gitignored
```

Then edit `docker/config.json`:

- **`secret`** - Leave as `GENERATE` for auto-generation by the container, or set your own hex string
- **`services.*.public_host`** - Each service (tasks, cities, lots) has its own `public_host`; change these
  if hosting remotely (defaults to `game.openso.org:<port>`)
- **`database.connectionString`** - set `pwd=` (shipped as `CHANGE_ME`) to your real MariaDB password, and
  match it in `docker/.env` (see below)

**2. Box-specific compose/env tweaks** — do NOT edit the tracked `docker-compose.yml` or `Caddyfile`.
Put box-specific values in either of these two gitignored files instead:

- `docker/.env` — variable overrides read by compose and passed into the containers, e.g.:

  ```env
  DB_ROOT_PASSWORD=a-strong-root-password
  DB_PASSWORD=a-strong-fso-password      # must equal the pwd= in config.json connectionString
  TSO_GAME_PATH=/path/to/your/TSOClient
  OPENSO_API_DOMAIN=api.openso.org       # public API hostname (Caddy); default api.openso.org
  OPENSO_ACME_EMAIL=admin@openso.org     # Let's Encrypt account email; default admin@openso.org
  ```

- `docker/docker-compose.override.yml` — structural compose tweaks (port remaps, resource limits, extra
  volumes). Compose auto-merges it when you run from the `docker/` directory (the update/deploy scripts do
  `cd docker` first, so it applies to them). If you instead invoke `-f docker/docker-compose.yml` from the
  repo root, add `-f docker/docker-compose.override.yml` too, since an explicit `-f` disables auto-merge.

Point the stack at your local TSO client installation — place the files at `docker/tso/TSOClient` (so
`docker/tso/TSOClient/tuning.dat` exists), or set `TSO_GAME_PATH` in `docker/.env` to wherever they live.

## What's Running

- **OpenSO server** (compose service `openso-server`) - Game server on ports 9000 (API), 33100-33101 (city), 34100-34101 (lots), 35100-35101 (tasks)
- **MariaDB 11** - Database with persistent storage in a Docker volume
- **Caddy** - HTTPS reverse proxy in front of the API (see `docker/Caddyfile`)

The database is automatically initialized on first run.

## Requirements

- The Sims Online client files
- Ports 9000, 33100-33101, 34100-34101, 35100-35101 available
