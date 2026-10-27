#!/usr/bin/env bash
# Release manager that runs INSIDE the MineClaw container. deploy/deploy-alice.ps1 pushes it to
# /usr/local/sbin/mineclaw-release on every deploy. You can also run it by hand: pct exec 114 -- /usr/local/sbin/mineclaw-release status
#
#   mineclaw-release install <tarball> <release-id> [--unit FILE] [--no-start] [--timeout SEC]
#   mineclaw-release rollback [--timeout SEC]      # current -> the next older release, restart, health check
#   mineclaw-release restart  [--timeout SEC]
#   mineclaw-release health   [--timeout SEC] [--since EPOCH]
#   mineclaw-release status
#
# Layout: /opt/mineclaw/releases/<yyyyMMdd-HHmmss>/ and /opt/mineclaw/current -> releases/<id> (atomic rename swap).
# The newest $KEEP releases (default 3) are kept, and the current one always stays.
# Data: /var/lib/mineclaw/mineclaw.db is never touched. If it doesn't exist yet, the committed clean-state seed is
# staged at /var/lib/mineclaw/seed/mineclaw-seed.db and the app bootstraps the db from it on first start.
set -euo pipefail
export LC_ALL=C

APP=/opt/mineclaw
REL=$APP/releases
CUR=$APP/current
DATA=/var/lib/mineclaw
UNIT=/etc/systemd/system/mineclaw.service
KEEP=${MINECLAW_KEEP:-3}
# Log lines the app writes once it is in the world (first connect, and every reconnect).
JOIN_RE='joined .* as |in game:'

TIMEOUT=180 SINCE="" UNIT_SRC="" NO_START=0

die() { echo "ERROR: $*" >&2; exit 1; }
current_id() { if [[ -L $CUR ]]; then basename "$(readlink "$CUR")"; fi; return 0; }
releases() { ls -1 "$REL" 2>/dev/null | grep -E '^[0-9]{8}-[0-9]{6}$' | sort || true; }

swap_to() { # new symlink beside the old one, then rename(2) over it: never a moment without "current"
    ln -sfn "releases/$1" "$APP/.current.new"
    mv -Tf "$APP/.current.new" "$CUR"
}

