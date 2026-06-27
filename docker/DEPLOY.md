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
   │  caddy  ──►  freeso-server (UserApi 9000 + city 33100 + lots 34100 +     │
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

**`docker/config.json`** — already set for `game.openso.org` and email verification. Fill in the real
values:

- `userApi.smtpHost / smtpPort / smtpUser / smtpPassword` → your SMTP provider's credentials. Having all
  four present is what turns email verification **on** (`SmtpEnabled`). Remove them to fall back to
  open (no-email) registration.
- `database.connectionString` → set `pwd=` to your real DB password (see `.env` below).
- `secret` → leave as `"GENERATE"` (the container generates a random one on first boot) or set your own
  64-hex string.
- `services.*.public_host` → already `game.openso.org:<port>`; change if your game host differs.
- `userApi.cdnUrl` → `https://api.openso.org` (already set; where the client fetches lot thumbnails).

**`docker/.env`** (create it) — overrides the compose defaults so secrets aren't the well-known ones:

```env
DB_ROOT_PASSWORD=<a-strong-root-password>
DB_PASSWORD=<a-strong-fso-password>     # must equal the pwd= in config.json connectionString
API_DOMAIN=api.openso.org
TSO_GAME_PATH=./tso/TSOClient
```

---

## 5. Bring it up

The server image is **built by CI and published to GHCR**, so the box never compiles anything — it just
pulls. The box tracks **`:release`** (set in docker-compose.yml): a stamped image cut ONLY when a
`dev-#`/`alpha-#`/`beta-#` release is tagged (release.yml). Main-branch builds are `:edge` (docker.yml) —
dirty, for testing, never deployed. From the repo root:

```bash
docker compose -f docker/docker-compose.yml pull        # download prebuilt server + mariadb + caddy
docker compose -f docker/docker-compose.yml up -d
docker compose -f docker/docker-compose.yml logs -f freeso-server   # watch startup
```

To ship new server code: cut a release (`git tag dev-2 && git push origin dev-2`) → release.yml builds the
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
docker compose -f docker/docker-compose.yml exec freeso-server dotnet FSO.Server.Core.dll db-init
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

- **Quick:** on the client login screen press **F1** → set the API URL to `https://api.openso.org`.
- **For distribution:** patch `TSOClient/FSO.UI/GlobalSettings.cs` defaults `GameEntryUrl` /
  `CitySelectorUrl` from `http://api.freeso.org` to `https://api.openso.org`, then build/release the client
  so players don't have to. (This is part of the not-yet-done updater-wiring workstream.)

---

## 9b. Updates — server auto-update + client patching

Two independent mechanisms. Both key off the **same release tag** (`dev-#`/`alpha-#`/`beta-#`).

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
specific version in docker-compose.yml (e.g. `:dev-1`) to freeze the box and skip auto-updates.

To **force** a restart or update off-schedule, use the admin webapp / API (`POST /admin/shards/shutdown`
with `restart` or `update`) — see §11.

### Client patching (the game updates itself at login)

At login the client compares its `version.txt` to the version the **shard advertises** and, if behind,
downloads the full client zip and relaunches via `update.exe`.

**The advertised version tracks the running server automatically — never set it by hand.** On every boot
the city server writes its own `version.txt` into the shard row (`CityServer.cs` → `Shards.UpdateStatus`),
so `fso_shards.version_name`/`version_number` always equals the build the server actually is. That's why
stamping the image (`VERSION` arg → `version.txt`) is essential: deploy the `dev-2` image and the shard
advertises `dev-2` the moment it boots. A hand-set version that doesn't match the running build would tell
clients to "update" to a build the server isn't on — a patch loop. So patching needs only:

- **The update URL** — already in `docker/config.json` (`userApi.updateUrl` →
  `…/releases/latest/download/OpenSO-client-win-x64.zip`), advertised as `FSOUpdateUrl`.
- **A published release** matching the deployed image, so `releases/latest` actually serves that version's
  client zip. (Deploy the image and publish the release together — if the server advertises `dev-2` but
  `releases/latest` is still `dev-1`, outdated clients download `dev-1` and never catch up.)

A client already on the advertised version sees no prompt; an older one patches up to it. **Caveat:** that
single URL is win-x64 — in-game patching targets Windows; Linux/macOS players update through the launcher
(which is per-platform). Smaller delta patches (vs. the full zip) need the admin-webapp update generator —
a later workstream.

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
  patch themselves at login against the shard-advertised version. Delta (incremental) patches via the admin
  update generator remain a later enhancement; today the client pulls the full release zip.
- **Backups:** the `mariadb_data` volume (DB) and `docker/nfs/` (lots/objects) are your state — snapshot both.
- **Constraints (from the brief):** no cash donations, never redistribute TSO/copyrighted assets (hosts
  supply their own), keep the build open-source (MPL).
