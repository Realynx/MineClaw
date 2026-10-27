#!/usr/bin/env bash
# One-time provisioning of the MineClaw LXC on a Proxmox node, kept for disaster recovery.
# ALREADY RUN ONCE (2026-10-26): CT 114 "claudechan" on alice, 10.1.2.73. Don't re-run it against a live setup.
#
# Run as root on the PVE node:
#   scp deploy/provision-alice.sh alice:/root/ && ssh alice bash /root/provision-alice.sh [options]
# then deploy from the dev box with: pwsh deploy/deploy-alice.ps1 -PushConfig
#
# What it does (each step stops on error):
#   1. checks that VMID is free cluster-wide and that IP is in no guest config; aborts if either is taken
#   2. pct create: unprivileged, nesting=1, onboot=1, startup order, VLAN-tagged veth with the PVE firewall on
#   3. /etc/pve/firewall/<vmid>.fw: GROUP allow-ssh, then GROUP isolated (isolated must stay LAST)
#   4. cluster.fw: backs up to /root/cluster.fw.bak-<ts>, then adds IP to [IPSET <ipset>] (skipped if already there)
#   5. in the CT: apt upgrade, the runtime deps of a self-contained linux-x64 .NET app, the mineclaw system user
#      (nologin), and /opt/mineclaw /etc/mineclaw (0750 root:mineclaw) /var/lib/mineclaw /var/log/mineclaw
#   6. connectivity: TCP to the Minecraft server, HTTPS to api.anthropic.com (+ --check-host hosts); status only
set -euo pipefail
export LC_ALL=C

VMID=114 HOSTNAME_=claudechan IP=10.1.2.73 CIDR=24 GW=10.1.2.1 VLAN=3 BRIDGE=vmbr0
CORES=4 MEMORY=4096 SWAP=512 DISK=16 STORAGE=local-lvm STARTUP=order=90,up=15
TEMPLATE=local:vztmpl/ubuntu-26.04-standard_26.04-1_amd64.tar.zst
IPSET=mc-servers MC=10.1.2.69:25565 CHECK_HOSTS=(api.anthropic.com)

usage() { sed -n '2,16p' "$0"; cat <<EOF

options (defaults):
  --vmid $VMID  --hostname $HOSTNAME_  --ip $IP  --cidr $CIDR  --gw $GW  --vlan $VLAN  --bridge $BRIDGE
  --cores $CORES  --memory $MEMORY  --swap $SWAP  --disk $DISK (GB)  --storage $STORAGE  --startup $STARTUP
  --template $TEMPLATE
  --ipset $IPSET  --mc $MC  --check-host HOST (repeatable; e.g. the Azure embeddings host)
EOF
}
while (($#)); do
    case $1 in
        --vmid) VMID=$2 ;; --hostname) HOSTNAME_=$2 ;; --ip) IP=$2 ;; --cidr) CIDR=$2 ;; --gw) GW=$2 ;;
        --vlan) VLAN=$2 ;; --bridge) BRIDGE=$2 ;; --cores) CORES=$2 ;; --memory) MEMORY=$2 ;; --swap) SWAP=$2 ;;
        --disk) DISK=$2 ;; --storage) STORAGE=$2 ;; --startup) STARTUP=$2 ;; --template) TEMPLATE=$2 ;;
        --ipset) IPSET=$2 ;; --mc) MC=$2 ;; --check-host) CHECK_HOSTS+=("$2") ;;
        -h | --help) usage; exit 0 ;;
        *) echo "unknown option $1" >&2; usage; exit 64 ;;
    esac
    shift 2
done
[[ $(id -u) -eq 0 ]] && command -v pct >/dev/null || { echo "run as root on a Proxmox node" >&2; exit 1; }
step() { echo; echo "== $*"; }

step "1. pre-flight: VMID $VMID and IP $IP must be unused"
if pvesh get /cluster/resources --type vm --output-format json |
    python3 -c "import json,sys; sys.exit(0 if any(r['vmid']==$VMID for r in json.load(sys.stdin)) else 1)"; then
    echo "STOP: VMID $VMID is already in use in the cluster" >&2; exit 2