prune() {
    local cur; cur=$(current_id)
    rm -rf -- "$REL"/*.tmp
    releases | sort -r | tail -n +$((KEEP + 1)) | while read -r r; do
        [[ $r == "$cur" ]] && continue
        rm -rf -- "${REL:?}/$r" && echo "pruned release $r"
    done
}

health() { # $1 timeout, $2 epoch: wait for "active" + a join line logged after $2
    local timeout=$1 since=$2 t=0 line
    while ((t < timeout)); do
        line=$(journalctl -u mineclaw --since "@$since" -o short-iso --no-pager 2>/dev/null | grep -m1 -E "$JOIN_RE" || true)
        if [[ -n $line ]] && systemctl is-active --quiet mineclaw; then
            echo "HEALTHY after ${t}s: $line"
            return 0
        fi
        sleep 2
        t=$((t + 2))
    done
    echo "UNHEALTHY after ${t}s: service is $(systemctl is-active mineclaw || true); recent log:"
    journalctl -u mineclaw --since "@$since" -o short-iso --no-pager | tail -n 25
    return 1
}

restart() {
    local since; since=$(date +%s)
    systemctl restart mineclaw
    health "$TIMEOUT" "$since"
}

install_release() {
    local tarball=$1 id=$2 prev
    [[ -f $tarball ]] || die "no tarball at $tarball"
    [[ $id =~ ^[0-9]{8}-[0-9]{6}$ ]] || die "bad release id '$id' (want yyyyMMdd-HHmmss)"
    id -u mineclaw >/dev/null 2>&1 || die "user mineclaw missing: run deploy/provision-alice.sh first"
    [[ -e $REL/$id ]] && die "release $id already exists"
    [[ -f /etc/mineclaw/appsettings.json ]] || die "/etc/mineclaw/appsettings.json missing (deploy with -PushConfig)"

    install -d -m 0755 "$REL"
    rm -rf -- "$REL/$id.tmp"
    mkdir -p "$REL/$id.tmp"
    tar -xzf "$tarball" -C "$REL/$id.tmp" --strip-components=1 --no-same-owner
    [[ -f $REL/$id.tmp/MineClaw.App ]] || die "tarball has no MineClaw.App"
    chmod -R u=rwX,go=rX "$REL/$id.tmp"
    chmod 0755 "$REL/$id.tmp/MineClaw.App"
    mv -T "$REL/$id.tmp" "$REL/$id"
    echo "unpacked releases/$id ($(du -sh "$REL/$id" | cut -f1))"

    if [[ -e $DATA/mineclaw.db ]]; then
        echo "data: existing mineclaw.db kept"
    elif [[ -f $REL/$id/seed/mineclaw-seed.db ]]; then
        install -d -m 0750 -o mineclaw -g mineclaw "$DATA" "$DATA/seed"
        install -m 0640 -o mineclaw -g mineclaw "$REL/$id/seed/mineclaw-seed.db" "$DATA/seed/mineclaw-seed.db"
        echo "data: no db yet; staged clean seed, the app bootstraps mineclaw.db from it on start"
    else
        echo "data: no db and no seed in the release; the app starts with an empty db"
    fi

    if [[ -n $UNIT_SRC ]]; then
        if cmp -s "$UNIT_SRC" "$UNIT"; then echo "unit: unchanged"; else install -m 0644 "$UNIT_SRC" "$UNIT" && echo "unit: installed/updated"; fi
    fi
    [[ -f $UNIT ]] || die "no systemd unit at $UNIT (pass --unit)"
    systemctl daemon-reload
    systemctl enable --quiet mineclaw

    prev=$(current_id)
    swap_to "$id"
    echo "current -> releases/$id (previous: ${prev:-none})"
    prune
    if ((NO_START)); then
        echo "not (re)started (--no-start); service is $(systemctl is-active mineclaw || true)"
        return 0
    fi
    restart
}

rollback() {
    local cur prev
    cur=$(current_id)
    prev=$(releases | awk -v c="$cur" '$0 < c' | tail -n1)
    [[ -n $prev ]] || die "no release older than '${cur:-none}' to roll back to"
    swap_to "$prev"
    echo "rolled back: current -> releases/$prev (was $cur)"
    restart
}

status() {
    echo "release:  $(current_id || true)   [kept: $(releases | tr '\n' ' ')]"
    systemctl show mineclaw -p ActiveState -p SubState -p UnitFileState -p NRestarts -p ExecMainStartTimestamp --no-pager | sed 's/^/  /'
    echo "db:       $(stat -c '%s bytes, modified %y' "$DATA/mineclaw.db" 2>/dev/null || echo 'not created yet')"
    echo "last join: $(journalctl -u mineclaw -o short-iso --no-pager 2>/dev/null | grep -E "$JOIN_RE" | tail -n1 || true)"
    echo "--- last 15 log lines"
    journalctl -u mineclaw -n 15 -o short-iso --no-pager 2>/dev/null || true
}

cmd=${1:-}; shift || true
args=()
while (($#)); do
    case $1 in
        --timeout) TIMEOUT=$2; shift 2 ;;
        --since) SINCE=$2; shift 2 ;;
        --unit) UNIT_SRC=$2; shift 2 ;;
        --no-start) NO_START=1; shift ;;
        *) args+=("$1"); shift ;;
    esac
done
[[ $(id -u) -eq 0 ]] || die "run as root"
case $cmd in
    install) ((${#args[@]} == 2)) || die "usage: install <tarball> <release-id>"; install_release "${args[0]}" "${args[1]}" ;;
    rollback) rollback ;;
    restart) restart ;;
    health) health "$TIMEOUT" "${SINCE:-$(date -d '-10 min' +%s)}" ;;
    status) status ;;
    *) sed -n '2,13p' "$0"; exit 64 ;;
esac
