#!/bin/sh
# Export the Let's Encrypt certificate Caddy maintains for the game domain into a passwordless
# PFX for the game server's TLS listeners (it loads certs/game.pfx at start; DEPLOY.md §9c).
# Run daily via openso-cert-sync.timer; restarts the game server only when the certificate
# actually changed (LE renews roughly every 60 days). Change detection compares the PEM cert —
# PFX exports are salted and never byte-identical.
set -e
cd "$(dirname "$0")"
DOMAIN="${OPENSO_GAME_DOMAIN:-game.openso.org}"
SRC="/data/caddy/certificates/acme-v02.api.letsencrypt.org-directory/$DOMAIN"
CADDY="$(docker ps --format '{{.Names}}' | grep caddy | head -1)"
mkdir -p certs
docker cp -q "$CADDY:$SRC/$DOMAIN.crt" certs/game.crt.new
docker cp -q "$CADDY:$SRC/$DOMAIN.key" certs/game.key.new
if [ -f certs/game.crt ] && cmp -s certs/game.crt certs/game.crt.new; then
    rm -f certs/game.crt.new certs/game.key.new
    echo "certificate unchanged"
    exit 0
fi
openssl pkcs12 -export -out certs/game.pfx -in certs/game.crt.new -inkey certs/game.key.new -passout pass:
mv certs/game.crt.new certs/game.crt
mv certs/game.key.new certs/game.key
chmod 600 certs/game.pfx certs/game.key
echo "certificate updated"
if docker ps --format '{{.Names}}' | grep -q openso-server; then
    docker compose restart openso-server
fi
