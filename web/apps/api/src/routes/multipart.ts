/**
 * Shard uploads (`shards.importPreview`, `admin.shardImport`): `multipart/form-data` with a `shard` file part (≤ 48 MB,
 * `SHARD_UPLOAD_MAX_BYTES`) and an optional `options` part holding the JSON of `ShardImportOptions`.
 *
 * Nest parses the upload (busboy, hard limits), validates `options` with the contracts schema, and re-encodes the same
 * two parts for the bridge with the options in snake_case (api.md §1: request bodies are converted with `toSnake`). The
 * HMAC body hash covers the re-encoded multipart bytes exactly as sent. The file bytes pass through unchanged.
 */
import { randomBytes } from 'node:crypto';
import type { IncomingMessage } from 'node:http';
import busboy from 'busboy';
import { SHARD_UPLOAD_MAX_BYTES } from '@mineclaw/contracts';
import { Errors } from '../common/errors.js';

export interface ShardUpload {
  options: unknown;
  file: Buffer;
  filename: string;
}

/** Slack for the multipart framing and the options part on top of the file cap. */
const FRAMING_SLACK = 256 * 1024;

export function parseShardUpload(req: IncomingMessage): Promise<ShardUpload> {
  const type = req.headers['content-type'] ?? '';
  if (!/^multipart\/form-data\b/i.test(type)) {
    req.resume();
    return Promise.reject(Errors.badRequest(undefined, 'expected multipart/form-data with a "shard" file part'));
  }
  const declared = Number(req.headers['content-length'] ?? NaN);
  if (Number.isFinite(declared) && declared > SHARD_UPLOAD_MAX_BYTES + FRAMING_SLACK) {
    req.resume();
    return Promise.reject(Errors.tooLarge('shard upload exceeds 48 MB'));
  }
  return new Promise<ShardUpload>((resolve, reject) => {
    let bb: busboy.Busboy;
    try {
      bb = busboy({
        headers: req.headers,
        limits: { files: 1, fields: 4, fileSize: SHARD_UPLOAD_MAX_BYTES, fieldSize: 64 * 1024, parts: 6 },
      });
    } catch {
      reject(Errors.badRequest(undefined, 'malformed multipart body'));
      return;
    }
    let options: unknown = {};
    let optionsError = false;
    let file: Buffer | undefined;
    let filename = 'upload.mcshard';
    let failed = false;
    const fail = (e: Error) => {
      if (failed) return;
      failed = true;
      req.unpipe(bb);
      req.resume();
      reject(e);
    };
    bb.on('field', (name, value) => {
      if (name !== 'options') return;
      try {
        options = value.trim() === '' ? {} : (JSON.parse(value) as unknown);
      } catch {
        optionsError = true;
      }
    });
    bb.on('file', (name, stream, info) => {
      if (name !== 'shard') {
        stream.resume();
        return;
      }
      filename = sanitizeFilename(info.filename) || filename;
      const chunks: Buffer[] = [];
      stream.on('data', (c: Buffer) => chunks.push(c));
      stream.on('limit', () => fail(Errors.tooLarge('shard upload exceeds 48 MB')));
      stream.on('end', () => {
        file = Buffer.concat(chunks);
      });
    });
    bb.on('partsLimit', () => fail(Errors.badRequest(undefined, 'too many multipart parts')));
    bb.on('filesLimit', () => fail(Errors.badRequest(undefined, 'only one file part is allowed')));
    bb.on('error', () => fail(Errors.badRequest(undefined, 'malformed multipart body')));
    bb.on('close', () => {
      if (failed) return;
      if (optionsError) return fail(Errors.badRequest([{ path: 'options', message: 'options must be JSON' }]));
      if (!file || file.length === 0)
        return fail(Errors.badRequest([{ path: 'shard', message: 'a "shard" file part is required' }]));
      resolve({ options, file, filename });
    });
    req.pipe(bb);
  });
}

export function sanitizeFilename(name: string | undefined): string {
  const base = (name ?? '').split(/[\\/]/).pop() ?? '';
  return base.replace(/[^\w.\- ]+/g, '_').slice(0, 120);
}

/** Re-encodes the upload for the bridge. Returns the exact bytes (hashed and sent) and the content type. */
export function encodeShardUpload(
  optionsSnake: unknown,
  file: Buffer,
  filename: string,
): { body: Buffer; contentType: string } {
  const boundary = `----mcx${randomBytes(12).toString('hex')}`;
  const head =
    `--${boundary}\r\nContent-Disposition: form-data; name="options"\r\nContent-Type: application/json\r\n\r\n` +
    `${JSON.stringify(optionsSnake)}\r\n` +
    `--${boundary}\r\nContent-Disposition: form-data; name="shard"; filename="${sanitizeFilename(filename) || 'upload.mcshard'}"\r\n` +
    `Content-Type: application/octet-stream\r\n\r\n`;
  const tail = `\r\n--${boundary}--\r\n`;
  return {
    body: Buffer.concat([Buffer.from(head, 'utf8'), file, Buffer.from(tail, 'utf8')]),
    contentType: `multipart/form-data; boundary=${boundary}`,
  };
}
