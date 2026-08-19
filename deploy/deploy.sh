#!/usr/bin/env bash
#
# Build and deploy NCPM via Docker Compose inside WSL.
#
# Usage (from Windows / PowerShell):
#   wsl -d Ubuntu-26.04 -- bash -c "cd /mnt/c/code-repos/ncpm-web && bash deploy/deploy.sh"
#
# Or from inside WSL:
#   cd /mnt/c/code-repos/ncpm-web && bash deploy/deploy.sh
#
# The project is copied to /tmp because /tmp is ephemeral across separate
# `wsl` invocations — chaining everything in one bash -c keeps it consistent.
set -euo pipefail

SRC="/mnt/c/code-repos/ncpm-web"
WORK="/tmp/ncpm-project"

# The compose file bind-mounts $WORK/deploy/data as the panel's state directory,
# so wiping $WORK would take users.yml, config.yml and every proxy host with it.
# Preserve it across the rebuild, and keep the copy in $HOME so it also survives
# /tmp being cleared when WSL restarts.
BACKUP="$HOME/ncpm-data-backup-$(date +%Y%m%d-%H%M%S)"
RESTORE_FROM=""

if [ -d "$WORK/deploy/data" ]; then
    echo "==> Backing up panel data to $BACKUP"
    cp -a "$WORK/deploy/data" "$BACKUP" 2>/dev/null || true
    RESTORE_FROM="$BACKUP"

    # Keep the last few backups only; this runs on every deploy.
    ls -1dt "$HOME"/ncpm-data-backup-* 2>/dev/null | tail -n +6 | xargs -r rm -rf
else
    echo "==> No existing panel data to preserve"
fi

echo "==> Cleaning previous build dir"
rm -rf "$WORK"

echo "==> Copying project to $WORK"
cp -r "$SRC" "$WORK"

# The Dockerfile only needs src/ and deploy/. Dropping build output and history
# keeps the context the daemon has to ingest small.
rm -rf "$WORK/src/Ncpm.Web/bin" "$WORK/src/Ncpm.Web/obj" "$WORK/.git"

if [ -n "$RESTORE_FROM" ]; then
    echo "==> Restoring panel data"
    rm -rf "$WORK/deploy/data"
    cp -r "$RESTORE_FROM" "$WORK/deploy/data"
fi

echo "==> Stopping and removing existing containers"
cd "$WORK"
docker compose down --remove-orphans || true

# compose declares both `image:` (ghcr.io/varcli/ncpm-web:latest) and `build:`.
# `up --build` reuses the existing local image when its tag is already present,
# silently skipping the local build — so source changes never reach the container.
# Build explicitly and force-recreate to guarantee the running container matches
# the freshly built image.
echo "==> Building image (forcing rebuild of all layers)"
docker compose build --no-cache

echo "==> Starting containers (force-recreate to use the new image)"
docker compose up -d --force-recreate

echo ""
echo "==> Done. Container status:"
docker compose ps

echo ""
echo "==> Tail logs (Ctrl+C to exit):"
echo "    wsl -d Ubuntu-26.04 -- bash -c \"cd /tmp/ncpm-project && docker compose logs -f\""
