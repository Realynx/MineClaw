#!/usr/bin/env bash
# Release manager for the Web Explorer stack INSIDE CT 114 (beside the bot's own mineclaw-release). deploy/deploy-web.ps1
# pushes it to /usr/local/sbin/mineclaw-web-release on every deploy. By hand: pct exec 114 -- /usr/local/sbin/mineclaw-web-release status
#
#   mineclaw-web-release install <web.tgz> <bridge.tgz> <release-id> [--units DIR] [--no-start] [--timeout SEC]
#   mineclaw-web-release rollback [--timeout SEC] [--dry-run]   # both trees -> the next older release, restart, health check
#   mineclaw-web-release restart  [--timeout SEC]               # bridge, then ui, then api; health check
#   mineclaw-web-release health   [--timeout SEC]
#   mineclaw-web-release status
#
# Layout (one release id for the pair, the same yyyyMMdd-HHmmss scheme as the bot):
#   /opt/mineclaw-web/releases/<id>/      apps/api/dist + node_modules (linux-x64), apps/web/.next/standalone (+ static, public)
#   /opt/mineclaw-bridge/releases/<id>/   MineClaw.ViewerBridge (self-contained linux-x64) + shards/defaults
#   /opt/mineclaw-web/current, /opt/mineclaw-bridge/current -> releases/<id>   (atomic rename swap; the newest $KEEP stay)
# Only the three web units are ever restarted here: mineclaw-bridge, mineclaw-web-ui, mineclaw-web-api. Never the bot.
# Healthy = all three active + GET http://127.0.0.1:8080/api/health says ok with bridge "up" (Nest's signed ping of the
# bridge's /v1/health, so it also proves the HMAC keys match) + the UI answers on 127.0.0.1:3000 and through :8080.
set -euo pipefail
export LC_ALL=C

WEB=${MINECLAW_WEB_APP:-/opt/mineclaw-web}
BRIDGE=${MINECLAW_BRIDGE_APP:-/opt/mineclaw-bridge}
UNIT_DIR=${MINECLAW_UNIT_DIR:-/etc/systemd/system}
NODE=${MINECLAW_NODE:-/usr/local/bin/node}
KEEP=${MINECLAW_KEEP:-3}
UNITS=(mineclaw-bridge mineclaw-web-ui mineclaw-web-api)
API_HEALTH=http://127.0.0.1:8080/api/health

TIMEOUT=120 UNIT_SRC="" NO_START=0 DRY=0

die() { echo "ERROR: $*" >&2; exit 1; }
current_of() { if [[ -L $1/current ]]; then basename "$(readlink "$1/current")"; fi; return 0; }
releases_of() { ls -1 "$1/releases" 2>/dev/null | grep -E '^[0-9]{8}-[0-9]{6}$' | sort || true; }

swap() { # $1 app dir, $2 id: new symlink beside the old one, then rename(2) over it
    ln -sfn "releases/$2" "$1/.current.new"
    mv -Tf "$1/.current.new" "$1/current"
}

