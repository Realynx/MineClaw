/**
 * Client IP and scheme. `CF-Connecting-IP` / `X-Forwarded-For` / `X-Forwarded-Proto` are honoured only when `Web:TrustProxy`
 * is on AND the socket peer is in `Web:TrustedProxies` (IPs or CIDRs, v4 or v6). Anything else uses the socket itself.
 */
import type { IncomingMessage } from 'node:http';
import { BlockList, isIP } from 'node:net';
import type { TLSSocket } from 'node:tls';

export interface ProxyTrust {
  trustProxy: boolean;
  trustedProxies: readonly string[];
}

/** `::ffff:10.1.2.73` → `10.1.2.73`. */
export function normalizeIp(ip: string | undefined): string {
  if (!ip) return '';
  const t = ip.trim();
  if (t.toLowerCase().startsWith('::ffff:') && isIP(t.slice(7)) === 4) return t.slice(7);
  return t;
}

export class TrustedProxies {
  private readonly list = new BlockList();
  private readonly any: boolean;

  constructor(private readonly cfg: ProxyTrust) {
    let count = 0;
    for (const entry of cfg.trustedProxies) {
      const [addr, bits] = entry.trim().split('/');
      const ip = normalizeIp(addr);
      const family = isIP(ip);
      if (!family) throw new Error(`Web:TrustedProxies: "${entry}" is not an IP or CIDR`);
      const type = family === 6 ? 'ipv6' : 'ipv4';
      if (bits !== undefined) this.list.addSubnet(ip, Number(bits), type);
      else this.list.addAddress(ip, type);
      count++;
    }
    this.any = cfg.trustProxy && count > 0;
  }

  isTrusted(peer: string): boolean {
    if (!this.any) return false;
    const ip = normalizeIp(peer);
    const family = isIP(ip);
    if (!family) return false;
    return this.list.check(ip, family === 6 ? 'ipv6' : 'ipv4');
  }

  /** The browser's IP and whether it reached us over HTTPS. */
  resolve(req: IncomingMessage): { ip: string; https: boolean } {
    const peer = normalizeIp(req.socket.remoteAddress);
    const direct = { ip: peer, https: Boolean((req.socket as TLSSocket).encrypted) };
    if (!this.isTrusted(peer)) return direct;

    const one = (name: string): string | undefined => {
      const v = req.headers[name];
      return Array.isArray(v) ? v[0] : v;
    };
    let ip = peer;
    const cf = normalizeIp(one('cf-connecting-ip'));
    if (cf && isIP(cf)) {
      ip = cf;
    } else {
      // Rightmost hop that is not one of our trusted proxies.
      const xff = (one('x-forwarded-for') ?? '')
        .split(',')
        .map((s) => normalizeIp(s))
        .filter((s) => isIP(s));
      for (let i = xff.length - 1; i >= 0; i--) {
        ip = xff[i]!;
        if (!this.isTrusted(ip)) break;
      }
    }
    const proto = (one('x-forwarded-proto') ?? '').split(',')[0]?.trim().toLowerCase();
    return { ip, https: proto ? proto === 'https' : direct.https };
  }
}

const LOCAL = new BlockList();
for (const [net, bits] of [
  ['127.0.0.0', 8],
  ['10.0.0.0', 8],
  ['172.16.0.0', 12],
  ['192.168.0.0', 16],
  ['169.254.0.0', 16],
  ['100.64.0.0', 10], // CGNAT / Tailscale
] as const)
  LOCAL.addSubnet(net, bits, 'ipv4');
for (const [net, bits] of [
  ['::1', 128],
  ['fc00::', 7],
  ['fe80::', 10],
] as const)
  LOCAL.addSubnet(net, bits, 'ipv6');

/** Loopback, RFC 1918, link-local, CGNAT (Tailscale) or IPv6 ULA / link-local. */
export function isLocalAddress(ip: string): boolean {
  const a = normalizeIp(ip);
  const family = isIP(a);
  if (!family) return false;
  return LOCAL.check(a, family === 6 ? 'ipv6' : 'ipv4');
}

function isLoopback(ip: string): boolean {
  const a = normalizeIp(ip);
  return a === '::1' || (isIP(a) === 4 && a.startsWith('127.'));
}

/** Headers only a Cloudflare edge / tunnel adds. */
const CLOUDFLARE_HEADERS = [
  'cf-connecting-ip',
  'cf-ray',
  'cf-visitor',
  'cf-ipcountry',
  'cdn-loop',
  'cf-access-jwt-assertion',
] as const;
const FORWARDING_HEADERS = ['x-forwarded-for', 'forwarded', 'x-real-ip'] as const;

/**
 * Why a request is NOT a direct LAN/loopback client (null = it is). Keeps the first-run setup off the tunnel:
 * - the socket peer is one of `Web:TrustedProxies` (cloudflared, a reverse proxy);
 * - it carries Cloudflare headers, whatever the trust config says (a tunnel set up before `TrustProxy` was turned on);
 * - it carries forwarding headers from a non-loopback peer (a proxy we were not told about; the dev server's own rewrite
 *   proxy on loopback is fine);
 * - the peer is not a LAN address (loopback, RFC 1918, link-local, CGNAT/Tailscale, ULA).
 */
export function notDirectLan(req: IncomingMessage, trusted: TrustedProxies): string | null {
  const peer = normalizeIp(req.socket.remoteAddress);
  if (trusted.isTrusted(peer)) return 'through a trusted proxy';
  if (CLOUDFLARE_HEADERS.some((h) => req.headers[h] !== undefined)) return 'through Cloudflare';
  if (!isLoopback(peer) && FORWARDING_HEADERS.some((h) => req.headers[h] !== undefined)) return 'through a proxy';
  if (!isLocalAddress(peer)) return 'not from a LAN address';
  return null;
}
