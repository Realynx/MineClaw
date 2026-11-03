# Web Explorer gateway and UI on CT 114

`web/apps/api` (NestJS, unit `mineclaw-web-api`) and `web/apps/web` (Next.js standalone, unit `mineclaw-web-ui`) run on
CT 114 beside the bot and the bridge. Design: `docs/web/architecture.md`; contract: `docs/web/api.md`.

```
browser ──HTTP :8080 (LAN now) / HTTPS via cloudflared (later)──▶ mineclaw-web-api 0.0.0.0:8080
                                                                    ├─ /api/*  auth, CSRF, RBAC, audit, SSE fan-out
                                                                    │          └─ signed HMAC ─▶ mineclaw-bridge 127.0.0.1:8766
                                                                    └─ else    reverse proxy ─▶ mineclaw-web-ui 127.0.0.1:3000
```

## Files

| Path on CT 114                                | Mode / owner                  | What                                                     |
| --------------------------------------------- | ----------------------------- | -------------------------------------------------------- |
| `/etc/systemd/system/mineclaw-web-api.service` | 0644 root                     | from `mineclaw-web-api.service`                          |
| `/etc/systemd/system/mineclaw-web-ui.service`  | 0644 root                     | from `mineclaw-web-ui.service`                           |
| `/etc/mineclaw-web/`                          | 0750 mineclaw-web             | config dir (the API rewrites `users.json` on TOTP verify) |
| `/etc/mineclaw-web/config.json`               | 0640 mineclaw-web             | from `config.example.json`                               |
| `/etc/mineclaw-web/users.json`                | 0600 mineclaw-web             | managed by the users CLI only                            |
| `/etc/mineclaw-web/bridge.key`                | 0600 root                     | HMAC key shared with the bridge (systemd credential)     |
| `/etc/mineclaw-web/mineclaw-web.env`          | 0640 root:mineclaw-web        | optional overrides, from `mineclaw-web.env.example`      |
| `/var/lib/mineclaw-web/web.db`                | 0600 mineclaw-web (StateDir)  | sessions, login counters, pending TOTP, web audit        |
| `/opt/mineclaw-web/current/`                  | read-only                     | -> `releases/<id>`: apps/api/dist + node_modules, apps/web/.next/standalone |

## First install

Scripted: `pwsh deploy/deploy-web.ps1 -Install` once, `-BotConfig [-Apply]` for the bot side, then `pwsh deploy/deploy-web.ps1`
per release. Runbook (firewall rule, verify, rollback, logs): `docs/web/deploy.md`. `deploy/viewer/install-ct114.sh`
(idempotent) installs Node 22 to `/opt/node` (`/usr/local/bin/node`, which the units use), creates `mineclaw-web` and
`mineclaw-bridge`, the dirs above, ONE bridge key in both root-only files (`/etc/mineclaw-web/bridge.key`,
`/etc/mineclaw-bridge/bridge.json`), `config.json` from `config.example.json`, and installs the units.

A release (`/opt/mineclaw-web/releases/<id>`, `current` symlink) holds `apps/api/{package.json,dist}`, the api's production
`node_modules` (installed for linux-x64 glibc on the dev box) and `apps/web/.next/standalone` with `.next/static` and `public/`
copied beside `server.js`. There is no workspace root in it, so the users CLI runs through a wrapper.

Users (no self-signup; passwords are prompted without echo or read from stdin, never from argv).
`/usr/local/sbin/mineclaw-web-users` runs `apps/api/dist/cli/users.js` as `mineclaw-web` with the right config:

```sh
ssh -t alice pct exec 114 -- mineclaw-web-users add fox --roles admin,chat,commands,viewer --player poofimafox
ssh -t alice pct exec 114 -- mineclaw-web-users add robin --roles chat,viewer --player kiwi
ssh -t alice pct exec 114 -- mineclaw-web-users totp fox enroll      # prints the otpauth URL once, asks for a code
ssh -t alice pct exec 114 -- mineclaw-web-users passwd|roles|player|disable|enable|remove|list ...
```

`passwd`, `disable` and `remove` end that user's sessions; role and player changes apply on the next request. Users can
also enrol TOTP themselves (`POST /api/auth/totp/enroll` + `verify`).

## Config keys

`Web:*` and `Bridge:*` from the contracts `NestConfig`, plus the gateway's own (marked +). JSON is PascalCase like the bot's
appsettings; env overrides are `MINECLAW_WEB_<Section>__<Key>`.

