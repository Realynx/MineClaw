#!/usr/bin/env bash
# End-to-end test of the systemd installer and updater on a throwaway host with systemd (the release workflow's ubuntu runner):
#
#   sudo -E bash deploy/install/test/ci-install-test.sh <assets-dir>
#
# <assets-dir> holds the flat release assets (tarballs, install.sh and the files in deploy/install/release-assets.txt). Without a
# SHA256SUMS there, one is made first, so the installer's verify path runs. No API key and no Minecraft server: the bot must
# reach setup mode. Steps: dry run; fresh install (units active, /api/health ok with bridge up, the `setup mode:` line, modes
# and owners, MINECLAW_INSTALL_KIND, no secret in the output); a re-run changes nothing; --api-key-file (0600, never printed);
# the web's update handshake (same version = a no-op status, a bad version = failed, the path unit stays armed); a forced
# reinstall through mineclaw-update; a broken release rolls back; --uninstall keeps data; --uninstall --purge --yes removes all.
# Any failure dumps the units' journals, the update status and logs, and exits 1.
set -euo pipefail
export LC_ALL=C

A=${1:-}
[[ -n $A && -d $A ]] || { echo "usage: $0 <assets-dir>" >&2; exit 64; }
A=$(cd "$A" && pwd)
[[ $(id -u) -eq 0 ]] || { echo "run as root (sudo -E bash $0 $A)" >&2; exit 1; }

case $(uname -m) in x86_64) ARCH=x64 ;; aarch64) ARCH=arm64 ;; *) echo "unsupported arch" >&2; exit 1 ;; esac
UNITS=(mineclaw mineclaw-bridge mineclaw-web-ui mineclaw-web-api)
UPDATE_DIR=/var/lib/mineclaw-web/update
SETUP_WAIT=${SETUP_WAIT:-120}
T=$(mktemp -d /tmp/mineclaw-ci.XXXXXX)
chmod 0755 "$T"
STEP="start"
NODE_ASIDE=()

step() { STEP=$*; echo; echo "################ $*"; }
ok() { echo "  ok: $*"; }
fail() { echo "FAIL [$STEP]: $*" >&2; exit 1; }

