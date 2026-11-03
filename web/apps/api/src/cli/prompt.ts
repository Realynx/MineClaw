/** Password input for the CLI: hidden on a TTY (raw mode, no echo), else the first line of stdin. */
import { createInterface } from 'node:readline';

export async function readStdinLine(stdin: NodeJS.ReadStream = process.stdin): Promise<string> {
  const rl = createInterface({ input: stdin, terminal: false });
  try {
    for await (const line of rl) return line.replace(/\r$/, '');
    return '';
  } finally {
    rl.close();
  }
}

export function promptHidden(
  question: string,
  stdin: NodeJS.ReadStream = process.stdin,
  out: NodeJS.WriteStream = process.stderr,
): Promise<string> {
  if (!stdin.isTTY) return readStdinLine(stdin);
  return new Promise((resolve, reject) => {
    out.write(question);
    let value = '';
    stdin.setRawMode(true);
    stdin.resume();
    stdin.setEncoding('utf8');
    const done = (err?: Error) => {
      stdin.setRawMode(false);
      stdin.pause();
      stdin.removeListener('data', onData);
      out.write('\n');
      if (err) reject(err);
      else resolve(value);
    };
    const onData = (chunk: string) => {
      for (const ch of chunk) {
        if (ch === '\r' || ch === '\n' || ch === '\u0004') return done();
        if (ch === '\u0003') return done(new Error('cancelled'));
        if (ch === '\u007f' || ch === '\b') value = value.slice(0, -1);
        else if (ch >= ' ') value += ch;
      }
    };
    stdin.on('data', onData);
  });
}

export async function promptLine(question: string): Promise<string> {
  const rl = createInterface({ input: process.stdin, output: process.stderr });
  try {
    return await new Promise<string>((resolve) => rl.question(question, resolve));
  } finally {
    rl.close();
  }
}
