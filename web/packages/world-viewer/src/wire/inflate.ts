import { inflateRaw as pakoInflateRaw } from 'pako';

// Raw DEFLATE (no zlib header): what .NET's DeflateStream writes for ChunkSnapshot.Encode / LightSnapshot.Encode.
// DecompressionStream('deflate-raw') where the platform has it (every current browser, workers, Node 22), pako otherwise.

let nativeOk: boolean | null = null;

function hasNative(): boolean {
  if (nativeOk !== null) return nativeOk;
  try {
    // Throws (TypeError) where the format is unknown, e.g. older Safari.
    new DecompressionStream('deflate-raw' as CompressionFormat);
    nativeOk = typeof Response !== 'undefined' && typeof Blob !== 'undefined';
  } catch {
    nativeOk = false;
  }
  return nativeOk;
}

/** Inflates with the platform's DecompressionStream. Rejects on corrupt input. */
export async function inflateRawNative(data: Uint8Array): Promise<Uint8Array> {
  const stream = new Blob([data as Uint8Array<ArrayBuffer>]).stream().pipeThrough(new DecompressionStream('deflate-raw' as CompressionFormat));
  return new Uint8Array(await new Response(stream).arrayBuffer());
}

/** Inflates with pako (synchronous). Throws on corrupt input. */
export function inflateRawPako(data: Uint8Array): Uint8Array {
  const out = pakoInflateRaw(data);
  if (!(out instanceof Uint8Array)) throw new Error('inflate failed');
  return out;
}

/** Raw-inflates one blob: native stream when available, else (or if the native path is missing) pako. */
export async function inflateRaw(data: Uint8Array, opts?: { forcePako?: boolean }): Promise<Uint8Array> {
  if (!opts?.forcePako && hasNative()) return inflateRawNative(data);
  return inflateRawPako(data);
}

/** For tests: forget the feature probe. */
export function resetInflateProbe(): void {
  nativeOk = null;
}