fi
if grep -lE "ip=${IP//./\\.}/" /etc/pve/nodes/*/lxc/*.conf /etc/pve/nodes/*/qemu-server/*.conf 2>/dev/null; then
    echo "STOP: $IP already appears in the guest config(s) above" >&2; exit 2
fi
echo "ok"

step "2. pct create $VMID ($HOSTNAME_)"
pct create "$VMID" "$TEMPLATE" --hostname "$HOSTNAME_" --unprivileged 1 --features nesting=1 \
    --cores "$CORES" --memory "$MEMORY" --swap "$SWAP" --rootfs "$STORAGE:$DISK" \
    --onboot 1 --startup "$STARTUP" --ostype ubuntu \
    --net0 "name=eth0,bridge=$BRIDGE,firewall=1,gw=$GW,ip=$IP/$CIDR,tag=$VLAN,type=veth" \
    --description "MineClaw (claude chan / maid_chan) Minecraft bot. Deployed via deploy/deploy-alice.ps1"
cat "/etc/pve/lxc/$VMID.conf"

step "3. container firewall /etc/pve/firewall/$VMID.fw"
cat >"/etc/pve/firewall/$VMID.fw" <<'EOF'
[OPTIONS]

ipfilter: 1
policy_in: ACCEPT
enable: 1

[RULES]

GROUP allow-ssh
GROUP isolated

EOF
cat "/etc/pve/firewall/$VMID.fw"

step "4. cluster.fw: add $IP to [IPSET $IPSET]"
FW=/etc/pve/firewall/cluster.fw
BAK=/root/cluster.fw.bak-$(date -u +%Y%m%dT%H%M%SZ)
cp "$FW" "$BAK"
echo "backup: $BAK"
python3 - "$FW" "$IPSET" "$IP" <<'PY'
import sys
path, ipset, ip = sys.argv[1:]
lines = open(path).read().split("\n")
hdr = [i for i, l in enumerate(lines) if l.split("#")[0].strip() == f"[IPSET {ipset}]"]
if len(hdr) != 1: sys.exit(f"[IPSET {ipset}] not found exactly once in {path}")
j = hdr[0] + 1
while j < len(lines) and not lines[j].strip(): j += 1           # blank line after the header
k = j
while k < len(lines) and lines[k].strip() and not lines[k].startswith("["): k += 1   # the entries
if ip in [l.split("#")[0].strip() for l in lines[j:k]]:
    print(f"{ip} already in {ipset}; unchanged")
else:
    lines.insert(k, ip)
    open(path, "w").write("\n".join(lines))
    print(f"added {ip} to {ipset}")
PY
diff -u "$BAK" "$FW" || true
pve-firewall compile >/dev/null && echo "firewall compiles"

step "5. container setup"
pct start "$VMID"
for _ in $(seq 30); do pct exec "$VMID" -- systemctl is-system-running --quiet 2>/dev/null && break; sleep 2; done
pct exec "$VMID" -- env LC_ALL=C DEBIAN_FRONTEND=noninteractive bash -euo pipefail -c '
apt-get update -qq
apt-get upgrade -y -qq >/dev/null
icu=$(apt-cache search --names-only "^libicu[0-9]+$" | awk "{print \$1}" | sort -V | tail -n1)
ssl=$(apt-cache show libssl3t64 >/dev/null 2>&1 && echo libssl3t64 || echo libssl3)
# runtime deps of a self-contained linux-x64 .NET app, plus curl/nc for the connectivity checks
apt-get install -y -qq --no-install-recommends "$icu" ca-certificates tzdata "$ssl" zlib1g curl netcat-openbsd >/dev/null
id mineclaw >/dev/null 2>&1 || useradd --system --home-dir /var/lib/mineclaw --no-create-home --shell /usr/sbin/nologin mineclaw
install -d -m 0755 -o root -g root /opt/mineclaw /opt/mineclaw/releases
install -d -m 0750 -o root -g mineclaw /etc/mineclaw
install -d -m 0750 -o mineclaw -g mineclaw /var/lib/mineclaw /var/log/mineclaw
echo "installed: $icu $ssl; user: $(getent passwd mineclaw)"
ls -ld /opt/mineclaw /etc/mineclaw /var/lib/mineclaw /var/log/mineclaw'

step "6. connectivity from the CT (status only)"
pct exec "$VMID" -- env LC_ALL=C bash -c "nc -zv -w5 ${MC%:*} ${MC#*:} 2>&1 | tail -n1"
for h in "${CHECK_HOSTS[@]}"; do
    pct exec "$VMID" -- curl -s -o /dev/null --max-time 10 -w "https://$h -> HTTP %{http_code}, tls verify %{ssl_verify_result}\n" "https://$h/" ||
        echo "https://$h -> FAILED"
done
echo
echo "done. Next: pwsh deploy/deploy-alice.ps1 -PushConfig   (from the dev box)"
