/** Incremental `text/event-stream` parser (WHATWG rules: LF, CRLF or CR line ends; multi-line data; comments; id; retry). */
export interface SseEvent {
  event: string;
  data: string;
  id?: string;
}

export class SseParser {
  private buf = '';
  private event = '';
  private data: string[] = [];
  private id: string | undefined;

  constructor(
    private readonly onEvent: (e: SseEvent) => void,
    private readonly onComment?: (text: string) => void,
  ) {}

  push(chunk: string): void {
    this.buf += chunk;
    let start = 0;
    for (let i = 0; i < this.buf.length; i++) {
      const ch = this.buf[i];
      if (ch !== '\n' && ch !== '\r') continue;
      // A CR at the very end may be the first half of a CRLF split across chunks: wait for more.
      if (ch === '\r' && i === this.buf.length - 1) break;
      this.line(this.buf.slice(start, i));
      if (ch === '\r' && this.buf[i + 1] === '\n') i++;
      start = i + 1;
    }
    this.buf = this.buf.slice(start);
  }

  private line(line: string): void {
    if (line === '') {
      if (this.data.length > 0)
        this.onEvent({ event: this.event || 'message', data: this.data.join('\n'), id: this.id });
      this.event = '';
      this.data = [];
      return;
    }
    if (line.startsWith(':')) {
      this.onComment?.(line.slice(1).trim());
      return;
    }
    const colon = line.indexOf(':');
    const field = colon < 0 ? line : line.slice(0, colon);
    let value = colon < 0 ? '' : line.slice(colon + 1);
    if (value.startsWith(' ')) value = value.slice(1);
    if (field === 'event') this.event = value;
    else if (field === 'data') this.data.push(value);
    else if (field === 'id' && !value.includes('\0')) this.id = value;
  }
}
