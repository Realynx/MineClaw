// Regenerates the route catalogue in docs/web/api.md (between the routes:begin/end markers) from the built route table.
// Usage: npm run gen:api-doc -w @mineclaw/contracts
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { z } from 'zod';
import * as C from '../dist/index.js';

const here = dirname(fileURLToPath(import.meta.url));
const docPath = join(here, '..', '..', '..', '..', 'docs', 'web', 'api.md');

const names = new Map();
for (const [k, v] of Object.entries(C)) if (v instanceof z.ZodType && !names.has(v)) names.set(v, k);
const n = (s) => (s ? `\`${names.get(s) ?? '(inline)'}\`` : '');

const groups = [
  ['Auth and web audit (Nest-native)', (id) => id === 'health' || id.startsWith('auth.') || id.startsWith('audit.')],
  [
    'Db reads (bridge-native)',
    (id) => /^(overview|memory\.|search|raptor\.|personality|shards\.|scripts\.|designs\.)/.test(id),
  ],
  ['Memory activity', (id) => id.startsWith('memoryActivity.')],
  ['Admin (bot writes)', (id) => id.startsWith('admin.') || id.startsWith('groom.')],
  [
    'Live hive (supervisor pass-through)',
    (id) =>
      /^(bots\.|hive\.|status|events|locate|world\.|feed|blueprints\.|buildplans\.|bodies\.|vision\.|brains\.)/.test(
        id,
      ),
  ],
  ['Live stream', (id) => id === 'live.stream'],
];

const lines = [];
const used = new Set();
for (const [title, match] of groups) {
  const rows = Object.entries(C.ROUTES).filter(([id]) => match(id) && !used.has(id));
  if (rows.length === 0) continue;
  lines.push(`### ${title}`, '');
  lines.push('| Route id | Public route | Role | Request | Response | Bridge `/v1` | Supervisor | Audit |');
  lines.push('|---|---|---|---|---|---|---|---|');
  for (const [id, r] of rows) {
    used.add(id);
    const req = [
      r.params ? `params ${n(r.params)}` : '',
      r.query ? `query ${n(r.query)}` : '',
      r.body ? `${r.bodyKind === 'multipart' ? 'multipart' : 'body'} ${n(r.body)}` : '',
    ]
      .filter(Boolean)
      .join('<br>');
    const res = r.response ? n(r.response) : r.responseKind;
    const status = r.status ? ` (${r.status})` : '';
    const bridge = r.bridge ? `\`${r.bridge.method} ${r.bridge.path}\`` : 'Nest-native';
    const sup =
      r.supervisor === null
        ? '–'
        : r.supervisor === 'bridge-native'
          ? 'bridge-native'
          : r.supervisor.replace(/\|/g, '\\|');
    lines.push(
      `| \`${id}\` | \`${r.method} ${r.path}\` | ${r.access} | ${req || '–'} | ${res}${status} | ${bridge} | ${sup} | ${r.audit ?? '–'} |`,
    );
  }
  lines.push('');
}
const missing = Object.keys(C.ROUTES).filter((id) => !used.has(id));
if (missing.length) throw new Error(`routes not in any group: ${missing.join(', ')}`);

const table = lines.join('\n');
const doc = readFileSync(docPath, 'utf8');
const begin = '<!-- routes:begin -->';
const end = '<!-- routes:end -->';
const a = doc.indexOf(begin);
const b = doc.indexOf(end);
if (a < 0 || b < a) throw new Error('markers not found in api.md');
writeFileSync(docPath, `${doc.slice(0, a + begin.length)}\n${table}\n${doc.slice(b)}`);
console.warn(`api.md: ${Object.keys(C.ROUTES).length} routes written`);
