#!/usr/bin/env bash
# MineClaw Linux installer (systemd). Installs, upgrades or repairs the bot ("server") and the Web Explorer ("web": bridge + api
# + ui) on one host, from a GitHub release. Safe to re-run: config, keys, users and data are kept.
#
#   curl -fsSL https://github.com/Realynx/MineClaw/releases/latest/download/install.sh | sudo bash -s -- [options]
#
#   --version VER|latest     release to install (default: latest; a re-run keeps the components it finds)
#   --components LIST        server | server,web (default server,web; web needs the server on the same host)
#   --from DIR               use release assets from DIR (flat, with SHA256SUMS) instead of downloading them
#   --repo OWNER/NAME        GitHub repo of the releases (default Realynx/MineClaw; remembered in /etc/mineclaw/install.conf)
#   --mc-host H --mc-port P --mc-username U [--mc-version auto|b1.8.1|1.12.2]
#                            Minecraft server for a NEW bot config (optional: Administration sets them later)
#   --api-key-file FILE      Anthropic API key (first line of FILE) -> /etc/mineclaw/mineclaw.env (0600 root); never pass a key in argv
#   --origin URL             web AllowedOrigins for a NEW web config (repeatable; default http://<primary IP>:<port>,
#                            http://<hostname>:<port>, http://localhost:<port>)
#   --port N                 web port for a NEW web config (default 8080)
#   --no-start               install everything, start nothing
#   --reinstall              install the release again even when the same version is current (a new release id)
#   --timeout SEC            health check budget per component (default 180 bot, 120 web)
#   --dry-run                print the plan and check the prerequisites; changes nothing
#   --uninstall [--purge [--yes]]
#                            remove units, helpers and releases; --purge also deletes /etc and /var/lib data and the users
#                            (asks first unless --yes)
#
# Layout: /opt/mineclaw{,-web,-bridge}/releases/<yyyyMMdd-HHmmss> + current, /etc/mineclaw/{appsettings.json,mineclaw.env,
# install.conf}, /etc/mineclaw-web/{config.json,bridge.key,users.json}, /etc/mineclaw-bridge/bridge.json, /var/lib/mineclaw,
# /var/lib/mineclaw-web, /var/log/mineclaw; users mineclaw, mineclaw-web, mineclaw-bridge; helpers mineclaw-release,
# mineclaw-web-release, mineclaw-update, mineclaw-web-users in /usr/local/sbin. Secrets are never printed.
set -euo pipefail
export LC_ALL=C
umask 022

# Everything runs from main() at the very end, so a download cut short by `curl | bash` never runs half a script.

DEFAULT_REPO=Realynx/MineClaw
NODE_VERSION=22.23.4
NODE_SHA256_x64=1805bcd35e6815559bd2eb97db438d3fdb0eb3a5e6a977b7c1badca76337e9ce   # node-v22.23.4-linux-x64.tar.xz
NODE_SHA256_arm64=6287aa46b6c375d816e63ae66d024fbbfd70404825aabdfdf898a970871d2f4b # node-v22.23.4-linux-arm64.tar.xz
VER_RE='^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$'
REPO_RE='^[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}$'

CONF=/etc/mineclaw/install.conf
LOCK=/run/lock/mineclaw-install.lock
SBIN=/usr/local/sbin
UNIT_DIR=/etc/systemd/system
UPDATE_DIR=/var/lib/mineclaw-web/update
SERVER_UNITS=(mineclaw)
WEB_UNITS=(mineclaw-bridge mineclaw-web-ui mineclaw-web-api)
# Release assets besides the tarballs (flat; deploy/install/release-assets.txt maps them to their source in the repo).
HELPERS=(mineclaw-release.sh:mineclaw-release mineclaw-web-release.sh:mineclaw-web-release mineclaw-update.sh:mineclaw-update
    mineclaw-web-users.sh:mineclaw-web-users)
UNIT_FILES=(mineclaw.service mineclaw-bridge.service mineclaw-web-ui.service mineclaw-web-api.service mineclaw-update.service
    mineclaw-update.path)

VERSION="" COMPONENTS="" FROM="" REPO="" MC_HOST="" MC_PORT="" MC_USER="" MC_VERSION="" KEY_FILE="" PORT="" ORIGINS=()
NO_START=0 REINSTALL=0 DRY=0 UNINSTALL=0 PURGE=0 YES=0 UPDATE=0 TIMEOUT=${MINECLAW_HEALTH_TIMEOUT:-}
ARCH="" WORK="" ASSETS="" WEB=0 NODE_MANAGED=0 RESTART_BOT=0 RESTART_WEB=0 NODE_BIN=/usr/local/bin/node

