import { EventEmitter } from 'node:events';
import WebSocket from 'ws';
import { SseParser } from './sse-parser.js';

/** The hub keeps its replay/filter/backpressure rules for both transports. */
export interface LiveSink {
  readonly writableEnded: boolean;
  write(text: string): boolean;
  end(): void;
  destroy(): void;
  on(event: string, callback: () => void): unknown;
  once(event: string, callback: () => void): unknown;
}

export class SocketSink extends EventEmitter implements LiveSink {
  private readonly parser: SseParser;
  constructor(private readonly socket: WebSocket) {
    super();
    this.parser = new SseParser((frame) => {
      socket.send(JSON.stringify({ event: frame.event, id: frame.id, data: JSON.parse(frame.data) as unknown }), (error) => {
        if (error) socket.terminate();
        else if (socket.bufferedAmount < 64 * 1024) this.emit('drain');
      });
    });
    socket.on('close', () => this.emit('close'));
    socket.on('error', () => this.emit('close'));
    // The live socket only subscribes; it cannot invoke writes or change its authenticated identity.
    socket.on('message', () => socket.close(1008, 'Read-only live stream'));
  }
  get writableEnded(): boolean { return this.socket.readyState !== WebSocket.OPEN; }
  write(text: string): boolean {
    if (this.writableEnded) return true;
    this.parser.push(text);
    return this.socket.bufferedAmount < 64 * 1024;
  }
  end(): void { this.socket.close(1000); }
  destroy(): void { this.socket.terminate(); }
}
