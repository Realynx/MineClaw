#!/usr/bin/env bash
# Copies the installer files a release ships as flat assets into <assets-dir>: everything in deploy/install/release-assets.txt
# (install.sh and what it and mineclaw-update fetch), plus the docker compose install.
#   deploy/release/stage-installers.sh <version> <assets-dir>
set -euo pipefail
cd "$(dirname "$0")/../.."
(($# == 2)) || { echo "usage: stage-installers.sh <version> <assets-dir>" >&2; exit 64; }
ASSETS=$2
mkdir -p "$ASSETS"
copy() { # $1 asset name, $2 source: LF only (a Windows checkout may have CRLF); scripts stay executable
    [[ -f $2 ]] || { echo "missing $2" >&2; exit 1; }
    sed 's/\r$//' "$2" > "$ASSETS/$1"
    if [[ $1 == *.sh ]]; then chmod 0755 "$ASSETS/$1"; fi
}
while read -r name src; do
    [[ -z $name || $name == \#* ]] && continue
    copy "$name" "$src"
done < <(sed 's/\r$//' deploy/install/release-assets.txt)
copy install-docker.sh deploy/docker/install-docker.sh
copy docker-compose.yml deploy/docker/docker-compose.yml
ls -la "$ASSETS"
