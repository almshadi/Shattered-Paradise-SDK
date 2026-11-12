#!/usr/bin/env bash
# Build the sp-ranked image from this checkout (game must be built: make TARGETPLATFORM=win-x64)
# and deploy it to the server. First-time server setup (nginx site, TLS, firewall, .env): see ranked/README.md.
#
#   ranked/deploy/deploy.sh            build + upload + restart
#   ranked/deploy/deploy.sh --build    build the image locally only
set -euo pipefail

HOST="${SP_RANKED_HOST:-root@tabletstrats.com}"
REMOTE_DIR=/opt/sp-ranked
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
VERSION="$(grep -m1 'Version:' "$ROOT/mods/sp/mod.yaml" | sed 's/.*Version: *//; s/\r$//')"

[ -f "$ROOT/engine/bin/OpenRA.Server.dll" ] || { echo "Game not built - run make first." >&2; exit 1; }
echo "==> Building sp-ranked:$VERSION"

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
mkdir -p "$STAGE/sdk/engine" "$STAGE/sdk/mods"
rsync -a --exclude obj --exclude bin "$ROOT/ranked/backend/" "$STAGE/backend/"
rsync -a "$ROOT/engine/bin" "$ROOT/engine/mods" "$ROOT/engine/glsl" "$ROOT/engine/global mix database.dat" "$ROOT/engine/VERSION" "$STAGE/sdk/engine/"
rsync -a "$ROOT/mods/sp" "$ROOT/mods/sp-content" "$STAGE/sdk/mods/"
cp "$ROOT/ranked/deploy/Dockerfile" "$STAGE/"
docker build -q -t "sp-ranked:$VERSION" "$STAGE"

[ "${1:-}" = "--build" ] && exit 0

echo "==> Uploading image to $HOST (this takes a while the first time)"
docker save "sp-ranked:$VERSION" | gzip -1 | ssh "$HOST" 'gunzip | docker load'
scp -q "$ROOT/ranked/deploy/docker-compose.prod.yml" "$HOST:$REMOTE_DIR/docker-compose.yml"

echo "==> Starting $VERSION"
ssh "$HOST" "cd $REMOTE_DIR && sed -i 's/^SP_RANKED_VERSION=.*/SP_RANKED_VERSION=$VERSION/' .env && docker compose up -d && sleep 5 && docker compose ps"
echo "==> Done. Check: curl https://ranked.tabletstrats.com/config"
