#!/usr/bin/env bash
# /usr/local/sbin/mineclaw-update: in-place updater of an install.sh install (systemd). Root does the work; the web only asks.
#
#   mineclaw-update check                          # installed version vs the latest release of REPO (install.conf)
#   mineclaw-update status                         # the last update's status.json, and what is installed
#   mineclaw-update apply VER [--force] [--from DIR]
#                                                  # update to VER by hand (--force: reinstall even when VER is current;
#                                                  # --from: flat release assets with SHA256SUMS instead of GitHub)
#   mineclaw-update run-request                    # mineclaw-update.service (started by mineclaw-update.path) runs this
#
# The handshake (the Web Explorer's Administration page): the api writes <dir>/request.json
# {"version":"1.2.3","requestedBy":"<user>","requestedAt":"<ISO>"} into /var/lib/mineclaw-web/update; the path unit starts
# this as root. It takes the request (as the web user, so nothing in the web-owned dir is ever touched with root rights), checks
# the version, takes the repo ONLY from /etc/mineclaw/install.conf, fetches that release's install.sh and checks it against the
# release's SHA256SUMS, and runs it in update mode: it downloads and sha256-verifies every asset, refreshes the helpers and
# units and installs the releases with mineclaw-release / mineclaw-web-release (each health-checked). On failure the bot and the
# web (bridge + api + ui) go back to exactly the releases that ran before. Progress and result: <dir>/status.json
# {"state":"running|succeeded|failed|rolled-back","version","from","requestedBy","startedAt","finishedAt","message","log":[...]}
# (0644; a root-owned copy in /var/lib/mineclaw-update/status.json); full logs in /var/log/mineclaw-update (0600).
# One run at a time: the installer's lock (/run/lock/mineclaw-install.lock) is held for the whole update.
set -euo pipefail
export LC_ALL=C
umask 022

CONF=${MINECLAW_INSTALL_CONF:-/etc/mineclaw/install.conf}
UPDATE_DIR=${MINECLAW_UPDATE_DIR:-/var/lib/mineclaw-web/update}
STATE_DIR=/var/lib/mineclaw-update
LOG_DIR=/var/log/mineclaw-update
LOCK=/run/lock/mineclaw-install.lock
SBIN=/usr/local/sbin
WEB_USER=mineclaw-web
KEEP_LOGS=10
VER_RE='^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$'
REPO_RE='^[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}$'
# Release assets from a local dir instead of GitHub: --from, or this (a root-only drop-in can set it; CI uses it).
FROM=${MINECLAW_UPDATE_ASSETS:-}

REPO="" ARCH="" COMPONENTS="" FORCE=0
S_STATE="" S_VERSION="" S_FROM="" S_BY="" S_STARTED="" S_FINISHED="" S_MESSAGE="" LOG_FILE=""

die() { echo "ERROR: $*" >&2; exit 1; }
now() { date -u +%Y-%m-%dT%H:%M:%SZ; }
log() { if [[ -n $LOG_FILE ]]; then echo "$(date -u +%H:%M:%S) $*" | tee -a "$LOG_FILE"; else echo "$*"; fi; }

conf_get() { # parsed, never sourced
    [[ -f $CONF ]] || return 0
    sed -n "s/^$1=\\([A-Za-z0-9_.,\\/:-]*\\)\$/\\1/p" "$CONF" | tail -n1
}

read_conf() {
    [[ -f $CONF ]] || die "no $CONF: this host was not installed with install.sh"
    REPO=$(conf_get REPO) ARCH=$(conf_get ARCH) COMPONENTS=$(conf_get COMPONENTS)
    [[ $REPO =~ $REPO_RE ]] || die "bad REPO in $CONF"
    [[ $ARCH == x64 || $ARCH == arm64 ]] || die "bad ARCH in $CONF"
    [[ $COMPONENTS == server || $COMPONENTS == server,web ]] || die "bad COMPONENTS in $CONF"
}

current_id() { if [[ -L $1/current ]]; then basename "$(readlink "$1/current")"; fi; return 0; }
current_version() {
    local v
    v=$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' /opt/mineclaw/current/RELEASE.json 2>/dev/null | tail -n1 || true)
    [[ -n $v ]] || v=$(conf_get VERSION)
    echo "$v"
}

