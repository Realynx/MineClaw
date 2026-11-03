#!/usr/bin/env bash
# One-time, idempotent install of the Web Explorer's host side INSIDE CT 114 (beside the bot, which provision-alice.sh set up).
# Normally run for you by `pwsh deploy/deploy-web.ps1 -Install`, which stages everything in /root/mineclaw-web-install via
# `ssh alice` + `pct push 114` and runs `pct exec 114 -- bash /root/mineclaw-web-install/install-ct114.sh --from <that dir>`.
# Safe to re-run: what is already there is kept (keys, config, users); units and helper scripts are refreshed.
#
#   install-ct114.sh [--from DIR] [--origin URL]... [--node-tarball FILE]
#     --from DIR        where the staged files are (default: this script's dir): node-v<ver>-linux-x64.tar.xz, the three .service
#                       files, config.example.json, mineclaw-web.env.example, web-release.sh, mineclaw-web-users, enable-viewer.sh
#     --origin URL      Web:AllowedOrigins for a NEW config.json (repeatable; default http://10.1.2.73:8080)
#
# What it does:
#   1. Node.js $NODE_VERSION (official linux-x64 tarball, sha256-pinned below) in /opt/node, /usr/local/bin/{node,npm,npx}
#   2. users: mineclaw-web (own group), mineclaw-bridge (primary group mineclaw: reads the bot config + db read-only)
#   3. dirs: /opt/mineclaw-web, /opt/mineclaw-bridge (releases), /etc/mineclaw-web (0750 mineclaw-web), /var/lib/mineclaw-web
#      (0700 mineclaw-web), /etc/mineclaw-bridge (0700 root)
#   4. the bridge HMAC key (32 random bytes, hex), ONE value in two root-only files handed to the services as systemd
#      credentials: /etc/mineclaw-bridge/bridge.json (0600, {"Bridge":{"Key":...}}) and /etc/mineclaw-web/bridge.key (0600).
#      Never printed. An existing key is kept (and copied to the other file if only one exists).
#   5. /etc/mineclaw-web/config.json from config.example.json (only when missing): AllowedOrigins = --origin values,
#      CookieSecure auto, TrustProxy off; a placeholder key reminds you of the cloudflared hostname for later
#   6. helpers: /usr/local/sbin/mineclaw-web-release, mineclaw-web-users, mineclaw-viewer-config
#   7. the units mineclaw-bridge, mineclaw-web-ui, mineclaw-web-api (+ daemon-reload). Nothing is enabled or started here:
#      the first deploy (deploy-web.ps1) does that once there is a release.
# It never touches the bot (/etc/mineclaw, /opt/mineclaw, the mineclaw unit): that is enable-viewer.sh, a separate step.
set -euo pipefail
export LC_ALL=C

NODE_VERSION=22.23.4
NODE_SHA256=1805bcd35e6815559bd2eb97db438d3fdb0eb3a5e6a977b7c1badca76337e9ce   # node-v22.23.4-linux-x64.tar.xz (nodejs.org SHASUMS256.txt)

FROM=$(cd "$(dirname "$0")" && pwd) TARBALL="" ORIGINS=()
while (($#)); do
    case $1 in
        --from) FROM=$2; shift 2 ;;
        --origin) ORIGINS+=("$2"); shift 2 ;;
        --node-tarball) TARBALL=$2; shift 2 ;;
        -h | --help) sed -n '2,27p' "$0"; exit 0 ;;
        *) echo "unknown option $1" >&2; exit 64 ;;
    esac
