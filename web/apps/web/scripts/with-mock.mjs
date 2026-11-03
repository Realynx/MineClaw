// Runs a command with NEXT_PUBLIC_MOCK=1 (cross-platform; no extra dependency). Usage: node scripts/with-mock.mjs next dev
import { spawn } from 'node:child_process';

const [cmd, ...args] = process.argv.slice(2);
if (!cmd) {
  console.error('usage: with-mock.mjs <command> [...args]');
  process.exit(2);
}
const child = spawn(cmd, args, {
  stdio: 'inherit',
  shell: process.platform === 'win32',
  env: { ...process.env, NEXT_PUBLIC_MOCK: '1', NEXT_DIST_DIR: process.env.NEXT_DIST_DIR ?? '.next-mock' },
});
child.on('exit', (code) => process.exit(code ?? 1));