fetch() { curl -fsSL --proto '=https' --tlsv1.2 --retry 3 --retry-delay 2 --connect-timeout 20 -o "$2" "$1"; }

latest_version() { # newest non-prerelease of REPO: the releases/latest redirect, the API as a fallback
    local url v
    url=$(curl -fsSLI --proto '=https' --retry 3 -o /dev/null -w '%{url_effective}' "https://github.com/$REPO/releases/latest" 2>/dev/null || true)
    v=${url##*/tag/}; v=${v#v}
    if ! [[ $v =~ $VER_RE ]]; then
        v=$(curl -fsSL --proto '=https' --retry 3 -H 'Accept: application/vnd.github+json' "https://api.github.com/repos/$REPO/releases/latest" 2>/dev/null |
            sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"v\{0,1\}\([^"]*\)".*/\1/p' | head -n1 || true)
    fi
    [[ $v =~ $VER_RE ]] && echo "$v"
    return 0
}

# ------------------------------------------------------------------ the web-owned handshake dir (only ever as the web user)
web_ok() { id -u "$WEB_USER" >/dev/null 2>&1 && [[ -d $UPDATE_DIR ]]; }
as_web() { setpriv --reuid="$(id -u "$WEB_USER")" --regid="$(id -g "$WEB_USER")" --clear-groups -- "$@"; }

