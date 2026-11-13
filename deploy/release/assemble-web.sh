#!/usr/bin/env bash
# Release packaging of the web stack (Nest web/apps/api + the Next standalone web/apps/web) for ONE Linux arch. The same
# tree deploy/deploy-web.ps1 assembles for the owner's container (step 3 there), so mineclaw-web-release installs either.
# Run after the web build:  (cd web && npm ci && npx turbo run build --filter=@mineclaw/api --filter=@mineclaw/web)
#
#   deploy/release/assemble-web.sh <arch> <version> <commit> <assets-dir> [stage-dir]
#     arch       x64 | arm64 (linux, glibc)
#     assets-dir gets mineclaw-web-<version>-linux-<arch>.tar.gz
#     stage-dir  (optional) keeps the unpacked tree as <stage-dir>/linux-<arch>/mineclaw-web (the web image copies it)
#
# Only the api's production dependencies ship, resolved from the lockfile for the target platform
# (npm ci --os=linux --cpu=<arch> --libc=glibc), so @node-rs/argon2 carries the right native binding.
set -euo pipefail
cd "$(dirname "$0")/../.."
(($# >= 4)) || { sed -n '2,13p' "$0"; exit 64; }
ARCH=$1 VER=$2 SHA=$3 ASSETS=$4 STAGE=${5:-}
case $ARCH in x64 | arm64) ;; *) echo "arch must be x64 or arm64" >&2; exit 64 ;; esac
[[ $VER =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] || { echo "bad version '$VER'" >&2; exit 64; }
mkdir -p "$ASSETS"
ASSETS=$(cd "$ASSETS" && pwd)
REPO=$(pwd)
WEB=$REPO/web
API=$WEB/apps/api
APP=$WEB/apps/web
[[ -f $API/dist/main.js ]] || { echo "no web/apps/api/dist/main.js: build the web first" >&2; exit 1; }
[[ -f $APP/.next/standalone/apps/web/server.js ]] || { echo "no Next standalone server: build the web first" >&2; exit 1; }
[[ -d $WEB/packages/contracts/dist ]] || { echo "no web/packages/contracts/dist: build the web first" >&2; exit 1; }

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
OUT=$WORK/mineclaw-web

# ---- api: package.json + dist
mkdir -p "$OUT/apps/api"
cp "$API/package.json" "$OUT/apps/api/"
cp -r "$API/dist" "$OUT/apps/api/"

# ---- api production deps for linux-<arch>-gnu: a scratch copy of the workspace manifests, so npm ci resolves exactly the lockfile
DEPS=$WORK/api-deps
mkdir -p "$DEPS"
cp "$WEB/package.json" "$WEB/package-lock.json" "$DEPS/"
for m in "$WEB"/apps/*/package.json "$WEB"/packages/*/package.json; do
    rel=${m#"$WEB/"}
    mkdir -p "$DEPS/$(dirname "$rel")"
    cp "$m" "$DEPS/$rel"
done
(cd "$DEPS" && npm ci --omit=dev --workspace=@mineclaw/api --include-workspace-root=false \
    --os=linux --cpu="$ARCH" --libc=glibc --ignore-scripts --no-audit --no-fund)
NM=$DEPS/node_modules
# Workspace links -> dropped; contracts comes back as a real package.
rm -rf "$NM/@mineclaw"
mkdir -p "$NM/@mineclaw/contracts"
cp "$WEB/packages/contracts/package.json" "$NM/@mineclaw/contracts/"
cp -r "$WEB/packages/contracts/dist" "$NM/@mineclaw/contracts/"
# npm may keep conflicting versions under a workspace instead of hoisting them: keep both locations.
[[ -d $DEPS/apps/api/node_modules ]] && cp -r "$DEPS/apps/api/node_modules" "$OUT/apps/api/node_modules"
[[ -d $DEPS/packages/contracts/node_modules ]] && cp -r "$DEPS/packages/contracts/node_modules" "$NM/@mineclaw/contracts/node_modules"
# Only this arch's glibc argon2 binding.
for d in "$NM"/@node-rs/argon2-*; do
    [[ $(basename "$d") == "argon2-linux-$ARCH-gnu" ]] || rm -rf "$d"
done
ARGON=$NM/@node-rs/argon2-linux-$ARCH-gnu/argon2.linux-$ARCH-gnu.node
[[ -f $ARGON ]] || { echo "no linux-$ARCH-gnu argon2 binding at $ARGON" >&2; exit 1; }
mv "$NM" "$OUT/node_modules"
node deploy/check-node-deps.mjs "$OUT/apps/api/package.json"

# ---- Next standalone + static + public (the standalone output leaves those two out on purpose)
SA=$OUT/apps/web/.next/standalone
mkdir -p "$(dirname "$SA")"
cp -r "$APP/.next/standalone" "$SA"
cp -r "$APP/.next/static" "$SA/apps/web/.next/static"
cp -r "$APP/public" "$SA/apps/web/public"
# sharp's prebuilt binaries for other platforms (unused: sharp falls back to its wasm build).
if [[ -d $SA/node_modules/@img ]]; then
    find "$SA/node_modules/@img" -mindepth 1 -maxdepth 1 -type d \
        \( -name 'sharp-win32*' -o -name 'sharp-darwin*' -o -name 'sharp-libvips-darwin*' \
           -o -name "sharp-*linuxmusl*" \) -exec rm -rf {} +
    other=$([[ $ARCH == x64 ]] && echo arm64 || echo x64)
    find "$SA/node_modules/@img" -mindepth 1 -maxdepth 1 -type d -name "*linux-$other" -exec rm -rf {} +
fi

# ---- checks: no links, no native module for another platform, no game assets
if find "$OUT" -type l | grep -q .; then
    # Next's standalone may contain symlinks into the workspace; resolve them into real copies.
    find "$OUT" -type l | while read -r l; do t=$(readlink -f "$l") && rm "$l" && cp -r "$t" "$l"; done
fi
want=$([[ $ARCH == x64 ]] && echo 'x86-64' || echo 'aarch64')
while read -r n; do
    file -b "$n" | grep -q "$want" || { echo "native module for the wrong platform: $n ($(file -b "$n"))" >&2; exit 1; }
done < <(find "$OUT" -name '*.node' -type f)
if find "$OUT" -path '*/assets/minecraft/*' -type f | grep -q .; then echo "game assets in the web release" >&2; exit 1; fi

printf '{"version":"%s","commit":"%s","built":"%s","component":"web","arch":"linux-%s","node":"%s"}\n' \
    "$VER" "$SHA" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$ARCH" "$(node --version)" > "$OUT/RELEASE.json"
cp LICENSE "$OUT/"
rm -rf "$DEPS"

tar -C "$WORK" -czf "$ASSETS/mineclaw-web-$VER-linux-$ARCH.tar.gz" --owner=0 --group=0 mineclaw-web
if [[ -n $STAGE ]]; then
    mkdir -p "$STAGE/linux-$ARCH"
    rm -rf "${STAGE:?}/linux-$ARCH/mineclaw-web"
    cp -a "$OUT" "$STAGE/linux-$ARCH/"
fi
echo "web release linux-$ARCH: $(du -sh "$ASSETS/mineclaw-web-$VER-linux-$ARCH.tar.gz" | cut -f1)"
