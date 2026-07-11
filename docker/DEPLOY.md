# OpenSO Deployment Runbook

How to stand up a public OpenSO server (game server + database + HTTPS API), the website, and
email-verification registration. Nothing here is live yet — this is the from-zero recipe.

> Hosting model: a single Linux box runs everything via Docker. The website is static (GitHub Pages).
> Cloudflare provides DNS. Domains used below: **`openso.org`** (website), **`api.openso.org`** (HTTPS
> API), **`game.openso.org`** (raw game traffic). Change these if your domain differs.

---

## 0. Architecture at a glance

```
                 player browser                         game client (OpenSO.exe)
                       |                                         |
        https://openso.org (GitHub Pages)            auth + city list over HTTPS
                       |                                         |
            POST userapi/registration  ──────────►  https://api.openso.org  (Cloudflare DNS-only)
                                                          |  :443  Caddy (auto-TLS)
                                                          ▼  :9000  UserApi
   ┌──────────────────────────── the box (Docker) ───────────────────────────┐
   │  caddy  ──►  openso-server (UserApi 9000 + city 33100 + lots 34100 +     │
   │                              tasks 35100)  ◄──►  mariadb                 │
   └──────────────────────────────────────────────────────────────────────────┘
                       ▲
        game.openso.org:33101/34101  (raw TCP, Cloudflare DNS-only)  ◄── game client gameplay
```

The API is HTTP-only inside the box (port 9000); **Caddy** terminates HTTPS in front of it because the
website and the email links must be `https://`. The **game ports are raw TCP** and cannot go through
Cloudflare's HTTP proxy, so `game.openso.org` must be a **DNS-only** record straight to the box.

---

## 1. What to procure (the only things only you can do)

| Item | Recommendation |
|---|---|
| **Linux box** | A ~2 vCPU / 4 GB US-East VPS with **bundled transfer**, **Ubuntu 24.04 LTS x64** (server target is `linux-x64`). Best value (2026): **Vultr High Frequency, $24/mo** (2 vCPU / 4 GB / 128 GB NVMe / 3 TB) — fast cores help lot-sim responsiveness; or **Vultr Regular, $20** (80 GB SSD); or **DigitalOcean Basic, $24** (4 TB transfer, top reliability). Budget: **Contabo VPS 10 ~$6/mo** (NYC, oversold/slow) or **Hetzner EU CX23 ~$6.50** (20 TB, but +100 ms US latency). 4 GB is plenty; resize later if lots get busy. **Avoid** per-GB-egress clouds (GCP/raw EC2), **Hetzner US** (tripled mid-2026 + transfer slashed), and **Oracle Always Free** (idle reclamation takes down a low-population server). |
| **Domain** | `openso.org` (or yours), added to **Cloudflare** (free plan is fine). |
| **SMTP provider** | For the verification emails. Mailgun / SendGrid / Amazon SES / Postmark, or for tiny scale a Gmail app-password. You need host, port, user, password, and a sender like `noreply@openso.org`. Set up **SPF + DKIM** on the domain or the mail lands in spam. |
| **TSO game files** | Your own copy of *The Sims Online* `TSOClient/` (the one with `tuning.dat`) — from archive.org. **Never redistribute these**; the host supplies their own. |

---

## 2. DNS (Cloudflare)

Add these records (Cloudflare → DNS):

| Type | Name | Value | Proxy |
|---|---|---|---|
| A | `api` | `<BOX_IP>` | **DNS only (grey cloud)** — Caddy needs the ACME challenge to reach the box |
| A | `game` | `<BOX_IP>` | **DNS only (grey cloud)** — raw game TCP can't be proxied |
| A | `@` (openso.org) | `185.199.108.153` (+ `.109`, `.110`, `.111` `.153`) | proxied or DNS-only — GitHub Pages apex IPs |
| CNAME | `www` | `voicemxil.github.io` | proxied or DNS-only |