say() { echo "== $*"; }
note() { echo "   $*"; }
warn() { echo "WARNING: $*" >&2; }
die() { echo "ERROR: $*" >&2; exit 1; }
usage() { sed -n '2,27p' "${BASH_SOURCE[0]}" 2>/dev/null || echo "see the header of deploy/install/install.sh"; }
need() { [[ $# -ge 2 && -n $2 ]] || die "$1 needs a value"; }
has() { [[ ,$COMPONENTS, == *,$1,* ]]; }

parse_args() {
    while (($#)); do
        case $1 in
            --version) need "$@"; VERSION=$2; shift 2 ;;
            --components) need "$@"; COMPONENTS=$2; shift 2 ;;
            --from) need "$@"; FROM=$2; shift 2 ;;
            --repo) need "$@"; REPO=$2; shift 2 ;;
            --mc-host) need "$@"; MC_HOST=$2; shift 2 ;;
            --mc-port) need "$@"; MC_PORT=$2; shift 2 ;;
            --mc-username) need "$@"; MC_USER=$2; shift 2 ;;
            --mc-version) need "$@"; MC_VERSION=$2; shift 2 ;;
            --api-key-file) need "$@"; KEY_FILE=$2; shift 2 ;;
            --api-key | --api-key=*) die "never pass an API key on the command line: put it in a file and use --api-key-file FILE" ;;
            --origin) need "$@"; ORIGINS+=("$2"); shift 2 ;;
            --port) need "$@"; PORT=$2; shift 2 ;;
            --timeout) need "$@"; TIMEOUT=$2; shift 2 ;;
            --no-start) NO_START=1; shift ;;
            --reinstall) REINSTALL=1; shift ;;
            --dry-run) DRY=1; shift ;;
            --uninstall) UNINSTALL=1; shift ;;
            --purge) PURGE=1; shift ;;
            --yes | -y) YES=1; shift ;;
            --update) UPDATE=1; shift ;; # internal: run by mineclaw-update (no packages, must already be installed)
            -h | --help) usage; exit 0 ;;
            *) usage >&2; die "unknown option $1" ;;
        esac
    done
    if [[ -n $VERSION && $VERSION != latest ]]; then
        VERSION=${VERSION#v}
        [[ $VERSION =~ $VER_RE ]] || die "bad --version '$VERSION' (want X.Y.Z or X.Y.Z-pre)"
    fi
    [[ -z $REPO || $REPO =~ $REPO_RE ]] || die "bad --repo '$REPO' (want OWNER/NAME)"
    [[ -z $MC_HOST || $MC_HOST =~ ^[A-Za-z0-9._:-]{1,253}$ ]] || die "bad --mc-host '$MC_HOST'"
    [[ -z $MC_PORT || ($MC_PORT =~ ^[0-9]{1,5}$ && $MC_PORT -ge 1 && $MC_PORT -le 65535) ]] || die "bad --mc-port '$MC_PORT'"
    [[ -z $MC_VERSION || $MC_VERSION =~ ^(auto|b1\.8\.1|1\.12\.2)$ ]] || die "bad --mc-version '$MC_VERSION' (auto, b1.8.1 or 1.12.2)"
    [[ -z $MC_USER || $MC_USER =~ ^[A-Za-z0-9_]{1,16}$ ]] || die "bad --mc-username '$MC_USER' (1-16 of A-Z a-z 0-9 _)"
    [[ -z $PORT || ($PORT =~ ^[0-9]{1,5}$ && $PORT -ge 1 && $PORT -le 65535) ]] || die "bad --port '$PORT'"
    [[ -z $TIMEOUT || $TIMEOUT =~ ^[0-9]{1,5}$ ]] || die "bad --timeout '$TIMEOUT'"
    local o
    for o in "${ORIGINS[@]}"; do
        [[ $o =~ ^https?://[A-Za-z0-9._:-]+(:[0-9]{1,5})?$ ]] || die "bad --origin '$o' (want http(s)://host[:port], no path)"
    done
    if [[ -n $KEY_FILE ]]; then [[ -r $KEY_FILE ]] || die "can't read --api-key-file $KEY_FILE"; fi
    if [[ -n $FROM ]]; then [[ -d $FROM ]] || die "--from $FROM is not a directory"; FROM=$(cd "$FROM" && pwd); fi
    ((PURGE == 0 || UNINSTALL)) || die "--purge goes with --uninstall"
}

conf_get() { # $1 key: a value from install.conf (parsed, never sourced)
    [[ -f $CONF ]] || return 0
    sed -n "s/^$1=\\([A-Za-z0-9_.,\\/:-]*\\)\$/\\1/p" "$CONF" | tail -n1
}

detect_platform() {
    case $(uname -m) in
        x86_64 | amd64) ARCH=x64 ;;
        aarch64 | arm64) ARCH=arm64 ;;
        *) die "unsupported CPU $(uname -m): releases exist for x86_64 and aarch64" ;;
    esac
    [[ $(uname -s) == Linux ]] || die "Linux only (use the Docker install elsewhere)"
    [[ -d /run/systemd/system ]] || die "systemd is not running here; use the Docker install (install-docker.sh) instead"
    local id="" like=""
    if [[ -r /etc/os-release ]]; then
        id=$(. /etc/os-release && echo "${ID:-}"); like=$(. /etc/os-release && echo "${ID_LIKE:-}")
    fi
    if [[ " $id $like " != *" debian "* && " $id $like " != *" ubuntu "* ]]; then
        warn "${id:-this distribution} is not Debian/Ubuntu: best effort (packages via dnf/yum if present, else check the deps yourself)"
    fi
}

defaults_from_conf() {
    local c r p
    c=$(conf_get COMPONENTS); r=$(conf_get REPO)
    if [[ -z $COMPONENTS ]]; then COMPONENTS=${c:-server,web}; fi
    COMPONENTS=$(echo "$COMPONENTS" | tr -d ' ' | tr ',' '\n' | sort -u | paste -sd, -)
    case $COMPONENTS in
        server) ;;
        server,web) ;;
        web) die "--components web needs the server on the same host: use server,web" ;;
        *) die "bad --components '$COMPONENTS' (server or server,web)" ;;
    esac
    if [[ $c == server,web && $COMPONENTS == server ]]; then
        warn "the web is installed and stays installed (--uninstall removes everything)"
        COMPONENTS=server,web
    fi
    if has web; then WEB=1; fi
    REPO=${REPO:-${r:-$DEFAULT_REPO}}
    [[ $REPO =~ $REPO_RE ]] || die "bad REPO in $CONF"
    p=$(conf_get PORT)
    PORT=${PORT:-${p:-8080}}
    if [[ $(conf_get NODE_MANAGED) == 1 ]]; then NODE_MANAGED=1; fi
}

