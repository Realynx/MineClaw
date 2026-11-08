/** A bounded, read-only JAR/ZIP reader. Only allowlisted game resources are decompressed; classes are never executed. */
export const MAX_ARCHIVE = 256 * 1024 * 1024;
const MAX_ENTRY = 16 * 1024 * 1024;
const MAX_EXPANDED = 384 * 1024 * 1024;
const decoder = new TextDecoder();

export function resourcePath(path: string): boolean {
  if (path.includes('\\') || path.split('/').some((s) => s === '..' || s === '.') || path.startsWith('/')) return false;
  return /^(assets\/minecraft\/(textures\/.*\.(png|png\.mcmeta)|(?:models|blockstates)\/.*\.json|sounds\/.*\.ogg|sounds\.json)|(?:terrain|char)\.png|(?:gui|mob|armor|item|art|environment|terrain|misc|font)\/.*\.png|font\.txt)$/.test(
    path,
  );
}

export async function readArchive(file: Blob, progress?: (fraction: number) => void): Promise<Map<string, Blob>> {
  if (file.size > MAX_ARCHIVE) throw new Error('This archive is too large (maximum 256 MB).');
  const bytes = new Uint8Array(await file.arrayBuffer()),
    view = new DataView(bytes.buffer);
  let end = -1;
  for (let i = bytes.length - 22; i >= Math.max(0, bytes.length - 65557); i--) {
    if (view.getUint32(i, true) === 0x06054b50 && i + 22 + view.getUint16(i + 20, true) === bytes.length) {
      end = i;
      break;
    }
  }
  if (end < 0) throw new Error('Not a valid JAR or ZIP archive.');
  const count = view.getUint16(end + 10, true),
    centralSize = view.getUint32(end + 12, true);
  let cursor = view.getUint32(end + 16, true),
    total = 0;
  if (count > 30000 || view.getUint16(end + 4, true) !== 0 || cursor + centralSize > end)
    throw new Error('Unsupported archive layout.');
  const output = new Map<string, Blob>();
  for (let n = 0; n < count; n++) {
    if (cursor + 46 > end || view.getUint32(cursor, true) !== 0x02014b50) throw new Error('Damaged archive directory.');
    const flags = view.getUint16(cursor + 8, true),
      method = view.getUint16(cursor + 10, true);
    const compressed = view.getUint32(cursor + 20, true),
      expanded = view.getUint32(cursor + 24, true);
    const nameLength = view.getUint16(cursor + 28, true),
      extra = view.getUint16(cursor + 30, true),
      comment = view.getUint16(cursor + 32, true);
    const offset = view.getUint32(cursor + 42, true);
    if (cursor + 46 + nameLength + extra + comment > end) throw new Error('Damaged archive directory.');
    const path = decoder.decode(bytes.subarray(cursor + 46, cursor + 46 + nameLength));
    cursor += 46 + nameLength + extra + comment;
    if (!resourcePath(path)) continue;
    total += expanded;
    if (flags & 1 || ![0, 8].includes(method) || expanded > MAX_ENTRY || total > MAX_EXPANDED)
      throw new Error('Unsupported or oversized game resource.');
    if (offset + 30 > bytes.length || view.getUint32(offset, true) !== 0x04034b50)
      throw new Error('Damaged archive entry.');
    const start = offset + 30 + view.getUint16(offset + 26, true) + view.getUint16(offset + 28, true);
    if (start + compressed > bytes.length) throw new Error('Truncated game resource.');
    let data = bytes.subarray(start, start + compressed);
    if (method === 8) {
      // Read the stream incrementally: an untrusted advertised size must not permit a ZIP bomb allocation.
      const reader = new Blob([data]).stream().pipeThrough(new DecompressionStream('deflate-raw')).getReader();
      const parts: Uint8Array<ArrayBuffer>[] = [];
      let length = 0;
      try {
        for (;;) {
          const next = await reader.read();
          if (next.done) break;
          length += next.value.length;
          if (length > expanded || length > MAX_ENTRY) throw new Error('Game resource exceeds its declared size.');
          parts.push(new Uint8Array(next.value));
        }
      } finally {
        await reader.cancel();
      }
      if (length !== expanded) throw new Error('Truncated game resource.');
      data = new Uint8Array(await new Blob(parts).arrayBuffer());
    }
    if (data.length !== expanded) throw new Error('Invalid game resource size.');
    output.set(
      path,
      new Blob([data], {
        type: path.endsWith('.png') ? 'image/png' : path.endsWith('.ogg') ? 'audio/ogg' : 'application/json',
      }),
    );
    if (n % 30 === 0) {
      progress?.(n / count);
      await new Promise((r) => setTimeout(r, 0));
    }
  }
  progress?.(1);
  return output;
}