prune() { # $1 app dir: keep the newest $KEEP and always the current one
    local cur r; cur=$(current_of "$1")
    rm -rf -- "$1"/releases/*.tmp
    releases_of "$1" | sort -r | tail -n +$((KEEP + 1)) | while read -r r; do
        [[ $r == "$cur" ]] && continue
        rm -rf -- "${1:?}/releases/$r" && echo "pruned $(basename "$1")/releases/$r"
    done
}

unpack() { # $1 tarball, $2 app dir, $3 id, $4 file that must exist inside
    local dst=$2/releases/$3
    [[ -f $1 ]] || die "no tarball at $1"
    [[ -e $dst ]] && die "$dst already exists"
    install -d -m 0755 "$2/releases"
    rm -rf -- "$dst.tmp"
    mkdir -p "$dst.tmp"
    tar -xzf "$1" -C "$dst.tmp" --strip-components=1 --no-same-owner
    [[ -e $dst.tmp/$4 ]] || { rm -rf -- "$dst.tmp"; die "$(basename "$1") has no $4"; }
    chmod -R u=rwX,go=rX "$dst.tmp"
    mv -T "$dst.tmp" "$dst"
    echo "unpacked $(basename "$2")/releases/$3 ($(du -sh "$dst" | cut -f1))"
}

preflight() {
    id -u mineclaw-web >/dev/null 2>&1 || die "user mineclaw-web missing: run deploy/viewer/install-ct114.sh first (deploy-web.ps1 -Install)"
    id -u mineclaw-bridge >/dev/null 2>&1 || die "user mineclaw-bridge missing: run install-ct114.sh first"
    [[ -x $NODE ]] || die "$NODE missing: run install-ct114.sh first"
    [[ -f /etc/mineclaw-web/config.json ]] || die "/etc/mineclaw-web/config.json missing: run install-ct114.sh first"
    [[ -s /etc/mineclaw-web/bridge.key ]] || die "/etc/mineclaw-web/bridge.key missing: run install-ct114.sh first"
    [[ -s /etc/mineclaw-bridge/bridge.json ]] || die "/etc/mineclaw-bridge/bridge.json missing: run install-ct114.sh first"
}

install_units() { # $1 dir holding the .service files
    local u
    for u in "${UNITS[@]}"; do
        [[ -f $1/$u.service ]] || die "no $u.service in $1"
        if cmp -s "$1/$u.service" "$UNIT_DIR/$u.service"; then echo "unit $u: unchanged"
        else install -m 0644 "$1/$u.service" "$UNIT_DIR/$u.service" && echo "unit $u: installed/updated"; fi
    done
    systemctl daemon-reload
    return 0
}

probe() { # one health probe; prints a summary line, exit 0 = healthy
    "$NODE" --input-type=module - "$API_HEALTH" <<'EOF'
const get = async (url) => {
  const c = new AbortController();
  const t = setTimeout(() => c.abort(), 3000);
  try {
    const r = await fetch(url, { signal: c.signal, redirect: 'manual' });
    return { status: r.status, body: await r.text() };
  } catch (e) {
    return { status: 0, body: String(e?.cause?.code ?? e?.message ?? e) };
  } finally {
    clearTimeout(t);
  }
};
const [health, ui, viaApi] = await Promise.all([get(process.argv[2]), get('http://127.0.0.1:3000/login'), get('http://127.0.0.1:8080/login')]);
let h = {};
try { h = JSON.parse(health.body); } catch { /* not JSON yet */ }
const ok = health.status === 200 && h.ok === true && h.bridge === 'up' && ui.status > 0 && ui.status < 500 && viaApi.status > 0 && viaApi.status < 500;
console.log(`api ${health.status} ${h.ok === true ? `ok v${h.version} bridge ${h.bridge}` : health.body.slice(0, 120)}; ui ${ui.status || ui.body}; ui via :8080 ${viaApi.status || viaApi.body}`);
process.exit(ok ? 0 : 1);
EOF
}

health() { # $1 timeout
    local timeout=$1 t=0 line="" u inactive
    while :; do
        inactive=""
        for u in "${UNITS[@]}"; do systemctl is-active --quiet "$u" || inactive+=" $u"; done
        if [[ -z $inactive ]] && line=$(probe 2>&1); then
            echo "HEALTHY after ${t}s: $line"
            return 0
        fi
        ((t >= timeout)) && break
        sleep 2
        t=$((t + 2))
    done
    echo "UNHEALTHY after ${t}s:${inactive:+ not active:$inactive;} ${line:-no probe}"
    for u in "${UNITS[@]}"; do
        echo "--- $u ($(systemctl is-active "$u" || true)), last 15 lines"
        journalctl -u "$u" -n 15 -o short-iso --no-pager 2>/dev/null || true
    done
    return 1
}

restart() {
    local u
    for u in "${UNITS[@]}"; do systemctl restart "$u"; done # bridge first: the api pings it at start
    health "$TIMEOUT"
}

