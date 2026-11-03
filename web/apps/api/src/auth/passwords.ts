/**
 * argon2id hashing (@node-rs/argon2, prebuilt N-API). OWASP baseline parameters: m = 19 MiB, t = 2, p = 1. At most two
 * verifications run at once so a login flood cannot exhaust memory; unknown users are verified against a dummy hash so the
 * response time does not reveal whether a username exists.
 */
import { PASSWORD_MIN_LENGTH } from '@mineclaw/contracts';
import { hash, verify, type Algorithm } from '@node-rs/argon2';

const ARGON2ID = 2 as Algorithm;

export const ARGON2_PARAMS = { algorithm: ARGON2ID, memoryCost: 19456, timeCost: 2, parallelism: 1 } as const;

/** The shared rule (contracts `PASSWORD_MIN_LENGTH`): the setup page and the users CLI. */
export const MIN_PASSWORD_LENGTH = PASSWORD_MIN_LENGTH;

export function hashPassword(password: string): Promise<string> {
  return hash(password, ARGON2_PARAMS);
}

class Semaphore {
  private active = 0;
  private readonly waiters: Array<() => void> = [];
  constructor(private readonly max: number) {}
  async run<T>(fn: () => Promise<T>): Promise<T> {
    if (this.active >= this.max) await new Promise<void>((r) => this.waiters.push(r));
    this.active++;
    try {
      return await fn();
    } finally {
      this.active--;
      this.waiters.shift()?.();
    }
  }
}

export class PasswordVerifier {
  private readonly gate = new Semaphore(2);
  private dummy: Promise<string> | undefined;

  private dummyHash(): Promise<string> {
    this.dummy ??= hashPassword('dummy password that never matches anything');
    return this.dummy;
  }

  /** Verifies `password` against `passwordHash`, or burns the same time against a dummy when there is no user. */
  async verify(passwordHash: string | undefined, password: string): Promise<boolean> {
    const target = passwordHash ?? (await this.dummyHash());
    return this.gate.run(async () => {
      try {
        const ok = await verify(target, password);
        return ok && passwordHash !== undefined;
      } catch {
        return false;
      }
    });
  }
}