diagnostics() {
    echo; echo "================ diagnostics (step: $STEP)"
    local u
    for u in "${UNITS[@]}" mineclaw-update.path mineclaw-update; do
        echo "---------------- $u"
        systemctl status "$u" --no-pager -l 2>&1 | head -n 20 || true
        journalctl -u "$u" -n 120 --no-pager -o short-iso 2>&1 || true
    done
    echo "---------------- mineclaw-update status"; /usr/local/sbin/mineclaw-update status 2>&1 || true
    echo "---------------- web status.json"; cat "$UPDATE_DIR/status.json" 2>&1 || true
    echo "---------------- update logs"; tail -n 80 /var/log/mineclaw-update/*.log 2>&1 || true
    echo "---------------- install.conf"; cat /etc/mineclaw/install.conf 2>&1 || true
    echo "---------------- trees"; ls -la /opt/mineclaw /opt/mineclaw/releases /opt/mineclaw-web/releases /etc/mineclaw /etc/mineclaw-web 2>&1 || true
    echo "---------------- listening"; ss -ltnp 2>&1 | head -n 30 || true
}

restore_node() {
    local f
    for f in "${NODE_ASIDE[@]}"; do
        if [[ ! -e /usr/local/bin/$f && -e /usr/local/bin/$f.mineclaw-ci ]]; then mv "/usr/local/bin/$f.mineclaw-ci" "/usr/local/bin/$f"; fi
    done
}

on_exit() {
    local rc=$?
    if ((rc != 0)); then diagnostics; echo; echo "CI INSTALL TEST FAILED at: $STEP" >&2; fi
    # Leave the runner as we found it (best effort, also after a failure).
    if [[ -f /etc/mineclaw/install.conf && $rc -ne 0 ]]; then bash "$A/install.sh" --uninstall --purge --yes >/dev/null 2>&1 || true; fi
    restore_node
    rm -rf -- "$T"
    exit "$rc"
}
trap on_exit EXIT

run_install() { # $1 log name, rest: install.sh options
    local name=$1; shift
    if ! bash "$A/install.sh" "$@" 2>&1 | tee "$T/$name.log"; then fail "install.sh $* exited non-zero"; fi
}

secret_absent() { # $1 log file, $2 label, $3 secret
    [[ -n $3 ]] || fail "no $2 to look for"
    if grep -F -- "$3" "$1" >/dev/null; then fail "$2 was printed in $(basename "$1")"; fi
    ok "$2 not printed in $(basename "$1")"
}

mode_is() { # $1 path, $2 "mode owner:group"
    local got; got=$(stat -c '%a %U:%G' "$1" 2>/dev/null || echo missing)
    [[ $got == "$2" ]] || fail "$1 is '$got', want '$2'"
    ok "$1 $2"
}

units_active() {
    local u
    for u in "${UNITS[@]}" mineclaw-update.path; do
        systemctl is-active --quiet "$u" || fail "$u is $(systemctl is-active "$u" || true)"
    done
    ok "active: ${UNITS[*]} mineclaw-update.path"
}

api_health() {
    local body t=0
    while :; do
        body=$(curl -fsS --max-time 5 http://127.0.0.1:8080/api/health 2>/dev/null || true)
        if [[ $body == *'"ok":true'* && $body == *'"bridge":"up"'* ]]; then ok "/api/health: $body"; return 0; fi
        ((t >= 60)) && fail "/api/health never said ok with bridge up (last: ${body:-no answer})"
        sleep 2; t=$((t + 2))
    done
}

setup_line() {
    local t=0
    while ((t < SETUP_WAIT)); do
        if journalctl -u mineclaw --no-pager -o cat 2>/dev/null | grep 'setup mode:' >/dev/null; then # no -q/-m1: SIGPIPE + pipefail
            ok "bot logged: $(journalctl -u mineclaw --no-pager -o cat | grep 'setup mode:' | tail -n1)"
            return 0
        fi
        sleep 2; t=$((t + 2))
    done
    fail "the bot never logged 'setup mode:' within ${SETUP_WAIT}s (no API key + no Minecraft server must wait in setup mode)"
}

cur() { basename "$(readlink "$1/current")"; }

as_web() { setpriv --reuid="$(id -u mineclaw-web)" --regid="$(id -g mineclaw-web)" --clear-groups -- "$@"; }

request_update() { # $1 = request JSON; waits for the updater's finished status.json; prints it
    rm -f "$UPDATE_DIR/status.json"
    printf '%s\n' "$1" | as_web sh -c 'cat > "$1/.request.tmp" && mv -f "$1/.request.tmp" "$1/request.json"' sh "$UPDATE_DIR"
    local t=0 s=""
    while ((t < 300)); do
        s=$(cat "$UPDATE_DIR/status.json" 2>/dev/null || true)
        if [[ -n $s && $s != *'"state":"running"'* && ! -e $UPDATE_DIR/request.json ]]; then echo "$s"; return 0; fi
        sleep 2; t=$((t + 2))
    done
    fail "no finished status.json 300s after the request (status: ${s:-none}; request.json $([[ -e $UPDATE_DIR/request.json ]] && echo still there || echo gone))"
}

# ------------------------------------------------------------------------------------------------------------------------------
step "assets"
shopt -s nullglob
servers=("$A"/mineclaw-server-*-linux-"$ARCH".tar.gz)
shopt -u nullglob
((${#servers[@]} == 1)) || fail "want exactly one mineclaw-server-*-linux-$ARCH.tar.gz in $A (found ${#servers[@]})"
VER=${servers[0]##*/mineclaw-server-}; VER=${VER%-linux-"$ARCH".tar.gz}
echo "version $VER, linux-$ARCH"
for f in install.sh $(grep -vE '^\s*(#|$)' "$(dirname "$0")/../release-assets.txt" | awk '{print $1}'); do
    [[ -f $A/$f ]] || fail "asset $f missing in $A (deploy/install/release-assets.txt)"
