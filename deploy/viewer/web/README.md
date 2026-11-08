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
| `/etc/mineclaw-web/`                          | 0750 mineclaw-web             | config dir (the API writes `users.json` for the first-run setup and on TOTP verify) |
| `/etc/mineclaw-web/config.json`               | 0640 mineclaw-web             | from `config.example.json`                               |
| `/etc/mineclaw-web/users.json`                | 0600 mineclaw-web             | first account from the setup page, then the users CLI    |
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

### First account: the setup page

After the first deploy, open **http://10.1.2.73:8080** and complete the setup page. While `users.json` has no users the app
is in setup mode: every page redirects to `/setup`, sign-in is off, and the page creates the first account (username,
password, linked Minecraft player, roles preset to admin + chat + commands + viewer), offers an optional authenticator (QR
code drawn in the browser, then a code to confirm), and signs you straight in. Whoever completes it first becomes the admin,
so do it right after the deploy.

- **LAN only.** `POST /api/setup` is refused (403, audited) for a request through a trusted proxy (`Web:TrustedProxies`), one
  that carries Cloudflare or forwarding headers, or one from a non-LAN address (loopback, RFC 1918, link-local, CGNAT/Tailscale
  and ULA count as LAN). `Web:Setup:AllowProxied=true` lifts this; leave it off.
- **Once.** The account is created by an atomic create-if-empty under the users file's lock file
  (`/etc/mineclaw-web/.users.json.lock`), so two simultaneous setups can't both win (the loser gets 409). As soon as any user
  exists, `/setup` redirects to `/login` and `POST /api/setup` answers 409, checked on every request.
- **Audited** as kind `setup` (refusals too), rate limited per IP (`Web:Setup:AttemptsPerMinute`), and `users.json` stays
  0600. Removing every user with the CLI re-opens setup mode.

### More users: the users CLI

For everything after the first account (and whenever you prefer the terminal): no self-signup; passwords are prompted
without echo or read from stdin, never from argv. `/usr/local/sbin/mineclaw-web-users` runs `apps/api/dist/cli/users.js` as
`mineclaw-web` with the right config. Call it by its full path (`pct exec`'s PATH has no `/usr/local/sbin`) and from a real
terminal (`ssh -t`): the password prompt needs a TTY, and the TOTP secret must not land in an agent transcript.

```sh
ssh -t alice pct exec 114 -- /usr/local/sbin/mineclaw-web-users add robin --roles chat,viewer --player kiwi
ssh -t alice pct exec 114 -- /usr/local/sbin/mineclaw-web-users totp robin enroll    # prints the otpauth URL once, asks for a code
ssh -t alice pct exec 114 -- /usr/local/sbin/mineclaw-web-users passwd|roles|player|disable|enable|remove|list ...
```

`passwd`, `disable` and `remove` end that user's sessions; role and player changes apply on the next request. Users can
also enrol TOTP themselves (`POST /api/auth/totp/enroll` + `verify`, the account menu).

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
| + `Web:Tls:{Enabled,Hostname,Port,CertFile,KeyFile}` | false, empty, 8443, empty, empty | optional additional HTTPS listener; PEM full chain and matching unencrypted private key are absolute server paths; port 1024–65535 |
| + `Web:LogLevel`                       | info                             | pino levels, `silent` |
| + `Web:ValidateResponses`              | off (dev warn, tests strict)     | check bridge replies and SSE frames against the contracts schemas |
| + `Web:BodyLimitBytes`                 | 1048576                          | JSON bodies; shard uploads are capped at 48 MB |
| + `Web:ChatRate:{Burst,PerMinute}`     | 5 / 20                           | per user |
| + `Web:Login:{UserThreshold,IpThreshold,LockoutBaseSeconds,LockoutMaxSeconds,IpAttemptsPerMinute}` | 5, 20, 30, 3600, 30 | lockout = base·2^(failures−threshold), capped |
| + `Web:Csp:{Scripts,ReportOnly}`       | nonce / false                    | `unsafe-inline` is the fallback if pages cannot render dynamically |
| + `Web:MaxSseClients`                  | 200                              | also 12 streams per user |
| + `Web:Setup:{AllowProxied,AttemptsPerMinute}` | false / 10               | first-run setup: allow it through a proxy or tunnel (leave off); `POST /api/setup` attempts per IP per minute |
| `Bridge:Url`                           | http://127.0.0.1:8766            | loopback, plain HTTP |
| `Bridge:Key`                           | required, ≥ 32 chars             | env `MINECLAW_BRIDGE_KEY`, or a file: `Bridge:KeyFile` / env `MINECLAW_BRIDGE_KEY_FILE` (the unit uses a systemd credential). Never logged. |
| `Bridge:TimeoutMs`                     | 15000                            | time to response headers and max idle on a body |
| + `Bridge:Mock`                        | false                            | dev only (`--mock-bridge`); refused when NODE_ENV=production |

## Cookies on plain HTTP vs HTTPS

Administration → **Web HTTPS and certificates** reviews and saves these settings to
`<Web:DataDir>/hosting.override.json` (0600, previous file `.bak`). Restart **mineclaw-web-api** to apply them;
the Administration bot restart action does not restart this service. The override loads after config files and before
environment variables. Any `MINECLAW_WEB_Web__Tls__*` override makes the editor read-only. Startup and saving validate
certificate dates, SAN hostname, key match and file readability. The private key is never returned by the API or stored
in the audit. This editor does not issue or renew certificates.

HTTP remains available on its original port for health checks/recovery. HTTPS uses the same authentication, routes,
WebSocket proxy and SSE streams. The configured `https://hostname:port` is added to allowed origins at startup.
Keep `CookieSecure: auto`; do not force insecure cookies. The service stays unprivileged, so use a reverse proxy for
port 443 or choose a free unprivileged port. Configure the firewall/DNS separately. See
[`docs/web/deploy.md`](../../../docs/web/deploy.md#https-and-app-installation) for the certificate/install workflow.

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
Keep port 8080 closed to anything but the LAN in OPNsense once the tunnel is the public way in. Finish the first-run setup on
the LAN before the tunnel goes live: the setup page refuses tunnelled requests anyway, but an open setup page is still a
page anyone could load.

## Dev on Fox's PC

```sh
cd web && npm install && npm run build
npm run dev -w @mineclaw/api -- --mock-bridge          # Nest on 127.0.0.1:4000 with fixture data, no bot needed
npm run dev -w @mineclaw/web                           # Next on :3000: with no users yet the first visit is the setup page
# or create the account from the terminal (writes apps/api/.data/users.json):
npm run users -w @mineclaw/api -- add fox --roles admin,chat,commands,viewer --player poofimafox --dev
```

The web app's mock mode (`npm run dev:mock -w @mineclaw/web`, no Nest at all) shows the setup page when
`sessionStorage['mcx-mock-setup']` is set (to `[]`) in the browser tab.

`--mock-bridge` serves every route from the contracts fixtures (or schema-generated samples) through a real signed
loopback bridge, and a synthetic live stream (vitals drift, thoughts cycle, mobs move, chat lines get a bot reply ~1.5 s
later). Against a real bridge, put `MINECLAW_BRIDGE_KEY=...` in `web/apps/api/.env.local` (gitignored) and drop the flag.

## Health and logs

`GET /api/health` → `{ok, version, bridge: up|down}` (a signed ping of `/v1/health`, cached 5 s). Logs are pino JSON on
journald: method, path, status, client IP, user and request id per request (`X-Request-Id` is echoed to the browser);
cookies, CSRF tokens, Access JWTs, signatures and bodies are never logged.
