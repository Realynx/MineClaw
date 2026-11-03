# MineClaw Web Explorer: API catalogue

This is the contract that the bridge (`tools/ViewerBridge`), Nest (`web/apps/api`) and the web app (`web/apps/web`) implement
against. The design behind it is `docs/web/architecture.md`.

The single source of truth is `web/packages/contracts` (`@mineclaw/contracts`). It holds a zod schema and inferred type for
every request and response, the route table `ROUTES`, and the SSE catalogue `SSE_EVENTS`. The route tables in §4 are generated
from `ROUTES` (`npm run gen:api-doc -w @mineclaw/contracts`), and a contracts test fails if a route is missing from this file.
Schema names in this document are the export names in the package.

All three new processes run on CT 114 beside the bot:

- Nest on `0.0.0.0:8080`
- Next on `127.0.0.1:3000`
- the bridge on `127.0.0.1:8766`
- the supervisor on `:8765`

---

## 1. Wire conventions

- **Casing.**
  - The public `/api` is camelCase JSON.
  - The bridge `/v1` and the supervisor are snake_case.
  - Nest converts response bodies with `toCamel`, and request bodies and query keys with `toSnake` / `camelToSnake`. It never
    reshapes domain data beyond that.
  - Conversion stops at opaque domain maps (`OPAQUE_KEYS`): `params`, `args`, `blackboard`, `fields`, `edit_prefill`, `counts`,
    `by_kind`, `see`, `bill`, `data`. Their inner keys pass through unchanged, e.g. a script's `params: {store_at: "home"}`.
- **Parsing bridge JSON.**
  - Nest parses every bridge JSON body with `parseBridgeJson`, then calls `toCamel`.
  - `parseBridgeJson` turns every number under a `hash` key (the i64 world column hashes) into its exact decimal string,
    using the reviver source-text access in Node ≥ 22. `WorldColumnInfo.hash` is therefore a string on the public API.
- **Nulls.**
  - The C# side writes with `WhenWritingNull`, so a null field is usually absent. Every nullable field is `.nullish()`
    (absent, null or a value).
- **Times.**
  - `DateTimeOffset` values are ISO-8601 strings with an offset (`IsoDateTime`).
  - Db timestamps (`UnixMs`) are unix milliseconds: memory records, designs, personality.
  - So are `at` on `/api/world/index` and `/api/world/entities`.
  - A .NET `TimeSpan` (only `RebuildJob.result.elapsed`) is a `"hh:mm:ss.fffffff"` string.
- **Doubles.**
  - The supervisor allows named float literals, so a `Double` field may be the string `"NaN"`, `"Infinity"` or
    `"-Infinity"`.
  - Read them with `toNumber`.
- **Forward compatibility.**
  - Response objects are non-strict. Unknown keys are stripped by `parse` and never rejected, so a newer bot can add fields.
  - Request bodies are strict. Unknown keys get a 400, so a client cannot smuggle `who`, `player` or `allowCommands`.
- **Query strings.**
  - Values are coerced (`QInt`, `QBool`: `1|0|true|false`).
  - Defaults are applied by the schema. Nest forwards the parsed query to the bridge with snake_case keys
    (`windowS` → `window_s`, `colorBy` → `color_by`). Booleans are sent as `1`/`0`.
- **Paging.**
  - Paged lists return `{total, offset, limit, items}`. The default limit is 100 and the max 500, unless a schema says
    otherwise.
  - Every table is server-paged. No route returns a whole table.
- **Errors.**
  - Every non-2xx response is `ApiError {error, code?, retryAfterMs?, details?}`.
  - Nest maps bridge and supervisor errors to it:

    | Status | Meaning                             | `code`                                          |
    | ------ | ----------------------------------- | ----------------------------------------------- |
    | 400    | validation                          | `bad_request` (`details` = zod issues)          |
    | 401    | no session                          | `unauthorized`                                  |
    | 403    | missing role                        | `forbidden`                                     |
    | 403    | bad or missing CSRF token or Origin | `csrf`                                          |
    | 404    | not found                           | `not_found`                                     |
    | 413    | upload too big                      | `payload_too_large`                             |
    | 423    | login lockout                       | `locked`                                        |
    | 429    | rate limited                        | `rate_limited` + `Retry-After` + `retryAfterMs` |
    | 502    | upstream error                      | `upstream_error`                                |
    | 503    | bridge or bot down                  | `upstream_unavailable`                          |
    | 503    | render gate busy                    | `busy` + `Retry-After`                          |

  - Auth errors are generic.
  - Upstream URLs, tokens and stack traces never reach the browser.
  - A supervisor `MemoryEditResult` refusal (400 with that body) is passed through as 400 with the same `MemoryEditResult`
    body, not as `ApiError`.
- **Binary responses** (`responseKind` png, binary, zip) are streamed unchanged. Nest passes these headers through:
  - `Content-Type`
  - `Content-Length`
  - `ETag`
  - `X-Sha256`, `X-Tokens`, `X-Width`, `X-Height`, `X-Seen`
  - `Retry-After`
  - `Content-Disposition` (shard export)
- **Caching.**
  - JSON is `Cache-Control: no-store`. (The world viewer bundles its own textures; there is no atlas route.)

## 2. Auth, sessions, roles (Nest-native)

- **Roles** (`Role`): `viewer`, `chat`, `commands`, `admin`, `design`, `story`. Route access (`RouteAccess`) is `public`,
  `authenticated` or a role. `hasRole` implements these semantics:
  - Every signed-in user can read: `viewer` is implied by holding any role.
  - `chat`, `commands` and `admin` are independent grants, and none implies another.
  - `design` (Design Studio writes) and `story` are the grants `admin` implies.
  - `commands` has an effect only together with `chat`: the user's viewer lines may arm CommandGate request grants.
  - `story` (added 2026-11-03) plays Story Mode: every `story.*` write. `admin` holds it too;
    viewers watch.
- **Session.**
  - An opaque id in the `__Host-mcx` cookie. It is HttpOnly, SameSite=Lax, and Secure when served over HTTPS.
  - Sessions are stored in `web.db`. The idle timeout is 12 h and the absolute timeout 7 d.
  - The id rotates on login.
- **CSRF.**
  1. The browser calls `GET /api/auth/csrf` first. This creates an anonymous pre-session if needed.
  2. It sends the token as `X-CSRF-Token` on every non-GET, including login.
  3. `Origin` must be in `Web:AllowedOrigins`.
  4. Login returns a new token, because it rotates the session.
- **Login.**
  - It is rate limited per IP and per user, with an exponential lockout (423).
  - `totp` is required once TOTP is enrolled.
  - The client IP is `CF-Connecting-IP` / `X-Forwarded-For` only when `Web:TrustProxy` is on and the socket peer is in
    `Web:TrustedProxies`.
  - Optional `Web:CloudflareAccess:{TeamDomain,Aud}` verifies `Cf-Access-Jwt-Assertion` before login.
- **TOTP.**
  1. `enroll` re-checks the password and returns a pending `otpauth://` URL and secret, shown once and never logged.
  2. `verify` activates it.
- **First-run setup** (`setup.status`, `setup.create`).
  - `GET /api/setup` → `SetupStatus {needed, allowedHere}`. `needed` is true while the users file is missing or holds no
    users (a file that does not parse never counts as empty). `allowedHere` says whether this request may complete it.
  - While `needed`: Nest redirects every page (not `/api`, `/_next` or files) to `/setup` with a 302, and login answers 409.
    Once any user exists, `/setup` redirects to `/login`.
  - `POST /api/setup` takes `SetupRequest {username, password, player, roles}` (username lower-cased; password
    `PASSWORD_MIN_LENGTH` = 12 to 256 chars, the same rule as the users CLI). It needs CSRF + `Origin` like every write, and
    answers like login: `LoginResponse {user: Me, csrfToken}` with a rotated session. TOTP then uses the normal
    `auth.totpEnroll` / `auth.totpVerify` routes.
  - Refused with 403 when the request is not a direct LAN/loopback client: the peer is in `Web:TrustedProxies`, it carries
    Cloudflare headers (`CF-Connecting-IP`, `CF-Ray`, `CDN-Loop`…), it carries `X-Forwarded-For` / `Forwarded` from a
    non-loopback peer, or the peer is not a LAN address. `Web:Setup:AllowProxied` lifts this.
  - 409 once any user exists: `UsersStore.createFirst` re-reads the file under an O_EXCL lock file and writes it (temp +
    rename, 0600) only if it holds no users, so of two simultaneous setups exactly one wins.
  - 429 past `Web:Setup:AttemptsPerMinute` per IP. Every attempt that reaches the handler is audited as `setup`.
