/**
 * Users file CLI (no self-signup). Run on CT 114 as the service user so file ownership stays right:
 *   sudo -u mineclaw-web npm run users -w @mineclaw/api -- <command> ...      (or: node dist/cli/users.js <command> ...)
 *
 *   add <user> --roles viewer,chat[,commands,admin,design] [--player <mc name>] [--password-stdin]
 *   passwd <user> [--password-stdin]          resets the password and ends the user's sessions
 *   roles <user> <role,role,...>              replaces the roles
 *   player <user> <mc name | ->               links (or unlinks with -) the Minecraft player
 *   totp <user> enroll [--code <123456>]      prints the otpauth URL + secret once, activates after a valid code
 *   totp <user> disable
 *   disable <user> | enable <user>
 *   remove <user>                             deletes the user and ends their sessions
 *   list
 * Options: --file <users.json> (default `Web:UsersFile` from the config), --config <config.json>, --dev.
 * Passwords are read without echo on a TTY, or as the first line of stdin (`--password-stdin` or a pipe). Never as an argument.
 */
import { existsSync } from 'node:fs';
import { canonicalRoles, Role } from '@mineclaw/contracts';
import {
  normalizeUsername,
  PLAYER_RE,
  readUsersFile,
  USERNAME_RE,
  writeUsersFile,
  type UserRecord,
} from '../auth/users-store.js';
import { hashPassword, MIN_PASSWORD_LENGTH } from '../auth/passwords.js';
import { newTotpSecret, otpauthUrl, verifyTotp } from '../auth/totp.js';
import { dbPathOf, loadConfig } from '../config/config.js';
import { WebDb } from '../db/web-db.js';
import { promptHidden, promptLine, readStdinLine } from './prompt.js';

export interface CliIo {
  out: (line: string) => void;
  err: (line: string) => void;
  /** Password source (tests inject it). */
  password?: () => Promise<string>;
  /** TOTP code source for `totp enroll` (tests inject it). */
  code?: () => Promise<string>;
  now?: () => number;
}

class CliError extends Error {}

function flag(args: string[], name: string): string | undefined {
  const i = args.indexOf(name);
  if (i < 0) return undefined;
  const v = args[i + 1];
  args.splice(i, 2);
  return v;
}

function bool(args: string[], name: string): boolean {
  const i = args.indexOf(name);
  if (i < 0) return false;
  args.splice(i, 1);
  return true;
}

function parseRoles(text: string | undefined): Role[] {
  if (!text) throw new CliError('roles are required, e.g. --roles viewer,chat');
  const roles = text
    .split(',')
    .map((r) => r.trim().toLowerCase())
    .filter(Boolean);
  for (const r of roles)
    if (!Role.options.includes(r as Role)) throw new CliError(`unknown role "${r}" (${Role.options.join(', ')})`);
  if (roles.length === 0) throw new CliError('at least one role is required');
  return canonicalRoles(roles as Role[]).split(',') as Role[];
}

function resolvePaths(args: string[]): { file: string; db: string | undefined } {
  const explicit = flag(args, '--file');
  const configFile = flag(args, '--config');
  const dev = bool(args, '--dev');
  const argv = [...(configFile ? ['--config', configFile] : []), ...(dev ? ['--dev'] : [])];
  let file = explicit;
  let db: string | undefined;
  try {
    // The CLI never talks to the bridge: placeholders satisfy the required keys.
    const { config } = loadConfig({
      argv,
      overrides: {
        bridge: { key: 'cli-placeholder-key-not-used-0123456789' },
        web: { allowedOrigins: ['http://cli'] },
      },
    });
    file ??= config.web.usersFile;
    db = dbPathOf(config);
  } catch {
    file ??= '/etc/mineclaw-web/users.json';
  }
  return { file, db };
}

function revokeSessions(dbPath: string | undefined, username: string, io: CliIo): void {
  if (!dbPath || !existsSync(dbPath)) return;
  try {
    const db = new WebDb(dbPath);
    const r = db.db.prepare('delete from sessions where username = ?').run(username);
    db.close();
    if (Number(r.changes) > 0) io.out(`ended ${r.changes} session(s) of ${username}`);
  } catch (e) {
    io.err(`warning: could not end sessions in ${dbPath}: ${(e as Error).message}`);
  }
}

async function newPassword(args: string[], io: CliIo): Promise<string> {
  const fromStdin = bool(args, '--password-stdin');
  let pw: string;
  if (io.password) pw = await io.password();
  else if (fromStdin || !process.stdin.isTTY) pw = await readStdinLine();
  else {
    pw = await promptHidden('New password: ');
    const again = await promptHidden('Repeat password: ');
    if (pw !== again) throw new CliError('passwords do not match');
  }
  if (pw.length < MIN_PASSWORD_LENGTH)
    throw new CliError(`password must be at least ${MIN_PASSWORD_LENGTH} characters`);
  if (pw.length > 256) throw new CliError('password must be at most 256 characters');
  return pw;
}

function find(users: UserRecord[], name: string): number {
  const i = users.findIndex((u) => u.username === normalizeUsername(name));
  if (i < 0) throw new CliError(`no such user "${normalizeUsername(name)}"`);
  return i;
}

