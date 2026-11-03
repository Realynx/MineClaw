#!/usr/bin/env bash
# The bot-side config step for the Web Explorer (CT 114), kept apart from install-ct114.sh on purpose: it edits the BOT's
# /etc/mineclaw/appsettings.json. Installed as /usr/local/sbin/mineclaw-viewer-config; deploy-web.ps1 -BotConfig [-Apply] runs it.
#
#   mineclaw-viewer-config                     # dry run: prints what it WOULD change, changes nothing
#   mineclaw-viewer-config --apply             # backs the file up (appsettings.json.bak-<utc>, same owner/mode), then writes it
#   mineclaw-viewer-config --apply --restart   # ... and restarts the bot (mineclaw-release restart, health-checked) and the bridge
#   options: --admin-writes (also Supervisor:AllowAdminWrites=true, the Admin page's writes), --config FILE
#
# Makes sure of: Supervisor:Enabled=true, Supervisor:Token set (generated when missing: 32 random bytes as hex),
# Supervisor:Viewer:Enabled=true. Viewer:AllowCommands is reported, never changed. Idempotent: a second run changes nothing.
# The token is NEVER printed (only whether it is set and its length). Plain JSON only: a file with comments or trailing commas is
# left alone with a message (edit it by hand then).
# Note: deploy-alice.ps1 -PushConfig overwrites /etc/mineclaw/appsettings.json with the local src/MineClaw.App/appsettings.json.
# Put the same Supervisor keys in the local file too (docs/web/deploy.md › Bot config), or re-run this after a -PushConfig.
set -euo pipefail
export LC_ALL=C

CFG=/etc/mineclaw/appsettings.json APPLY=0 RESTART=0 ADMIN=0
NODE=${MINECLAW_NODE:-/usr/local/bin/node}
while (($#)); do
    case $1 in
        --apply) APPLY=1; shift ;;
        --restart) RESTART=1; shift ;;
        --admin-writes) ADMIN=1; shift ;;
        --config) CFG=$2; shift 2 ;;
        -h | --help) sed -n '2,17p' "$0"; exit 0 ;;
        *) echo "unknown option $1" >&2; exit 64 ;;
    esac
done
[[ $(id -u) -eq 0 ]] || { echo "run as root" >&2; exit 1; }
[[ -f $CFG ]] || { echo "no bot config at $CFG" >&2; exit 1; }
[[ -x $NODE ]] || { echo "$NODE missing: run install-ct114.sh first" >&2; exit 1; }
((RESTART && !APPLY)) && { echo "--restart goes with --apply" >&2; exit 64; }

# Names (never values) of env overrides the bot's unit would apply on top of the file.
ENV_OVERRIDES=$(grep -oE '^[[:space:]]*MINECLAW_Supervisor__[A-Za-z_]+' /etc/mineclaw/mineclaw.env 2>/dev/null | tr -d ' ' | tr '\n' ' ' || true)

rc=0
CFG=$CFG APPLY=$APPLY ADMIN=$ADMIN ENV_OVERRIDES=$ENV_OVERRIDES "$NODE" --input-type=module - <<'EOF' || rc=$?
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';

const file = process.env.CFG;
const apply = process.env.APPLY === '1';
const admin = process.env.ADMIN === '1';
let text = fs.readFileSync(file, 'utf8');
const bom = text.charCodeAt(0) === 0xfeff;
if (bom) text = text.slice(1);
let cfg;
try {
  cfg = JSON.parse(text);
} catch (e) {
  console.error(`cannot parse ${file} as plain JSON (${e.message}); it may hold comments or trailing commas.`);
  console.error('Make the change by hand: "Supervisor": { "Enabled": true, "Token": "<64 hex chars>", "Viewer": { "Enabled": true } }');
  process.exit(3);
}
if (cfg === null || typeof cfg !== 'object' || Array.isArray(cfg)) { console.error(`${file}: not a JSON object`); process.exit(3); }

// .NET configuration keys are case-insensitive: reuse whatever spelling the file already has.
const keyOf = (obj, name) => Object.keys(obj).find((k) => k.toLowerCase() === name.toLowerCase()) ?? name;
const section = (obj, name, label) => {
  const k = keyOf(obj, name);
  if (obj[k] === undefined || obj[k] === null) obj[k] = {};
  if (typeof obj[k] !== 'object' || Array.isArray(obj[k])) { console.error(`${label} is not an object`); process.exit(3); }
  return obj[k];
};
const changes = [];
const notes = [];
const show = (v) => (v === undefined ? '(unset)' : JSON.stringify(v));
const ensure = (obj, name, value, label) => {
  const k = keyOf(obj, name);
  if (obj[k] === value) notes.push(`${label}: ${show(value)} (already)`);
  else { changes.push(`${label}: ${show(obj[k])} -> ${show(value)}`); obj[k] = value; }
};