install_release() {
    local web_tgz=$1 bridge_tgz=$2 id=$3 pw pb u
    [[ $id =~ ^[0-9]{8}-[0-9]{6}$ ]] || die "bad release id '$id' (want yyyyMMdd-HHmmss)"
    preflight
    unpack "$web_tgz" "$WEB" "$id" apps/api/dist/main.js
    [[ -f $WEB/releases/$id/apps/web/.next/standalone/apps/web/server.js ]] || die "web release has no Next standalone server.js"
    unpack "$bridge_tgz" "$BRIDGE" "$id" MineClaw.ViewerBridge
    chmod 0755 "$BRIDGE/releases/$id/MineClaw.ViewerBridge"

    if [[ -n $UNIT_SRC ]]; then install_units "$UNIT_SRC"; fi
    for u in "${UNITS[@]}"; do [[ -f $UNIT_DIR/$u.service ]] || die "no $UNIT_DIR/$u.service (pass --units)"; done
    if ((NO_START)); then
        echo "units: enabled state left as it is"
    else
        systemctl enable --quiet "${UNITS[@]}"
    fi

    pw=$(current_of "$WEB"); pb=$(current_of "$BRIDGE")
    swap "$WEB" "$id"; swap "$BRIDGE" "$id"
    echo "current -> releases/$id (previous: web ${pw:-none}, bridge ${pb:-none})"
    prune "$WEB"; prune "$BRIDGE"
    if ((NO_START)); then
        echo "not (re)started (--no-start)"
        return 0
    fi
    restart
}

rollback() {
    local cur prev
    cur=$(current_of "$WEB")
    [[ -n $cur ]] || die "no current web release"
    prev=$(releases_of "$WEB" | awk -v c="$cur" '$0 < c' | tail -n1)
    [[ -n $prev ]] || die "no web release older than $cur to roll back to"
    [[ -d $BRIDGE/releases/$prev ]] || die "bridge release $prev is gone (pruned?); redeploy instead"
    echo "rollback: releases/$cur -> releases/$prev (web + bridge together)"
    echo "note: /var/lib/mineclaw-web/web.db (sessions, audit) is not rolled back; an older api opens it as it is"
    if ((DRY)); then echo "dry run: nothing changed"; return 0; fi
    swap "$WEB" "$prev"; swap "$BRIDGE" "$prev"
    echo "rolled back: current -> releases/$prev (was $cur)"
    restart
}

status() {
    local u
    echo "web:    $(current_of "$WEB")   [kept: $(releases_of "$WEB" | tr '\n' ' ')]"
    echo "bridge: $(current_of "$BRIDGE")   [kept: $(releases_of "$BRIDGE" | tr '\n' ' ')]"
    echo "node:   $("$NODE" --version 2>/dev/null || echo missing)"
    for u in "${UNITS[@]}"; do
        echo "$u: $(systemctl show "$u" -p ActiveState --value 2>/dev/null)/$(systemctl show "$u" -p SubState --value 2>/dev/null)," \
            "$(systemctl show "$u" -p UnitFileState --value 2>/dev/null), restarts $(systemctl show "$u" -p NRestarts --value 2>/dev/null)," \
            "memory $(systemctl show "$u" -p MemoryCurrent --value 2>/dev/null) of max $(systemctl show "$u" -p MemoryMax --value 2>/dev/null)"
    done
    echo "probe:  $(probe 2>&1 || true)"
    for u in "${UNITS[@]}"; do
        echo "--- $u, last 5 lines"
        journalctl -u "$u" -n 5 -o short-iso --no-pager 2>/dev/null || true
    done
}

cmd=${1:-}; shift || true
args=()
while (($#)); do
    case $1 in
        --timeout) TIMEOUT=$2; shift 2 ;;
        --units) UNIT_SRC=$2; shift 2 ;;
        --no-start) NO_START=1; shift ;;
        --dry-run) DRY=1; shift ;;
        *) args+=("$1"); shift ;;
    esac
done
[[ $(id -u) -eq 0 ]] || die "run as root"
case $cmd in
    install) ((${#args[@]} == 3)) || die "usage: install <web.tgz> <bridge.tgz> <release-id>"; install_release "${args[@]}" ;;
    rollback) rollback ;;
    restart) restart ;;
    health) health "$TIMEOUT" ;;
    status) status ;;
    *) sed -n '2,18p' "$0"; exit 64 ;;
esac