export async function runUsersCli(argv: readonly string[], io: CliIo): Promise<number> {
  const args = [...argv];
  try {
    const { file, db } = resolvePaths(args);
    const [cmd, name, ...rest] = args;
    const now = new Date(io.now?.() ?? Date.now()).toISOString();
    const data = readUsersFile(file);
    const save = () => writeUsersFile(file, data);

    switch (cmd) {
      case 'list': {
        if (data.users.length === 0) io.out(`(no users in ${file})`);
        for (const u of data.users) {
          io.out(
            `${u.username.padEnd(16)} roles=${u.roles.join(',').padEnd(28)} player=${(u.player ?? '-').padEnd(16)} totp=${u.totp ? 'on' : 'off'}${u.disabled ? ' DISABLED' : ''}`,
          );
        }
        return 0;
      }
      case 'add': {
        if (!name) throw new CliError('usage: add <user> --roles viewer,chat [--player <name>]');
        const username = normalizeUsername(name);
        if (!USERNAME_RE.test(username)) throw new CliError('usernames are 1-64 chars of a-z 0-9 _ . -');
        if (data.users.some((u) => u.username === username))
          throw new CliError(`user "${username}" exists (use passwd/roles)`);
        const roles = parseRoles(flag(rest, '--roles'));
        const player = flag(rest, '--player');
        if (player !== undefined && !PLAYER_RE.test(player))
          throw new CliError('player names are 1-16 chars of A-Z a-z 0-9 _');
        const passwordHash = await hashPassword(await newPassword(rest, io));
        data.users.push({
          username,
          passwordHash,
          roles,
          player: player ?? null,
          totp: null,
          disabled: false,
          createdAt: now,
          updatedAt: now,
        });
        save();
        io.out(`added ${username} (${roles.join(',')}${player ? `, player ${player}` : ''})`);
        return 0;
      }
      case 'passwd': {
        if (!name) throw new CliError('usage: passwd <user>');
        const i = find(data.users, name);
        const u = data.users[i]!;
        data.users[i] = { ...u, passwordHash: await hashPassword(await newPassword(rest, io)), updatedAt: now };
        save();
        revokeSessions(db, u.username, io);
        io.out(`password changed for ${u.username}`);
        return 0;
      }
      case 'roles': {
        if (!name || !rest[0]) throw new CliError('usage: roles <user> viewer,chat,commands,admin');
        const i = find(data.users, name);
        data.users[i] = { ...data.users[i]!, roles: parseRoles(rest[0]), updatedAt: now };
        save();
        io.out(`roles of ${data.users[i].username}: ${data.users[i].roles.join(',')}`);
        return 0;
      }
      case 'player': {
        if (!name || !rest[0]) throw new CliError('usage: player <user> <mc name | ->');
        const i = find(data.users, name);
        const player = rest[0] === '-' ? null : rest[0];
        if (player !== null && !PLAYER_RE.test(player))
          throw new CliError('player names are 1-16 chars of A-Z a-z 0-9 _');
        data.users[i] = { ...data.users[i]!, player, updatedAt: now };
        save();
        io.out(
          player
            ? `${data.users[i].username} is linked to ${player}`
            : `${data.users[i].username} has no linked player`,
        );
        return 0;
      }
      case 'totp': {
        const sub = rest[0];
        if (!name || (sub !== 'enroll' && sub !== 'disable')) throw new CliError('usage: totp <user> enroll|disable');
        const i = find(data.users, name);
        const u = data.users[i]!;
        if (sub === 'disable') {
          data.users[i] = { ...u, totp: null, updatedAt: now };
          save();
          io.out(`TOTP disabled for ${u.username}`);
          return 0;
        }
        const codeArg = flag(rest, '--code');
        const secret = newTotpSecret();
        io.out(`otpauth URL (shown once): ${otpauthUrl(secret, u.username)}`);
        io.out(`secret (manual entry):    ${secret}`);
        const code = codeArg ?? (io.code ? await io.code() : await promptLine('Enter the 6-digit code from the app: '));
        if (verifyTotp(secret, code.trim(), io.now?.() ?? Date.now()) === null)
          throw new CliError('code did not match; TOTP not enabled');
        data.users[i] = { ...u, totp: { secret, enrolledAt: now }, updatedAt: now };
        save();
        io.out(`TOTP enabled for ${u.username}`);
        return 0;
      }
      case 'disable':
      case 'enable': {
        if (!name) throw new CliError(`usage: ${cmd} <user>`);
        const i = find(data.users, name);
        data.users[i] = { ...data.users[i]!, disabled: cmd === 'disable', updatedAt: now };
        save();
        if (cmd === 'disable') revokeSessions(db, data.users[i].username, io);
        io.out(`${data.users[i].username} ${cmd}d`);
        return 0;
      }
      case 'remove': {
        if (!name) throw new CliError('usage: remove <user>');
        const i = find(data.users, name);
        const [u] = data.users.splice(i, 1);
        save();
        revokeSessions(db, u!.username, io);
        io.out(`removed ${u!.username}`);
        return 0;
      }
      default:
        io.err('usage: users <add|passwd|roles|player|totp|disable|enable|remove|list> ... (see src/cli/users.ts)');
        return 2;
    }
  } catch (e) {
    io.err(e instanceof CliError ? e.message : `error: ${(e as Error).message}`);
    return 1;
  }
}

const isMain = process.argv[1] !== undefined && /[\\/]cli[\\/]users\.(js|ts)$/.test(process.argv[1]);
if (isMain) {
  runUsersCli(process.argv.slice(2), {
    out: (l) => process.stdout.write(`${l}\n`),
    err: (l) => process.stderr.write(`${l}\n`),
  }).then(
    (code) => process.exit(code),
    () => process.exit(1),
  );
}
