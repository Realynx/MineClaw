/**
 * Optional Cloudflare Access gate (`Web:CloudflareAccess:{TeamDomain,Aud}`): every request except `/api/health` must carry a
 * valid `Cf-Access-Jwt-Assertion` (or the `CF_Authorization` cookie) signed by the team's keys (JWKS cached by jose), with
 * the configured audience and issuer. It is a second gate in front of login, not a replacement for it.
 */
import type { IncomingMessage } from 'node:http';
import { createRemoteJWKSet, jwtVerify, type JWTVerifyGetKey } from 'jose';
import { parseCookies } from '../common/http.js';

export interface CfAccessConfig {
  teamDomain: string;
  aud: string;
}

export function teamOrigin(teamDomain: string): string {
  const d = teamDomain.trim().replace(/\/+$/, '');
  if (/^https:\/\//i.test(d)) return d;
  return `https://${d.includes('.') ? d : `${d}.cloudflareaccess.com`}`;
}

export class CloudflareAccess {
  private readonly issuer: string;
  private readonly jwks: JWTVerifyGetKey;

  constructor(
    private readonly cfg: CfAccessConfig,
    jwks?: JWTVerifyGetKey,
  ) {
    this.issuer = teamOrigin(cfg.teamDomain);
    this.jwks =
      jwks ??
      createRemoteJWKSet(new URL(`${this.issuer}/cdn-cgi/access/certs`), {
        cacheMaxAge: 10 * 60 * 1000,
        cooldownDuration: 30 * 1000,
        timeoutDuration: 5000,
      });
  }

  static tokenOf(req: IncomingMessage): string | undefined {
    const h = req.headers['cf-access-jwt-assertion'];
    const v = Array.isArray(h) ? h[0] : h;
    if (v) return v;
    return parseCookies(req.headers.cookie).get('CF_Authorization');
  }

  /** True when the request carries a valid Access token. */
  async verify(req: IncomingMessage): Promise<boolean> {
    const token = CloudflareAccess.tokenOf(req);
    if (!token) return false;
    try {
      await jwtVerify(token, this.jwks, {
        issuer: this.issuer,
        audience: this.cfg.aud,
        algorithms: ['RS256'],
        clockTolerance: 30,
      });
      return true;
    } catch {
      return false;
    }
  }
}
