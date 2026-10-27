#!/usr/bin/env sh
# Opens the read-only Hive Supervisor viewer: an SSH tunnel to the main bot's endpoint, then the browser.
#   tools/HiveSupervisor/hive-supervisor.sh [ssh-host] [target-ip] [port]
# Token (Supervisor:Token, if configured): MINECLAW_SUPERVISOR_TOKEN=... (never printed). Ctrl+C closes the tunnel.
set -eu
SSH_HOST="${1:-alice}"
TARGET="${2:-10.1.2.73}"
PORT="${3:-8765}"
LOCAL_PORT="${LOCAL_PORT:-8765}"

echo "tunnel: localhost:$LOCAL_PORT -> $TARGET:$PORT via $SSH_HOST (Ctrl+C to close)"
ssh -N -o ExitOnForwardFailure=yes -o ServerAliveInterval=30 -L "$LOCAL_PORT:$TARGET:$PORT" "$SSH_HOST" &
TUNNEL=$!
trap 'kill "$TUNNEL" 2>/dev/null || true; echo "tunnel closed"' EXIT INT TERM

i=0
until (exec 3<>"/dev/tcp/127.0.0.1/$LOCAL_PORT") 2>/dev/null || nc -z 127.0.0.1 "$LOCAL_PORT" 2>/dev/null; do
  i=$((i + 1))
  if [ "$i" -gt 40 ] || ! kill -0 "$TUNNEL" 2>/dev/null; then echo "the tunnel didn't come up" >&2; exit 1; fi
  sleep 0.25
done

URL="http://localhost:$LOCAL_PORT/"
OPEN="$URL"
if [ -n "${MINECLAW_SUPERVISOR_TOKEN:-}" ]; then OPEN="$URL?token=$MINECLAW_SUPERVISOR_TOKEN"; fi
echo "viewer: $URL"
if command -v xdg-open >/dev/null 2>&1; then xdg-open "$OPEN" >/dev/null 2>&1 || true
elif command -v open >/dev/null 2>&1; then open "$OPEN" || true; fi
wait "$TUNNEL"
