// Resolve from the assembled app, never the development workspace. Native Linux modules are not loaded on the build host.
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { resolve } from 'node:path';
const manifest = resolve(process.argv[2]);
const require = createRequire(manifest);
const dependencies = Object.keys(JSON.parse(readFileSync(manifest, 'utf8')).dependencies ?? {});
for (const name of dependencies) require.resolve(name);
console.log(`Resolved all ${dependencies.length} assembled API dependencies.`);
