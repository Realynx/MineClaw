#!/bin/sh
# Entrypoint of the ghcr.io/realynx/mineclaw-server image (deploy/docker/server.Dockerfile).
#
#   server [args]   the bot / control server, headless (default command)
#   bridge [args]   the web explorer's viewer bridge (same image, run beside the bot in one network namespace)
#   anything else   exec'd as is (e.g. `sh` for a look around)
#
# Short env names the compose file and the Helm chart pass are mapped to config keys here, and only when they are set and
# non-empty, so an empty value in .env never blanks a setting (the bot then starts in setup mode and the web's
# Administration page fills it in):
#   MC_HOST / MC_PORT / MC_USERNAME   -> MINECLAW_Minecraft__Host / __Port / __Username
#   MC_VERSION (auto|b1.8.1|1.12.2)   -> MINECLAW_Bot__Server__Version
#   ANTHROPIC_API_KEY                 -> MINECLAW_Agent__Llm__ApiKey
#   MINECLAW_SUPERVISOR_TOKEN         -> MINECLAW_Supervisor__Token (the bridge reads MINECLAW_SUPERVISOR_TOKEN itself)
# An explicit MINECLAW_Section__Key always wins over the short name. Secrets are never printed.
set -eu

CONFIG=/etc/mineclaw/appsettings.json

# map SHORT LONG: export LONG=$SHORT when SHORT is non-empty and LONG is unset or empty; drop an empty SHORT.
map() {
    eval "short=\${$1:-}"
    eval "long=\${$2:-}"
    if [ -n "$short" ]; then
        [ -n "$long" ] || export "$2=$short"
    else
        unset "$1"
    fi
}
map MC_HOST MINECLAW_Minecraft__Host
map MC_PORT MINECLAW_Minecraft__Port
map MC_USERNAME MINECLAW_Minecraft__Username
map MC_VERSION MINECLAW_Bot__Server__Version
map ANTHROPIC_API_KEY MINECLAW_Agent__Llm__ApiKey
map MINECLAW_SUPERVISOR_TOKEN MINECLAW_Supervisor__Token
unset short long

cmd=${1:-server}
case $cmd in
    server|bot)
        [ $# -gt 0 ] && shift
        # --config with a missing file is a startup error (exit 2), so it is only passed when the file exists; without it
        # the bot runs on env + defaults.
        if [ -f "$CONFIG" ]; then set -- --config "$CONFIG" "$@"; fi
        exec /app/server/MineClaw.App --no-tui --data-dir /var/lib/mineclaw --log-dir /var/log/mineclaw "$@"
        ;;
    bridge)
        shift
        exec /app/bridge/MineClaw.ViewerBridge "$@"
        ;;
    -*)
        if [ -f "$CONFIG" ]; then set -- --config "$CONFIG" "$@"; fi
        exec /app/server/MineClaw.App --no-tui --data-dir /var/lib/mineclaw --log-dir /var/log/mineclaw "$@"
        ;;
    *)
        exec "$@"
        ;;
esac
