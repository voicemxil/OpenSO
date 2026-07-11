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

Edit `docker/config.json` before starting:

- **`secret`** - Leave as `GENERATE` for auto-generation by the container, or set your own hex string
- **`services.*.public_host`** - Each service (tasks, cities, lots) has its own `public_host`; change these
  if hosting remotely (defaults to `game.openso.org:<port>`)
- **`database.connectionString`** - Update if you changed MariaDB credentials in `docker-compose.yml`

Point the stack at your local TSO client installation — place the files at `docker/tso/TSOClient` (so
`docker/tso/TSOClient/tuning.dat` exists), or set the `TSO_GAME_PATH` environment variable (e.g. in a
`docker/.env` file) to wherever they live:

```env
TSO_GAME_PATH=/path/to/your/TSOClient
```

## What's Running

- **OpenSO server** (compose service `openso-server`) - Game server on ports 9000 (API), 33100-33101 (city), 34100-34101 (lots), 35100-35101 (tasks)
- **MariaDB 11** - Database with persistent storage in a Docker volume
- **Caddy** - HTTPS reverse proxy in front of the API (see `docker/Caddyfile`)

The database is automatically initialized on first run.

## Requirements

- The Sims Online client files
- Ports 9000, 33100-33101, 34100-34101, 35100-35101 available