# ------------------------------------------------------------------ packages
deb_installed() { [[ $(dpkg-query -W -f='${Status}' "$1" 2>/dev/null) == "install ok installed" ]]; }

install_packages() {
    say "packages"
    if command -v apt-get >/dev/null 2>&1; then
        export DEBIAN_FRONTEND=noninteractive
        local want=(ca-certificates tzdata zlib1g curl util-linux) missing=() p icu ssl
        ((WEB)) && want+=(xz-utils)
        for p in "${want[@]}"; do deb_installed "$p" || missing+=("$p"); done
        # grep without -q reads all of ldconfig's output (no SIGPIPE false negative under pipefail)
        if ! ldconfig -p 2>/dev/null | grep 'libicuuc\.so\.' >/dev/null; then missing+=(libicu); fi
        if ! ldconfig -p 2>/dev/null | grep 'libssl\.so\.3' >/dev/null; then missing+=(libssl); fi
        if ((${#missing[@]} == 0)); then note "all present"; return 0; fi
        note "installing: ${missing[*]}"
        apt-get update -qq
        local final=()
        for p in "${missing[@]}"; do
            case $p in
                libicu)
                    icu=$(apt-cache search --names-only '^libicu[0-9]+$' | awk '{print $1}' | sort -V | tail -n1)
                    [[ -n $icu ]] || die "no libicuNN package found"; final+=("$icu") ;;
                libssl)
                    ssl=$(apt-cache show libssl3t64 >/dev/null 2>&1 && echo libssl3t64 || echo libssl3); final+=("$ssl") ;;
                *) final+=("$p") ;;
            esac
        done
        apt-get install -y -qq --no-install-recommends "${final[@]}" >/dev/null
    elif command -v dnf >/dev/null 2>&1 || command -v yum >/dev/null 2>&1; then
        local pm; pm=$(command -v dnf || command -v yum)
        "$pm" install -y ca-certificates tzdata libicu zlib curl xz openssl-libs tar gzip util-linux >/dev/null ||
            warn "$pm could not install every dependency; continuing"
    else
        warn "no apt-get/dnf: make sure ca-certificates, tzdata, ICU, OpenSSL 3, zlib, curl, xz, tar, flock are installed"
    fi
}

