// Parses bridge JSON snapshots with the @mineclaw/contracts schemas exactly as Nest does: parseBridgeJson → toCamel → Schema.parse.
// Usage: node contract-check.mjs <contracts dist/index.js> <SchemaName> <file.json> [<SchemaName> <file.json> ...]
// Exit 0 = every snapshot parsed; 1 = a parse failed (the zod issues are printed).
import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

const [, , dist, ...pairs] = process.argv;
const C = await import(pathToFileURL(dist).href);
let failed = 0;
for (let i = 0; i < pairs.length; i += 2) {
  const name = pairs[i];
  const file = pairs[i + 1];
  const schema = C[name];
  if (!schema || typeof schema.safeParse !== 'function') {
    console.log(`FAIL ${name}: no such schema`);
    failed++;
    continue;
  }
  const value = C.toCamel(C.parseBridgeJson(readFileSync(file, 'utf8')));
  const r = schema.safeParse(value);
  if (r.success) console.log(`ok ${name}`);
  else {
    failed++;
    console.log(`FAIL ${name} (${file}): ${JSON.stringify(r.error.issues.slice(0, 8))}`);
  }
}
process.exit(failed ? 1 : 0);
