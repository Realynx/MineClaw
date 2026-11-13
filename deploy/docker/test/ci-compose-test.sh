#!/usr/bin/env bash
# CI test of the Docker Compose install (.github/workflows/release.yml, job test-docker).
#
#   deploy/docker/test/ci-compose-test.sh <assets-dir> <version>
#
# Expects the amd64 images ghcr.io/realynx/mineclaw-{server,web}:<version> already `docker load`ed (nothing is pulled) and
# <assets-dir> holding the release's install-docker.sh + docker-compose.yml. Installs with the one-liner script, waits for
# /api/health (ok, bridge up), checks first-run state (/api/setup needed, the bot logged `setup mode:`), re-runs the
# installer (idempotent: secrets unchanged), then uninstalls with --purge. Container logs are printed on failure.
set -euo pipefail
(($# == 2)) || { sed -n '2,10p' "$0"; exit 64; }
ASSETS=$(cd "$1" && pwd)
VER=$2
PORT=8080
WORK=$(mktemp -d)
DIR=$WORK/mineclaw-docker
INSTALLER=$ASSETS/install-docker.sh
[[ -f $INSTALLER && -f $ASSETS/docker-compose.yml ]] || { echo "need install-docker.sh and docker-compose.yml in $ASSETS" >&2; exit 1; }

compose() { docker compose --project-directory "$DIR" -f "$DIR/docker-compose.yml" "$@"; }
step() { echo; echo "::group::$*"; }
endstep() { echo "::endgroup::"; }
on_fail() {
    local rc=$?
    echo "::endgroup::"
    echo "::error::compose test failed (exit $rc)"
    if [[ -f $DIR/docker-compose.yml ]]; then
        compose ps -a || true
        for s in server bridge web net; do echo "---- logs: $s"; compose logs --no-color --tail 300 "$s" || true; done
    fi
    exit "$rc"
}
trap on_fail ERR

# wait_for SECONDS DESCRIPTION COMMAND...: retry COMMAND every 3 s until it succeeds.
wait_for() {
    local secs=$1 what=$2; shift 2
    local end=$((SECONDS + secs))
    until "$@"; do
        ((SECONDS < end)) || { echo "timed out after ${secs}s waiting for $what" >&2; return 1; }
        sleep 3
    done
    echo "ok: $what"
}
health_up() {
    local body
    body=$(curl -fsS --max-time 5 "http://127.0.0.1:$PORT/api/health" 2>/dev/null) || return 1
    [[ $body == *'"ok":true'* && $body == *'"bridge":"up"'* ]] || { echo "health: $body"; return 1; }
}
setup_needed() {
    local body
    body=$(curl -fsS --max-time 5 "http://127.0.0.1:$PORT/api/setup") || return 1
    echo "setup: $body"
    [[ $body == *'"needed":true'* ]]
}
setup_mode_logged() { compose logs --no-color server 2>/dev/null | grep -q 'setup mode:'; }
secrets_of() { grep -E '^(MINECLAW_SUPERVISOR_TOKEN|MINECLAW_BRIDGE_KEY)=' "$DIR/.env" | sha256sum | cut -d' ' -f1; }

step "install (version $VER, no pull)"
docker image inspect "ghcr.io/realynx/mineclaw-server:$VER" "ghcr.io/realynx/mineclaw-web:$VER" >/dev/null
env -u ANTHROPIC_API_KEY bash "$INSTALLER" --from "$ASSETS" --version "$VER" --dir "$DIR" --no-pull --port "$PORT"
[[ $(stat -c %a "$DIR/.env") == 600 ]] || { echo ".env is not 0600" >&2; false; }
grep -q "^MINECLAW_VERSION=$VER$" "$DIR/.env"
endstep

step "health, first-run state"
wait_for 300 "/api/health ok with the bridge up" health_up
wait_for 30 "/api/setup says setup is needed" setup_needed
wait_for 180 "the bot logged 'setup mode:'" setup_mode_logged
compose ps
endstep

step "re-run the installer (idempotent)"
before=$(secrets_of)
bash "$INSTALLER" --from "$ASSETS" --version "$VER" --dir "$DIR" --no-pull
after=$(secrets_of)
[[ $before == "$after" ]] || { echo "the re-run changed the secrets in .env" >&2; false; }
echo "ok: secrets unchanged"
wait_for 300 "/api/health ok with the bridge up after the re-run" health_up
endstep

step "uninstall --purge"
project=$(basename "$DIR")
bash "$INSTALLER" --uninstall --purge --dir "$DIR"
[[ ! -e $DIR ]] || { echo "$DIR still exists" >&2; false; }
if docker volume ls -q | grep -q "^${project}_"; then echo "volumes left behind:" >&2; docker volume ls -q | grep "^${project}_" >&2; false; fi
endstep
trap - ERR
rm -rf "$WORK"
echo "compose install test passed"
