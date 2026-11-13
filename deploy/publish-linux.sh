#!/usr/bin/env bash
# Builds a self-contained Linux MineClaw into deploy/out/mineclaw (no .NET runtime needed on the host).
#
#   deploy/publish-linux.sh            # single-file binary + native libs next to it (default)
#   deploy/publish-linux.sh --multi    # classic multi-file layout (if you'd rather see every dll)
#
#   MINECLAW_RID=linux-arm64           # another runtime (default linux-x64); the release workflow builds x64 and arm64
#   MINECLAW_OUT=dir                   # another output dir (default deploy/out/mineclaw)
#   MINECLAW_VERSION=1.2.3 MINECLAW_COMMIT=<sha>
#                                      # stamp the build (AssemblyInformationalVersion 1.2.3+<sha>) and write RELEASE.json
#
# Single-file is compatible with SQLite: Microsoft.Data.Sqlite's native libe_sqlite3.so (with FTS5 + R*Tree) is NOT
# bundled (IncludeNativeLibrariesForSelfExtract=false), it ships next to the binary and is loaded from there. Nothing is
# extracted to $HOME or /tmp at startup, so it works with ProtectHome/PrivateTmp. Trimming stays off (System.Text.Json
# reflection, the Anthropic SDK).
set -euo pipefail
cd "$(dirname "$0")/.."
RID=${MINECLAW_RID:-linux-x64}
OUT=${MINECLAW_OUT:-deploy/out/mineclaw}
SINGLE=true
[[ "${1:-}" == "--multi" ]] && SINGLE=false

STAMP=()
if [[ -n ${MINECLAW_VERSION:-} ]]; then
    # Version= takes only the numeric part; the informational version carries the full tag and the commit.
    STAMP=(-p:Version="${MINECLAW_VERSION%%-*}" -p:InformationalVersion="${MINECLAW_VERSION}+${MINECLAW_COMMIT:-unknown}")
fi

rm -rf "$OUT"
dotnet publish src/MineClaw.App/MineClaw.App.csproj -c Release -r "$RID" --self-contained true -m:1 \
  -p:PublishSingleFile=$SINGLE -p:DebugType=none -p:GenerateDocumentationFile=false "${STAMP[@]}" -o "$OUT"

# Never ship the real (gitignored) config: secrets go in /etc/mineclaw (see deploy/README.md).
rm -f "$OUT/appsettings.json"
# Knowledge docs and the clean-state seed db travel with the binary (relative paths fall back to the exe folder).
mkdir -p "$OUT/docs"
cp -r docs/knowledge "$OUT/docs/"
if [[ -d data/seed ]]; then mkdir -p "$OUT/seed" && cp -r data/seed/. "$OUT/seed/"; fi
cp deploy/mineclaw.service deploy/mineclaw.env.example "$OUT/"
[[ -f "$OUT/MineClaw.App" ]] && chmod +x "$OUT/MineClaw.App"
if [[ -n ${MINECLAW_VERSION:-} ]]; then
    printf '{"version":"%s","commit":"%s","built":"%s","component":"server","rid":"%s"}\n' \
        "$MINECLAW_VERSION" "${MINECLAW_COMMIT:-unknown}" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$RID" > "$OUT/RELEASE.json"
fi

echo "published $RID to $OUT:"
ls -la "$OUT"
