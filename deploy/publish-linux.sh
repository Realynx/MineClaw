#!/usr/bin/env bash
# Builds a self-contained linux-x64 MineClaw into deploy/out/mineclaw (no .NET runtime needed on the host).
#
#   deploy/publish-linux.sh            # single-file binary + native libs next to it (default)
#   deploy/publish-linux.sh --multi    # classic multi-file layout (if you'd rather see every dll)
#
# Single-file is compatible with SQLite: Microsoft.Data.Sqlite's native libe_sqlite3.so (with FTS5 + R*Tree) is NOT
# bundled (IncludeNativeLibrariesForSelfExtract=false), it ships next to the binary and is loaded from there. Nothing is
# extracted to $HOME or /tmp at startup, so it works with ProtectHome/PrivateTmp. Trimming stays off (System.Text.Json
# reflection, the Anthropic SDK).
set -euo pipefail
cd "$(dirname "$0")/.."
OUT="deploy/out/mineclaw"
SINGLE=true
[[ "${1:-}" == "--multi" ]] && SINGLE=false

rm -rf "$OUT"
dotnet publish src/MineClaw.App/MineClaw.App.csproj -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=$SINGLE -p:DebugType=none -p:GenerateDocumentationFile=false -o "$OUT"

# Never ship the real (gitignored) config: secrets go in /etc/mineclaw (see deploy/README.md).
rm -f "$OUT/appsettings.json"
# Knowledge docs and the clean-state seed db travel with the binary (relative paths fall back to the exe folder).
mkdir -p "$OUT/docs"
cp -r docs/knowledge "$OUT/docs/"
if [[ -d data/seed ]]; then mkdir -p "$OUT/seed" && cp -r data/seed/. "$OUT/seed/"; fi
cp deploy/mineclaw.service deploy/mineclaw.env.example "$OUT/"
chmod +x "$OUT/MineClaw.App"

echo "published to $OUT:"
ls -la "$OUT"