const sup = section(cfg, 'Supervisor', 'Supervisor');
ensure(sup, 'Enabled', true, 'Supervisor:Enabled');
const tk = keyOf(sup, 'Token');
const tok = typeof sup[tk] === 'string' ? sup[tk].trim() : '';
if (tok.length >= 32) notes.push(`Supervisor:Token: set, ${tok.length} chars (kept; value not shown)`);
else if (tok.length > 0) notes.push(`Supervisor:Token: set but only ${tok.length} chars (kept; value not shown). Consider a longer one.`);
else { changes.push('Supervisor:Token: (unset) -> generated, 64 hex chars (value never shown)'); sup[tk] = crypto.randomBytes(32).toString('hex'); }
const viewer = section(sup, 'Viewer', 'Supervisor:Viewer');
ensure(viewer, 'Enabled', true, 'Supervisor:Viewer:Enabled');
const ac = viewer[keyOf(viewer, 'AllowCommands')];
notes.push(`Supervisor:Viewer:AllowCommands: ${show(ac)} (not changed here; true lets web users with the "commands" role arm command grants)`);
if (admin) ensure(sup, 'AllowAdminWrites', true, 'Supervisor:AllowAdminWrites');
else notes.push(`Supervisor:AllowAdminWrites: ${show(sup[keyOf(sup, 'AllowAdminWrites')])} (pass --admin-writes for the Admin page's writes)`);

const bind = sup[keyOf(sup, 'Bind')];
const port = sup[keyOf(sup, 'Port')] ?? 8765;
const b = typeof bind === 'string' ? bind.trim() : '*';
const loopbackOk = ['', '*', '+', '0.0.0.0', 'localhost', '127.0.0.1'].includes(b);
notes.push(`Supervisor:Bind: ${show(bind)} (default "*"), Port: ${port}`);
if (!loopbackOk)
  notes.push(`WARNING: the bridge calls http://127.0.0.1:${port}; with Bind ${show(bind)} the Supervisor may not answer on loopback. ` +
    `Either set Bind to "127.0.0.1" / "*", or put MINECLAW_Bridge__SupervisorUrl=http://${b}:${port} in /etc/mineclaw-bridge/bridge.env.`);
if (port !== 8765) notes.push('note: the bridge reads Supervisor:Port from this file, so a non-default port is fine');
if (process.env.ENV_OVERRIDES?.trim())
  notes.push(`note: /etc/mineclaw/mineclaw.env overrides ${process.env.ENV_OVERRIDES.trim()} on top of this file (values not shown)`);

const hive = cfg[keyOf(cfg, 'Hive')];
const res = hive && typeof hive === 'object' ? hive[keyOf(hive, 'Resources')] : undefined;
const limit = res && typeof res === 'object' ? res[keyOf(res, 'LimitMb')] : undefined;
notes.push(`Hive:Resources:LimitMb: ${show(limit)} (recommendation only, not changed: ~12800 on the 16 GB CT leaves ~1.5 GB for the web units)`);

for (const n of notes) console.log(`  ${n}`);
if (changes.length === 0) { console.log(`${file}: nothing to change`); process.exit(0); }
console.log(`${apply ? 'changing' : 'would change'} in ${file}:`);
for (const c of changes) console.log(`  ${c}`);
if (!apply) { console.log('dry run: nothing written. Re-run with --apply to write it.'); process.exit(0); }

const indent = (text.match(/\n([ \t]+)"/) ?? [, '  '])[1];
const st = fs.statSync(file);
const stamp = new Date().toISOString().replace(/[-:]/g, '').replace(/\.\d+Z$/, 'Z');
const backup = `${file}.bak-${stamp}`;
fs.copyFileSync(file, backup);
fs.chmodSync(backup, st.mode & 0o777);
fs.chownSync(backup, st.uid, st.gid);
const tmp = path.join(path.dirname(file), `.${path.basename(file)}.${process.pid}.tmp`);
fs.writeFileSync(tmp, (bom ? '﻿' : '') + JSON.stringify(cfg, null, indent) + '\n', { mode: st.mode & 0o777 });
fs.chownSync(tmp, st.uid, st.gid);
fs.chmodSync(tmp, st.mode & 0o777);
fs.renameSync(tmp, file);
console.log(`written; backup of the previous file: ${backup} (same owner and mode)`);
process.exit(10);
EOF
case $rc in
    0) exit 0 ;;
    10) ;;
    *) exit "$rc" ;;
esac
if ((RESTART)); then
    echo "restarting the bot (mineclaw-release restart, health-checked) so it picks the new Supervisor settings up"
    /usr/local/sbin/mineclaw-release restart
    if systemctl is-enabled --quiet mineclaw-bridge 2>/dev/null; then
        systemctl restart mineclaw-bridge && echo "mineclaw-bridge restarted (it reads Supervisor:Token at start)"
    fi
else
    echo "the bot reads this at start: restart it when convenient: /usr/local/sbin/mineclaw-release restart"
    echo "then restart the bridge (it reads Supervisor:Token at start): systemctl restart mineclaw-bridge"
fi