- **Web audit.** Nest records every login (ok or failed), lockout, chat line and write in `web.db`, as a `WebAuditEntry` with
  kind `WebAuditKind`. Admins read it at `GET /api/audit`. The Audit page also shows the bot's own trail from
  `GET /api/admin/audit`.
- **Nest config** (`NestConfig`, key names only):
  - `Web:{Port, Bind, NextUrl, AllowedOrigins, TrustProxy, TrustedProxies, CloudflareAccess, UsersFile, DbPath,
SessionIdleHours, SessionAbsoluteDays}`
  - `Bridge:{Url (default http://127.0.0.1:8766), Key, TimeoutMs}`
  - There is no TLS and no cert pin on loopback.
  - `Bridge:Key` is a secret. It comes from a file or env on CT 114 and from `.env.local` in dev, and is never committed or
    logged.

## 3. Nest ↔ bridge signing and identity

The bridge accepts loopback sources only (127.0.0.1 / ::1) and plain HTTP on 127.0.0.1:8766. Every request is signed.

**Signed string** (architecture.md): HMAC-SHA256 over

```
method\npath?query\ntimestamp\nnonce\nsha256(body)
```

followed by the three identity headers, each newline-separated, after the body hash. In full, eight lines joined by `\n` with
no trailing newline:

```
METHOD
PATH?QUERY
TIMESTAMP
NONCE
SHA256(BODY)
X-MCX-User
X-MCX-Player
X-MCX-Roles
```

`bridgeSigningString(parts)` in the contracts package builds this string.

**Encoding rules** (these resolve ambiguities in architecture.md; both sides must match exactly):

| Line           | Rule                                                                                                                                                                                                                                    |
| -------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `METHOD`       | Upper case.                                                                                                                                                                                                                             |
| `PATH?QUERY`   | The request target exactly as sent on the wire: path plus `?` plus the raw query, with no scheme or host. With no query it is the path alone, with no `?`. The bridge verifies against the raw target it received and never re-encodes. |
| `TIMESTAMP`    | Unix seconds in decimal. It is the `X-MCX-Ts` value. The bridge rejects a skew over 30 s.                                                                                                                                               |
| `NONCE`        | 16 random bytes as 32 lowercase hex chars. It is the `X-MCX-Nonce` value. The bridge rejects a nonce seen in the last 5 min (LRU).                                                                                                      |
| `SHA256(BODY)` | Lowercase hex SHA-256 of the exact body bytes. With no body it is the empty-string hash `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855`. For multipart it covers the raw multipart bytes.                            |
| `X-MCX-User`   | The username. `""` when absent. Must not contain CR or LF.                                                                                                                                                                              |
| `X-MCX-Player` | The linked player. `""` when absent. Must not contain CR or LF.                                                                                                                                                                         |
| `X-MCX-Roles`  | `canonicalRoles`: lowercase, de-duplicated, sorted, comma-joined with no spaces (`admin,chat,commands,viewer`). `""` when absent. Must not contain CR or LF.                                                                            |

- **Signature** (`X-MCX-Sig`): the lowercase hex `HMAC-SHA256(key = UTF-8 bytes of Bridge:Key, message = UTF-8 bytes of the
signed string)`. The bridge compares in constant time.
- **Other headers:** Nest may also send `X-MCX-Request-Id` for log correlation. It is not signed.

**Test vectors.** The key is `test-key-not-a-secret-0123456789abcdef`. Both share TIMESTAMP `1762077600`.

