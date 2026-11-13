#!/usr/bin/env bash
# Lays out the docker build context for deploy/docker/{server,web}.Dockerfile from the staged release trees.
#   deploy/release/stage-docker.sh <stage-dir> <context-dir>
#     stage-dir:   <stage>/linux-{x64,arm64}/{mineclaw,mineclaw-bridge,mineclaw-web} (package-dotnet.sh / assemble-web.sh)
#     context-dir: <ctx>/{server,web}.Dockerfile, entrypoint.sh, start.mjs, dist/{amd64,arm64}/{mineclaw,mineclaw-bridge,mineclaw-web}
set -euo pipefail
cd "$(dirname "$0")/../.."
(($# == 2)) || { echo "usage: stage-docker.sh <stage-dir> <context-dir>" >&2; exit 64; }
STAGE=$1 CTX=$2
rm -rf "$CTX"
mkdir -p "$CTX/dist"
for f in server.Dockerfile web.Dockerfile entrypoint.sh start.mjs; do sed 's/\r$//' "deploy/docker/$f" > "$CTX/$f"; done
chmod 0755 "$CTX/entrypoint.sh"
for pair in x64:amd64 arm64:arm64; do
    src=$STAGE/linux-${pair%%:*} dst=$CTX/dist/${pair##*:}
    for tree in mineclaw mineclaw-bridge mineclaw-web; do
        [[ -d $src/$tree ]] || { echo "missing $src/$tree" >&2; exit 1; }
    done
    [[ -f $src/mineclaw-web/apps/web/.next/standalone/apps/web/server.js ]] || { echo "no Next server.js in $src/mineclaw-web (hidden .next dropped?)" >&2; exit 1; }
    mkdir -p "$dst"
    cp -a "$src/mineclaw" "$src/mineclaw-bridge" "$src/mineclaw-web" "$dst/"
    # upload/download-artifact drops the exec bit
    chmod 0755 "$dst/mineclaw/MineClaw.App" "$dst/mineclaw-bridge/MineClaw.ViewerBridge"
done
du -sh "$CTX"/dist/*/*
