#!/usr/bin/env bash
# MineClaw Docker Compose installer (a release asset next to docker-compose.yml).
#
#   curl -fsSL https://github.com/Realynx/MineClaw/releases/latest/download/install-docker.sh | sudo bash
#   curl -fsSL https://github.com/Realynx/MineClaw/releases/latest/download/install-docker.sh | sudo bash -s -- \
#        [--dir /opt/mineclaw-docker] [--version X.Y.Z] [--port 8080] [--mc-host mc.example.com] [--mc-port 25565] \
#        [--mc-username NAME] [--origins https://mineclaw.example.com,...] [--from DIR] [--no-pull]
#   ... | sudo bash -s -- --uninstall [--purge] [--dir DIR]
#
#   --dir DIR        install dir: docker-compose.yml, .env (secrets, 0600), config/ (optional appsettings.json)
#   --version VER    image tag / release to install; on a re-run it updates MINECLAW_VERSION in .env (that is the update)
#   --from DIR       take docker-compose.yml from DIR instead of downloading it from the release
#   --no-pull        skip `docker compose pull` (images already loaded locally)
#   --repo O/N       GitHub repo of the release (default Realynx/MineClaw)
#   --registry P     image prefix written to a NEW .env (default ghcr.io/realynx)
#   --uninstall      `docker compose down`; keeps the volumes and the dir. With --purge: also volumes, images and the dir.
# The Anthropic API key is never a flag (it would land in the shell history): export ANTHROPIC_API_KEY before running, or
# set it later in the web's Administration page.
#
# Idempotent: .env is created only when missing (random secrets, never printed); a re-run replaces docker-compose.yml,
# applies only the flags given, pulls and runs `up -d`. First run: open the printed URL; the first visit creates the admin
# account, and the bot waits in setup mode until the API key and the Minecraft server are set in Administration.
set -euo pipefail

DIR=/opt/mineclaw-docker
VERSION=""
FROM=""
PULL=1
REPO=Realynx/MineClaw
REGISTRY=""
PORT=""
MC_HOST="" MC_PORT="" MC_USERNAME="" ORIGINS=""
UNINSTALL=0
PURGE=0

die() { echo "install-docker: $*" >&2; exit 1; }
say() { echo "==> $*"; }
need_arg() { [[ $# -ge 2 && -n $2 ]] || die "$1 needs a value"; }

while [[ $# -gt 0 ]]; do
    case $1 in
        --dir) need_arg "$@"; DIR=$2; shift 2 ;;
        --version) need_arg "$@"; VERSION=${2#v}; shift 2 ;;
        --from) need_arg "$@"; FROM=$2; shift 2 ;;
        --no-pull) PULL=0; shift ;;
        --repo) need_arg "$@"; REPO=$2; shift 2 ;;
        --registry) need_arg "$@"; REGISTRY=$2; shift 2 ;;
        --port) need_arg "$@"; PORT=$2; shift 2 ;;
        --mc-host) need_arg "$@"; MC_HOST=$2; shift 2 ;;
        --mc-port) need_arg "$@"; MC_PORT=$2; shift 2 ;;
        --mc-username) need_arg "$@"; MC_USERNAME=$2; shift 2 ;;
        --origins) need_arg "$@"; ORIGINS=$2; shift 2 ;;
        --uninstall) UNINSTALL=1; shift ;;
        --purge) PURGE=1; shift ;;
        -h|--help) sed -n '2,24p' "$0" 2>/dev/null || true; exit 0 ;;
        *) die "unknown option: $1 (see --help)" ;;
    esac
done

