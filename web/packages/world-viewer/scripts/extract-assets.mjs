#!/usr/bin/env node
// Extracts the Minecraft assets the viewer renders with from a client jar (a zip: read, never run) into assets/<version>/.
// They are Mojang's files: they stay on your machine (git-ignored) and are never committed. `npm run assets:pack` then
// builds generated/<version>/ from them for the tests.
//
//   node scripts/extract-assets.mjs --jar <path/to/minecraft-1.12.2-client.jar> --version 1.12.2
//   node scripts/extract-assets.mjs --jar <path/to/minecraft-b1.8.1-client.jar> --version b1.8.1
//   node scripts/extract-assets.mjs --find            (looks in the PrismLauncher / .minecraft folders and extracts both)
//
// Only what MineClaw.Render and this viewer use is kept: 1.12.2 blockstates, block and item models, block, item, entity,
// environment, misc, colormap and painting textures with their .mcmeta and the default font (textures/font/ascii.png); b1.8.1
// terrain.png, gui/items.png, mob/*, char.png, environment, misc, armor and the font (font/default.png with font.txt, the
// character table ChatAllowedCharacters reads).

import { readFileSync, writeFileSync, mkdirSync, rmSync, existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { inflateRawSync } from 'node:zlib';
import { homedir } from 'node:os';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const outRoot = resolve(here, '..', 'assets');

/** Entries of a zip archive (central directory), decompressed on demand. */
function readZip(buf) {
  let eocd = -1;
  for (let i = buf.length - 22; i >= Math.max(0, buf.length - 65557); i--) if (buf.readUInt32LE(i) === 0x06054b50) { eocd = i; break; }
  if (eocd < 0) throw new Error('not a zip (no end of central directory)');
  const count = buf.readUInt16LE(eocd + 10);
  let o = buf.readUInt32LE(eocd + 16);
  const entries = [];
  for (let n = 0; n < count; n++) {
    if (buf.readUInt32LE(o) !== 0x02014b50) throw new Error('bad central directory entry');
    const method = buf.readUInt16LE(o + 10);
    const csize = buf.readUInt32LE(o + 20);
    const nameLen = buf.readUInt16LE(o + 28), extraLen = buf.readUInt16LE(o + 30), commentLen = buf.readUInt16LE(o + 32);
    const local = buf.readUInt32LE(o + 42);
    const name = buf.toString('utf8', o + 46, o + 46 + nameLen);
    o += 46 + nameLen + extraLen + commentLen;
    entries.push({
      name,
      read() {
        const lnameLen = buf.readUInt16LE(local + 26), lextra = buf.readUInt16LE(local + 28);
        const start = local + 30 + lnameLen + lextra;
        const data = buf.subarray(start, start + csize);
        if (method === 0) return Buffer.from(data);
        if (method === 8) return inflateRawSync(data);
        throw new Error(`unsupported zip method ${method} for ${name}`);
      },
    });
  }
  return entries;
}

const RULES = {
  '1.12.2': [
    /^assets\/minecraft\/blockstates\/[^/]+\.json$/,
    /^assets\/minecraft\/models\/(block|item)\/[^/]+\.json$/,
    /^assets\/minecraft\/textures\/(blocks|items|environment|misc|colormap)\/[^/]+\.png(\.mcmeta)?$/,
    /^assets\/minecraft\/textures\/entity\/.+\.png(\.mcmeta)?$/,
    /^assets\/minecraft\/textures\/models\/armor\/[^/]+\.png$/,
    /^assets\/minecraft\/textures\/painting\/[^/]+\.png$/,
    /^assets\/minecraft\/textures\/font\/ascii\.png$/,
  ],
  'b1.8.1': [/^terrain\.png$/, /^gui\/items\.png$/, /^mob\/[^/]+\.png$/, /^char\.png$/, /^environment\/[^/]+\.png$/, /^misc\/[^/]+\.png$/, /^armor\/[^/]+\.png$/, /^item\/[^/]+\.png$/, /^art\/[^/]+\.png$/, /^terrain\/[^/]+\.png$/, /^font\/default\.png$/, /^font\.txt$/],
};

function extract(jar, version) {
  const rules = RULES[version];
  if (!rules) throw new Error(`unknown version ${version} (want 1.12.2 or b1.8.1)`);
  const zip = readZip(readFileSync(jar));
  const dest = join(outRoot, version);
  rmSync(dest, { recursive: true, force: true });
  let files = 0, bytes = 0;
  for (const e of zip) {
    if (e.name.endsWith('/') || !rules.some((r) => r.test(e.name))) continue;
    const rel = version === '1.12.2' ? e.name.replace(/^assets\/minecraft\//, '') : e.name;
    const out = join(dest, rel);
    mkdirSync(dirname(out), { recursive: true });
    const data = e.read();
    writeFileSync(out, data);
    files++;
    bytes += data.length;
  }
  console.log(`${version}: ${files} files, ${(bytes / 1048576).toFixed(2)} MB → ${dest}`);
}

function candidates(version) {
  const roots = [process.env.APPDATA, join(homedir(), '.local', 'share'), homedir()].filter(Boolean);
  return roots.flatMap((r) => [
    join(r, 'PrismLauncher', 'libraries', 'com', 'mojang', 'minecraft', version, `minecraft-${version}-client.jar`),
    join(r, '.minecraft', 'versions', version, `${version}.jar`),
  ]);
}

const args = process.argv.slice(2);
const arg = (k) => {
  const i = args.indexOf(k);
  return i >= 0 ? args[i + 1] : undefined;
};
if (args.includes('--find')) {
  for (const v of ['1.12.2', 'b1.8.1']) {
    const jar = candidates(v).find((p) => existsSync(p));
    if (!jar) console.error(`${v}: no client jar found (looked in ${candidates(v).join(', ')})`);
    else extract(jar, v);
  }
} else if (arg('--jar') && arg('--version')) extract(arg('--jar'), arg('--version'));
else {
  console.error('usage: extract-assets.mjs --jar <client.jar> --version <1.12.2|b1.8.1>   |   extract-assets.mjs --find');
  process.exit(2);
}
