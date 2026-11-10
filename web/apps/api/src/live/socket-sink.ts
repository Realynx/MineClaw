import { EventEmitter } from 'node:events';
import WebSocket from 'ws';
import { SseParser, type SseEvent } from './sse-parser.js';

/** The hub keeps its replay/filter/backpressure rules for both transports. */
export interface LiveSink {
  readonly writableEnded: boolean;
  write(text: string): boolean;
  end(): void;
  destroy(): void;
  on(event: string, callback: () => void): unknown;
  once(event: string, callback: () => void): unknown;
}

/** The socket message for an SSE event: `{"event":..,"id":..,"data":<the event's JSON data>}`. */
function socketMessage(frame: SseEvent): string {
  return JSON.stringify({ event: frame.event, id: frame.id, data: JSON.parse(frame.data) as unknown });
}

/**
 * A whole frame exactly as `formatSseFrame` writes it (`event: E\nid: N\ndata: D\n\n`, D one line of JSON), or null for
 * anything else. Its data is `JSON.stringify` output, so it is spliced into the socket message as it is: parsing and
 * stringifying it again would give back the same text.
 */
function formattedFrame(text: string): string | null {
  if (!text.startsWith('event: ') || !text.endsWith('\n\n')) return null;
  const idLine = text.indexOf('\nid: ', 7);
  if (idLine < 0) return null;
  const dataLine = text.indexOf('\ndata: ', idLine + 5);
  if (dataLine < 0) return null;
  const event = text.slice(7, idLine);
  const id = text.slice(idLine + 5, dataLine);
  const start = dataLine + 7;
  const end = text.length - 2;
  if (event === '' || event.includes('\n') || /[\r\0]/.test(event + id) || !/^\d+$/.test(id)) return null;
  if (text.indexOf('\n', start) !== end || text.indexOf('\r', start) >= 0) return null;
  return `{"event":${JSON.stringify(event)},"id":${JSON.stringify(id)},"data":${text.slice(start, end)}}`;
}

/** The last frame turned into a socket message: the hub hands every client the same frame text, so it is built once. */
let lastFrame = '';
let lastMessage = '';

export class SocketSink extends EventEmitter implements LiveSink {
  private readonly parser: SseParser;
  constructor(private readonly socket: WebSocket) {
    super();
    this.parser = new SseParser((frame) => this.send(socketMessage(frame)));
    socket.on('close', () => this.emit('close'));
    socket.on('error', () => this.emit('close'));
    // The live socket only subscribes; it cannot invoke writes or change its authenticated identity.
    socket.on('message', () => socket.close(1008, 'Read-only live stream'));
  }
  get writableEnded(): boolean {
    return this.socket.readyState !== WebSocket.OPEN;
  }
  write(text: string): boolean {
    if (this.writableEnded) return true;
    const message = this.parser.idle ? this.messageOf(text) : null;
    if (message !== null) this.send(message);
    else this.parser.push(text);
    return this.socket.bufferedAmount < 64 * 1024;
  }
  end(): void {
    this.socket.close(1000);
  }
  destroy(): void {
    this.socket.terminate();
  }

  private messageOf(text: string): string | null {
    if (text === lastFrame) return lastMessage;
    const message = formattedFrame(text);
    if (message !== null) {
      lastFrame = text;
      lastMessage = message;
    }
    return message;
  }

  private send(message: string): void {
    this.socket.send(message, (error) => {
      if (error) this.socket.terminate();
      else if (this.socket.bufferedAmount < 64 * 1024) this.emit('drain');
    });
  }
}