|                | GET vector                                                         | POST vector                                                                        |
| -------------- | ------------------------------------------------------------------ | ---------------------------------------------------------------------------------- |
| Request target | `GET /v1/memory/facts?q=fox&offset=0&limit=50`                     | `POST /v1/sup/viewer/chat`                                                         |
| NONCE          | `00112233445566778899aabbccddeeff`                                 | `ffeeddccbbaa99887766554433221100`                                                 |
| Body           | none (empty-string hash)                                           | `{"bot":"maid_chan","text":"claude, set it to day","request_id":"r-8f14e45fceea"}` |
| Body hash      | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` | `1799cd6a496b955c3b09614f6ea849740d8a960cf4953323d89de775321e6da9`                 |
| User           | `fox`                                                              | `robin`                                                                            |
| Player         | `poofimafox`                                                       | `kiwi`                                                                             |
| Roles          | `admin,chat,commands,viewer`                                       | `chat,viewer`                                                                      |
| Signature      | `675be9cf1726a3d05bd596235a6f55f84d52c0982a7f2c5450a6a221ebff20b5` | `c13b3496a88ed8926bf6f910f93443800d5d0f0a56c77a545280da5e11b505d0`                 |

`web/packages/contracts/test/bridge.test.ts` checks both vectors. The C# bridge tests must reproduce them.

**What the bridge does with identity:**

- **`POST /v1/sup/viewer/chat`.**
  - Nest sends `{bot, text, request_id}`, with `bot` from the public path.
  - The bridge adds `player` from `X-MCX-Player`. It answers 403 when the player is empty.
  - It sets `allow_commands` to true only if `X-MCX-Roles` contains `commands`.
  - It forwards to `/api/viewer/chat` with the supervisor token.
  - A body that already carries `player` or `allow_commands` is rejected with 400.
- **`/v1/sup/admin/*`** (GET and POST):
  - It requires `admin` in `X-MCX-Roles`.
  - The bridge sets `who` to `"<X-MCX-User> (web)"` and never takes it from the body.
  - Multipart shard imports are re-encoded as the supervisor's JSON `{shard: base64, mode, conflict, include_rules,
include_world, max_usd, who}`.
- **Pass-through allow-list** (`SUPERVISOR_PASSTHROUGH_ALLOW`): `/v1/sup/<path>` forwards to supervisor `/api/<path>`.
  - GET: `bots`, `hive/tree`, `status`, `events`, `locate`, `blueprints`, `buildplans`, `world/*`, `scripts`, `bodies/*`,
    `brains/*`, `viewer/feed`, `viewer/now`, `viewer/memory-activity`, `story` and `story/*`, plus the admin reads
    `admin/rebuild/*` and `admin/audit` (admin role).
  - POST: `viewer/chat`, `admin/*` (admin role) and `story/*` (`storyWrite`: the `story` or the `admin` role; `who` is set
    like for admin writes).
  - Anything else gets a 404.
  - Binary replies are streamed.
- **Render routes:** `/v1/render/tile/{bot}.png` forwards to the supervisor's `/view/{bot}?mode&radius&xray`. This is the only
  non-`/api` supervisor path the bridge calls.
- **Bridge-native routes** are the bridge's own C# code over the read-only db and the shared libraries. "bridge-native" in the
  tables below means no supervisor call.

## 4. Route catalogue

| Column     | Meaning                                                                                                                                                                             |
| ---------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Role       | What `hasRole` checks.                                                                                                                                                              |
| Request    | The schemas for path params, query and body. `multipart` means a `shard` file part (≤ 48 MB, `SHARD_UPLOAD_MAX_BYTES`) plus an `options` part holding the JSON of the named schema. |
| Response   | The JSON schema, or the binary kind. A number in brackets is a non-200 success status.                                                                                              |
| Bridge     | The `/v1` call. `{param}` is filled from the public path, and the public query is appended in snake_case.                                                                           |
| Supervisor | The bot route the bridge calls, or `bridge-native`.                                                                                                                                 |
| Audit      | The `WebAuditKind` Nest records.                                                                                                                                                    |

Notes on specific routes:

- **Registration order matters.** It is the table order: specific paths come before parameterised siblings.
- **`scripts.*`** take `source=live|db`. Live reads the running bot's library with real lint. Db reads the routines through the
  bridge with offline lint. The bridge picks the upstream.
- **`groom.preview`** is phase 2. The bridge may answer 501 until it ports the grooming pipeline.

<!-- routes:begin -->
### Setup, auth and web audit (Nest-native)

| Route id | Public route | Role | Request | Response | Bridge `/v1` | Supervisor | Audit |
|---|---|---|---|---|---|---|---|
| `health` | `GET /api/health` | public | – | `HealthResponse` | Nest-native | – | – |
| `auth.csrf` | `GET /api/auth/csrf` | public | – | `CsrfResponse` | Nest-native | – | – |
| `setup.status` | `GET /api/setup` | public | – | `SetupStatus` | Nest-native | – | – |
| `setup.create` | `POST /api/setup` | public | body `SetupRequest` | `LoginResponse` | Nest-native | – | setup |
| `auth.login` | `POST /api/auth/login` | public | body `LoginRequest` | `LoginResponse` | Nest-native | – | login |
| `auth.logout` | `POST /api/auth/logout` | authenticated | body `Empty` | `LogoutResponse` | Nest-native | – | logout |
| `auth.me` | `GET /api/auth/me` | authenticated | – | `Me` | Nest-native | – | – |
| `auth.totpEnroll` | `POST /api/auth/totp/enroll` | authenticated | body `TotpEnrollRequest` | `TotpEnrollResponse` | Nest-native | – | totp_enroll |
| `auth.totpVerify` | `POST /api/auth/totp/verify` | authenticated | body `TotpVerifyRequest` | `TotpVerifyResponse` | Nest-native | – | totp_verify |
| `audit.list` | `GET /api/audit` | admin | query `WebAuditQuery` | `WebAuditPage` | Nest-native | – | – |

### Db reads (bridge-native)

| Route id | Public route | Role | Request | Response | Bridge `/v1` | Supervisor | Audit |
|---|---|---|---|---|---|---|---|
| `overview` | `GET /api/overview` | viewer | – | `Overview` | `GET /v1/overview` | bridge-native | – |
| `memory.kinds` | `GET /api/memory/kinds` | viewer | – | `MemoryKindsResponse` | `GET /v1/memory/kinds` | bridge-native | – |
| `memory.list` | `GET /api/memory/:kind` | viewer | params `MemoryKindParams`<br>query `MemoryListQuery` | `MemoryPage` | `GET /v1/memory/{kind}` | bridge-native | – |
| `memory.get` | `GET /api/memory/:kind/:id` | viewer | params `MemoryItemParams` | `MemoryDetail` | `GET /v1/memory/{kind}/{id}` | bridge-native | – |
| `search` | `GET /api/search` | viewer | query `SearchQuery` | `SearchResponse` | `GET /v1/search` | bridge-native | – |
| `raptor.tree` | `GET /api/raptor/tree` | viewer | query `RaptorTreeQuery` | `RaptorTreeResponse` | `GET /v1/raptor/tree` | bridge-native | – |
| `raptor.graph` | `GET /api/raptor/graph` | viewer | query `RaptorGraphQuery` | `RaptorGraph` | `GET /v1/raptor/graph` | bridge-native | – |
| `raptor.leaves` | `GET /api/raptor/node/:id/leaves` | viewer | params `RaptorNodeParams` | `RaptorLeavesResponse` | `GET /v1/raptor/node/{id}/leaves` | bridge-native | – |
| `raptor.node` | `GET /api/raptor/node/:id` | viewer | params `RaptorNodeParams` | `RaptorNodeDetail` | `GET /v1/raptor/node/{id}` | bridge-native | – |
| `personality` | `GET /api/personality` | viewer | query `PersonalityQuery` | `PersonalityResponse` | `GET /v1/personality` | bridge-native | – |
| `shards.packs` | `GET /api/shards/packs` | viewer | – | `ShardPacksResponse` | `GET /v1/shards/packs` | bridge-native | – |
| `shards.exportPreview` | `POST /api/shards/export/preview` | viewer | body `ShardExportRequest` | `ShardPreview` | `POST /v1/shards/export/preview` | bridge-native | – |
| `shards.export` | `POST /api/shards/export` | viewer | body `ShardExportRequest` | zip | `POST /v1/shards/export` | bridge-native | shard_export |
| `shards.importPreview` | `POST /api/shards/import/preview` | admin | multipart `ShardImportOptions` | `ShardImportPreviewResponse` | `POST /v1/shards/import/preview` | bridge-native | shard_import_preview |
| `scripts.list` | `GET /api/scripts` | viewer | query `ScriptLibraryQuery` | `ScriptLibraryResponse` | `GET /v1/scripts` | /api/scripts (source=live) \| bridge-native (source=db) | – |
| `scripts.diff` | `GET /api/scripts/:name/diff` | viewer | params `ScriptNameParams`<br>query `ScriptDiffQuery` | `ScriptDiff` | `GET /v1/scripts/{name}/diff` | bridge-native | – |
| `scripts.lint` | `GET /api/scripts/:name/lint` | viewer | params `ScriptNameParams`<br>query `ScriptLintQuery` | `ScriptLintResponse` | `GET /v1/scripts/{name}/lint` | bridge-native | – |
| `scripts.get` | `GET /api/scripts/:name` | viewer | params `ScriptNameParams`<br>query `ScriptDetailQuery` | `ScriptVersionDetail` | `GET /v1/scripts/{name}` | /api/scripts/{name}[/v{v}] (source=live) \| bridge-native (source=db) | – |
| `designs.list` | `GET /api/designs` | viewer | query `DesignListQuery` | `DesignListResponse` | `GET /v1/designs` | bridge-native | – |
| `designs.picture` | `GET /api/designs/:id/picture.png` | viewer | params `DesignParams` | png | `GET /v1/designs/{id}/picture.png` | bridge-native | – |
| `designs.diff` | `GET /api/designs/:id/diff` | viewer | params `DesignParams`<br>query `DesignDiffQuery` | `DesignDiff` | `GET /v1/designs/{id}/diff` | bridge-native | – |
| `designs.get` | `GET /api/designs/:id` | viewer | params `DesignParams` | `DesignDetail` | `GET /v1/designs/{id}` | bridge-native | – |

### Memory activity

| Route id | Public route | Role | Request | Response | Bridge `/v1` | Supervisor | Audit |
|---|---|---|---|---|---|---|---|
| `memoryActivity.heat` | `GET /api/memory-activity/heat` | viewer | query `MemoryHeatQuery` | `MemoryHeatResponse` | `GET /v1/memory-activity/heat` | bridge-native (aggregates /api/viewer/memory-activity over the RAPTOR tree) | – |
| `memoryActivity.list` | `GET /api/memory-activity` | viewer | query `MemoryActivityQuery` | `MemoryActivityResponse` | `GET /v1/sup/viewer/memory-activity` | /api/viewer/memory-activity | – |

### Admin (bot writes)

| Route id | Public route | Role | Request | Response | Bridge `/v1` | Supervisor | Audit |
|---|---|---|---|---|---|---|---|
| `admin.schema` | `GET /api/admin/schema` | admin | – | `EditSchemaResponse` | `GET /v1/admin/schema` | bridge-native | – |
| `admin.memoryEdit` | `POST /api/admin/memory` | admin | body `MemoryEditRequest` | `MemoryEditResult` | `POST /v1/sup/admin/memory` | /api/admin/memory | memory_edit |
| `admin.rebuildEstimate` | `POST /api/admin/rebuild/estimate` | admin | body `RebuildRequestBody` | `RebuildEstimate` | `POST /v1/sup/admin/rebuild/estimate` | /api/admin/rebuild/estimate | – |
| `admin.rebuildStart` | `POST /api/admin/rebuild` | admin | body `RebuildRequestBody` | `RebuildStarted` (202) | `POST /v1/sup/admin/rebuild` | /api/admin/rebuild | rebuild |
| `admin.rebuildCancel` | `POST /api/admin/rebuild/:jobId/cancel` | admin | params `RebuildJobParams`<br>body `Empty` | `OkResponse` | `POST /v1/sup/admin/rebuild/{jobId}/cancel` | /api/admin/rebuild/{jobId}/cancel | rebuild_cancel |
| `admin.rebuildJob` | `GET /api/admin/rebuild/:jobId` | admin | params `RebuildJobParams` | `RebuildJob` | `GET /v1/sup/admin/rebuild/{jobId}` | /api/admin/rebuild/{jobId} | – |
| `admin.backup` | `POST /api/admin/backup` | admin | body `Empty` | `BackupResponse` | `POST /v1/sup/admin/backup` | /api/admin/backup | backup |
| `admin.audit` | `GET /api/admin/audit` | admin | query `AdminAuditQuery` | `AdminAuditResponse` | `GET /v1/sup/admin/audit` | /api/admin/audit | – |
| `admin.compact` | `POST /api/admin/activity/compact` | admin | body `CompactRequest` | `ActivityCompactionReport` | `POST /v1/sup/admin/activity/compact` | /api/admin/activity/compact | compact |
| `admin.shardImport` | `POST /api/admin/shard/import` | admin | multipart `ShardImportOptions` | `ShardImportResult` | `POST /v1/sup/admin/shard/import` | /api/admin/shard/import (bridge sends JSON {shard: base64, …options, who}) | shard_import |
| `groom.preview` | `POST /api/groom/preview` | admin | body `GroomPreviewRequest` | `GroomPreviewResponse` | `POST /v1/groom/preview` | bridge-native | admin_other |

### Live hive (supervisor pass-through)

| Route id | Public route | Role | Request | Response | Bridge `/v1` | Supervisor | Audit |
|---|---|---|---|---|---|---|---|
| `bots.list` | `GET /api/bots` | viewer | – | `BotsResponse` | `GET /v1/sup/bots` | /api/bots | – |
| `hive.tree` | `GET /api/hive/tree` | viewer | – | `HiveTreeNode` | `GET /v1/sup/hive/tree` | /api/hive/tree | – |
| `status` | `GET /api/status` | viewer | query `StatusQuery` | `StatusResponse` | `GET /v1/sup/status` | /api/status | – |
| `events` | `GET /api/events` | viewer | query `SinceQuery` | `EventsResponse` | `GET /v1/sup/events` | /api/events | – |
| `locate` | `GET /api/locate` | viewer | query `LocateQuery` | `LocateResult` | `GET /v1/sup/locate` | /api/locate | – |
| `world.index` | `GET /api/world/index` | viewer | query `WorldIndexQuery` | `WorldIndex` | `GET /v1/sup/world/index` | /api/world/index | – |
| `world.chunks` | `GET /api/world/chunks` | viewer | query `WorldChunksQuery` | binary | `GET /v1/sup/world/chunks` | /api/world/chunks | – |
| `world.entities` | `GET /api/world/entities` | viewer | query `WorldEntitiesQuery` | `WorldEntities` | `GET /v1/sup/world/entities` | /api/world/entities | – |
| `world.overlays` | `GET /api/world/overlays` | viewer | query `DimQuery` | `WorldOverlays` | `GET /v1/sup/world/overlays` | /api/world/overlays | – |
| `world.blocks` | `GET /api/world/blocks` | viewer | query `WorldBlocksQuery` | `WorldBlockDeltas` | `GET /v1/sup/world/blocks` | /api/world/blocks | – |
| `world.blockEntities` | `GET /api/world/blockentities` | viewer | query `WorldBlockEntitiesQuery` | `WorldBlockEntities` | `GET /v1/sup/world/blockentities` | /api/world/blockentities | – |
| `bots.tile` | `GET /api/bots/:bot/tile.png` | viewer | params `BotParams`<br>query `TileQuery` | png | `GET /v1/render/tile/{bot}.png` | /view/{bot}?mode&radius&xray | – |
| `bots.now` | `GET /api/bots/:bot/now` | viewer | params `BotParams` | `BotNow` | `GET /v1/sup/viewer/now?bot={bot}` | /api/viewer/now?bot= | – |
| `bots.chat` | `POST /api/bots/:bot/chat` | chat | params `BotParams`<br>body `ChatRequest` | `ChatAccepted` (202) | `POST /v1/sup/viewer/chat` | /api/viewer/chat (bridge adds bot, player from X-MCX-Player, allow_commands from X-MCX-Roles) | chat |
| `feed` | `GET /api/feed` | viewer | query `FeedQuery` | `FeedResponse` | `GET /v1/sup/viewer/feed` | /api/viewer/feed | – |
| `blueprints.list` | `GET /api/blueprints` | viewer | – | `BlueprintsResponse` | `GET /v1/sup/blueprints` | /api/blueprints | – |
| `blueprints.get` | `GET /api/blueprints/:id` | viewer | params `BlueprintParams` | `BlueprintBill` | `GET /v1/sup/blueprints/{id}` | /api/blueprints/{id} | – |
| `buildplans.get` | `GET /api/buildplans/:id` | viewer | params `BlueprintParams` | `BuildPlanData` | `GET /v1/sup/buildplans/{id}` | /api/buildplans/{id} | – |
| `bodies.scripts` | `GET /api/bodies/scripts` | viewer | – | `LiveScriptsResponse` | `GET /v1/sup/bodies/scripts` | /api/bodies/scripts | – |
| `bodies.script` | `GET /api/bodies/:body/script` | viewer | params `BodyParams`<br>query `BodyScriptQuery` | `BodyScriptReply` | `GET /v1/sup/bodies/{body}/script` | /api/bodies/{body}/script | – |
| `bodies.controller` | `GET /api/bodies/:body/controller` | viewer | params `BodyParams` | `BodyControllerInfo` | `GET /v1/sup/bodies/{body}/controller` | /api/bodies/{body}/controller | – |
| `vision.render` | `GET /api/bodies/:body/vision/render` | viewer | params `BodyParams`<br>query `VisionRenderQuery` | png | `GET /v1/sup/bodies/{body}/vision/render` | /api/bodies/{body}/vision/render | – |
| `vision.png` | `GET /api/bodies/:body/vision/:id/image.png` | viewer | params `VisionSheetParams` | png | `GET /v1/sup/bodies/{body}/vision/{id}.png` | /api/bodies/{body}/vision/{id}.png | – |
| `vision.sheet` | `GET /api/bodies/:body/vision/:id` | viewer | params `VisionSheetParams` | `VisionSheetInfo` | `GET /v1/sup/bodies/{body}/vision/{id}` | /api/bodies/{body}/vision/{id} | – |
| `vision.list` | `GET /api/bodies/:body/vision` | viewer | params `BodyParams`<br>query `VisionListQuery` | `VisionList` | `GET /v1/sup/bodies/{body}/vision` | /api/bodies/{body}/vision | – |
| `brains.context` | `GET /api/brains/:brain/context` | viewer | params `BrainParams`<br>query `BrainContextQuery` | `BrainContextPage` | `GET /v1/sup/brains/{brain}/context` | /api/brains/{brain}/context | – |
| `brains.turn` | `GET /api/brains/:brain/turn/:n` | viewer | params `BrainTurnParams`<br>query `BrainTurnQuery` | `BrainTurnDetail` | `GET /v1/sup/brains/{brain}/turn/{n}` | /api/brains/{brain}/turn/{n} | – |

### Story Mode (supervisor pass-through)

| Route id | Public route | Role | Request | Response | Bridge `/v1` | Supervisor | Audit |
|---|---|---|---|---|---|---|---|
| `story.list` | `GET /api/story` | viewer | – | `StoryListResponse` | `GET /v1/sup/story` | /api/story | – |
| `story.recipes` | `GET /api/story/recipes` | viewer | query `StoryRecipesQuery` | `StoryRecipesResponse` | `GET /v1/sup/story/recipes` | /api/story/recipes | – |
| `story.create` | `POST /api/story/colonies` | story | body `StoryCreateRequest` | `StoryCreateResponse` | `POST /v1/sup/story/colonies` | /api/story/colonies | story |
| `story.time` | `POST /api/story/time` | story | body `StoryTimeRequest` | `StoryTimeResponse` | `POST /v1/sup/story/time` | /api/story/time | story |
| `story.directives` | `POST /api/story/directives` | story | body `StoryDirectiveRequest` | `StoryDirectiveResponse` | `POST /v1/sup/story/directives` | /api/story/directives (bridge adds player from X-MCX-Player, allow_commands from X-MCX-Roles) | story |
| `story.directives.list` | `GET /api/story/directives` | viewer | – | `StoryDirectiveList` | `GET /v1/sup/story/directives` | /api/story/directives | – |
| `story.directive` | `GET /api/story/directives/:id` | viewer | params `StoryDirectiveParams` | `StoryDirectiveDetail` | `GET /v1/sup/story/directives/{id}` | /api/story/directives/{id} | – |
| `story.get` | `GET /api/story/colonies/:id` | viewer | params `StoryColonyParams`<br>query `StoryStateQuery` | `StoryColonyState` | `GET /v1/sup/story/colonies/{id}` | /api/story/colonies/{id} | – |
| `story.release` | `POST /api/story/colonies/:id/release` | story | params `StoryColonyParams`<br>body `Empty` | `StoryAck` | `POST /v1/sup/story/colonies/{id}/release` | /api/story/colonies/{id}/release | story |
| `story.resume` | `POST /api/story/colonies/:id/resume` | story | params `StoryColonyParams`<br>body `Empty` | `StoryAck` | `POST /v1/sup/story/colonies/{id}/resume` | /api/story/colonies/{id}/resume | story |
| `story.pause` | `POST /api/story/colonies/:id/pause` | story | params `StoryColonyParams`<br>body `StoryPauseRequest` | `StoryAck` | `POST /v1/sup/story/colonies/{id}/pause` | /api/story/colonies/{id}/pause | story |
| `story.home` | `POST /api/story/colonies/:id/home` | story | params `StoryColonyParams`<br>body `StoryHomeRequest` | `StoryHomeResponse` | `POST /v1/sup/story/colonies/{id}/home` | /api/story/colonies/{id}/home | story |
| `story.beds` | `POST /api/story/colonies/:id/beds` | story | params `StoryColonyParams`<br>body `StoryBedsRequest` | `StoryBedsResponse` | `POST /v1/sup/story/colonies/{id}/beds` | /api/story/colonies/{id}/beds | story |
| `story.priorities` | `POST /api/story/colonies/:id/priorities` | story | params `StoryColonyParams`<br>body `StoryPrioritiesRequest` | `StoryAck` | `POST /v1/sup/story/colonies/{id}/priorities` | /api/story/colonies/{id}/priorities | story |
| `story.schedule` | `POST /api/story/colonies/:id/schedule` | story | params `StoryColonyParams`<br>body `StoryScheduleRequest` | `StoryAck` | `POST /v1/sup/story/colonies/{id}/schedule` | /api/story/colonies/{id}/schedule | story |
| `story.draft` | `POST /api/story/colonies/:id/draft` | story | params `StoryColonyParams`<br>body `StoryDraftRequest` | `StoryAck` | `POST /v1/sup/story/colonies/{id}/draft` | /api/story/colonies/{id}/draft | story |
| `story.orders` | `POST /api/story/colonies/:id/orders` | story | params `StoryColonyParams`<br>body `StoryOrderRequest` | `StoryAck` | `POST /v1/sup/story/colonies/{id}/orders` | /api/story/colonies/{id}/orders | story |
| `story.zones` | `POST /api/story/colonies/:id/zones` | story | params `StoryColonyParams`<br>body `StoryZoneRequest` | `StoryAck` | `POST /v1/sup/story/colonies/{id}/zones` | /api/story/colonies/{id}/zones | story |
| `story.designations` | `POST /api/story/colonies/:id/designations` | story | params `StoryColonyParams`<br>body `StoryDesignationRequest` | `StoryDesignationResponse` | `POST /v1/sup/story/colonies/{id}/designations` | /api/story/colonies/{id}/designations | story |
| `story.builds` | `POST /api/story/colonies/:id/builds` | story | params `StoryColonyParams`<br>body `StoryBuildRequest` | `StoryBuildResponse` | `POST /v1/sup/story/colonies/{id}/builds` | /api/story/colonies/{id}/builds | story |
| `story.storage` | `POST /api/story/colonies/:id/storage` | story | params `StoryColonyParams`<br>body `StoryStorageRequest` | `StoryStorageResponse` | `POST /v1/sup/story/colonies/{id}/storage` | /api/story/colonies/{id}/storage | story |
| `story.bills` | `POST /api/story/colonies/:id/bills` | story | params `StoryColonyParams`<br>body `StoryBillsRequest` | `StoryBillsResponse` | `POST /v1/sup/story/colonies/{id}/bills` | /api/story/colonies/{id}/bills | story |

### Live stream

| Route id | Public route | Role | Request | Response | Bridge `/v1` | Supervisor | Audit |
|---|---|---|---|---|---|---|---|
| `live.stream` | `GET /api/live/stream` | viewer | query `LiveStreamQuery` | sse | `GET /v1/live/stream` | bridge-native (polls status, events, viewer/feed, viewer/now, world/entities, world/blocks, hive/tree, viewer/memory-activity, story) | – |

### Design Studio (supervisor pass-through; writes need `design` or `admin`)

| Route id | Public route | Role | Request | Response | Bridge `/v1` | Supervisor | Audit |
|---|---|---|---|---|---|---|---|
| `designer.models` | `GET /api/designer/models` | viewer | – | `DesignerModelsResponse` | `GET /v1/sup/designer/models` | /api/designer/models | – |
| `designer.create` | `POST /api/designer/sessions` | design | body `DesignerCreateRequest` | `DesignerSession` | `POST /v1/sup/designer/sessions` | /api/designer/sessions | design |
| `designer.get` | `GET /api/designer/sessions/:id` | viewer | params `DesignerSessionParams` | `DesignerSession` | `GET /v1/sup/designer/sessions/{id}` | /api/designer/sessions/{id} | – |
| `designer.message` | `POST /api/designer/sessions/:id/messages` | design | params `DesignerSessionParams`<br>body `DesignerMessageRequest` | `DesignerMessageResponse` | `POST /v1/sup/designer/sessions/{id}/messages` | /api/designer/sessions/{id}/messages | design |
| `designer.canvas` | `POST /api/designer/sessions/:id/canvas` | design | params `DesignerSessionParams`<br>body `DesignerCanvasRequest` | `DesignerCanvasResponse` | `POST /v1/sup/designer/sessions/{id}/canvas` | /api/designer/sessions/{id}/canvas | – |
| `designer.save` | `POST /api/designer/sessions/:id/save` | design | params `DesignerSessionParams`<br>body `DesignerSaveRequest` | `DesignerSaveResponse` | `POST /v1/sup/designer/sessions/{id}/save` | /api/designer/sessions/{id}/save | design |
| `designer.version` | `POST /api/designer/sessions/:id/version` | design | params `DesignerSessionParams`<br>body `DesignerVersionRequest` | `DesignerVersionResponse` | `POST /v1/sup/designer/sessions/{id}/version` | /api/designer/sessions/{id}/version | design |

<!-- routes:end -->

## 5. SSE: `GET /api/live/stream`

Nest holds one upstream `GET /v1/live/stream` (signed, no identity needed beyond Nest's own) and fans it out to every browser.
The bridge polls the supervisor on loopback and emits typed events when something changed.

**Frame format** (`text/event-stream`). Nest builds frames with `formatSseFrame`:

```
event: <name>
id: <n>
data: <one-line JSON, camelCase>

```

- **`id`** is Nest's monotonically increasing counter.
- **Resume.**
  - Nest keeps the last 256 frames and replays everything after `Last-Event-ID`.
  - If that id is too old, Nest sends `status` with `resync: true` instead, and the client re-fetches its REST state.
- **Keep-alive.** `: ping` every 15 s, and `retry: 3000`.
- **Upstream vs public.** The bridge stream has the same event names and snake_case payloads. The `status` event is
  Nest-only.

**Query** (`LiveStreamQuery`). Filtering happens per browser in Nest's fan-out; the upstream carries everything.

| Key      | Meaning                                         | Default             |
| -------- | ----------------------------------------------- | ------------------- |
| `topics` | Comma list of `SseTopic`                        | all topics          |
| `bots`   | Comma list; limits `now` and `feed`             | all bots (also `*`) |
| `brain`  | Limits `memoryActivity`                         | all brains          |
| `dim`    | Limits `entities`; bots are always sent in full | all dimensions      |

**Cursors.** On (re)connect the client fetches the REST route once, then applies stream payloads whose cursor is newer than
what it holds:

| Topic            | REST route             | Cursor |
| ---------------- | ---------------------- | ------ |
| `feed`           | `/api/feed`            | `seq`  |
| `event`          | `/api/events`          | `id`   |
| `entities`       | `/api/world/entities`  | `rev`  |
| `memoryActivity` | `/api/memory-activity` | `seq`  |
| `blocks`         | `/api/world/blocks`    | `rev`  |
| `story`          | `/api/story/colonies/{id}` | `rev` per colony `id` |

| Event            | Payload schema                                                       | Bridge source and cadence                                               | Emitted                  |
| ---------------- | -------------------------------------------------------------------- | ----------------------------------------------------------------------- | ------------------------ |
| `bots`           | `StatusResponse`                                                     | supervisor `/api/status`, 1 Hz                                          | on change                |
| `event`          | `EventsResponse`                                                     | `/api/events?since=`, 2 Hz                                              | when non-empty           |
| `feed`           | `FeedResponse`                                                       | `/api/viewer/feed?since=`, 2 Hz                                         | when non-empty           |
| `now`            | `BotNow` (one frame per bot)                                         | `/api/viewer/now?bot=`, 2 Hz per bot, 1 Hz per subhive body (every online row of `/api/status`) | on change                |
| `entities`       | `WorldEntities` (delta; `full: true` means replace the set)          | `/api/world/entities?since=rev`, 5 Hz                                   | when changed             |
| `hive`           | `HiveTreeNode`                                                       | `/api/hive/tree`, every 3 s                                             | on change                |
| `memoryActivity` | `MemoryActivityResponse` (new events + the full in-context snapshot) | `/api/viewer/memory-activity?since=`, 1 Hz                              | on change                |
| `blocks`         | `WorldBlockDeltas` (per-block changes; `full: true` = refetch via index/chunks) | `/api/world/blocks?since=rev`, 5 Hz (pages while `more`)       | when non-empty or full   |
| `story`          | `StoryColonyFrame` (one colony's delta + its `id`)                   | `/api/story` every 5 s, then `/api/story/colonies/{id}?since=rev` per colony, 2 Hz, while the stream has clients | when the colony's `rev` moved |
| `status`         | `LiveStatus`                                                         | Nest-native: upstream state, bot reachability, latency                  | on change and every 15 s |

## 6. New bot routes (supervisor, snake_case)

These are the bot-side additions from architecture.md, in their wire form. They live in new files under
`src/MineClaw.App/Supervisor/`. The public camelCase schemas are named in brackets.

- **`POST /api/viewer/chat`** (`SupervisorChatBody` → `ChatAccepted`).
  - Body: `{bot, text, player, allow_commands, request_id}`.
  - It is accepted only with the supervisor token header and when `Supervisor:Viewer:Enabled` is true. `player` must be in
    `TrustedPlayers`.
  - The bot answers `{accepted, id}` with `id` = the feed seq of the `viewer` line (which carries `request_id`). The bridge
    answers 202 `ChatAccepted {accepted, id, feed_seq, note?}`: `id` echoes `request_id`, `feed_seq` is the bot's seq, so the
    client can match the `viewer` feed line and the reply.
- **`GET /api/viewer/feed?since=<seq>`** (`FeedResponse`).
  - Response: `{lines:[{seq, at, bot, kind, from, text, origin, request_id?, reply_to?}], last}`.
  - `kind` is one of chat, said, whisper, internal, muted, system, viewer.
  - It is a 1000-line ring. `at` is ISO-8601.
- **`GET /api/viewer/now?bot=`** (`BotNow`). Response:

  ```
  {bot, at, brain_state,
   in_flight{purpose, model, since},
   thought{text, thinking, at},
   tool{name, args_short, at},
   task,
   script{name, version, status, line, paused},
   body{hp, food, pos, dim, held},
   kind, owner}
  ```

  `body.pos` is canonically `[x, y, z]` numbers. The `"x,y,z"` string that `/api/status` uses is also accepted; read either
  form with `parsePos`.

  A subhive body answers too: `brain_state` reads `driven by <owner short>`, `thought`, `tool` and `in_flight` are null (its
  owner hive's brain thinks for it), and `body` and `script` come from the snapshot its hive streams to main.

- **Every body in the bot lists (added 2026-11-03).** `/api/bots`, `/api/status` `bots[]` (and the `bots` frame),
  `/api/world/entities` `bots[]`, `/api/viewer/now` and `/api/bodies/scripts` cover every body the process controls: main,
  each hive (in-process or its own process), main's own subhive and script bodies, every hive's subhive bodies (each hive
  clone streams them with its telemetry snapshot, `BotSnapshot.bodies`), and tree nodes still spawning. Additive fields:
  - `kind`: `main | hive | subhive | script`; `owner`: the owning hive's account (main's login for main's children; null for
    main); `state`: the tree state (`spawning | running | releasing`).
  - `script{name, status, line, paused, version}` on `/api/bots` and `/api/status` rows; `short` on `/api/status` rows;
    `online` on `/api/bots` and `/api/world/entities` rows (false = in the tree, not in game yet).
  - A body without a telemetry source gets a synthetic `/api/status` row (`budget_mode` Normal, `chat` [], no usage).
  - `owner` on `/api/bodies/scripts` rows.
  The web groups the hive grid by `owner` (falling back to the hive tree for an older bot).

- **World looks (added 2026-11-03).** Additive fields on `/api/world/entities` and `/api/world/blockentities`, each left
  out when absent or default (both versions where the game has the fact):
  - **Equipment glint.** An equipment slot is `[id, meta]`, `[id, meta, rgb]` (dyed), or either followed by a flags object
    when the item glints: `[id, meta, {glint: true}]`, `[id, meta, rgb, {glint: true}]` (`ItemFlags`, `EquipmentItem`;
    `equipmentGlints(slot)` reads it). A reader that only takes numbers ignores the object. 1.12.2 only: an `ench` list
    (`ItemStack.isItemEnchanted`), an enchanted book's `StoredEnchantments`, or a potion (373/438/441) whose `Potion` type or
    `CustomPotionEffects` has effects. b1.8.1 items carry no NBT (mc-dev `ItemStack` has only id, count and damage), so
    never there. Items that glint by id alone (enchanted book, nether star, bottle o' enchanting, written book, end crystal,
    the enchanted golden apple) the viewer knows itself.
  - `glint: true` on `item` and `item_frame` entities: the dropped or framed stack glints (same rule).
  - `carried: [id, meta]` on an enderman: 1.12.2 `CARRIED_BLOCK` (optional block state, id | meta << 12), b1.8.1
    metadata 16 (id) and 17 (data).
  - `look` token `holding_rose` on an iron golem: from 1.12.2 Entity Status 11 for 400 ticks (`holdRoseTick`), ended early
    by status 34. Status 4 (its attack) is a `swing` event. b1.8.1 has no iron golem.
  - `shoulders: {left?, right?}` on a 1.12.2 player: the parrot colour (`red_blue`, `blue`, `green`, `yellow_blue`,
    `grey`) from `LEFT/RIGHT_SHOULDER_ENTITY` (NBT `{id: "minecraft:parrot", Variant}`); a free shoulder is left out.
  - `arrows: N` on a living entity: 1.12.2 `ARROW_COUNT_IN_ENTITY` (b1.8.1 has no such metadata). The viewer draws them on
    players only, like vanilla's `LayerArrow`.
  - `equipment` on `/api/world/entities` `bots[]` rows of in-process bodies: their own armour and hands (same slot shape).
  - `rich` on a 1.12.2 sign whose JSON text has styles: four lines of runs `{text, color?, bold?, italic?, underlined?,
    strikethrough?, obfuscated?}` (`SignRun`), built from the components, their `extra` and § codes in the text, read like
    `FontRenderer` (a colour resets the styles; no colour = the sign's black). `lines` stays plain for older readers.
  - Still not on the wire: player skins and capes (offline server). Horse coats and villager professions are always sent on
    1.12.2 (`look`); b1.8.1 has neither mob.

- **`GET /api/viewer/memory-activity?since&brain`** (`MemoryActivityResponse`). Response:

  ```
  {events:[{seq, at, brain, body, memory_kind, memory_id, raptor_node_id|null, source: rag|tool|search|recall|other,
            score|null, turn|null}],
   in_context:[{brain, memory_kind, memory_id, raptor_node_id|null, added_at, last_seen_at, turns_in_context,
                position_from_end|null}],
   last}
  ```

  `position_from_end: null` means the memory was evicted.

- **Bridge-native heat** (`MemoryHeatResponse`): `GET /v1/memory-activity/heat?brain&window_s`. The bridge aggregates the
  ring over the RAPTOR tree:
  - **Ids.** Node ids are the same as `/api/raptor/graph` node ids, and `stateKey` matches `RaptorGraph.stateKey`.
  - **Coverage.** It covers the leaf and its ancestors, with heat max-propagated up the tree.
  - **Per node.** Each node carries `{node_id, last_access_at, access_count, heat (0..1), brains:[{brain, last_access_at,
in_context, staleness_s, evicted}]}`.
  - **Decay.** `heat = 0.5^((at − last_access_at)/half_life_s)`. The client re-decays between refreshes with `decayHeat`.

- **Story Mode (added 2026-11-03).** The bot's routes and every wire shape are in `docs/web/story-mode.md` ("Supervisor
  routes", "Designations", "Construction", "Storage", "Workbenches & bills", "AI hives"); the schemas are `story.ts` in the
  contracts (lenient on responses: designations may carry `target`/`blocks` or `block`/`targets`, order and job targets may be
  `{x,y,z}`, `[x,y,z]` or `"x,y,z"`; read them with `designationCells`, `designationEntity`, `targetPos`). What the web
  relies on:
  - **Deltas.** `GET /api/story/colonies/{id}?since=rev`: only what changed, every pawn, and `removed: {zones, designations,
    builds, storages}` (storages as `"x,y,z"` keys). No `since` (or `full: true`) replaces the whole state. `ghosts` and
    `categories` replace when present; `alerts` append (de-duplicated). The SSE `story` frame is the same delta plus the colony
    `id`; the client also catches up with `since=rev` every 3 s and refetches in full every minute.
  - **Bills** use `{op:"add", bench, bill}`, `{op:"update", id, …bill fields}`, `{op:"remove"|"pause"|"resume", id}`,
    `{op:"reorder", bench, ids}`. `bill` is an opaque key for case conversion only in responses (a blueprint's material
    map): request bodies convert with `REQUEST_OPAQUE_KEYS`, so a bill's `minSkill` reaches the bot as `min_skill`.
  - **Directives** (`POST /api/story/directives`): the public body is `{bots, directive, requestId}`; the bridge adds `player`
    from `X-MCX-Player` (403 when empty) and `allow_commands` from `X-MCX-Roles` (`commands`), and refuses a body that
    carries either (400), as for viewer chat. `GET /api/story/directives[/{id}]` reads them back.
  - **Release / resume.** `POST …/release` keeps the colony (`active:false`); `POST …/resume` spawns its pawns again.
  - **Unsupported benches** (`supported:false`: brewing stands, anvils, enchanting tables) show without the Add bill button.
  - **Designs** (`/story?design=N`): the web reads the design's blocks from `/api/blueprints/{id}` (or `/api/buildplans/{id}`),
    lets the user place the ghost, and queues it as `block` build items with explicit blocks, one per material.

### Design Studio (`/api/designer/*`, added 2026-11-03)

The bot's sandbox canvas for co-designing a build with a model (src/MineClaw.App/Supervisor/DesignerRoutes.cs, the canvas
and tools in src/MineClaw.Bot/Blueprints/Designer*.cs). Never the live world. Behind `Supervisor:Viewer:Enabled`,
`Supervisor:Viewer:Designer:Enabled` (default true) and the token header. JSON is snake_case; schemas in contracts `designer.ts`.

- **Canvas.** Local coordinates x, z 0..63 (+x east, +z south) and y -16..47. y 0 is the first layer above the plot's
  grass; y -1 is the grass layer and y -16..-1 is the ground under it, solid earth until it is dug. A cell is
  `[x, y, z, id, meta]` in the session version's ids: id 0 = air (below y 0 that digs the cell out), id -1 = undisturbed
  ground again (fills a dug cell back; below y 0 only). The session lists `blocks` and `dug` (`[[x,y,z]]`, the cells below
  ground dug out to air); `plot` is `{size, height, depth}`. Any state of the version's design palette is accepted (below).
  Two-cell blocks are kept whole: a door or a double plant as two halves (upper = lower | 8, above it), a bed as foot + head
  (head = foot | 8, one step along its facing S W N E); replacing either half clears the other. A chest placed beside a single
  chest of its kind takes that chest's facing when it is across the pair (they pair, as the game pairs them). The browser's
  canvas applies the same rules, so both stay in step.
- **Versions (added 2026-11-03).** A session is drawn for one game version, `b1.8.1` or `1.12.2` (`version`; the server's is
  `server_version`, `versions` lists both). A new canvas starts in the server's version (or `version` in the create body);
  a design opens in the version it was saved for. Every save records the session's version in the design's DSL as a
  `version X` line, so every revision, the bridge's design list/detail (`version` per design and revision) and every builder
  see it. Migration rule: a design (or a session) saved before versions has none and is the version of the server whose db
  holds it (one bot db has only ever served one version); a DSL without a `version` line also keeps the legacy palette
  (only `BuildBlocks` blocks), a `version` line opens that version's whole design palette.
- **The palette (added 2026-11-03).** `palette` is every placeable block state of the session's version
  (src/MineClaw.Bot/Blueprints/DesignPalette.cs, curated from minecraft-data pc/1.12.2 blocks + variations and the b1.8.1
  table): 398 entries for 1.12.2, 120 for b1.8.1, one per variant (every colour of wool, carpet, stained glass and panes,
  terracotta, concrete and powder, glazed terracotta, shulker boxes; every wood of planks, logs, slabs, stairs, fences,
  gates, doors, saplings, leaves; every stone, sandstone, stone brick, quartz and prismarine kind; plants, crops, redstone,
  rails, beds, signs, banners, skulls, liquids' sources). Each is `{id, meta, name, facing, spec, cat, family, icon}`: `spec`
  the canonical spelling ("wool:red", "spruce_door", "stained_glass:light_blue"), `cat` one of `categories` (Building,
  Decoration, Colored, Nature, Redstone, Transport, Misc: the creative tabs), `family` its facing rule (stairs, slab, log,
  door, tall, bed, torch, wall, front, facing6, hopper, horizontal, gate, trapdoor, button, lever, rail, poweredrail,
  rotation16, skull, vine, age…), `icon` an icon hint (1.12.2: an item model, b1.8.1: "item:N"). Facings, halves and
  runtime states are metas of an entry, all accepted (stairs 0..7 in 1.12.2, slabs + 8 top, log axes…). Left out (refused
  with the reason): air; flowing water and lava (place a source); piston heads and moving pistons; fire; portals, end
  portals and gateways; spawners, bedrock, silverfish blocks, end portal frames, chorus plants (unobtainable); command,
  structure and structure-void blocks, barriers (operator-only); frosted ice; the technical double slabs whose full block
  exists (double wooden / red sandstone / purpur slabs, double stone slab metas but 0 and 8 "smooth stone"; 1.12.2's
  petrified oak slab 44:2); runtime states (lit furnace, lit redstone ore, lit redstone lamp, unlit redstone torch, powered
  repeater and comparator). b1.8.1 also leaves out what it can't obtain: grass, mycelium-like silk-touch blocks (ice, huge
  mushroom blocks, ores that drop items: coal, lapis, diamond, redstone), leaves (placed leaves decay), cobwebs (shears give
  string) and its technical locked chest.
- **Version compatibility (added 2026-11-03).** `POST sessions/{id}/version {version, apply?}` →
  `{version, target, changed, report?, session?}`: an empty canvas switches at once (`changed`, the new `session`); a full one
  is checked (`report`: `{from, to, same, compatible, exact, text, substituted:[{name, count}], missing:[{name, count}]}`) and,
  with `apply`, converted (the missing blocks left out, the model told, an assistant line in the transcript). The mapping
  (DesignConvert): 1.12.2's ids are a superset of b1.8.1's with the same numbers and metas, so a state carries over when the
  target's palette has it, except id 95 (b1.8.1's locked chest, 1.12.2's stained glass: never carried over as is) and
  b1.8.1's wooden slab 44:2 = 1.12.2's oak slab 126:0. Back to b1.8.1, the nearest block substitutes where one is close
  (woods → oak, stone/sand/sandstone/stone-brick kinds and grass/podzol/coarse dirt → their plain block, stained glass and
  panes → glass, top halves and upside-down stairs → bottom ones, other woods' stairs/fences/gates/doors → oak's, wooden
  buttons → stone, trapped chests → chests, iron trapdoors → trapdoors); everything else (concrete, terracotta, quartz…) is
  missing. `POST sessions {design_id, convert_to}` opens a design converted to a version with the report as `compat` (Story
  Mode does this for the server's version and refuses a design whose `compat` is not `compatible`, naming the blocks); a
  session drawn for another version than the server's carries `server_compat` (how it would build here).
- **Builds of a versioned design.** `BlueprintService.PlanOf` (every builder: `design build`, the crew, revert): a design
  drawn for another version is converted to the world's (`VoxelPlan.Converted`, said in the build's lines) or refused when a
  block of it doesn't exist here (`design build` answers "#12 "hut" is drawn for 1.12.2; these blocks don't exist in b1.8.1:
  12 White Concrete … (open it in the Design Studio and convert it, or pick other blocks)"); then the cells the survival
  builder can't place yet (not `BuildBlocks` states: beds, redstone, upside-down stairs, new woods…) are set aside as
  `VoxelPlan.Unsupported` and reported ("4 cells the builder can't place yet, left out: 2 Bed, 2 Redstone Wire"), never
  placed wrong. Story's build queue gives each design cell the item that places exactly it (red wool, a spruce door's item)
  and keeps blocks pawns can't place yet (crops, liquids, signs, banners, skulls) as `unsupported` ghosts with an alert.
- **Attached blocks (added 2026-11-03).** Wall torches and ladders hang on a full block: every apply (the AI's ops and the
  user's) checks the changed ones and the ones next to a changed cell; one whose wall side is open but whose opposite side
  is solid is turned around, a wall torch with neither but a solid block under it stands on the floor (meta 5), anything
  else is left for the design checks ("torch … has no wall or floor"). The AI's tool result says what was corrected; the
  canvas route returns the user's corrected cells as `fixed` (the browser applies them), and a message's corrections of
  pending edits come first in its `ops` (kind `fix`). Opening a session or loading a design settles the whole canvas the
  same way (the browser starts from the corrected canvas: not an unsaved change; the design gets it on the next save; the
  model hears about it with the next message). The model spells them by the side the supporting block is on:
  `torch:wall=west` (or `torch:on=west`, `torch:west`) hangs on the block to its west, `torch:floor`, `ladder:wall=south`;
  numeric metas still work.
- **`GET models`** → `{models:[{id, label, provider, model, tier, profile, cost_hint, default}], default, version, budget}`:
  every tier model (`main`, `worker`, `cheap`) and every usable profile (`profile:<name>`, Ollama/DeepSeek too), de-duplicated;
  a Claude model outside the 5.5 generation is never offered.
- **`POST sessions {model?, design_id?, version?, convert_to?}`** → the session `{id, owner, model, design_id, name, rev, blocks,
  dug, anchor, messages, cost_usd, calls, version, server_version, versions, palette, categories, compat, server_compat, plot,
  revisions}` (each revision with its `version`): an empty canvas in `version` (default the server's), or the design's newest
  revision centred on the plot in its own version (a design that digs: its `ground N` layer on y 0, its forced air under it
  as dug cells); `convert_to` converts it (see "Version compatibility").
- **`POST sessions/{id}/messages {text, model?, canvas:{rev, edits?}, selection?:{min, max}}`** → `{reply, ops:[{kind, label,
  by, cells}], rev, cost_usd, session_cost_usd, model, calls, usage, message, user_message, refusal}`. Pending user edits are
  applied first and summarised for the model, with the canvas summary and the selection. The model edits through seven
  tools (place, box fill/hollow/walls, line, remove, replace, clear, describe; remove below y 0 digs, the block "earth" fills
  back) and looks names up with `find_blocks {q, n?}` ("magenta concrete" → `concrete:magenta = Magenta Concrete (Colored)`)
  for up to `MaxRounds` calls. Its blocks are any of the session version's design palette, by minecraft name, display
  name, `name:variant`, `variant_name` or `name:meta`, then facing modifiers (`oak_stairs:north:top`, `bed:north`,
  `torch:wall=west`, `log:spruce:x`); a wrong name answers "unknown 1.12.2 block 'redwol'; did you mean wool:red …?". The
  prompt carries a compact category summary of the palette (about 200 tokens: the base names per category, `<colour>_` and
  `<wood>_` families folded, the colour and wood words), never the whole list; the whole turn runs in the session's version.
  The system prompt
  and tools are cached, history is the last `HistoryTurns` exchanges as plain text. Spend is recorded on the bot's
  `UsageTracker` (the shared daily/monthly caps; `designer` purpose rollup); a paused budget answers 429, the cheap-only
  stage refuses Claude models above the memory tier, and a message stops at `MaxUsdPerMessage`. A second write while a
  message runs is 409 (the bridge maps it to a retryable 429).
- **`POST sessions/{id}/canvas {rev, ops:[{kind, label, cells}], anchor?, clear_anchor?}`** → `{rev, applied, stale}`: the
  user's own edits (manual tools, undo of AI ops), and the anchor ("move origin").
- **`POST sessions/{id}/save {name, summary?, as_new?}`** → `{design_id, rev, name, blocks, created, text}`. The canvas is
  drawn as DSL exactly like a capture (`DesignHistory.Draw`, interior air between each column's lowest and highest block),
  saved as an accepted design with no site (the bot builds a copy where asked); dug cells are drawn as forced air '.', undug
  ground under y 0 as '_' (left as it is), and when anything is below y 0 the DSL says `ground N` (the layers under the
  canvas's y 0: a site puts layer N one above its ground block, so the build digs the basement and places its blocks into
  the terrain). Its revision is added through
  `DesignHistory.Sync` with the signed-in player as author: a new design, or the next revision of the loaded one (Git-style,
  never locked). Over 32 a side is refused.
- **Bridge.** `/v1/sup/designer/*` passes reads for any signed role; every POST needs `design` or `admin`, and the bridge
  writes `player` (the linked player) and `who` ("user (web)") into the body, refusing a body that sets them.
- **Persistence.** `designer_sessions` and `designer_messages` in the agent db.

## 7. Binary formats

- **`/api/world/chunks`** (`application/octet-stream`, "MCWC"). It is little-endian, and Nest streams it unchanged:

  ```
  "MCWC"  u16 version(1|2)  u16 flags(0)  i32 dim  i32 count(<=1024; server caps a request at 64)
  count × { i32 cx, i32 cz, i64 hash, i64 seen_unix_ms, u8 live(0|1), u8 by_len, by_len×utf8,
            i32 data_len, data_len×byte            // data_len 0 = column unknown → drop it
            [v2 only: i32 light_len, light_len×byte] }   // light 0 = none (remembered column)
  ```

  - `data` is raw DEFLATE (no zlib header) of `ChunkSnapshot`.
    - It holds V block-id bytes, then V/2 bytes of meta nibbles, where V = 16·H·16 and H is 128 (b1.8.1) or 256 (1.12.2).
    - Cell index: `idx(lx,y,lz) = (y>>7)<<15 | (lx*16 + lz)*128 + (y & 127)`.
  - `light` is raw DEFLATE of `LightSnapshot`:
    - a header of `u8 version(1) u8 sections u8 flags(1=biomes)`
    - then per 16³ section a `u8 kind`: 0 = uniform + `u8 value`, or 1 = full + 4096 bytes of `sky<<4 | block` in the order
      `lx<<8 | lz<<4 | (y&15)`
    - then, when flagged, 256 biome bytes
  - Trailing bytes or truncation are errors. A column blob is at most 256 KB.
  - Full detail: `docs/web/reference/explorer-spec.md` §13.2.

- **PNG routes** (`bots.tile`, `designs.picture`, `vision.png`, `vision.render`). Vision and render replies
  carry `X-Sha256` / `X-Tokens` / `X-Width` / `X-Height`. `vision.render?meta=1` returns JSON `ViewMeta` instead.
- **`/api/shards/export`.** It returns `application/zip` with `Content-Disposition: attachment;
filename="<slug>-<yyyyMMdd-HHmm>.mcshard"`. The format is `ShardFile` FormatVersion 1 (explorer-spec §8):
  - `manifest.json` (`ShardManifest`)
  - `records/{kind}.jsonl`
  - `embeddings.bin`
  - `summaries.jsonl`
- **Shard uploads** (`shards.importPreview`, `admin.shardImport`). They are `multipart/form-data` with:
  - `shard`: the file
  - `options`: the JSON of `ShardImportOptions`

  Nest caps the upload at 48 MB.

## 8. Ambiguities in architecture.md and how this contract resolves them

| #   | Topic                                | Resolution                                                                                                                                                                                                                                              |
| --- | ------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 1   | Role hierarchy                       | Any role implies read. `chat`, `commands` and `admin` are independent, and `commands` needs `chat` to matter (`hasRole`).                                                                                                                               |
| 2   | CSRF before login                    | `GET /api/auth/csrf` creates an anonymous pre-session, so the "every non-GET needs X-CSRF-Token" rule also covers login. Login rotates the session and the token.                                                                                       |
| 3   | Exact HMAC encoding                  | §3 fixes it: unix seconds, a 32-hex nonce, lowercase hex body hash and signature, the UTF-8 key string, the raw request target, canonical sorted roles, and `""` for an absent identity header.                                                         |
| 4   | Watched bots for `now`               | The bridge polls `viewer/now` for every online bot at 2 Hz (hives are small), and each browser filters with `?bots=`. The alternative, a watch list sent upstream, would force Nest to reconnect its single stream.                                     |
| 5   | Admin GETs through the pass-through  | The allow-list said "GET only" plus "POST admin/*". `GET admin/rebuild/{id}` and `GET admin/audit` are allowed too, behind the admin role.                                                                                                              |
| 6   | `memory-activity` pass-through       | `viewer/memory-activity` was added to the GET allow-list.                                                                                                                                                                                               |
| 7   | i64 world hashes                     | They are strings on the public API, via `parseBridgeJson`, because JSON.parse would lose precision past 2^53.                                                                                                                                           |
| 8   | Opaque maps during case conversion   | The `OPAQUE_KEYS` list (§1) applies. Script params and blackboards keep their own keys.                                                                                                                                                                 |
| 9   | Routes the architecture did not list | These bridge routes are added: `/v1/raptor/node/{id}/leaves` ("show leaves under"), `/v1/shards/export/preview` (the export dialog's preview), `/v1/designs/{id}/picture.png`, `/v1/memory-activity/heat`, `/v1/groom/preview` (phase 2), `/v1/health`. |
| 10  | Script source                        | One public route takes `source=live                                                                                                                                                                                                                     | db`. The bridge's `/v1/scripts*` picks the supervisor (live) or its own db reader (db). |
| 11  | Import preview                       | It is bridge-native (`ShardImporter.Preview` on the read-only db), as the architecture lists. Apply goes to the bot (`/api/admin/shard/import`). The supervisor's own preview route is not exposed.                                                     |
| 12  | Vision PNG path                      | The public path is `/api/bodies/:body/vision/:id/image.png`, so it cannot collide with `/vision/:id`. The bridge path keeps the supervisor's `{id}.png`.                                                                                                |
| 13  | `BotNow.body.pos`                    | Canonically a number array; the string form is accepted.                                                                                                                                                                                                |
| 14  | `FeedLine` correlation               | Optional `request_id` on viewer lines and `reply_to` on replies were added, so the composer's pending state can resolve. `ChatAccepted.id` echoes the request id.                                                                                       |
| 15  | Export permission                    | Shard export (preview and download) is `viewer`, because it is a read: secrets are redacted and the export is audited. Import preview and apply are `admin`.                                                                                            |
| 16  | Topology (2026-11-02)                | Everything runs on CT 114. Nest ↔ bridge is plain HTTP on loopback with no cert pin, and the bridge accepts loopback sources only. HMAC is unchanged. Cloudflare client-IP trust comes from `Web:TrustedProxies`.                                       |