done
if [[ ! -f $A/SHA256SUMS ]]; then
    (cd "$A" && find . -maxdepth 1 -type f ! -name SHA256SUMS -printf '%f\n' | sort | xargs -d '\n' sha256sum) > "$T/SHA256SUMS"
    mv "$T/SHA256SUMS" "$A/SHA256SUMS"
    ok "made SHA256SUMS ($(wc -l < "$A/SHA256SUMS") files)"
fi
ss -ltn | grep -E '[:.]8080\s' >/dev/null && fail "port 8080 is already in use on this runner"
# The runner image ships its own /usr/local/bin/node; a real server has none. Move it aside so the installer's Node is tested.
for f in node npm npx corepack; do
    if [[ -e /usr/local/bin/$f || -L /usr/local/bin/$f ]] && [[ $(readlink "/usr/local/bin/$f" 2>/dev/null) != /opt/node/* ]]; then
        mv "/usr/local/bin/$f" "/usr/local/bin/$f.mineclaw-ci"; NODE_ASIDE+=("$f")
    fi
done
((${#NODE_ASIDE[@]})) && ok "moved the runner's ${NODE_ASIDE[*]} aside (restored at the end)"

step "dry run"
run_install dry --from "$A" --dry-run
grep -q 'dry run' "$T/dry.log" || fail "no dry-run plan printed"
[[ ! -e /etc/mineclaw/install.conf ]] || fail "the dry run wrote install.conf"
ok "nothing written"

step "fresh install"
run_install install1 --from "$A" --timeout "$SETUP_WAIT"
units_active
setup_line
api_health
code=$(curl -sS -o /dev/null -w '%{http_code}' --max-time 5 http://127.0.0.1:8080/login || true)
[[ $code =~ ^[23] ]] || fail "GET /login through :8080 answered $code"
ok "GET /login: $code"
mode_is /etc/mineclaw "750 root:mineclaw"
mode_is /etc/mineclaw/appsettings.json "640 root:mineclaw"
mode_is /etc/mineclaw/install.conf "644 root:root"
mode_is /etc/mineclaw-web "750 mineclaw-web:mineclaw-web"
mode_is /etc/mineclaw-web/config.json "640 mineclaw-web:mineclaw-web"
mode_is /etc/mineclaw-web/bridge.key "600 root:root"
mode_is /etc/mineclaw-bridge/bridge.json "600 root:root"
mode_is "$UPDATE_DIR" "750 mineclaw-web:mineclaw-web"
grep -qx "VERSION=$VER" /etc/mineclaw/install.conf || fail "install.conf has no VERSION=$VER"
grep -qx "COMPONENTS=server,web" /etc/mineclaw/install.conf || fail "install.conf has no COMPONENTS=server,web"
grep -qx "ARCH=$ARCH" /etc/mineclaw/install.conf || fail "install.conf has no ARCH=$ARCH"
ok "install.conf: $(grep -E '^(REPO|VERSION|ARCH|COMPONENTS)=' /etc/mineclaw/install.conf | tr '\n' ' ')"
systemctl show mineclaw-web-api -p Environment --value | tr ' ' '\n' | grep -x MINECLAW_INSTALL_KIND=systemd >/dev/null ||
    fail "mineclaw-web-api has no MINECLAW_INSTALL_KIND=systemd"
ok "mineclaw-web-api: MINECLAW_INSTALL_KIND=systemd"
[[ $(/usr/local/bin/node --version) == v22.* ]] || fail "node is $(/usr/local/bin/node --version)"
ok "node $(/usr/local/bin/node --version) at $(readlink -f /usr/local/bin/node)"
TOKEN=$(sed -n 's/.*"Token"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' /etc/mineclaw/appsettings.json | head -n1)
BKEY=$(tr -d '[:space:]' < /etc/mineclaw-web/bridge.key)
((${#TOKEN} == 64)) || fail "Supervisor:Token is ${#TOKEN} chars, want 64"
secret_absent "$T/install1.log" "the Supervisor token" "$TOKEN"
secret_absent "$T/install1.log" "the bridge key" "$BKEY"
for f in mineclaw-release mineclaw-web-release mineclaw-update mineclaw-web-users; do
    [[ -x /usr/local/sbin/$f ]] || fail "/usr/local/sbin/$f missing"
done
ok "helpers in /usr/local/sbin"

step "re-run (idempotent)"
snap() {
    sha256sum /etc/mineclaw/appsettings.json /etc/mineclaw-web/config.json /etc/mineclaw-web/bridge.key /etc/mineclaw-bridge/bridge.json
    echo "bot $(cur /opt/mineclaw) web $(cur /opt/mineclaw-web) bridge $(cur /opt/mineclaw-bridge)"
    id mineclaw; id mineclaw-web; id mineclaw-bridge
}
snap > "$T/snap1"
run_install install2 --from "$A" --timeout "$SETUP_WAIT"
snap > "$T/snap2"
diff -u "$T/snap1" "$T/snap2" || fail "a re-run changed config, keys, users or releases"
ok "config, keys, users and releases unchanged"
units_active
api_health

step "--api-key-file (written 0600, never printed)"
KEY="sk-ant-api03-ci-$(od -An -tx1 -N24 /dev/urandom | tr -d ' \n')"
printf '%s\n' "$KEY" > "$T/key"; chmod 0600 "$T/key"
run_install key --from "$A" --no-start --api-key-file "$T/key"
mode_is /etc/mineclaw/mineclaw.env "600 root:root"
grep -qxF "MINECLAW_Agent__Llm__ApiKey=$KEY" /etc/mineclaw/mineclaw.env || fail "the key is not in mineclaw.env"
secret_absent "$T/key.log" "the API key" "$KEY"
sed -i '/^MINECLAW_Agent__Llm__ApiKey=/d' /etc/mineclaw/mineclaw.env # back to setup mode for the steps below (no restart happened)
units_active

step "update request: the installed version (no-op)"
s=$(request_update "{\"version\":\"$VER\",\"requestedBy\":\"ci\",\"requestedAt\":\"$(date -u +%Y-%m-%dT%H:%M:%SZ)\"}")
echo "  $s"
[[ $s == *'"state":"succeeded"'* && $s == *'already on'* ]] || fail "want state succeeded (already on $VER)"
[[ $(stat -c '%a' "$UPDATE_DIR/status.json") == 644 ]] || fail "status.json is not 0644"
ok "status: succeeded, nothing to do"

step "update request: a bad version"
s=$(request_update '{"version":"1.2.3; touch /tmp/mineclaw-ci-pwned","requestedBy":"ci"}')
echo "  $s"
[[ $s == *'"state":"failed"'* && $s == *'invalid update request'* ]] || fail "want state failed (invalid update request)"
[[ ! -e /tmp/mineclaw-ci-pwned ]] || fail "the bad version ran something"
systemctl is-active --quiet mineclaw-update.path || fail "mineclaw-update.path is not armed after a rejected request"
ok "rejected; the path unit is still armed"

step "mineclaw-update apply --force (reinstall through the updater)"
before_bot=$(cur /opt/mineclaw) before_web=$(cur /opt/mineclaw-web)
/usr/local/sbin/mineclaw-update apply "$VER" --force --from "$A" 2>&1 | tee "$T/apply.log" || fail "mineclaw-update apply exited non-zero"
[[ $(cur /opt/mineclaw) != "$before_bot" && $(cur /opt/mineclaw-web) != "$before_web" ]] || fail "no new release ids after a forced reinstall"
[[ $(cur /opt/mineclaw-web) == "$(cur /opt/mineclaw-bridge)" ]] || fail "web and bridge are on different release ids"
grep -q '"state":"succeeded"' /var/lib/mineclaw-update/status.json || fail "status is not succeeded"
ok "bot $before_bot -> $(cur /opt/mineclaw), web $before_web -> $(cur /opt/mineclaw-web)"
units_active
api_health
secret_absent "$T/apply.log" "the Supervisor token" "$TOKEN"

step "a broken release rolls back"
B=$T/broken; mkdir -p "$B" "$T/unpack"
for f in "$A"/*; do [[ -f $f && $(basename "$f") != SHA256SUMS ]] && ln -s "$f" "$B/$(basename "$f")"; done
srv=mineclaw-server-$VER-linux-$ARCH.tar.gz
rm -f "$B/$srv"
tar -xzf "$A/$srv" -C "$T/unpack"
printf '#!/bin/sh\necho "broken build for the rollback test" >&2\nexit 1\n' > "$T/unpack/mineclaw/MineClaw.App"
chmod 0755 "$T/unpack/mineclaw/MineClaw.App"
tar -czf "$B/$srv" -C "$T/unpack" mineclaw
(cd "$B" && sha256sum -- * > "$T/SUMS.broken") && mv "$T/SUMS.broken" "$B/SHA256SUMS"
before_bot=$(cur /opt/mineclaw) before_web=$(cur /opt/mineclaw-web)
if MINECLAW_HEALTH_TIMEOUT=40 /usr/local/sbin/mineclaw-update apply "$VER" --force --from "$B" 2>&1 | tee "$T/broken.log"; then
    fail "the broken release was reported as a success"
fi
grep -q '"state":"rolled-back"' /var/lib/mineclaw-update/status.json || fail "status is not rolled-back: $(cat /var/lib/mineclaw-update/status.json)"
[[ $(cur /opt/mineclaw) == "$before_bot" ]] || fail "the bot is on $(cur /opt/mineclaw), want $before_bot after the rollback"
[[ $(cur /opt/mineclaw-web) == "$before_web" ]] || fail "the web moved to $(cur /opt/mineclaw-web) although only the bot broke"
ok "rolled back to bot $before_bot; web stayed on $before_web"
units_active
api_health

step "--uninstall (keeps data)"
run_install uninstall --uninstall
for u in "${UNITS[@]}" mineclaw-update; do
    [[ ! -e /etc/systemd/system/$u.service ]] || fail "unit $u.service still installed"
done
[[ ! -e /opt/mineclaw && ! -e /opt/mineclaw-web && ! -e /usr/local/sbin/mineclaw-update ]] || fail "releases or helpers left behind"
[[ -f /etc/mineclaw/appsettings.json && -d /var/lib/mineclaw && -f /etc/mineclaw-web/bridge.key ]] || fail "data or config deleted without --purge"
id mineclaw >/dev/null 2>&1 || fail "user mineclaw deleted without --purge"
ok "units, helpers, releases gone; config, data and users kept"

step "--uninstall --purge --yes"
run_install purge --uninstall --purge --yes
for p in /etc/mineclaw /etc/mineclaw-web /etc/mineclaw-bridge /var/lib/mineclaw /var/lib/mineclaw-web /var/lib/mineclaw-update \
    /var/log/mineclaw /var/log/mineclaw-update /opt/node; do
    [[ ! -e $p ]] || fail "$p still exists"
done
for u in mineclaw mineclaw-web mineclaw-bridge; do ! id "$u" >/dev/null 2>&1 || fail "user $u still exists"; done
[[ -z $(systemctl list-unit-files 'mineclaw*' --no-legend 2>/dev/null) ]] || fail "units left: $(systemctl list-unit-files 'mineclaw*' --no-legend)"
ss -ltn | grep -E '[:.]8080\s' >/dev/null && fail "something still listens on 8080"
ok "everything removed"

STEP="done"
echo
echo "CI INSTALL TEST PASSED ($VER, linux-$ARCH)"
