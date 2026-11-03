/**
 * Nest ↔ bridge request signing, exactly per docs/web/api.md §3: HMAC-SHA256 (key = UTF-8 bytes of `Bridge:Key`) over
 * `bridgeSigningString` from the contracts package, lowercase hex. The bridge side of `verifySignature` is the C# bridge;
 * this module's copy is used by the in-process mock bridge and the tests.
 */
import { createHash, createHmac, randomBytes, timingSafeEqual } from 'node:crypto';
import {
  BRIDGE_HEADERS,
  BRIDGE_MAX_SKEW_S,
  EMPTY_BODY_SHA256,
  bridgeSigningString,
  canonicalRoles,
  type Role,
  type SigningParts,
} from '@mineclaw/contracts';

export interface BridgeIdentity {
  user: string;
  player: string;
  /** canonicalRoles(...) or "". */
  roles: string;
}

export const NO_IDENTITY: BridgeIdentity = { user: '', player: '', roles: '' };

export function identityOf(user: { username: string; player?: string | null; roles: readonly Role[] }): BridgeIdentity {
  return { user: user.username, player: user.player ?? '', roles: canonicalRoles(user.roles) };
}

export function sha256Hex(body: Uint8Array | undefined): string {
  if (!body || body.length === 0) return EMPTY_BODY_SHA256;
  return createHash('sha256').update(body).digest('hex');
}

export function hmacHex(key: string, message: string): string {
  return createHmac('sha256', Buffer.from(key, 'utf8')).update(Buffer.from(message, 'utf8')).digest('hex');
}

export function sign(key: string, parts: SigningParts): string {
  return hmacHex(key, bridgeSigningString(parts));
}

/** The signed headers for one request. `pathAndQuery` must be the exact request target that goes on the wire. */
export function signedHeaders(
  key: string,
  req: { method: string; pathAndQuery: string; body?: Uint8Array; identity: BridgeIdentity },
  nowMs: number,
  nonce: string = randomBytes(16).toString('hex'),
): Record<string, string> {
  const timestamp = String(Math.floor(nowMs / 1000));
  const parts: SigningParts = {
    method: req.method.toUpperCase(),
    pathAndQuery: req.pathAndQuery,
    timestamp,
    nonce,
    bodySha256: sha256Hex(req.body),
    user: req.identity.user,
    player: req.identity.player,
    roles: req.identity.roles,
  };
  return {
    [BRIDGE_HEADERS.ts]: timestamp,
    [BRIDGE_HEADERS.nonce]: nonce,
    [BRIDGE_HEADERS.sig]: sign(key, parts),
    [BRIDGE_HEADERS.user]: req.identity.user,
    [BRIDGE_HEADERS.player]: req.identity.player,
    [BRIDGE_HEADERS.roles]: req.identity.roles,
  };
}

export type VerifyResult = { ok: true; identity: BridgeIdentity } | { ok: false; reason: string };

/** Bridge-side verification (mock bridge + tests): skew, nonce replay, constant-time signature compare. */
export class SignatureVerifier {
  private readonly seen = new Map<string, number>();

  constructor(
    private readonly key: string,
    private readonly now: () => number = Date.now,
  ) {}

  verify(req: {
    method: string;
    rawTarget: string;
    headers: Record<string, string | string[] | undefined>;
    body?: Uint8Array;
  }): VerifyResult {
    const h = (name: string): string => {
      const v = req.headers[name.toLowerCase()];
      return (Array.isArray(v) ? v[0] : v) ?? '';
    };
    const ts = h(BRIDGE_HEADERS.ts);
    const nonce = h(BRIDGE_HEADERS.nonce);
    const sig = h(BRIDGE_HEADERS.sig);
    if (!/^\d+$/.test(ts) || !/^[0-9a-f]{32}$/.test(nonce) || !/^[0-9a-f]{64}$/.test(sig)) {
      return { ok: false, reason: 'missing or malformed signature headers' };
    }
    const nowS = Math.floor(this.now() / 1000);
    if (Math.abs(nowS - Number(ts)) > BRIDGE_MAX_SKEW_S) return { ok: false, reason: 'clock skew' };
    const nowMs = this.now();
    for (const [n, t] of this.seen) if (nowMs - t > 300_000) this.seen.delete(n);
    if (this.seen.has(nonce)) return { ok: false, reason: 'replayed nonce' };
    const identity: BridgeIdentity = {
      user: h(BRIDGE_HEADERS.user),
      player: h(BRIDGE_HEADERS.player),
      roles: h(BRIDGE_HEADERS.roles),
    };
    const expected = sign(this.key, {
      method: req.method,
      pathAndQuery: req.rawTarget,
      timestamp: ts,
      nonce,
      bodySha256: sha256Hex(req.body),
      ...identity,
    });
    const a = Buffer.from(expected, 'hex');
    const b = Buffer.from(sig, 'hex');
    if (a.length !== b.length || !timingSafeEqual(a, b)) return { ok: false, reason: 'bad signature' };
    this.seen.set(nonce, nowMs);
    return { ok: true, identity };
  }
}
