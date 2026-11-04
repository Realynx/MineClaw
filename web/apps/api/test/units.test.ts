import { mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { verify } from '@node-rs/argon2';
import { describe, expect, it } from 'vitest';
import { runUsersCli } from '../src/cli/users.js';
import { base32Decode, base32Encode, hotp, totpCode, verifyTotp } from '../src/auth/totp.js';
import { loadConfig, envOverrides, normalizeKeys } from '../src/config/config.js';
import { readUsersFile } from '../src/auth/users-store.js';
import { WebDb } from '../src/db/web-db.js';
import { createApp } from '../src/app.js';
import { createLogger } from '../src/common/logger.js';

describe('TOTP (RFC 6238 / 4226 vectors)', () => {
  const key = Buffer.from('12345678901234567890');
  it('HOTP vectors', () => {
    expect([0, 1, 2, 9].map((c) => hotp(key, c))).toEqual(['755224', '287082', '359152', '520489']);
  });
  it('TOTP SHA-1 vectors (8 digits)', () => {
    expect(hotp(key, Math.floor(59 / 30), 8)).toBe('94287082');
    expect(hotp(key, Math.floor(1111111109 / 30), 8)).toBe('07081804');
    expect(hotp(key, Math.floor(20000000000 / 30), 8)).toBe('65353130');
  });
  it('base32 round trip and the ±1 step window', () => {
    const secret = base32Encode(key);
    expect(secret).toBe('GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ');
    expect(base32Decode(secret)).toEqual(key);
    const t0 = 1_762_077_600_000;
    const step = Math.floor(t0 / 30_000);
    expect(verifyTotp(secret, totpCode(secret, t0 - 30_000), t0)).toBe(step - 1);
    expect(verifyTotp(secret, totpCode(secret, t0 + 30_000), t0)).toBe(step + 1);
    expect(verifyTotp(secret, totpCode(secret, t0 - 60_000), t0)).toBeNull();
    expect(verifyTotp(secret, '12345', t0)).toBeNull();
  });
});

describe('users CLI', () => {
  const dir = mkdtempSync(join(tmpdir(), 'mcx-cli-'));
  const file = join(dir, 'users.json');
  const run = async (args: string[], extra: { password?: string; code?: string; now?: number } = {}) => {
    const out: string[] = [];
    const err: string[] = [];
    const code = await runUsersCli([...args, '--file', file], {
      out: (l) => out.push(l),
      err: (l) => err.push(l),
      password: extra.password !== undefined ? () => Promise.resolve(extra.password!) : undefined,
      code: extra.code !== undefined ? () => Promise.resolve(extra.code!) : undefined,
      now: extra.now !== undefined ? () => extra.now! : undefined,
    });
    return { code, out, err };
  };

  it('adds, lists, changes roles/player/password, enrolls TOTP and removes users', async () => {
    expect(
      (
        await run(['add', 'Fox', '--roles', 'viewer,admin,chat', '--player', 'poofimafox'], {
          password: 'correct horse battery staple',
        })
      ).code,
    ).toBe(0);
    let u = readUsersFile(file).users[0]!;
    expect(u).toMatchObject({ username: 'fox', roles: ['admin', 'chat', 'viewer'], player: 'poofimafox', totp: null });
    expect(u.passwordHash.startsWith('$argon2id$v=19$m=19456,t=2,p=1$')).toBe(true);
    expect(await verify(u.passwordHash, 'correct horse battery staple')).toBe(true);
    expect(readFileSync(file, 'utf8')).not.toContain('correct horse');

    expect((await run(['add', 'fox', '--roles', 'viewer'], { password: 'another long password' })).code).toBe(1);
    expect((await run(['add', 'bad user', '--roles', 'viewer'], { password: 'another long password' })).code).toBe(1);
    expect((await run(['add', 'short', '--roles', 'viewer'], { password: 'tiny' })).err[0]).toMatch(/at least 12/);
    expect((await run(['add', 'x', '--roles', 'root'], { password: 'another long password' })).err[0]).toMatch(
      /unknown role/,
    );

    expect((await run(['roles', 'fox', 'viewer,commands,chat'])).code).toBe(0);
    expect((await run(['player', 'fox', '-'])).code).toBe(0);
    u = readUsersFile(file).users[0]!;
    expect(u.roles).toEqual(['chat', 'commands', 'viewer']);
    expect(u.player).toBeNull();

    const before = u.passwordHash;
    expect((await run(['passwd', 'fox'], { password: 'brand new password 42' })).code).toBe(0);
    expect(readUsersFile(file).users[0]!.passwordHash).not.toBe(before);

    // TOTP: the printed secret has to produce a valid code before it is saved
    const now = 1_762_077_600_000;
    const wrong = await run(['totp', 'fox', 'enroll'], { code: '000000', now });
    expect(wrong.code).toBe(1);
    expect(readUsersFile(file).users[0]!.totp).toBeNull();
    let printed = '';
    const out: string[] = [];
    const code = await runUsersCli(['totp', 'fox', 'enroll', '--file', file], {
      out: (l) => {
        out.push(l);
        const m = /secret \(manual entry\):\s+(\S+)/.exec(l);
        if (m) printed = m[1]!;
      },
      err: () => {},
      code: () => Promise.resolve(totpCode(printed, now)),
      now: () => now,
    });
    expect(code).toBe(0);
    expect(out[0]).toMatch(/^otpauth URL \(shown once\): otpauth:\/\/totp\/MineClaw:fox\?secret=/);
    expect(readUsersFile(file).users[0]!.totp?.secret).toBe(printed);

    const list = await run(['list']);
    expect(list.out[0]).toMatch(/^fox\s+roles=chat,commands,viewer\s+player=-\s+totp=on/);
    expect(list.out.join('\n')).not.toMatch(/argon2|secret/);

    expect((await run(['totp', 'fox', 'disable'])).code).toBe(0);
    expect((await run(['remove', 'fox'])).code).toBe(0);
    expect(readUsersFile(file).users).toEqual([]);
    expect((await run(['remove', 'fox'])).code).toBe(1);
    expect((await run(['frobnicate'])).code).toBe(2);
  });

  it('prints the same lines, usages and exit codes for every command', async () => {
    const f2 = join(dir, 'users-lines.json');
    const lines = async (args: string[], password = 'correct horse battery staple') => {
      const all: string[] = [];
      const code = await runUsersCli([...args, '--file', f2], {
        out: (l) => all.push(`out ${l}`),
        err: (l) => all.push(`err ${l}`),
        password: () => Promise.resolve(password),
        code: () => Promise.resolve('000000'),
        now: () => 1_762_077_600_000,
      });
      return [code, ...all.map((l) => l.replace(f2, '<file>'))];
    };
    expect(await lines(['list'])).toEqual([0, 'out (no users in <file>)']);
    expect(await lines(['add'])).toEqual([1, 'err usage: add <user> --roles viewer,chat [--player <name>]']);
    expect(await lines(['add', 'bun', '--roles', 'viewer', '--player', 'bad name!'])).toEqual([
      1,
      'err player names are 1-16 chars of A-Z a-z 0-9 _',
    ]);
    expect(await lines(['add', 'bun', '--roles', ''])).toEqual([1, 'err roles are required, e.g. --roles viewer,chat']);
    expect(await lines(['add', 'bun', '--roles', ','])).toEqual([1, 'err at least one role is required']);
    expect(await lines(['add', 'bun', '--roles', 'viewer'], 'x'.repeat(257))).toEqual([
      1,
      'err password must be at most 256 characters',
    ]);
    expect(await lines(['add', 'bun', '--roles', 'viewer,story', '--player', 'Robin'])).toEqual([
      0,
      'out added bun (story,viewer, player Robin)',
    ]);
    expect(await lines(['add', 'kit', '--roles', 'viewer'])).toEqual([0, 'out added kit (viewer)']);
    expect(await lines(['add', 'bun', '--roles', 'viewer'])).toEqual([1, 'err user "bun" exists (use passwd/roles)']);
    expect(await lines(['passwd'])).toEqual([1, 'err usage: passwd <user>']);
    expect(await lines(['passwd', 'nobody'])).toEqual([1, 'err no such user "nobody"']);
    expect(await lines(['passwd', 'bun'])).toEqual([0, 'out password changed for bun']);
    expect(await lines(['roles', 'bun'])).toEqual([1, 'err usage: roles <user> viewer,chat,commands,admin,story']);
    expect(await lines(['roles', 'bun', 'chat,viewer'])).toEqual([0, 'out roles of bun: chat,viewer']);
    expect(await lines(['player', 'bun'])).toEqual([1, 'err usage: player <user> <mc name | ->']);
    expect(await lines(['player', 'bun', 'no way'])).toEqual([1, 'err player names are 1-16 chars of A-Z a-z 0-9 _']);
    expect(await lines(['player', 'bun', 'Bun2'])).toEqual([0, 'out bun is linked to Bun2']);
    expect(await lines(['player', 'bun', '-'])).toEqual([0, 'out bun has no linked player']);
    expect(await lines(['totp', 'bun'])).toEqual([1, 'err usage: totp <user> enroll|disable']);
    expect(await lines(['totp', 'bun', 'disable'])).toEqual([0, 'out TOTP disabled for bun']);
    const enroll = await lines(['totp', 'bun', 'enroll', '--code', '000000']);
    expect(enroll[0]).toBe(1);
    expect(enroll.slice(1).map((l) => String(l).split(':')[0])).toEqual([
      'out otpauth URL (shown once)',
      'out secret (manual entry)',
      'err code did not match; TOTP not enabled',
    ]);
    expect(await lines(['disable'])).toEqual([1, 'err usage: disable <user>']);
    expect(await lines(['disable', 'bun'])).toEqual([0, 'out bun disabled']);
    expect(await lines(['list'])).toEqual([
      0,
      'out bun              roles=chat,viewer                  player=-                totp=off DISABLED',
      'out kit              roles=viewer                       player=-                totp=off',
    ]);
    expect(await lines(['enable', 'bun'])).toEqual([0, 'out bun enabled']);
    expect(await lines(['remove'])).toEqual([1, 'err usage: remove <user>']);
    expect(await lines(['remove', 'bun'])).toEqual([0, 'out removed bun']);
    expect(await lines([])).toEqual([
      2,
      'err usage: users <add|passwd|roles|player|totp|disable|enable|remove|list> ... (see src/cli/users.ts)',
    ]);
  });

  it('passwd/remove end the user sessions in web.db', async () => {
    const dbPath = join(dir, 'web.db');
    const cfg = join(dir, 'config.json');
    writeFileSync(cfg, JSON.stringify({ Web: { DbPath: dbPath, UsersFile: file, AllowedOrigins: ['http://x'] } }));
    const db = new WebDb(dbPath);
    db.db
      .prepare(
        "insert into sessions (id_hash, username, csrf, created_at, last_seen_at, idle_ms, absolute_expires_at) values ('h1', 'bun', 'c', 0, 0, 1, 1)",
      )
      .run();
    db.close();
    const io = { out: () => {}, err: () => {}, password: () => Promise.resolve('correct horse battery staple') };
    await runUsersCli(['add', 'bun', '--roles', 'viewer', '--config', cfg], io);
    await runUsersCli(['passwd', 'bun', '--config', cfg], io);
    const db2 = new WebDb(dbPath);
    expect((db2.db.prepare('select count(*) as n from sessions').get() as { n: number }).n).toBe(0);
    db2.close();
  });
});

describe('config', () => {
  it('reads PascalCase files, MINECLAW_WEB_ env overrides and the bridge key from env or a file', () => {
    const dir = mkdtempSync(join(tmpdir(), 'mcx-cfg-'));
    const file = join(dir, 'config.json');
    const keyFile = join(dir, 'bridge.key');
    writeFileSync(keyFile, `${'k'.repeat(40)}\n`);
    writeFileSync(
      file,
      JSON.stringify({
        Web: {
          AllowedOrigins: ['http://10.1.2.73:8080'],
          TrustProxy: false,
          CloudflareAccess: { TeamDomain: 'example-team', Aud: 'abc' },
        },
        Bridge: { KeyFile: keyFile },
      }),
    );
    const { config } = loadConfig({
      argv: ['--config', file],
      env: {
        MINECLAW_WEB_Web__TrustProxy: 'true',
        MINECLAW_WEB_Web__TrustedProxies: '127.0.0.1, ::1',
        MINECLAW_WEB_Web__ChatRate__Burst: '9',
      },
    });
    expect(config.web.port).toBe(8080);
    expect(config.web.bind).toBe('0.0.0.0');
    expect(config.web.trustProxy).toBe(true);
    expect(config.web.trustedProxies).toEqual(['127.0.0.1', '::1']);
    expect(config.web.cloudflareAccess).toEqual({ teamDomain: 'example-team', aud: 'abc' });
    expect(config.web.chatRate.burst).toBe(9);
    expect(config.web.dataDir).toBe('/var/lib/mineclaw-web');
    expect(config.web.usersFile).toBe('/etc/mineclaw-web/users.json');
    expect(config.bridge.key).toBe('k'.repeat(40));
    expect(config.bridge.url).toBe('http://127.0.0.1:8766');
    const env = loadConfig({ argv: ['--config', file], env: { MINECLAW_BRIDGE_KEY: 'e'.repeat(32) } });
    expect(env.config.bridge.key).toBe('e'.repeat(32));
  });

  it('refuses a missing or short key without printing it, and mock mode in production', () => {
    expect(() =>
      loadConfig({
        argv: [],
        env: {},
        overrides: { web: { allowedOrigins: ['http://x'] }, bridge: { key: 'short-secret' } },
      }),
    ).toThrow(/bridge.key: missing or shorter/);
    try {
      loadConfig({
        argv: [],
        env: {},
        overrides: { web: { allowedOrigins: ['http://x'] }, bridge: { key: 'short-secret' } },
      });
    } catch (e) {
      expect((e as Error).message).not.toContain('short-secret');
    }
    expect(() =>
      loadConfig({
        argv: ['--mock-bridge'],
        env: { NODE_ENV: 'production' },
        overrides: { web: { allowedOrigins: ['http://x'] } },
      }),
    ).toThrow(/refused/);
    const dev = loadConfig({ argv: ['--dev', '--mock-bridge'], env: {}, cwd: tmpdir() });
    expect(dev.config.bridge.mock).toBe(true);
    expect(dev.config.web.port).toBe(4000);
    expect(dev.config.web.proxyNext).toBe(false);
    expect(dev.config.bridge.key.length).toBeGreaterThanOrEqual(32);
  });

  it('the deploy example config loads as-is', () => {
    const example = join(import.meta.dirname, '..', '..', '..', '..', 'deploy', 'viewer', 'web', 'config.example.json');
    const { config } = loadConfig({ argv: ['--config', example], env: { MINECLAW_BRIDGE_KEY: 'k'.repeat(64) } });
    expect(config.web.allowedOrigins).toContain('http://10.1.2.73:8080');
    expect(config.web.csp).toEqual({ scripts: 'nonce', reportOnly: false });
    expect(config.web.login.userThreshold).toBe(5);
    expect(config.web.cloudflareAccess).toBeNull();
  });

  it('env paths and key normalization', () => {
    expect(envOverrides({ MINECLAW_WEB_Bridge__TimeoutMs: '5000', OTHER: 'x' })).toEqual({
      bridge: { timeoutMs: 5000 },
    });
    expect(normalizeKeys({ Web: { CloudflareAccess: { TeamDomain: 'a' } } })).toEqual({
      web: { cloudflareAccess: { teamDomain: 'a' } },
    });
  });

  it('createApp refuses mock mode under NODE_ENV=production even if the config says so', async () => {
    const prev = process.env.NODE_ENV;
    process.env.NODE_ENV = 'production';
    try {
      const dev = loadConfig({ argv: ['--dev', '--mock-bridge'], env: {}, cwd: tmpdir() });
      await expect(createApp(dev.config, { logger: createLogger('silent') })).rejects.toThrow(/refused/);
    } finally {
      process.env.NODE_ENV = prev;
    }
  });
});
