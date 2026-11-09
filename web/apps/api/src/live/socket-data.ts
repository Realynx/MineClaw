import type { RawData } from 'ws';

export function socketText(data: RawData): string {
  return (Array.isArray(data) ? Buffer.concat(data) : Buffer.isBuffer(data) ? data : Buffer.from(data)).toString('utf8');
}