done
((${#ORIGINS[@]})) || ORIGINS=(http://10.1.2.73:8080)
TARBALL=${TARBALL:-$FROM/node-v$NODE_VERSION-linux-x64.tar.xz}

die() { echo "ERROR: $*" >&2; exit 1; }
step() { echo "== $*"; }
[[ $(id -u) -eq 0 ]] || die "run as root (pct exec 114 -- ...)"
id -u mineclaw >/dev/null 2>&1 || die "user mineclaw missing: this CT was not provisioned for the bot (deploy/provision-alice.sh)"
[[ -d /etc/mineclaw ]] || die "/etc/mineclaw missing: provision the bot first"
umask 022

step "1. Node.js v$NODE_VERSION"
want=v$NODE_VERSION
have=$(/usr/local/bin/node --version 2>/dev/null || true)
if [[ $have == "$want" ]]; then
    echo "node: $have (kept)"
else
    if [[ -e /usr/local/bin/node && ! -L /usr/local/bin/node ]]; then
        die "/usr/local/bin/node exists and is not our symlink (have: ${have:-unknown}); remove it or install v$NODE_VERSION there yourself"
    fi
    [[ -f $TARBALL ]] || die "no Node tarball at $TARBALL (deploy-web.ps1 -Install downloads and stages it)"
    command -v xz >/dev/null 2>&1 || die "xz missing (apt-get install xz-utils)"
    echo "$NODE_SHA256  $TARBALL" | sha256sum -c --quiet - || die "sha256 of $TARBALL does not match the pinned $NODE_SHA256"
    dst=/opt/node/node-$want-linux-x64
    install -d -m 0755 -o root -g root /opt/node
    if [[ ! -x $dst/bin/node ]]; then
        rm -rf -- "$dst.tmp"
        mkdir -p "$dst.tmp"
        tar -xJf "$TARBALL" -C "$dst.tmp" --strip-components=1 --no-same-owner
        chown -R root:root "$dst.tmp"
        chmod -R u=rwX,go=rX "$dst.tmp"
        mv -T "$dst.tmp" "$dst"
    fi
    "$dst/bin/node" --version >/dev/null || die "$dst/bin/node does not run on this CT"
    ln -sfn "node-$want-linux-x64" /opt/node/.current.new
    mv -Tf /opt/node/.current.new /opt/node/current
    for b in node npm npx; do ln -sfn "/opt/node/current/bin/$b" "/usr/local/bin/$b"; done
    echo "node: ${have:-none} -> $(/usr/local/bin/node --version) (/opt/node/current; older versions under /opt/node stay until you remove them)"
fi

step "2. users"
getent group mineclaw-web >/dev/null || { groupadd --system mineclaw-web && echo "group mineclaw-web: created"; }
if id -u mineclaw-web >/dev/null 2>&1; then echo "user mineclaw-web: exists"
else
    useradd --system --gid mineclaw-web --home-dir /var/lib/mineclaw-web --no-create-home --shell /usr/sbin/nologin \
        --comment "MineClaw Web Explorer" mineclaw-web
    echo "user mineclaw-web: created"
fi
if id -u mineclaw-bridge >/dev/null 2>&1; then echo "user mineclaw-bridge: exists"
else
    useradd --system --gid mineclaw --home-dir /nonexistent --no-create-home --shell /usr/sbin/nologin \
        --comment "MineClaw viewer bridge" mineclaw-bridge
    echo "user mineclaw-bridge: created (primary group mineclaw)"
fi
if ! id -nG mineclaw-bridge | tr ' ' '\n' | grep -qx mineclaw; then
    usermod -aG mineclaw mineclaw-bridge && echo "user mineclaw-bridge: added to group mineclaw"
fi

step "3. directories"
install -d -m 0755 -o root -g root /opt/mineclaw-web /opt/mineclaw-web/releases /opt/mineclaw-bridge /opt/mineclaw-bridge/releases
# users.json is rewritten (temp + rename) by the api and the users CLI, so mineclaw-web owns the dir.
install -d -m 0750 -o mineclaw-web -g mineclaw-web /etc/mineclaw-web
install -d -m 0700 -o mineclaw-web -g mineclaw-web /var/lib/mineclaw-web
install -d -m 0700 -o root -g root /etc/mineclaw-bridge
echo "ok: /opt/mineclaw-web /opt/mineclaw-bridge /etc/mineclaw-web /var/lib/mineclaw-web /etc/mineclaw-bridge"

step "4. bridge HMAC key"
NEST_KEY=/etc/mineclaw-web/bridge.key
BRIDGE_JSON=/etc/mineclaw-bridge/bridge.json
read_bridge_json() { # the Bridge:Key in bridge.json, or nothing
    [[ -s $BRIDGE_JSON ]] || return 0
    /usr/local/bin/node -e 'try { const j = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8")); const k = (j.Bridge ?? j.bridge ?? {}).Key ?? (j.Bridge ?? j.bridge ?? {}).key; if (typeof k === "string") process.stdout.write(k.trim()); } catch {}' "$BRIDGE_JSON"
}
write_secret() { # $1 path, $2 content: root 0600, atomic, never echoed
    local tmp; tmp=$(mktemp "$(dirname "$1")/.$(basename "$1").XXXXXX")
    chmod 0600 "$tmp"; chown root:root "$tmp"
    printf '%s\n' "$2" > "$tmp"
    mv -f "$tmp" "$1"
}
nest_key=""; [[ -s $NEST_KEY ]] && nest_key=$(tr -d '[:space:]' < "$NEST_KEY")
bridge_key=$(read_bridge_json)
key=""
if [[ -n $nest_key && -n $bridge_key && $nest_key != "$bridge_key" ]]; then
    die "$NEST_KEY and $BRIDGE_JSON hold DIFFERENT keys. Delete the wrong one (or both, to generate a new pair) and re-run."
elif [[ -n $nest_key ]]; then key=$nest_key; src="kept (from $NEST_KEY)"
elif [[ -n $bridge_key ]]; then key=$bridge_key; src="kept (from $BRIDGE_JSON)"
else key=$(od -An -tx1 -N32 /dev/urandom | tr -d ' \n'); src="generated (32 random bytes, 64 hex chars)"
fi
((${#key} >= 32)) || die "the existing bridge key is shorter than 32 chars; delete $NEST_KEY and $BRIDGE_JSON and re-run"
[[ $key =~ ^[A-Za-z0-9+/=_-]+$ ]] || die "the existing bridge key has characters this script does not write into JSON; replace it"
[[ $nest_key == "$key" ]] || write_secret "$NEST_KEY" "$key"
[[ $bridge_key == "$key" ]] || write_secret "$BRIDGE_JSON" "{\"Bridge\":{\"Key\":\"$key\"}}"
chown root:root "$NEST_KEY" "$BRIDGE_JSON"; chmod 0600 "$NEST_KEY" "$BRIDGE_JSON"
unset key nest_key bridge_key
echo "key: $src; $NEST_KEY + $BRIDGE_JSON (0600 root, one value, loaded by systemd as credentials; not shown)"

step "5. /etc/mineclaw-web/config.json"
CONF=/etc/mineclaw-web/config.json
if [[ -f $CONF ]]; then
    echo "config: kept ($(/usr/local/bin/node -e 'try { const j = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8")); console.log("AllowedOrigins " + JSON.stringify((j.Web ?? {}).AllowedOrigins)); } catch (e) { console.log("not valid JSON: " + e.message); }' "$CONF"))"
else
    [[ -f $FROM/config.example.json ]] || die "no config.example.json in $FROM"
    tmp=$(mktemp /etc/mineclaw-web/.config.json.XXXXXX)
    /usr/local/bin/node - "$FROM/config.example.json" "$tmp" "${ORIGINS[@]}" <<'EOF'
const fs = require('fs');
const [src, dst, ...origins] = process.argv.slice(2);
const j = JSON.parse(fs.readFileSync(src, 'utf8'));
j.Web.AllowedOrigins = origins;
j.Web.CookieSecure = 'auto';
j.Web.TrustProxy = false;
j.Web.TrustedProxies = [];
// Unknown keys are ignored by the api's config schema: this one only reminds the next editor (docs/web/deploy.md › Cloudflare).
j.Web._TunnelOriginTodo = 'later: add https://<cloudflared hostname> to AllowedOrigins, then set TrustProxy + TrustedProxies';
fs.writeFileSync(dst, JSON.stringify(j, null, 2) + '\n');
EOF
    chown mineclaw-web:mineclaw-web "$tmp"; chmod 0640 "$tmp"
    mv -f "$tmp" "$CONF"
    echo "config: written from config.example.json (AllowedOrigins ${ORIGINS[*]}; CookieSecure auto)"
fi
if [[ -f $FROM/mineclaw-web.env.example ]]; then
    install -m 0640 -o root -g mineclaw-web "$FROM/mineclaw-web.env.example" /etc/mineclaw-web/mineclaw-web.env.example
fi

step "6. helper scripts"
for pair in web-release.sh:mineclaw-web-release mineclaw-web-users:mineclaw-web-users enable-viewer.sh:mineclaw-viewer-config; do
    src=$FROM/${pair%%:*} dst=/usr/local/sbin/${pair##*:}
    [[ -f $src ]] || die "no ${pair%%:*} in $FROM"
    if cmp -s "$src" "$dst"; then echo "$dst: unchanged"; else install -m 0755 -o root -g root "$src" "$dst" && echo "$dst: installed/updated"; fi
done

step "7. systemd units"
for u in mineclaw-bridge mineclaw-web-ui mineclaw-web-api; do
    [[ -f $FROM/$u.service ]] || die "no $u.service in $FROM"
    if cmp -s "$FROM/$u.service" "/etc/systemd/system/$u.service"; then echo "unit $u: unchanged"
    else install -m 0644 "$FROM/$u.service" "/etc/systemd/system/$u.service" && echo "unit $u: installed/updated"; fi
done
systemctl daemon-reload
if [[ -L /opt/mineclaw-web/current ]]; then
    echo "a release is installed: restart the stack to pick up changed units: /usr/local/sbin/mineclaw-web-release restart"
else
    echo "units are not enabled or started yet: the first deploy (pwsh deploy/deploy-web.ps1) does that"
fi

step "memory"
limit=$(cat /sys/fs/cgroup/memory.max 2>/dev/null || true)
if [[ $limit =~ ^[0-9]+$ ]]; then limit="$((limit / 1048576)) MB"; else limit="$(free -m | awk '/^Mem:/ {print $2}') MB (free)"; fi
echo "CT memory: $limit; web units MemoryMax: api 512M + ui 384M + bridge 600M = ~1.5 GB"
echo "recommendation (not applied): lower the hive's Hive:Resources:LimitMb from 14336 to ~12800 in the bot config so the"
echo "ceiling leaves room for the web units (mineclaw-viewer-config shows the current value)"

echo
echo "done. Next: the bot config step (dry run first): pct exec 114 -- /usr/local/sbin/mineclaw-viewer-config   (then --apply)"
echo "      then the first deploy from the dev box: pwsh deploy/deploy-web.ps1   (docs/web/deploy.md)"
if [[ -s /etc/mineclaw-web/users.json ]] && grep -q '"username"' /etc/mineclaw-web/users.json; then
    echo "      web users exist; manage them with /usr/local/sbin/mineclaw-web-users (from a real terminal: ssh -t)"
else
    echo "      then open http://10.1.2.73:8080 on the LAN and complete the setup page: it creates the first (admin) account."
    echo "      Whoever completes it first becomes the admin, so do it right after the first deploy."
fi