check_commands() {
    local c miss=()
    for c in curl tar gzip sha256sum systemctl journalctl flock setpriv install mktemp awk sed od; do
        command -v "$c" >/dev/null 2>&1 || miss+=("$c")
    done
    if ((WEB)); then command -v xz >/dev/null 2>&1 || miss+=(xz); fi
    ((${#miss[@]} == 0)) || die "missing commands: ${miss[*]}"
}

# ------------------------------------------------------------------ assets
fetch() { # $1 url, $2 dst
    curl -fsSL --proto '=https' --tlsv1.2 --retry 3 --retry-delay 2 --connect-timeout 20 -o "$2" "$1"
}

resolve_version() {
    if [[ -n $VERSION && $VERSION != latest ]]; then return 0; fi
    if [[ -n $FROM ]]; then
        local f n=()
        for f in "$FROM"/mineclaw-server-*-linux-"$ARCH".tar.gz; do [[ -f $f ]] && n+=("$f"); done
        ((${#n[@]} == 1)) || die "$FROM must hold exactly one mineclaw-server-<version>-linux-$ARCH.tar.gz (found ${#n[@]}); pass --version"
        f=${n[0]##*/mineclaw-server-}; VERSION=${f%-linux-"$ARCH".tar.gz}
    else
        local url
        url=$(curl -fsSLI --proto '=https' --retry 3 -o /dev/null -w '%{url_effective}' "https://github.com/$REPO/releases/latest" 2>/dev/null || true)
        VERSION=${url##*/tag/}; VERSION=${VERSION#v}
        if ! [[ $VERSION =~ $VER_RE ]]; then # the API as a fallback (rate limited, but no redirect parsing)
            VERSION=$(curl -fsSL --proto '=https' --retry 3 -H 'Accept: application/vnd.github+json' \
                "https://api.github.com/repos/$REPO/releases/latest" 2>/dev/null |
                sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"v\{0,1\}\([^"]*\)".*/\1/p' | head -n1 || true)
        fi
    fi
    [[ $VERSION =~ $VER_RE ]] || die "could not resolve the latest release of $REPO (pass --version X.Y.Z)"
}

asset_names() {
    local h
    for h in "${HELPERS[@]}"; do echo "${h%%:*}"; done
    printf '%s\n' "${UNIT_FILES[@]}"
    echo "mineclaw-server-$VERSION-linux-$ARCH.tar.gz"
    if ((WEB)); then
        echo "mineclaw-bridge-$VERSION-linux-$ARCH.tar.gz"
        echo "mineclaw-web-$VERSION-linux-$ARCH.tar.gz"
    fi
}

verify() { # $1 file: its sha256 must match its SHA256SUMS line
    local n want got
    n=$(basename "$1")
    want=$(awk -v n="$n" '{ x = $2; sub(/^\*/, "", x); sub(/^\.\//, "", x); if (x == n) { print tolower($1); exit } }' "$ASSETS/SHA256SUMS")
    [[ $want =~ ^[0-9a-f]{64}$ ]] || die "$n is not listed in SHA256SUMS"
    got=$(sha256sum "$1" | cut -d' ' -f1)
    [[ $got == "$want" ]] || die "sha256 of $n does not match SHA256SUMS (corrupt or tampered download)"
}

get_assets() {
    say "release $VERSION ($ARCH) from ${FROM:-github.com/$REPO}"
    local n base="https://github.com/$REPO/releases/download/v$VERSION"
    if [[ -n $FROM ]]; then
        ASSETS=$FROM
        [[ -f $ASSETS/SHA256SUMS ]] || die "$FROM has no SHA256SUMS (create it there with: sha256sum * > SHA256SUMS)"
    else
        ASSETS=$WORK/assets; mkdir -p "$ASSETS"
        fetch "$base/SHA256SUMS" "$ASSETS/SHA256SUMS" || die "no release v$VERSION in $REPO (SHA256SUMS not found)"
    fi
    while read -r n; do
        if [[ -z $FROM ]]; then fetch "$base/$n" "$ASSETS/$n" || die "download of $n failed"; fi
        [[ -f $ASSETS/$n ]] || die "release asset $n missing in $ASSETS"
        verify "$ASSETS/$n"
    done < <(asset_names)
    note "all assets verified against SHA256SUMS"
}

field() { # $1 key: a string field of the RELEASE.json on stdin
    sed -n "s/.*\"$1\"[[:space:]]*:[[:space:]]*\"\\([^\"]*\\)\".*/\\1/p" | tail -n1
}
current_field() { # $1 app dir, $2 key
    field "$2" < "$1/current/RELEASE.json" 2>/dev/null || true
}
same_release() { # $1 tarball, $2 top dir, $3 app dir: the same version + commit is already current
    [[ -L $3/current && -f $3/current/RELEASE.json ]] || return 1
    local j v c
    j=$(tar -xzOf "$1" "$2/RELEASE.json" 2>/dev/null || tar -xzOf "$1" "./$2/RELEASE.json" 2>/dev/null || true)
    v=$(field version <<< "$j"); c=$(field commit <<< "$j")
    [[ -n $v && $v == "$(current_field "$3" version)" && $c == "$(current_field "$3" commit)" ]]
}

# ------------------------------------------------------------------ host setup
nologin() { command -v nologin 2>/dev/null || echo /usr/sbin/nologin; }

ensure_user() { # $1 user, $2 primary group, $3 home, $4 comment
    getent group "$2" >/dev/null || { groupadd --system "$2" && note "group $2: created"; }
    if id -u "$1" >/dev/null 2>&1; then return 0; fi
    useradd --system --gid "$2" --home-dir "$3" --no-create-home --shell "$(nologin)" --comment "$4" "$1"
    note "user $1: created"
}

setup_users_dirs() {
    say "users and directories"
    ensure_user mineclaw mineclaw /var/lib/mineclaw "MineClaw bot"
    install -d -m 0755 -o root -g root /opt/mineclaw /opt/mineclaw/releases
    install -d -m 0750 -o root -g mineclaw /etc/mineclaw
    install -d -m 0750 -o mineclaw -g mineclaw /var/lib/mineclaw /var/log/mineclaw
    install -d -m 0755 -o root -g root /var/lib/mineclaw-update
    install -d -m 0700 -o root -g root /var/log/mineclaw-update
    if ((WEB)); then
        ensure_user mineclaw-web mineclaw-web /var/lib/mineclaw-web "MineClaw Web Explorer"
        ensure_user mineclaw-bridge mineclaw /nonexistent "MineClaw viewer bridge"
        if ! id -nG mineclaw-bridge | tr ' ' '\n' | grep -x mineclaw >/dev/null; then usermod -aG mineclaw mineclaw-bridge; fi
        install -d -m 0755 -o root -g root /opt/mineclaw-web /opt/mineclaw-web/releases /opt/mineclaw-bridge /opt/mineclaw-bridge/releases
        install -d -m 0750 -o mineclaw-web -g mineclaw-web /etc/mineclaw-web
        install -d -m 0700 -o mineclaw-web -g mineclaw-web /var/lib/mineclaw-web
        # The update handshake: the api writes request.json here; mineclaw-update (root) answers with status.json.
        install -d -m 0750 -o mineclaw-web -g mineclaw-web "$UPDATE_DIR"
        install -d -m 0700 -o root -g root /etc/mineclaw-bridge
    fi
    note "ok"
}

install_node() {
    say "Node.js v$NODE_VERSION"
    local want=v$NODE_VERSION have target tarball sum dst
    have=$("$NODE_BIN" --version 2>/dev/null || true)
    if [[ -e $NODE_BIN || -L $NODE_BIN ]]; then
        target=$(readlink "$NODE_BIN" 2>/dev/null || true)
        if [[ $target != /opt/node/* ]]; then # someone else's node
            if [[ $have == v22.* ]]; then NODE_MANAGED=0; note "using the existing $NODE_BIN ($have)"; return 0; fi
            die "$NODE_BIN (${have:-unknown version}) is not Node 22 and not installed by MineClaw; remove or move it and re-run"
        fi
    fi
    NODE_MANAGED=1
    if [[ $have == "$want" ]]; then note "kept ($have)"; return 0; fi
    tarball=node-$want-linux-$ARCH.tar.xz
    if [[ -n $FROM && -f $FROM/$tarball ]]; then cp "$FROM/$tarball" "$WORK/$tarball"
    else fetch "https://nodejs.org/dist/$want/$tarball" "$WORK/$tarball" || die "download of $tarball from nodejs.org failed"; fi
    sum=NODE_SHA256_$ARCH
    echo "${!sum}  $WORK/$tarball" | sha256sum -c --quiet - || die "sha256 of $tarball does not match the pinned ${!sum}"
    dst=/opt/node/node-$want-linux-$ARCH
    install -d -m 0755 -o root -g root /opt/node
    if [[ ! -x $dst/bin/node ]]; then
        rm -rf -- "$dst.tmp"; mkdir -p "$dst.tmp"
        tar -xJf "$WORK/$tarball" -C "$dst.tmp" --strip-components=1 --no-same-owner
        chown -R root:root "$dst.tmp"; chmod -R u=rwX,go=rX "$dst.tmp"
        mv -T "$dst.tmp" "$dst"
    fi
    "$dst/bin/node" --version >/dev/null || die "$dst/bin/node does not run here"
    ln -sfn "node-$want-linux-$ARCH" /opt/node/.current.new
    mv -Tf /opt/node/.current.new /opt/node/current
    local b
    for b in node npm npx; do ln -sfn "/opt/node/current/bin/$b" "/usr/local/bin/$b"; done
    note "${have:-none} -> $("$NODE_BIN" --version) (/opt/node/current)"
    RESTART_WEB=1
}

put() { # $1 dst, $2 mode, $3 owner:group; content on stdin. Atomic (temp + rename), never echoed.
    local tmp; tmp=$(mktemp "$(dirname "$1")/.$(basename "$1").XXXXXX")
    cat > "$tmp"
    chown "$3" "$tmp"; chmod "$2" "$tmp"
    mv -f "$tmp" "$1"
}
rand_hex() { od -An -tx1 -N"$1" /dev/urandom | tr -d ' \n'; }

bot_config() {
    say "bot config /etc/mineclaw"
    local cfg=/etc/mineclaw/appsettings.json
    if [[ -f $cfg ]]; then
        note "appsettings.json: kept"
        if [[ -n $MC_HOST$MC_PORT$MC_USER$MC_VERSION ]]; then
            warn "--mc-host/--mc-port/--mc-username are only used for a new config; change them in Administration (web) or in $cfg"
        fi
    else
        # Supervisor on loopback with a fresh token (the bridge reads it from this file); setup mode until the server and the
        # key are set (Administration in the web, or this file / mineclaw.env).
        put "$cfg" 0640 root:mineclaw <<EOF
{
  "Minecraft": {
    "Host": "$MC_HOST",
    "Port": ${MC_PORT:-25565},
    "Username": "$MC_USER"
  },
  "Bot": { "Server": { "Version": "${MC_VERSION:-auto}" } },
  "Supervisor": {
    "Enabled": true,
    "Bind": "127.0.0.1",
    "Port": 8765,
    "Token": "$(rand_hex 32)",
    "AllowAdminWrites": true,
    "Viewer": { "Enabled": true }
  }
}
EOF
        note "appsettings.json: written (Supervisor on 127.0.0.1:8765 with a new token, Viewer on, admin writes on; token not shown)"
    fi
    if [[ -n $KEY_FILE ]]; then
        local key env=/etc/mineclaw/mineclaw.env
        key=$(head -n1 "$KEY_FILE" | tr -d '[:space:]')
        [[ ${#key} -ge 20 && $key =~ ^[A-Za-z0-9._-]+$ ]] || die "--api-key-file: the first line does not look like an API key"
        { if [[ -f $env ]]; then grep -v '^[[:space:]]*MINECLAW_Agent__Llm__ApiKey=' "$env" || true
          else echo "# /etc/mineclaw/mineclaw.env (0600 root): MINECLAW_<Section>__<Key>=value overrides; see mineclaw.env.example"; fi
          printf 'MINECLAW_Agent__Llm__ApiKey=%s\n' "$key"; } | put "$env" 0600 root:root
        unset key
        note "mineclaw.env: API key set (0600 root; not shown)"
        RESTART_BOT=1
    elif [[ -f /etc/mineclaw/mineclaw.env ]]; then
        chown root:root /etc/mineclaw/mineclaw.env; chmod 0600 /etc/mineclaw/mineclaw.env
    fi
}

json_key_of() { # the Bridge:Key string in a bridge.json, or nothing
    [[ -s $1 ]] || return 0
    sed -n 's/.*"[Kk]ey"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$1" | tail -n1 | tr -d '[:space:]' || true
}

primary_ip() {
    local ip
    ip=$(ip -4 route get 1.1.1.1 2>/dev/null | sed -n 's/.* src \([0-9.]*\).*/\1/p' | head -n1 || true)
    [[ -n $ip ]] || ip=$(hostname -I 2>/dev/null | awk '{print $1}' || true)
    echo "${ip:-127.0.0.1}"
}

web_config() {
    say "web config /etc/mineclaw-web, /etc/mineclaw-bridge"
    local nest=/etc/mineclaw-web/bridge.key bjson=/etc/mineclaw-bridge/bridge.json nk="" bk="" key src
    [[ -s $nest ]] && nk=$(tr -d '[:space:]' < "$nest")
    bk=$(json_key_of "$bjson")
    if [[ -n $nk && -n $bk && $nk != "$bk" ]]; then
        die "$nest and $bjson hold different keys: delete both (a new pair is generated) or fix one, then re-run"
    elif [[ -n $nk ]]; then key=$nk; src=kept
    elif [[ -n $bk ]]; then key=$bk; src=kept
    else key=$(rand_hex 32); src="generated (32 random bytes)"; RESTART_WEB=1
    fi
    ((${#key} >= 32)) && [[ $key =~ ^[A-Za-z0-9+/=_-]+$ ]] || die "the existing bridge key is unusable; delete $nest and $bjson and re-run"
    [[ $nk == "$key" ]] || printf '%s\n' "$key" | put "$nest" 0600 root:root
    [[ $bk == "$key" ]] || printf '{"Bridge":{"Key":"%s"}}\n' "$key" | put "$bjson" 0600 root:root
    chown root:root "$nest" "$bjson"; chmod 0600 "$nest" "$bjson"
    unset key nk bk
    note "bridge key: $src (0600 root, handed to the services as systemd credentials; not shown)"

    local conf=/etc/mineclaw-web/config.json
    if [[ -f $conf ]]; then
        note "config.json: kept"
        if ((${#ORIGINS[@]})); then warn "--origin is only used for a new config; edit Web.AllowedOrigins in $conf"; fi
    else
        local ip host o list=""
        if ((${#ORIGINS[@]} == 0)); then
            ip=$(primary_ip); host=$(hostname -s 2>/dev/null || hostname)
            ORIGINS=("http://$ip:$PORT")
            [[ -n $host && $host != localhost ]] && ORIGINS+=("http://$host:$PORT")
            ORIGINS+=("http://localhost:$PORT")
        fi
        for o in "${ORIGINS[@]}"; do list+="${list:+, }\"$o\""; done
        put "$conf" 0640 mineclaw-web:mineclaw-web <<EOF
{
  "Web": {
    "Port": $PORT,
    "Bind": "0.0.0.0",
    "NextUrl": "http://127.0.0.1:3000",
    "ProxyNext": true,
    "AllowedOrigins": [$list],
    "TrustProxy": false,
    "TrustedProxies": [],
    "CloudflareAccess": null,
    "UsersFile": "/etc/mineclaw-web/users.json",
    "DataDir": "/var/lib/mineclaw-web",
    "CookieSecure": "auto",
    "LogLevel": "info",
    "Updates": { "Enabled": true, "Repo": "$REPO", "Dir": "$UPDATE_DIR" }
  },
  "Bridge": {
    "Url": "http://127.0.0.1:8766",
    "TimeoutMs": 15000
  }
}
EOF
        note "config.json: written (port $PORT, AllowedOrigins ${ORIGINS[*]})"
    fi
}

install_file() { # $1 src, $2 dst, $3 mode: atomic replace when it differs (a running script keeps its old inode); 0 = changed
    if cmp -s "$1" "$2"; then return 1; fi
    local tmp; tmp=$(mktemp "$(dirname "$2")/.$(basename "$2").XXXXXX")
    cp "$1" "$tmp"; chown root:root "$tmp"; chmod "$3" "$tmp"
    mv -f "$tmp" "$2"
    return 0
}

install_helpers_units() {
    say "helpers and systemd units"
    local h src dst u changed=()
    for h in "${HELPERS[@]}"; do
        src=$ASSETS/${h%%:*} dst=$SBIN/${h##*:}
        [[ ${h##*:} == mineclaw-web-users ]] && ((WEB == 0)) && continue
        if install_file "$src" "$dst" 0755; then note "$dst: installed/updated"; fi
    done
    local units=(mineclaw.service mineclaw-update.service)
    ((WEB)) && units+=(mineclaw-bridge.service mineclaw-web-ui.service mineclaw-web-api.service mineclaw-update.path)
    for u in "${units[@]}"; do
        if install_file "$ASSETS/$u" "$UNIT_DIR/$u" 0644; then note "unit $u: installed/updated"; changed+=("$u"); fi
    done
    if ((WEB)); then # the web's update checker shows the systemd update button for this kind of install
        install -d -m 0755 "$UNIT_DIR/mineclaw-web-api.service.d"
        printf '# written by the MineClaw installer\n[Service]\nEnvironment=MINECLAW_INSTALL_KIND=systemd\n' > "$WORK/kind.conf"
        if install_file "$WORK/kind.conf" "$UNIT_DIR/mineclaw-web-api.service.d/10-install-kind.conf" 0644; then
            note "drop-in mineclaw-web-api.service.d/10-install-kind.conf: installed/updated"; changed+=(mineclaw-web-api.service)
        fi
    fi
    systemctl daemon-reload
    for u in "${changed[@]}"; do
        case $u in
            mineclaw.service) RESTART_BOT=1 ;;
            mineclaw-bridge.service | mineclaw-web-*) RESTART_WEB=1 ;;
            mineclaw-update.path) systemctl try-restart mineclaw-update.path || true ;; # re-arm with the new watch
        esac
    done
}

new_release_id() {
    local id
    while :; do
        id=$(date -u +%Y%m%d-%H%M%S)
        [[ -e /opt/mineclaw/releases/$id || -e /opt/mineclaw-web/releases/$id || -e /opt/mineclaw-bridge/releases/$id ]] || break
        sleep 1
    done
    echo "$id"
}

since_of() { # epoch of the unit's current start (for a health check of a service that was not restarted now)
    local ts; ts=$(systemctl show "$1" -p ExecMainStartTimestamp --value 2>/dev/null || true)
    if [[ -n $ts && $ts != n/a ]]; then date -d "$ts" +%s 2>/dev/null && return 0; fi
    date -d '-10 min' +%s
}

install_releases() {
    local id srv bridge web opts=()
    id=$(new_release_id)
    ((NO_START)) && opts+=(--no-start)
    [[ -n $TIMEOUT ]] && opts+=(--timeout "$TIMEOUT")
    srv=$ASSETS/mineclaw-server-$VERSION-linux-$ARCH.tar.gz

    say "bot release"
    if ((REINSTALL == 0)) && same_release "$srv" mineclaw /opt/mineclaw; then
        note "$VERSION is current (releases/$(basename "$(readlink /opt/mineclaw/current)")): kept"
        if ((NO_START == 0)); then
            if ((RESTART_BOT)) || ! systemctl is-active --quiet mineclaw; then
                systemctl enable --quiet mineclaw
                "$SBIN/mineclaw-release" restart "${opts[@]}"
            else
                systemctl enable --quiet mineclaw
                "$SBIN/mineclaw-release" health --since "$(since_of mineclaw)" "${opts[@]}"
            fi
        fi
    else
        "$SBIN/mineclaw-release" install "$srv" "$id" "${opts[@]}"
    fi

    ((WEB)) || return 0
    say "web release (bridge + api + ui)"
    bridge=$ASSETS/mineclaw-bridge-$VERSION-linux-$ARCH.tar.gz web=$ASSETS/mineclaw-web-$VERSION-linux-$ARCH.tar.gz
    if ((REINSTALL == 0)) && same_release "$web" mineclaw-web /opt/mineclaw-web && same_release "$bridge" mineclaw-bridge /opt/mineclaw-bridge; then
        note "$VERSION is current (releases/$(basename "$(readlink /opt/mineclaw-web/current)")): kept"
        if ((NO_START == 0)); then
            systemctl enable --quiet "${WEB_UNITS[@]}"
            local u down=0
            for u in "${WEB_UNITS[@]}"; do systemctl is-active --quiet "$u" || down=1; done
            if ((RESTART_WEB || RESTART_BOT || down)); then "$SBIN/mineclaw-web-release" restart "${opts[@]}"
            else "$SBIN/mineclaw-web-release" health "${opts[@]}"; fi
        fi
    else
        "$SBIN/mineclaw-web-release" install "$web" "$bridge" "$id" "${opts[@]}"
    fi
    if ((NO_START == 0)); then systemctl enable --quiet --now mineclaw-update.path; fi
}

write_install_conf() { # $1 = the installed version (empty until the releases are in)
    {
        echo "# MineClaw install (written by install.sh; read by install.sh and mineclaw-update). KEY=value, no shell code."
        echo "REPO=$REPO"
        echo "VERSION=$1"
        echo "ARCH=$ARCH"
        echo "COMPONENTS=$COMPONENTS"
        echo "PORT=$PORT"
        echo "NODE_MANAGED=$NODE_MANAGED"
        echo "UPDATED=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    } | put "$CONF" 0644 root:root
}

final_message() {
    local ip; ip=$(primary_ip)
    echo
    echo "MineClaw $VERSION is installed ($COMPONENTS, linux-$ARCH)."
    if ((NO_START)); then
        echo "Nothing was started (--no-start). Start it with:"
        if ((WEB)); then echo "  systemctl enable --now mineclaw mineclaw-bridge mineclaw-web-ui mineclaw-web-api mineclaw-update.path"
        else echo "  systemctl enable --now mineclaw"; fi
    fi
    if ((WEB)); then
        echo
        echo "Open  http://$ip:$PORT/  in a browser on your network."
        if [[ -s /etc/mineclaw-web/users.json ]] && grep -q '"username"' /etc/mineclaw-web/users.json 2>/dev/null; then
            echo "Sign in with your web account (manage accounts with: mineclaw-web-users)."
        else
            echo "The first visit creates the admin account: whoever completes that page first becomes the admin, so do it now."
        fi
        echo "The Anthropic API key and the Minecraft server are set under Administration; until then the bot waits in setup mode."
        echo "Updates: Administration shows new releases and installs them (or run: mineclaw-update check / mineclaw-update apply X.Y.Z)."
    else
        echo
        echo "Set the Minecraft server in /etc/mineclaw/appsettings.json and the API key in /etc/mineclaw/mineclaw.env"
        echo "(MINECLAW_Agent__Llm__ApiKey=...), then: mineclaw-release restart. Until then the bot waits in setup mode."
    fi
    echo "Status: mineclaw-release status$( ((WEB)) && echo '; mineclaw-web-release status'). Logs: journalctl -u mineclaw -f"
}

# ------------------------------------------------------------------ uninstall
uninstall() {
    [[ -f $CONF ]] || die "no $CONF: this host was not set up by install.sh, so nothing is removed (do it by hand)"
    if ((PURGE && !YES)); then
        local answer=""
        [[ -r /dev/tty ]] || die "--purge deletes all MineClaw data and config; pass --yes to confirm (no terminal to ask)"
        printf 'Delete ALL MineClaw data (bot memory db, web accounts, config, keys, logs) for good? Type "purge": ' > /dev/tty
        read -r answer < /dev/tty || true
        [[ $answer == purge ]] || die "not confirmed; nothing removed"
    fi
    say "stopping and removing units"
    local u units=(mineclaw-update.path mineclaw-update.service mineclaw-web-api.service mineclaw-web-ui.service
        mineclaw-bridge.service mineclaw.service)
    for u in "${units[@]}"; do
        systemctl disable --now "$u" >/dev/null 2>&1 || true
        rm -f "$UNIT_DIR/$u"
    done
    rm -rf "$UNIT_DIR/mineclaw-web-api.service.d"
    systemctl daemon-reload
    systemctl reset-failed "${units[@]}" >/dev/null 2>&1 || true
    say "removing helpers and releases"
    rm -f "$SBIN/mineclaw-release" "$SBIN/mineclaw-web-release" "$SBIN/mineclaw-update" "$SBIN/mineclaw-web-users"
    rm -rf /opt/mineclaw /opt/mineclaw-web /opt/mineclaw-bridge
    if [[ $(conf_get NODE_MANAGED) == 1 ]]; then
        local b
        for b in node npm npx; do
            if [[ $(readlink "/usr/local/bin/$b" 2>/dev/null) == /opt/node/* ]]; then rm -f "/usr/local/bin/$b"; fi
        done
        rm -rf /opt/node
        note "Node.js removed (/opt/node)"
    fi
    if ((PURGE)); then
        say "purging data, config, logs and users"
        rm -rf /etc/mineclaw /etc/mineclaw-web /etc/mineclaw-bridge /var/lib/mineclaw /var/lib/mineclaw-web /var/lib/mineclaw-update \
            /var/log/mineclaw /var/log/mineclaw-bridge /var/log/mineclaw-update /var/cache/mineclaw-web-ui
        rm -rf /var/lib/mineclaw-backup-* 2>/dev/null || true
        for u in mineclaw-bridge mineclaw-web mineclaw; do
            if id -u "$u" >/dev/null 2>&1; then userdel "$u" >/dev/null 2>&1 || warn "could not delete user $u"; fi
        done
        for u in mineclaw-web mineclaw; do
            if getent group "$u" >/dev/null; then groupdel "$u" >/dev/null 2>&1 || warn "could not delete group $u"; fi
        done
        echo "MineClaw is removed, data included."
    else
        echo "MineClaw is removed. Kept: /etc/mineclaw*, /var/lib/mineclaw*, /var/log/mineclaw*, the users."
        echo "Re-run the installer to bring it back with the same data, or --uninstall --purge to delete them."
    fi
}

# ------------------------------------------------------------------ main
dry_run() {
    say "dry run (nothing changes)"
    note "arch linux-$ARCH, components $COMPONENTS, repo $REPO, version $VERSION, web port $PORT"
    note "assets: $(asset_names | tr '\n' ' ')"
    note "from: ${FROM:-https://github.com/$REPO/releases/download/v$VERSION/}"
    if [[ -f /etc/mineclaw/appsettings.json ]]; then note "bot config: kept"; else note "bot config: would be written"; fi
    if ((WEB)); then
        if [[ -f /etc/mineclaw-web/config.json ]]; then note "web config: kept"; else note "web config: would be written"; fi
        note "node: $("$NODE_BIN" --version 2>/dev/null || echo "none; v$NODE_VERSION would be installed")"
    fi
    note "bot release: $(basename "$(readlink /opt/mineclaw/current 2>/dev/null || echo none)"), version $(current_field /opt/mineclaw version)"
    [[ -n $KEY_FILE ]] && note "API key: would be written to /etc/mineclaw/mineclaw.env"
    return 0
}

main() {
    exec </dev/null # under `curl | bash` stdin is the script itself: no child may read it (--purge asks on /dev/tty)
    parse_args "$@"
    detect_platform
    if ((DRY == 0)); then [[ $(id -u) -eq 0 ]] || die "run as root (curl ... | sudo bash -s -- ...)"; fi
    if ((UNINSTALL)); then uninstall; return 0; fi
    defaults_from_conf
    if ((UPDATE)); then [[ -f $CONF ]] || die "--update: no $CONF (install first)"; fi

    if ((DRY == 0)) && [[ -z ${MINECLAW_LOCK_HELD:-} ]]; then # mineclaw-update holds it already when it runs us
        mkdir -p "$(dirname "$LOCK")"
        exec 9>"$LOCK"
        flock -n 9 || die "another MineClaw install or update is running (lock $LOCK)"
    fi
    WORK=$(mktemp -d "${TMPDIR:-/tmp}/mineclaw-install.XXXXXX")
    trap 'rc=$?; rm -rf -- "$WORK"; if ((rc != 0)); then echo "MineClaw install did not finish (exit $rc); the output above says why. Re-running is safe." >&2; fi' EXIT

    if ((UPDATE == 0 && DRY == 0)); then install_packages; fi
    check_commands
    resolve_version
    if ((DRY)); then dry_run; return 0; fi
    get_assets
    setup_users_dirs
    if ((WEB)); then install_node; fi
    bot_config
    if ((WEB)); then web_config; fi
    install_helpers_units
    write_install_conf "$(conf_get VERSION)" # early, so --uninstall works even when this first install fails below
    install_releases
    write_install_conf "$VERSION"
    if ((UPDATE == 0)); then final_message; fi
}

main "$@"