| Key                                    | Default                          | Notes |
| -------------------------------------- | -------------------------------- | ----- |
| `Web:Port` / `Web:Bind`                | 8080 / 0.0.0.0 (dev 4000 / 127.0.0.1) | |
| `Web:NextUrl`                          | http://127.0.0.1:3000            | Next standalone |
| + `Web:ProxyNext`                      | true (dev false)                 | prod reverse proxy for everything outside `/api`, WebSocket upgrades included |
| `Web:AllowedOrigins`                   | required (dev: localhost:3000/4000) | exact `Origin` values accepted on every non-GET |
| `Web:TrustProxy` / `Web:TrustedProxies`| false / []                       | IPs or CIDRs whose `CF-Connecting-IP`, `X-Forwarded-For`, `X-Forwarded-Proto` are honoured |
| `Web:CloudflareAccess:{TeamDomain,Aud}`| null                             | second gate: valid `Cf-Access-Jwt-Assertion` (or `CF_Authorization` cookie) on everything but `/api/health` |
| `Web:UsersFile`                        | /etc/mineclaw-web/users.json     | |
| + `Web:DataDir`                        | /var/lib/mineclaw-web            | web.db goes to `<DataDir>/web.db` |
| `Web:DbPath`                           | `<DataDir>/web.db`               | full path override |
| `Web:SessionIdleHours` / `Web:SessionAbsoluteDays` | 12 / 7               | |
| + `Web:CookieSecure`                   | auto                             | `auto`: Secure + `__Host-mcx` only on HTTPS; `always` / `never` force it |
| + `Web:LogLevel`                       | info                             | pino levels, `silent` |
| + `Web:ValidateResponses`              | off (dev warn, tests strict)     | check bridge replies and SSE frames against the contracts schemas |
| + `Web:BodyLimitBytes`                 | 1048576                          | JSON bodies; shard uploads are capped at 48 MB |
| + `Web:ChatRate:{Burst,PerMinute}`     | 5 / 20                           | per user |
| + `Web:Login:{UserThreshold,IpThreshold,LockoutBaseSeconds,LockoutMaxSeconds,IpAttemptsPerMinute}` | 5, 20, 30, 3600, 30 | lockout = base·2^(failures−threshold), capped |
| + `Web:Csp:{Scripts,ReportOnly}`       | nonce / false                    | `unsafe-inline` is the fallback if pages cannot render dynamically |
| + `Web:MaxSseClients`                  | 200                              | also 12 streams per user |
| `Bridge:Url`                           | http://127.0.0.1:8766            | loopback, plain HTTP |
| `Bridge:Key`                           | required, ≥ 32 chars             | env `MINECLAW_BRIDGE_KEY`, or a file: `Bridge:KeyFile` / env `MINECLAW_BRIDGE_KEY_FILE` (the unit uses a systemd credential). Never logged. |
| `Bridge:TimeoutMs`                     | 15000                            | time to response headers and max idle on a body |
| + `Bridge:Mock`                        | false                            | dev only (`--mock-bridge`); refused when NODE_ENV=production |

## Cookies on plain HTTP vs HTTPS

Browsers only accept a `__Host-` cookie when it is `Secure`, and they drop `Secure` cookies set over plain HTTP. So:

- **HTTPS** (the request is TLS, or `X-Forwarded-Proto: https` from a trusted proxy): `__Host-mcx`, HttpOnly, Secure,
  SameSite=Lax, Path=/. HSTS (`max-age=15552000`, no includeSubDomains) is sent too.
- **Plain HTTP on the LAN** (today, `http://10.1.2.73:8080`): `mcx` with the same flags minus Secure, and no HSTS.

Only the name that matches the request's scheme is read, so an `mcx` cookie can never be replayed against the HTTPS origin.

## CSP and Next (coordination with apps/web)

For every proxied page request the gateway generates a nonce and:

- sends the browser `Content-Security-Policy: default-src 'self'; script-src 'self' 'nonce-<n>' 'strict-dynamic'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self'; worker-src 'self' blob:; manifest-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'` (plus `upgrade-insecure-requests` on HTTPS);
- forwards the same policy to Next as the **request** header `content-security-policy`, and the nonce as `x-nonce`. Next's
  app router reads the nonce from that request header and puts it on its own scripts; `headers().get('x-nonce')` is there
  for any `<Script>` the app adds. Pages must render dynamically (a prerendered page has no nonce).
- drops Next's own `Content-Security-Policy`, `X-Frame-Options`, `Strict-Transport-Security`, `Referrer-Policy` and
  `X-Powered-By` from the response, and strips any client-sent `x-middleware-*` / `x-nonce` headers (CVE-2025-29927).

`/api` responses carry `default-src 'none'; frame-ancestors 'none'`. All responses: `X-Frame-Options: DENY`,
`Referrer-Policy: same-origin`, `X-Content-Type-Options: nosniff`, COOP/CORP same-origin.

In dev Nest is not in front of Next (Next on :3000 rewrites `/api/*` to Nest on :4000), so page CSP is the web app's concern
there.

## Cloudflare (later)

cloudflared runs on CT 114 and points its ingress at `http://127.0.0.1:8080`:

```yaml
ingress:
  - hostname: mineclaw.example.com
    service: http://127.0.0.1:8080
    originRequest: { disableChunkedEncoding: false, noTLSVerify: false }
  - service: http_status:404
```

Then set `Web:TrustProxy=true`, `Web:TrustedProxies=["127.0.0.1","::1"]` (the tunnel connects from loopback), add the
`https://` hostname to `Web:AllowedOrigins`, and optionally put the hostname behind a Cloudflare Access application and
set `Web:CloudflareAccess:{TeamDomain,Aud}` (the AUD tag from the Access app). The SSE stream sends
`Cache-Control: no-cache, no-transform` and `X-Accel-Buffering: no` and is never compressed, so the tunnel streams it as is.
Keep port 8080 closed to anything but the LAN in OPNsense once the tunnel is the public way in.

## Dev on Fox's PC

```sh
cd web && npm install && npm run build
npm run users -w @mineclaw/api -- add fox --roles admin,chat,commands,viewer --player poofimafox --dev   # writes apps/api/.data/users.json
npm run dev -w @mineclaw/api -- --mock-bridge          # Nest on 127.0.0.1:4000 with fixture data, no bot needed
```

`--mock-bridge` serves every route from the contracts fixtures (or schema-generated samples) through a real signed
loopback bridge, and a synthetic live stream (vitals drift, thoughts cycle, mobs move, chat lines get a bot reply ~1.5 s
later). Against a real bridge, put `MINECLAW_BRIDGE_KEY=...` in `web/apps/api/.env.local` (gitignored) and drop the flag.

## Health and logs

`GET /api/health` → `{ok, version, bridge: up|down}` (a signed ping of `/v1/health`, cached 5 s). Logs are pino JSON on
journald: method, path, status, client IP, user and request id per request (`X-Request-Id` is echoed to the browser);
cookies, CSRF tokens, Access JWTs, signatures and bodies are never logged.
