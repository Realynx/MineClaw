#!/usr/bin/env bash
# The install block at the top of every GitHub Release's notes (the generated changelog follows it).
#   deploy/release/release-notes.sh <version> <ghcr owner, lower case> <owner/repo>
set -euo pipefail
(($# == 3)) || { echo "usage: release-notes.sh <version> <ghcr-owner> <owner/repo>" >&2; exit 64; }
v=$1 o=$2 repo=$3
dl="https://github.com/$repo/releases/download/v$v"
cat <<EOF
## Install

| | |
|---|---|
| **Linux (systemd)** | \`curl -fsSL $dl/install.sh \| sudo bash -s -- --version $v\` |
| **Docker compose** | \`curl -fsSL $dl/install-docker.sh \| bash -s -- --version $v\` |
| **Kubernetes (Helm)** | \`helm install mineclaw oci://ghcr.io/$o/charts/mineclaw --version $v\` |

Then open the web panel on port 8080: the first visit creates the admin account, and the Anthropic API key and the
Minecraft server are set in **Administration** (the bot waits in setup mode until then). Already installed? The panel
shows an "update available" notice; see [docs/install.md](https://github.com/$repo/blob/v$v/docs/install.md#updating).

Images: \`ghcr.io/$o/mineclaw-server:$v\`, \`ghcr.io/$o/mineclaw-web:$v\` (linux/amd64, linux/arm64).
Checksums: \`SHA256SUMS\` (\`sha256sum -c SHA256SUMS --ignore-missing\`).

EOF
