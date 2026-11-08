import {
  closeSync,
  copyFileSync,
  existsSync,
  fstatSync,
  fsyncSync,
  mkdirSync,
  openSync,
  readSync,
  renameSync,
  unlinkSync,
  writeFileSync,
  chmodSync,
  constants,
} from 'node:fs';
import { createPrivateKey, randomUUID, X509Certificate } from 'node:crypto';
import { isAbsolute, join } from 'node:path';
import { isIP } from 'node:net';
import { createSecureContext, type SecureContextOptions } from 'node:tls';
import { WebTlsSettings, type WebHostingResponse } from '@mineclaw/contracts';
import type { ApiConfig } from './config.js';
import { Errors } from '../common/errors.js';

export const hostingPath = (dataDir: string) => join(dataDir, 'hosting.override.json');
export const tlsOrigin = (tls: WebTlsSettings) =>
  tls.enabled ? `https://${tls.hostname.toLowerCase()}${tls.port === 443 ? '' : ':' + tls.port}` : null;

export function readHostingOverride(dataDir: string): WebTlsSettings | null {
  const path = hostingPath(dataDir);
  if (!existsSync(path)) return null;
  const data = JSON.parse(boundedFile(path, 64 * 1024).toString('utf8')) as { version?: unknown; tls?: unknown };
  if (data.version !== 1) throw new Error('Unsupported web hosting settings version');
  return WebTlsSettings.parse(data.tls);
}

function boundedFile(path: string, max: number): Buffer {
  // Do not let an administrator's accidental FIFO/device path block the gateway before fstat can reject it.
  const fd = openSync(path, constants.O_RDONLY | constants.O_NONBLOCK);
  try {
    const stat = fstatSync(fd);
    if (!stat.isFile() || stat.size > max) throw new Error('not a regular file or too large');
    const bytes = Buffer.alloc(stat.size + 1);
    let n = 0,
      read = 0;
    while ((read = readSync(fd, bytes, n, bytes.length - n, null)) > 0) {
      n += read;
      if (n > stat.size) throw new Error('file changed while reading');
    }
    return bytes.subarray(0, n);
  } finally {
    closeSync(fd);
  }
}

/** Reads keys only inside the gateway. Returned buffers are never part of an API response or audit record. */
export function tlsMaterial(tls: WebTlsSettings, httpPort: number, now = Date.now()): SecureContextOptions | undefined {
  if (!tls.enabled) return undefined;
  const fail = (path: string, message: string): never => {
    throw Errors.badRequest([{ path, message }], 'Invalid HTTPS configuration');
  };
  if (!tls.hostname || tls.hostname.split('.').some((part) => !/^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/i.test(part)))
    fail('hostname', 'Enter the intended hostname or IPv4 address without a scheme, port or path');
  if (tls.port === httpPort) fail('port', 'HTTPS needs a different port from the existing HTTP gateway');
  if (!isAbsolute(tls.certFile)) fail('certFile', 'Use an absolute server path to a PEM certificate chain');
  if (!isAbsolute(tls.keyFile)) fail('keyFile', 'Use an absolute server path to its unencrypted PEM private key');
  let cert: Buffer, key: Buffer;
  try {
    cert = boundedFile(tls.certFile, 2 * 1024 * 1024);
  } catch {
    return fail('certFile', 'Cannot read a regular certificate file (maximum 2 MiB) as the web service user');
  }
  try {
    key = boundedFile(tls.keyFile, 512 * 1024);
  } catch {
    return fail('keyFile', 'Cannot read a regular private key file (maximum 512 KiB) as the web service user');
  }
  let leaf: X509Certificate;
  try {
    leaf = new X509Certificate(cert);
  } catch {
    return fail('certFile', 'The file does not contain a valid PEM certificate');
  }
  if (!(Date.parse(leaf.validFrom) <= now && Date.parse(leaf.validTo) > now))
    fail('certFile', 'The certificate is expired or not valid yet');
  const match = isIP(tls.hostname) ? leaf.checkIP(tls.hostname) : leaf.checkHost(tls.hostname, { subject: 'never' });
  if (!match) fail('hostname', 'The certificate subject alternative names do not cover this hostname');
  try {
    if (!leaf.checkPrivateKey(createPrivateKey(key))) fail('keyFile', 'The private key does not match the certificate');
    createSecureContext({ key, cert, minVersion: 'TLSv1.2' });
  } catch {
    return fail('keyFile', 'Use the matching, unencrypted PEM private key and a valid certificate chain');
  }
  return { key, cert, minVersion: 'TLSv1.2' };
}

/** A deliberately small independent override; never writes bot settings or certificate/key files. */
export class WebHostingSettings {
  constructor(private readonly config: ApiConfig) {}

  read(): WebHostingResponse {
    const running = this.config.web.tls;
    const saved = readHostingOverride(this.config.web.dataDir) ?? running;
    return {
      running,
      saved,
      pendingRestart: JSON.stringify(saved) !== JSON.stringify(running),
      editable: !this.config.web.tlsEnvOverride,
      overridePath: hostingPath(this.config.web.dataDir),
      httpsOrigin: tlsOrigin(running),
      savedOrigin: tlsOrigin(saved),
    };
  }

  save(tls: WebTlsSettings): WebHostingResponse {
    if (this.config.web.tlsEnvOverride)
      throw Errors.conflict('HTTPS is configured by environment variables; update those before using this editor');
    tlsMaterial(tls, this.config.web.port);
    const dir = this.config.web.dataDir,
      path = hostingPath(dir),
      temp = path + '.' + randomUUID() + '.tmp';
    mkdirSync(dir, { recursive: true, mode: 0o700 });
    try {
      if (existsSync(path)) {
        copyFileSync(path, path + '.bak');
        chmodSync(path + '.bak', 0o600);
      }
      const fd = openSync(temp, 'wx', 0o600);
      try {
        writeFileSync(fd, JSON.stringify({ version: 1, tls }, null, 2) + '\n');
        fsyncSync(fd);
      } finally {
        closeSync(fd);
      }
      renameSync(temp, path);
    } catch {
      if (existsSync(temp)) unlinkSync(temp);
      throw Errors.internal();
    }
    return this.read();
  }
}