j() { # a JSON string
    local s=$1
    s=${s//\\/\\\\}; s=${s//\"/\\\"}; s=${s//$'\t'/ }; s=${s//$'\r'/}; s=${s//$'\n'/ }
    s=$(printf '%s' "$s" | tr -d '\000-\037\177')
    printf '"%s"' "$s"
}
redact() { sed -E 's/sk-[A-Za-z0-9_-]{8,}/sk-***/g; s/[0-9A-Fa-f]{64,}/<redacted>/g'; }

write_status() {
    local tmp lines="" line
    if [[ -n $LOG_FILE && -s $LOG_FILE ]]; then
        while IFS= read -r line; do lines+="${lines:+,}$(j "$line")"; done < <(tail -n 40 "$LOG_FILE" | redact)
    fi
    install -d -m 0755 "$STATE_DIR"
    tmp=$(mktemp "$STATE_DIR/.status.XXXXXX")
    printf '{"state":%s,"version":%s,"from":%s,"requestedBy":%s,"startedAt":%s,"finishedAt":%s,"message":%s,"log":[%s]}\n' \
        "$(j "$S_STATE")" "$(j "$S_VERSION")" "$(j "$S_FROM")" "$(j "$S_BY")" "$(j "$S_STARTED")" \
        "$(if [[ -n $S_FINISHED ]]; then j "$S_FINISHED"; else echo null; fi)" "$(j "$S_MESSAGE")" "$lines" > "$tmp"
    chmod 0644 "$tmp"; mv -f "$tmp" "$STATE_DIR/status.json"
    if web_ok; then
        as_web sh -c 'umask 022; t=$(mktemp "$1/.status.XXXXXX") && cat > "$t" && chmod 0644 "$t" && mv -f "$t" "$1/status.json"' \
            sh "$UPDATE_DIR" < "$STATE_DIR/status.json" || echo "WARNING: could not write $UPDATE_DIR/status.json" >&2
    fi
}

finish() { # $1 state, $2 message
    S_STATE=$1 S_MESSAGE=$2 S_FINISHED=$(now)
    log "$1: $2"
    write_status
}

on_exit() {
    local rc=$?
    if [[ $S_STATE == running ]]; then finish failed "the updater stopped unexpectedly (exit $rc); see $LOG_FILE"; fi
}

prune_logs() {
    ls -1 "$LOG_DIR" 2>/dev/null | grep -E '^update-.*\.log$' | sort -r | tail -n +$((KEEP_LOGS + 1)) |
        while read -r f; do rm -f -- "$LOG_DIR/$f"; done || true
}

lock() { # $1 = seconds to wait (0 = fail at once)
    mkdir -p "$(dirname "$LOCK")"
    exec 9>"$LOCK"
    if [[ $1 == 0 ]]; then flock -n 9 || die "another MineClaw install or update is running"
    else flock -w "$1" 9 || die "another MineClaw install or update kept running for ${1}s"; fi
}

# ------------------------------------------------------------------ update
verify_in() { # $1 dir with SHA256SUMS, $2 file name
    local want got
    want=$(awk -v n="$2" '{ x = $2; sub(/^\*/, "", x); sub(/^\.\//, "", x); if (x == n) { print tolower($1); exit } }' "$1/SHA256SUMS")
    [[ $want =~ ^[0-9a-f]{64}$ ]] || return 1
    got=$(sha256sum "$1/$2" | cut -d' ' -f1)
    [[ $got == "$want" ]]
}

do_update() { # $1 version, $2 requested by
    local ver=$1 stage rc pw ps nw ns rolled=0 rb_ok=1 args=()
    read_conf
    install -d -m 0700 "$LOG_DIR"
    LOG_FILE=$LOG_DIR/update-$(date -u +%Y%m%dT%H%M%SZ).log
    : > "$LOG_FILE"; chmod 0600 "$LOG_FILE"
    prune_logs
    S_VERSION=$ver S_BY=$2 S_FROM=$(current_version) S_STARTED=$(now) S_FINISHED="" S_STATE=running
    trap on_exit EXIT
    if [[ $ver == "$S_FROM" ]] && ((FORCE == 0)); then
        finish succeeded "already on $ver; nothing to do"
        return 0
    fi
    S_MESSAGE="updating ${S_FROM:-?} -> $ver ($COMPONENTS, linux-$ARCH, from ${FROM:-github.com/$REPO})"
    log "$S_MESSAGE (requested by ${2:-?})"
    write_status

    stage=$(mktemp -d "$STATE_DIR/stage.XXXXXX")
    if [[ -n $FROM ]]; then
        [[ -f $FROM/install.sh && -f $FROM/SHA256SUMS ]] || { finish failed "no install.sh + SHA256SUMS in $FROM; nothing changed"; rm -rf -- "$stage"; return 0; }
        cp "$FROM/SHA256SUMS" "$FROM/install.sh" "$stage/"
    elif ! fetch "https://github.com/$REPO/releases/download/v$ver/SHA256SUMS" "$stage/SHA256SUMS" ||
        ! fetch "https://github.com/$REPO/releases/download/v$ver/install.sh" "$stage/install.sh"; then
        finish failed "release v$ver of $REPO could not be downloaded (no such release, or no network); nothing changed"
        rm -rf -- "$stage"; return 0
    fi
    if ! verify_in "$stage" install.sh; then
        finish failed "install.sh of v$ver does not match its SHA256SUMS; nothing changed"
        rm -rf -- "$stage"; return 0
    fi
    log "install.sh of v$ver verified against SHA256SUMS"

    ps=$(current_id /opt/mineclaw); pw=$(current_id /opt/mineclaw-web)
    args=(--update --version "$ver" --repo "$REPO" --components "$COMPONENTS")
    [[ -n $FROM ]] && args+=(--from "$FROM")
    ((FORCE)) && args+=(--reinstall)
    set +e
    MINECLAW_LOCK_HELD=1 bash "$stage/install.sh" "${args[@]}" 2>&1 | tee -a "$LOG_FILE"
    rc=${PIPESTATUS[0]}
    set -e
    rm -rf -- "$stage"
    if ((rc == 0)); then
        finish succeeded "updated ${S_FROM:-?} -> $ver"
        return 0
    fi

    log "the update failed (exit $rc): rolling back what changed"
    ns=$(current_id /opt/mineclaw); nw=$(current_id /opt/mineclaw-web)
    if [[ -n $ps && $ns != "$ps" ]]; then
        rolled=1
        "$SBIN/mineclaw-release" rollback --to "$ps" 2>&1 | tee -a "$LOG_FILE" || rb_ok=0
    fi
    if [[ $COMPONENTS == *web* && -n $pw && $nw != "$pw" ]]; then
        rolled=1
        "$SBIN/mineclaw-web-release" rollback --to "$pw" 2>&1 | tee -a "$LOG_FILE" || rb_ok=0
    fi
    if ((rolled == 0)); then
        finish failed "update to $ver failed before any release was switched; still on ${S_FROM:-?}"
    elif ((rb_ok)); then
        finish rolled-back "update to $ver failed; rolled back to ${S_FROM:-?} (bot ${ps:-?}, web ${pw:-?})"
    else
        finish failed "update to $ver failed and the rollback did not come up healthy; see $LOG_FILE (mineclaw-release status)"
    fi
    return 0 # the outcome is in status.json (S_STATE)
}

run_request() {
    local body rc ver by
    web_ok || die "no $WEB_USER user or no $UPDATE_DIR: the web is not installed here"
    lock 1800
    # Take the request as the web user: a symlink is refused, at most 4 KiB is read, and the file is gone afterwards either way.
    set +e
    body=$(as_web sh -c 'cd "$1" || exit 3
        if [ ! -e request.json ] && [ ! -L request.json ]; then exit 2; fi
        if [ -L request.json ] || [ ! -f request.json ]; then rm -f request.json; exit 4; fi
        mv -f request.json .request.taken || exit 3
        head -c 4096 .request.taken; rm -f .request.taken' sh "$UPDATE_DIR")
    rc=$?
    set -e
    case $rc in
        0) ;;
        2) echo "no request"; return 0 ;;
        *) S_STATE=running S_STARTED=$(now); finish failed "invalid update request (not a regular file)"; return 0 ;;
    esac
    body=$(printf '%s' "$body" | tr -d '\r\n')
    ver=$(printf '%s' "$body" | sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -c 64 || true)
    by=$(printf '%s' "$body" | sed -n 's/.*"requestedBy"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | tr -cd 'A-Za-z0-9._@ -' | head -c 64 || true)
    ver=${ver#v}
    if ! [[ $ver =~ $VER_RE ]]; then
        S_STATE=running S_STARTED=$(now) S_BY=$by S_FROM=$(current_version)
        finish failed "invalid update request: the version must look like 1.2.3 or 1.2.3-rc.1"
        return 0
    fi
    do_update "$ver" "$by"
}

cmd_check() {
    read_conf
    local cur latest
    cur=$(current_version); latest=$(latest_version)
    echo "installed: ${cur:-unknown}   ($COMPONENTS, linux-$ARCH, repo $REPO)"
    [[ -n $latest ]] || die "could not read the latest release of $REPO"
    echo "latest:    $latest"
    if [[ $latest == "$cur" ]]; then echo "up to date"
    else echo "update available: mineclaw-update apply $latest   (or Administration in the web)"; fi
}

cmd_status() {
    echo "installed: $(current_version || true)   bot releases/$(current_id /opt/mineclaw)   web releases/$(current_id /opt/mineclaw-web)"
    if [[ -f $STATE_DIR/status.json ]]; then echo "last update:"; cat "$STATE_DIR/status.json"
    else echo "last update: none yet"; fi
}

main() {
    local cmd=${1:-} ver=""
    shift || true
    while (($#)); do
        case $1 in
            --force) FORCE=1; shift ;;
            --from) [[ -n ${2:-} && -d $2 ]] || die "--from needs a directory"; FROM=$(cd "$2" && pwd); shift 2 ;;
            -*) die "unknown option $1" ;;
            *) [[ -z $ver ]] || die "one version only"; ver=$1; shift ;;
        esac
    done
    case $cmd in
        check) cmd_check ;;
        status) cmd_status ;;
        run-request | apply)
            [[ $(id -u) -eq 0 ]] || die "run as root"
            # run-request exits 0 once status.json holds the outcome (a failed update is not a failed unit; the path unit
            # stays armed); apply exits 1 unless the update succeeded.
            if [[ $cmd == run-request ]]; then run_request; return 0; fi
            ver=${ver#v}
            [[ $ver =~ $VER_RE ]] || die "usage: mineclaw-update apply X.Y.Z[-pre] [--force] [--from DIR]"
            lock 0
            do_update "$ver" "${SUDO_USER:-root} (shell)"
            [[ $S_STATE == succeeded ]]
            ;;
        *) sed -n '2,10p' "$0"; exit 64 ;;
    esac
}

main "$@"
