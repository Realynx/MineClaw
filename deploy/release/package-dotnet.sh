#!/usr/bin/env bash
# Release packaging of the .NET side for ONE runtime: the control server (src/MineClaw.App) and, on Linux, the viewer bridge
# (tools/ViewerBridge). Used by .github/workflows/release.yml; runs on any machine with the .NET 10 SDK.
#
#   deploy/release/package-dotnet.sh <rid> <version> <commit> <assets-dir> [stage-dir]
#     rid        linux-x64 | linux-arm64 | win-x64
#     assets-dir gets mineclaw-server-<version>-<rid>.tar.gz (.zip for win-x64) and mineclaw-bridge-<version>-<rid>.tar.gz
#     stage-dir  (optional) keeps the unpacked trees as <stage-dir>/<rid>/mineclaw and .../mineclaw-bridge (the image builds
#                copy them)
#
# The trees are the same ones deploy/publish-linux.sh and deploy/deploy-web.ps1 build for the owner's container, plus a
# RELEASE.json with the version. Nothing from the developer's machine is shipped: no appsettings.json, no game assets
# (the 1.12.2 lang file is only copied when a local launcher install has the jar, which CI runners don't).
set -euo pipefail
cd "$(dirname "$0")/../.."
(($# >= 4)) || { sed -n '2,15p' "$0"; exit 64; }
RID=$1 VER=$2 SHA=$3 ASSETS=$4 STAGE=${5:-}
[[ $VER =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] || { echo "bad version '$VER'" >&2; exit 64; }
mkdir -p "$ASSETS"
ASSETS=$(cd "$ASSETS" && pwd)
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
BUILT=$(date -u +%Y-%m-%dT%H:%M:%SZ)
STAMP=(-p:Version="${VER%%-*}" -p:InformationalVersion="$VER+$SHA")
release_json() { printf '{"version":"%s","commit":"%s","built":"%s","component":"%s","rid":"%s"}\n' "$VER" "$SHA" "$BUILT" "$1" "$RID"; }

# ---- control server
if [[ $RID == linux-* ]]; then
    MINECLAW_RID=$RID MINECLAW_OUT="$WORK/mineclaw" MINECLAW_VERSION=$VER MINECLAW_COMMIT=$SHA deploy/publish-linux.sh
else
    dotnet publish src/MineClaw.App/MineClaw.App.csproj -c Release -r "$RID" --self-contained true -m:1 \
        -p:PublishSingleFile=true -p:DebugType=none -p:GenerateDocumentationFile=false "${STAMP[@]}" -o "$WORK/mineclaw"
    rm -f "$WORK/mineclaw/appsettings.json"
    mkdir -p "$WORK/mineclaw/docs" && cp -r docs/knowledge "$WORK/mineclaw/docs/"
    if [[ -d data/seed ]]; then mkdir -p "$WORK/mineclaw/seed" && cp -r data/seed/. "$WORK/mineclaw/seed/"; fi
    cp src/MineClaw.App/appsettings.example.json "$WORK/mineclaw/"
    release_json server > "$WORK/mineclaw/RELEASE.json"
fi
# No game assets in a public release, ever.
rm -rf "$WORK/mineclaw/assets/1.12.2"
cp LICENSE THIRD_PARTY_NOTICES.md "$WORK/mineclaw/"

if [[ $RID == win-* ]]; then
    (cd "$WORK" && if command -v zip >/dev/null; then zip -qr "$ASSETS/mineclaw-server-$VER-$RID.zip" mineclaw
     else powershell -NoProfile -Command "Compress-Archive -Path mineclaw -DestinationPath '$ASSETS/mineclaw-server-$VER-$RID.zip'"; fi)
else
    tar -C "$WORK" -czf "$ASSETS/mineclaw-server-$VER-$RID.tar.gz" --owner=0 --group=0 mineclaw
fi

# ---- viewer bridge (Linux only: it runs beside the bot on the server)
if [[ $RID == linux-* ]]; then
    B="$WORK/mineclaw-bridge"
    dotnet publish tools/ViewerBridge/MineClaw.ViewerBridge.csproj -c Release -r "$RID" --self-contained true -m:1 \
        -p:PublishSingleFile=true -p:DebugType=none -p:GenerateDocumentationFile=false "${STAMP[@]}" -o "$B"
    rm -f "$B"/appsettings*.json   # only the .example ships; config comes from /etc (or the container env)
    mkdir -p "$B/shards/defaults"
    find shards/defaults -maxdepth 1 -type f \( -name '*.mcshard' -o -name '*.json' \) -exec cp {} "$B/shards/defaults/" \;
    cp LICENSE "$B/"
    release_json bridge > "$B/RELEASE.json"
    chmod +x "$B/MineClaw.ViewerBridge"
    tar -C "$WORK" -czf "$ASSETS/mineclaw-bridge-$VER-$RID.tar.gz" --owner=0 --group=0 mineclaw-bridge
fi

if [[ -n $STAGE ]]; then
    mkdir -p "$STAGE/$RID"
    rm -rf "${STAGE:?}/$RID/mineclaw" "${STAGE:?}/$RID/mineclaw-bridge"
    cp -a "$WORK/mineclaw" "$STAGE/$RID/"
    [[ -d $WORK/mineclaw-bridge ]] && cp -a "$WORK/mineclaw-bridge" "$STAGE/$RID/"
fi
ls -la "$ASSETS"