[[ -z $VERSION || $VERSION =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] || die "bad version '$VERSION' (X.Y.Z[-pre])"
[[ -z $PORT || $PORT =~ ^[0-9]+$ ]] || die "bad port '$PORT'"
[[ -z $MC_PORT || $MC_PORT =~ ^[0-9]+$ ]] || die "bad Minecraft port '$MC_PORT'"
[[ $REPO =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || die "bad repo '$REPO'"
case $DIR in /*) ;; *) DIR=$(pwd)/$DIR ;; esac
DIR=${DIR%/}
[[ -n $DIR && $DIR != / ]] || die "refusing --dir /"
for v in MC_HOST MC_USERNAME ORIGINS REGISTRY; do
    [[ ${!v} != *[$'\n\r"\\$`']* ]] || die "${v,,} contains characters .env can't hold"
done

command -v docker >/dev/null 2>&1 || die "docker is not installed (https://docs.docker.com/engine/install/)"
docker compose version >/dev/null 2>&1 || die "the docker compose v2 plugin is missing (docker compose version failed)"
docker info >/dev/null 2>&1 || die "can't talk to the docker daemon (start it, or run as root / a member of the docker group)"

compose() { docker compose --project-directory "$DIR" -f "$DIR/docker-compose.yml" "$@"; }

# ---- uninstall
if ((UNINSTALL)); then
    if [[ ! -f $DIR/docker-compose.yml ]]; then echo "nothing installed in $DIR"; exit 0; fi
    if ((PURGE)); then
        say "removing the containers, volumes and images"
        compose down --volumes --remove-orphans --rmi all || true
        [[ -f $DIR/.env ]] || die "$DIR has no .env; not deleting a dir this installer didn't make"
        rm -rf -- "$DIR"
        say "purged $DIR"
    else
        compose down --remove-orphans
        say "stopped; data volumes and $DIR are kept (--uninstall --purge removes them)"
    fi
    exit 0
fi

# ---- compose file
mkdir -p "$DIR/config"
chmod 0755 "$DIR" "$DIR/config"
fetch() { # url dest
    if command -v curl >/dev/null 2>&1; then curl -fsSL --retry 3 -o "$2" "$1"
    elif command -v wget >/dev/null 2>&1; then wget -q -O "$2" "$1"
    else die "need curl or wget to download $1"; fi
}
if [[ -n $FROM ]]; then
    [[ -f $FROM/docker-compose.yml ]] || die "no docker-compose.yml in $FROM"
    if [[ $(cd "$FROM" && pwd)/docker-compose.yml != "$DIR/docker-compose.yml" ]]; then
        cp "$FROM/docker-compose.yml" "$DIR/docker-compose.yml.new"
        mv -f "$DIR/docker-compose.yml.new" "$DIR/docker-compose.yml"
    fi
else
    # A re-run without --version keeps the installed version's compose file (the one matching the images in .env).
    want=$VERSION
    if [[ -z $want && -f $DIR/.env ]]; then
        want=$(grep -E '^MINECLAW_VERSION=' "$DIR/.env" | tail -n1 | cut -d= -f2 || true)
        [[ $want =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] || want=""
    fi
    if [[ -n $want ]]; then base="https://github.com/$REPO/releases/download/v$want"
    else base="https://github.com/$REPO/releases/latest/download"; fi
    tmp=$(mktemp -d)
    trap 'rm -rf "$tmp"' EXIT
    say "downloading docker-compose.yml from $base"
    fetch "$base/docker-compose.yml" "$tmp/docker-compose.yml"
    if fetch "$base/SHA256SUMS" "$tmp/SHA256SUMS" 2>/dev/null && command -v sha256sum >/dev/null 2>&1; then
        (cd "$tmp" && grep -E ' \*?docker-compose\.yml$' SHA256SUMS | sha256sum -c --quiet -) \
            || die "docker-compose.yml does not match the release's SHA256SUMS"
    else
        echo "install-docker: no SHA256SUMS checked (not found or no sha256sum)" >&2
    fi
    mv -f "$tmp/docker-compose.yml" "$DIR/docker-compose.yml"
    if [[ -z $VERSION && ! -f $DIR/.env ]] && command -v curl >/dev/null 2>&1; then
        # The tag the "latest" redirect points to, so .env pins a real version instead of :latest.
        tag=$(curl -fsSLI -o /dev/null -w '%{url_effective}' "https://github.com/$REPO/releases/latest" 2>/dev/null || true)
        tag=${tag##*/}; tag=${tag#v}
        [[ $tag =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] && VERSION=$tag
    fi
fi
chmod 0644 "$DIR/docker-compose.yml"

# ---- .env
rand_hex() {
    if command -v openssl >/dev/null 2>&1; then openssl rand -hex 32
    else od -An -tx1 -N32 /dev/urandom | tr -d ' \n'; echo; fi
}
host_ip() { hostname -I 2>/dev/null | awk '{print $1}'; }
ENV_FILE=$DIR/.env
# set_env KEY VALUE: replace or append one line (values are plain: no quotes, no newlines).
set_env() {
    local tmpf
    tmpf=$(mktemp "$DIR/.env.XXXXXX")
    chmod 0600 "$tmpf"
    if [[ -f $ENV_FILE ]]; then grep -v "^$1=" "$ENV_FILE" > "$tmpf" || true; fi
    printf '%s=%s\n' "$1" "$2" >> "$tmpf"
    mv -f "$tmpf" "$ENV_FILE"
}
if [[ ! -f $ENV_FILE ]]; then
    say "creating $ENV_FILE (random secrets)"
    port=${PORT:-8080}
    ip=$(host_ip || true)
    origins=${ORIGINS:-http://localhost:$port,http://127.0.0.1:$port${ip:+,http://$ip:$port}}
    name=$(hostname 2>/dev/null || true)
    if [[ -z $ORIGINS && -n $name && $name != localhost ]]; then origins="$origins,http://$name:$port"; fi
    old_umask=$(umask)
    umask 077
    {
        echo "# MineClaw (docker compose). Secrets: keep this file 0600 and out of backups you share."
        echo "MINECLAW_VERSION=${VERSION:-latest}"
        echo "MINECLAW_REGISTRY=${REGISTRY:-ghcr.io/realynx}"
        echo "MINECLAW_HTTP_PORT=$port"
        echo "MINECLAW_ORIGINS=$origins"
        echo "MINECLAW_COMPOSE_DIR=$DIR"
        echo "MINECLAW_SUPERVISOR_TOKEN=$(rand_hex)"
        echo "MINECLAW_BRIDGE_KEY=$(rand_hex)"
        echo "# Optional; empty = set them in the web's Administration page (the bot waits in setup mode)."
        echo "ANTHROPIC_API_KEY=${ANTHROPIC_API_KEY:-}"
        echo "MC_HOST=$MC_HOST"
        echo "MC_PORT=$MC_PORT"
        echo "MC_USERNAME=$MC_USERNAME"
        echo "# Behind a TLS reverse proxy: MINECLAW_TRUST_PROXY=true, its source IPs/CIDRs, and its https:// URL in MINECLAW_ORIGINS."
        echo "MINECLAW_TRUST_PROXY=false"
        echo "MINECLAW_TRUSTED_PROXIES="
    } > "$ENV_FILE"
    umask "$old_umask"
else
    say "keeping $ENV_FILE (secrets unchanged)"
    [[ -n $VERSION ]] && set_env MINECLAW_VERSION "$VERSION"
    [[ -n $REGISTRY ]] && set_env MINECLAW_REGISTRY "$REGISTRY"
    [[ -n $PORT ]] && set_env MINECLAW_HTTP_PORT "$PORT"
    [[ -n $ORIGINS ]] && set_env MINECLAW_ORIGINS "$ORIGINS"
    [[ -n $MC_HOST ]] && set_env MC_HOST "$MC_HOST"
    [[ -n $MC_PORT ]] && set_env MC_PORT "$MC_PORT"
    [[ -n $MC_USERNAME ]] && set_env MC_USERNAME "$MC_USERNAME"
    [[ -n ${ANTHROPIC_API_KEY:-} ]] && set_env ANTHROPIC_API_KEY "$ANTHROPIC_API_KEY"
    grep -q '^MINECLAW_SUPERVISOR_TOKEN=.' "$ENV_FILE" || set_env MINECLAW_SUPERVISOR_TOKEN "$(rand_hex)"
    grep -q '^MINECLAW_BRIDGE_KEY=.' "$ENV_FILE" || set_env MINECLAW_BRIDGE_KEY "$(rand_hex)"
    grep -q '^MINECLAW_COMPOSE_DIR=' "$ENV_FILE" || set_env MINECLAW_COMPOSE_DIR "$DIR"
fi
chmod 0600 "$ENV_FILE"

# ---- start
compose config --quiet || die "docker compose rejected $DIR/docker-compose.yml with this .env"
if ((PULL)); then say "pulling images"; compose pull; fi
say "starting"
compose up -d --remove-orphans

port=$(grep -E '^MINECLAW_HTTP_PORT=' "$ENV_FILE" | tail -n1 | cut -d= -f2)
port=${port:-8080}
if command -v curl >/dev/null 2>&1; then
    for _ in $(seq 1 60); do
        if curl -fsS -o /dev/null "http://127.0.0.1:$port/api/health" 2>/dev/null; then healthy=1; break; fi
        sleep 2
    done
    [[ ${healthy:-0} == 1 ]] || echo "install-docker: the web didn't answer on :$port yet; check: cd $DIR && docker compose logs" >&2
fi

ip=$(host_ip || true)
cat <<EOF

MineClaw is running from $DIR.
  Web:     http://${ip:-localhost}:$port   (any URL you use must be listed in MINECLAW_ORIGINS in $ENV_FILE)
  First run: the first visit creates the admin account (from this machine or your LAN, not through a proxy).
             Then open Administration and set the Anthropic API key and the Minecraft server; until then the bot
             waits in setup mode and restarts itself once they are saved.
  Logs:    cd $DIR && docker compose logs -f server
  Update:  cd $DIR && sed -i 's/^MINECLAW_VERSION=.*/MINECLAW_VERSION=X.Y.Z/' .env && docker compose pull && docker compose up -d
           (or re-run this installer with --version X.Y.Z)
  Remove:  re-run with --uninstall [--purge]
EOF