(If you put `openso.org` behind Cloudflare's proxy, set SSL/TLS mode to **Full**.)

---

## 3. Prepare the box

```bash
# Install Docker + compose plugin (Ubuntu)
curl -fsSL https://get.docker.com | sh
sudo usermod -aG docker $USER   # re-login after this

# Open the firewall (ufw example)
sudo ufw allow 80,443/tcp                 # Caddy / HTTPS API
sudo ufw allow 33100:33101,34100:34101,35100:35101/tcp   # game traffic
sudo ufw enable
# Note: do NOT expose 3306 (MariaDB) or 9000 (raw API) publicly — Caddy fronts 9000.

# Get the deploy files onto the box (clone the repo, or copy just the docker/ folder + TSO files)
git clone https://github.com/voicemxil/OpenSO.git
cd OpenSO
```

Put your TSO files at `docker/tso/TSOClient/` (so `docker/tso/TSOClient/tuning.dat` exists), or set
`TSO_GAME_PATH` to wherever they live.

---

## 4. Configure (`docker/config.json` + a `.env`)

Box-local config is deliberately **kept out of git** — `docker/config.json`, `docker/.env`, and
`docker/docker-compose.override.yml` are all in `.gitignore` — so a `git pull` on the box never fights your
local secrets or tweaks. The repo ships *examples/defaults*; you create your real files once and edit only
those, never the tracked files.

**`docker/config.json`** — copy it from the tracked example, then fill in the real values:

```bash
cp docker/config.example.json docker/config.json    # your real config; gitignored, so pulls won't touch it
```

- `userApi.smtpHost / smtpPort / smtpUser / smtpPassword` → your SMTP provider's credentials
  (`smtpPassword` ships as `REPLACE_WITH_SMTP_APP_PASSWORD`). Having all four present is what turns email
  verification **on** (`SmtpEnabled`). Remove them to fall back to open (no-email) registration.
- `database.connectionString` → set `pwd=` (ships as `CHANGE_ME`) to your real DB password (see `.env` below).
- `secret` → leave as `"GENERATE"` (the container generates a random one on first boot) or set your own
  64-hex string.
- `services.*.public_host` → already `game.openso.org:<port>`; change if your game host differs.
- `userApi.cdnUrl` → `https://api.openso.org` (already set; where the client fetches lot thumbnails).

**`docker/.env`** (create it) — overrides the compose defaults so secrets aren't the well-known ones, and
points Caddy/mounts at your box without editing tracked files:

```env
DB_ROOT_PASSWORD=<a-strong-root-password>
DB_PASSWORD=<a-strong-fso-password>       # must equal the pwd= in config.json connectionString
OPENSO_API_DOMAIN=api.openso.org          # public API hostname; Caddy reads it as {$OPENSO_API_DOMAIN:…}
OPENSO_ACME_EMAIL=admin@openso.org        # Let's Encrypt account/expiry email
TSO_GAME_PATH=./tso/TSOClient
```

**`docker/docker-compose.override.yml`** (optional, create only if needed) — box-specific *structural*
compose tweaks (port remaps, resource limits, extra volumes) go here, **never** in the tracked
`docker-compose.yml`. Compose auto-merges an override file that sits next to the compose file **when you run
from the `docker/` directory** — which the update/deploy scripts do (`cd docker` first), so overrides apply
to the nightly auto-update too. If you instead run `docker compose -f docker/docker-compose.yml …` from the
repo root, an explicit `-f` disables auto-merge, so add `-f docker/docker-compose.override.yml` as well.
Example (`docker/docker-compose.override.yml`):

```yaml
services:
  openso-server:
    deploy:
      resources:
        limits:
          memory: 3g
```

> **Already-running box?** If your box predates the `freeso-server` → `openso-server` service rename or the
> untracking of `docker/config.json` (both landed together), do NOT just `git pull` — follow the one-visit
> migration in §9b (["One-time: migrate an existing box"](#one-time-migrate-an-existing-box-service-rename--untracked-config)),
> which applies both changes safely without leaking or losing your local config.

---

## 5. Bring it up

The server image is **built by CI and published to GHCR**, so the box never compiles anything — it just
pulls. The box tracks **`:release`** (set in docker-compose.yml): a stamped image cut when a release tag
is pushed (release.yml). **Semantic version tags (`v0.1.0`, `v0.2.0`, …) are the current release scheme**;
the legacy `dev-#`/`alpha-#`/`beta-#` tag naming still triggers a release too (kept so old tags keep
resolving), but new releases should be cut as `vMAJOR.MINOR.PATCH`. Main-branch builds are `:edge`
(docker.yml) — dirty, for testing, never deployed. `:release` is the channel you deploy. From the repo root:

```bash
docker compose -f docker/docker-compose.yml pull        # download prebuilt server + mariadb + caddy
docker compose -f docker/docker-compose.yml up -d
docker compose -f docker/docker-compose.yml logs -f openso-server   # watch startup
```

To ship new server code: cut a release (`git tag v0.2.2 && git push origin v0.2.2`) → release.yml builds the
stamped image + moves `:release` → the box's nightly timer (below) pulls it. To deploy immediately instead
of waiting for the timer: `docker compose pull && docker compose up -d` on the box.

> **One-time:** make the GHCR package public so the box can pull without logging in — GitHub →
> your packages → `openso-server` → Package settings → Change visibility → Public. Otherwise run
> `docker login ghcr.io` on the box with a PAT that has `read:packages`.

`entrypoint.sh` (baked into the image) auto-generates the `secret` (if `GENERATE`), runs `db-init`
(creates all `fso_*` tables), then `run`. Caddy fetches a Let's Encrypt cert for `api.openso.org` on first
request.

Quick checks:
```bash
curl -s https://api.openso.org/cityselector/app/InitialConnectServlet | head   # API reachable over HTTPS
```

To re-run migrations after a server update that changes the schema:
```bash
docker compose -f docker/docker-compose.yml exec openso-server dotnet FSO.Server.Core.dll db-init
```

---

## 6. Create an admin user

Register one account (via the website once it's up, or by POSTing to the API), then promote it in the DB:

```bash
docker compose -f docker/docker-compose.yml exec mariadb \
  mariadb -ufsoserver -p fso -e \
  "UPDATE fso_users SET is_admin=1, is_moderator=1 WHERE username='YOURNAME';"
```

That account can then use the admin webapp (`TSOClient/FSO.Server/Admin`, separate build) to manage
shards, users, events, and — later — updates.

---

## 7. Website (GitHub Pages)

The site is in `…/continuation/website/` and is **already wired** for this server: `OPENSO_API_BASE` is
`https://api.openso.org` and registration defaults to the email-verification flow.

1. Put the `website/` contents in a repo (its own repo, or `voicemxil/OpenSO` under `website/`). It already
   contains `CNAME` = `openso.org`.
2. Move `website/deploy-pages.yml` to `.github/workflows/deploy-pages.yml`. If the site is at the repo
   root rather than `website/`, drop the `paths: ["website/**"]` filter and change `path: website` → `path: .`.
3. Repo **Settings → Pages**: Source = **GitHub Actions**, Custom domain = `openso.org`, tick **Enforce HTTPS**.
4. Push → the workflow deploys. Confirm `https://openso.org` loads and the download links point to your
   GitHub Releases.

If your SMTP sender/domain differs, also double-check `website/assets/openso.js` `OPENSO_API_BASE`.

---

## 8. How registration works (email verification)

1. `register.html` → `POST https://api.openso.org/userapi/registration/request` with `email` +
   `confirmation_url` (`…/confirm.html?token=%token%`).
2. Server emails a link with `%token%` substituted (via your SMTP).
3. Player clicks it → `confirm.html` → `POST userapi/registration/confirm` with `username,password,token`
   → account created.
4. Password reset is the same shape via `userapi/password/request` + `reset.html`.

Test the loop end to end with a real inbox before launch. If emails don't arrive, it's almost always SMTP
creds or missing SPF/DKIM, not the server.

---

## 9. Point the game client at the server

- **Quick:** on the client login screen press **F1** → set the API URL to your own server.
- **For distribution:** `GameEntryUrl`/`CitySelectorUrl` in `TSOClient/FSO.UI/GlobalSettings.cs` already
  default to `https://api.openso.org` — released client builds point at this server out of the box, no
  patch needed. **Migration note:** an existing install with a saved config pointing at the old
  `http://api.freeso.org` is auto-migrated to `https://api.openso.org` on load (see the `GameEntryUrl`
  check in `GlobalSettings.cs`); if you're running your *own* server under a different domain, still set it
  via F1 or your own client config.

---

## 9b. Updates — server auto-update + client patching

Two independent mechanisms. Both key off the **same release tag** — a semver tag (`v0.2.1`, current
scheme) or a legacy `dev-#`/`alpha-#`/`beta-#` tag (still supported, not recommended for new releases).

### Nightly warned restart (fully automatic, like FreeSO)

The server restarts itself every night with a player warning — this is FreeSO's native **`shutdown` task**
in the `tasks` schedule (`config.json`, `"cron": "0 9 * * *"` = 09:00 UTC ≈ 4–5 AM US East). It broadcasts
a **15-minute countdown** to everyone online (*"The game server will go down for maintenance in N
minutes"* at 15/10/5/4/3/2/1 min + 30 s), **saves all lots**, then exits cleanly; `restart: unless-stopped`
brings it back. No external trigger, no auth — it's in the config. (Adjust the cron time to taste; the
container runs UTC.)

### Server image auto-update (the box upgrades itself)

A separate nightly systemd timer (**09:30 UTC**, just after the restart) pulls `:release` and recreates the
server **only if the image changed** — a normal night is a no-op, a release night is a brief swap onto the
already-emptied server. Install it once on the box:

```bash
# from the repo root on the box (adjust paths in the unit files if you cloned elsewhere than /root/OpenSO)
chmod +x docker/openso-update.sh
sudo cp docker/systemd/openso-update.service docker/systemd/openso-update.timer /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now openso-update.timer
systemctl list-timers openso-update.timer        # confirm next run (~09:30 UTC)
sudo systemctl start openso-update.service        # optional: run an update check right now
```

Because the box tracks `:release` (not `:edge`/main), it only ever moves to a **cut release**. Pin a
specific version in docker-compose.yml (e.g. `:v0.2.1`) to freeze the box and skip auto-updates.

### Admin-driven deploy (update from the dashboard, on demand)

You don't have to wait for the nightly timer. The admin webapp's shard **Update** action (Shards → Shutdown
→ tick *Update*, or `POST /admin/shards/shutdown {update:true}`) now drives a real image deploy:

1. The server broadcasts the countdown to players and **saves all lots** (the normal graceful drain).
2. After the drain, `ToolRunServer.WriteDeployRequest` drops a `deploy.request` flag into the
   `./deploy-trigger` volume (config key `serverDeployTriggerDir`: `/deploy-trigger`).
3. A host-side **systemd path-unit** sees the flag and runs `openso-deploy.sh` → `docker compose pull` +
   `up -d`, swapping the drained server onto the latest `:release` image (no-op if `:release` didn't move).

This replaces FreeSO's dead watchdog/server-zip self-update (the container can't update itself — no Docker
socket is mounted, by design). Install the watcher once on the box:

```bash
# from the repo root on the box (adjust paths in the unit files if you cloned elsewhere than /root/OpenSO)
chmod +x docker/openso-deploy.sh
sudo cp docker/systemd/openso-deploy.path docker/systemd/openso-deploy.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now openso-deploy.path
systemctl status openso-deploy.path               # confirm it's watching
```

Recreate the container once (`docker compose up -d openso-server`) after pulling these changes so the new
`./deploy-trigger` volume + `serverDeployTriggerDir` config take effect. Watch a deploy with
`journalctl -u openso-deploy.service -f`.

To **force** a plain restart (no image swap) off-schedule, use the same dialog with *Restart* instead.

### Client patching (the game updates itself at login)

At login the client compares its `version.txt` to the version the **shard advertises** and, if behind,
downloads an update package — the full client zip, or an incremental patch if one exists for the gap (see
below) — and relaunches via `update.exe`.

**The advertised version tracks the running server automatically — never set it by hand.** On every boot
the city server writes its own `version.txt` into the shard row (`CityServer.cs` → `Shards.UpdateStatus`),
so `fso_shards.version_name`/`version_number` always equals the build the server actually is. That's why
stamping the image (`VERSION` arg → `version.txt`) is essential: deploy the `v0.2.2` image and the shard
advertises `v0.2.2` the moment it boots. A hand-set version that doesn't match the running build would tell
clients to "update" to a build the server isn't on — a patch loop. So patching needs only:

- **The update URL** — already in `docker/config.json` (`userApi.updateUrl` →
  `…/releases/latest/download/OpenSO-client-win-x64.zip`), advertised as `FSOUpdateUrl`.
- **A published release** matching the deployed image, so `releases/latest` actually serves that version's
  client zip. (Deploy the image and publish the release together — if the server advertises `v0.2.2` but
  `releases/latest` is still `v0.2.1`, outdated clients download `v0.2.1` and never catch up.)

A client already on the advertised version sees no prompt; an older one patches up to it. **Caveat:** that
single URL is win-x64 — in-game patching targets Windows; Linux/macOS players update through the launcher
(which is per-platform, see [update-manifest.md](../Documentation/update-manifest.md)).

**Delta patches are automatic, no admin step needed.** At every UserApi startup, `UpdateReconciler` scans
this repo's GitHub Releases for semver (`vX.Y.Z`) tags and populates the `fso_updates` chain (full zip +
win-x64 incremental + manifest, all attached by `release.yml`'s `client-delta`/`client-manifest` jobs) that
`GET /userapi/update` serves — so a Windows client several versions behind downloads only the incremental
diffs, not a fresh full zip each time. This replaces FreeSO's old admin-webapp/GitHub-OAuth update-generator
flow (still described in [Updates.md](../Documentation/Updates.md) for reference) — GitHub Releases are now
the sole source of truth, nothing to configure beyond publishing releases normally.

That reconciled chain lives under an **update branch** named by `updateBranch` in `docker/config.json`
(shipped as `"dev"`). This is **not a git branch** — it's just the label of the `fso_update_branch` DB row
the chain is stored under. Leave it as `dev` unless you specifically want to run more than one independent
chain (e.g. to test a build against a subset of clients); renaming it starts a fresh chain from the next
reconcile.

### One-time: migrate an existing box (service rename + untracked config)

Two box-affecting changes landed together and are applied in **one visit** (one `git pull`):

1. **Service rename:** the compose game-server service `freeso-server` became **`openso-server`**. The
   image, the database, and every volume are unchanged — only the service (and therefore its
   container/DNS) name moves.
2. **Untracked box config:** the tracked `docker/config.json` became the template
   `docker/config.example.json`; your real `docker/config.json` (plus `docker/.env` and
   `docker/docker-compose.override.yml`) is now **gitignored** (§4), so future pulls can never collide
   with it again.

⚠️ **Do NOT plain-`git pull` over local `config.json` edits, and do NOT hand-resolve a config.json
conflict if one appears.** Git detects the `config.json → config.example.json` rename and will three-way
merge your local edits — dragging your real secrets *into* the tracked `docker/config.example.json`
(leaking them into a tracked file) while deleting your real `config.json`. The sequence below parks your
secrets **outside** the repo, neutralizes the local edit so the pull is conflict-free, then drops the real
file back in — verified end-to-end. Run it on the box (assumes the usual case: your local edits are
*uncommitted*, e.g. you've been stashing them across pulls):

```bash
cd /root/OpenSO

# 0. Record every local edit OUTSIDE the repo (reference copy), and back up the real config.
git diff > ~/openso-local-edits.patch
cp docker/config.json ~/openso-config.backup.json

# 1. Neutralize the local config.json edit FIRST (see warning above), then stash any OTHER local edits.
git checkout -- docker/config.json
git stash push -m "box-local edits pre-migration"    # says "No local changes to save" if config was all

# 2. Pull both changes — with the tree clean this fast-forwards, NO conflicts.
git pull --no-rebase

# 3. Put the real config back. It's gitignored now: git will never touch it again.
cp ~/openso-config.backup.json docker/config.json
git check-ignore docker/config.json                  # must print docker/config.json

# 4. Re-express your OTHER local edits the untracked way (skip if step 1 said "No local changes to save").
git stash pop
#    Edits on lines the rename didn't touch re-apply (staged); edits that hit renamed lines report a
#    CONFLICT (e.g. in docker-compose.yml or a script). Either way do NOT keep them in tracked files —
#    that's what used to break every pull. Re-create the values you still want the untracked way (§4):
#    compose tweaks in docker/docker-compose.override.yml or docker/.env; script/path tweaks via the
#    OPENSO_DIR / OPENSO_SERVICE env vars. Your full original diff is in ~/openso-local-edits.patch.
#    Then reset ALL tracked files to the pulled state — this clears conflicts and staged re-applies in one
#    shot, and does not touch untracked/ignored files (your config.json, .env, override are safe):
git checkout HEAD -- .
git stash drop        # only if pop reported a conflict (a conflicted pop keeps the stash; a clean pop drops it)
git status --porcelain                               # must print nothing: tree clean, box files invisible

# 5. Swap the running stack onto the new service name (+ any newer :release image).
#    --remove-orphans deletes the now-orphaned `freeso-server` container FIRST, freeing the game ports
#    (33100-35101) so the new openso-server can bind them. mariadb and caddy keep running.
cd docker
docker compose pull
docker compose up -d --remove-orphans

# 6. Reload Caddy so it re-resolves the reverse-proxy upstream to the NEW service DNS name
#    (openso-server:9000). Step 5 does NOT recreate caddy — its service definition is unchanged; only the
#    bind-mounted Caddyfile *content* changed — so caddy still holds the dead `freeso-server` upstream and
#    the API 502s until it reloads (zero-downtime; `docker compose restart caddy` also works, ~1s blip):
docker compose exec caddy caddy reload --config /etc/caddy/Caddyfile

# 7. Refresh the systemd unit whose tracked text changed (openso-deploy.path — comment-only, but keeps
#    /etc in sync). The .service/.timer units and both .sh scripts run from the git checkout, so the pull
#    already updated them — nothing else to re-copy.
sudo cp systemd/openso-deploy.path /etc/systemd/system/ && sudo systemctl daemon-reload

# 8. Idempotent DB migration pass (safe no-op when the schema didn't change).
docker compose exec openso-server dotnet FSO.Server.Core.dll db-init

# 9. Verify.
docker compose ps                                    # openso-server + mariadb + caddy Up; NO freeso-server left
git -C .. status --porcelain                         # clean — your config.json is invisible (ignored)
grep -o 'pwd=[^;]*' config.example.json              # -> pwd=CHANGE_ME  (your real secret was NOT leaked)
curl -s https://api.openso.org/cityselector/app/InitialConnectServlet | head   # API answers through Caddy
```

Delete `~/openso-config.backup.json` and `~/openso-local-edits.patch` once everything checks out.

*If the box had **committed** local edits instead:* first restore the tracked config.json to its upstream
base and commit that — `git checkout "$(git merge-base HEAD origin/main)" -- docker/config.json && git
commit -m "restore config.json to base before untracking"` — then continue from step 2 (the pull becomes a
merge; resolve any non-config conflicts keeping the openso-server naming; **never** hand-merge
config.json/config.example.json).

**No data is lost by the rename.** The database lives in the named volume `openso_mariadb_data` (attached
to the *mariadb* service, not the renamed one), and lots/objects live in the `./nfs` **bind mount** (a host
path). Neither is keyed to the game server's name, so a service rename cannot touch them; the other named
volumes (`openso_caddy_data`, `openso_caddy_config`) belong to caddy and are likewise untouched. The game
server itself mounts only bind mounts (`./tso/TSOClient`, `./nfs`, `./config.json`, `./deploy-trigger`) —
all host paths, all name-independent.

---

## 10. Go-live checklist

- [ ] DNS: `api`/`game` DNS-only → box; `openso.org` → Pages; HTTPS enforced.
- [ ] Firewall: 80/443 + the six game ports open; 3306/9000 NOT public.
- [ ] `.env` + `config.json` secrets changed off the defaults; SMTP creds real; SPF/DKIM set.
- [ ] TSO files mounted; `tuning.dat` present at `/game/TSOClient/`.
- [ ] `https://api.openso.org/cityselector/app/InitialConnectServlet` responds.
- [ ] Register a test account → verification email arrives → confirm → log in with the client.
- [ ] One account promoted to admin.

---

## 11. Admin webapp (admin.openso.org)

The admin UI is the AngularJS SPA in `TSOClient/FSO.Server/Admin/`. It's a **standalone static bundle** —
the API server does not serve it — so you build it and host it anywhere over HTTPS. It talks to the
`/admin/*` endpoints; you log in with an account that has `is_admin=1`. From it you can **force** a
restart/update, schedule shutdowns, manage users/shards/hosts, and generate client updates.

**Build it** (old toolchain — Node 20 via nvm; builds "with complaining"):
```bash
cd TSOClient/FSO.Server/Admin
npm install
npm install -g bower && bower install
npm run start            # gulp build → outputs to dist/
```
The default API URL is already set to `https://api.openso.org` (`src/app/login/login.controller.js`); it's
also editable on the login form.

**Server side (already wired in this repo, ships in the next image):** `https://admin.openso.org` is added
to the `AdminAppPolicy` CORS origins (`FSO.Server.Api.Core/Startup.cs`). That policy uses
`AllowCredentials()`, so the admin origin must be listed explicitly — a wildcard won't work. If you host the
admin UI on a *different* hostname, add it there too and redeploy.

**Host it** — cleanest is a second Caddy site on the box (same auto-TLS as the API). Copy `dist/` to the box
(e.g. `/root/OpenSO/admin`), mount it into the Caddy container, and add to the `Caddyfile`:
```
admin.openso.org {
    encode zstd gzip
    root * /srv/admin
    try_files {path} /index.html
    file_server
}
```
Add a `./admin:/srv/admin:ro` volume to the `caddy` service in `docker-compose.yml`, point a **DNS-only**
`admin.openso.org` A record at the box (so Caddy can issue the cert), and `docker compose up -d caddy`.
(Alternatives: a GitHub Pages repo with `admin.openso.org` as its custom domain; both still need the CORS
origin above.)

**Log in:** open `https://admin.openso.org`, enter your admin username + password, API URL
`https://api.openso.org`. Auth posts to `/admin/oauth/token`, which **rejects non-admins** — make sure your
account is promoted (`UPDATE fso_users SET is_admin=1, is_moderator=1 WHERE username='osab';`).

---

## Notes

- **Updates** are wired (§9b): the box auto-updates to the latest `:release` image nightly, and clients
  patch themselves at login against the shard-advertised version, via an automatically-reconciled delta
  chain built from GitHub Releases (no admin update-generator step required).
- **Backups:** the `mariadb_data` volume (DB) and `docker/nfs/` (lots/objects) are your state — snapshot both.
- **Constraints (from the brief):** no cash donations, never redistribute TSO/copyrighted assets (hosts
  supply their own), keep the build open-source (MPL).
