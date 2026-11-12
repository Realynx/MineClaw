# Human browser player skins

Administration → Player skins enables account skins in Controller and all browser world viewers. It is off by default. The setting applies without a bot restart. Accounts that cannot be resolved to a Minecraft profile keep the normal default player appearance, including offline bot account names with no matching profile.

The web gateway reads current player names on initialization, observes joins in entity snapshots and live events, and checks for additional players every 30 seconds even without a browser connected. It resolves each name once per web-service initialization, including unsuccessful lookups. Downloads run four at a time, with at most 512 distinct names, five-second request deadlines and 64 KiB response bounds. Only Mojang's profile/session APIs and HTTPS `textures.minecraft.net/texture/<hash>` are allowed; redirects are refused. Skins must be 64×32 or 64×64 PNGs.

For official skins, only the boolean setting is saved in `human-skins.json` in the web data directory. Official skin bytes, profiles, failures and in-flight requests remain in memory. A web-service restart creates an empty cache and a new epoch. Disabling skins immediately stops the gateway from serving skin bytes; browser views revert within the next five-second poll. Re-enabling in the same initialization reuses the cache. API and browser requests use `no-store`; the PWA never caches API responses.

## Pawn skins (Skin Browser)

Controller → click a pawn in the colonist bar → **Skin Browser** (next to Orders) chooses what that pawn looks like in Controller and
every other browser world view. The game server runs in offline mode, so this cannot change what Minecraft clients see; the tab
says so. The sources:

- **Library**: skins uploaded or looked up before, shared by all pawns (one upload can dress several pawns). Searchable; each tile
  shows the layout (64×32, 64×64, slim) and how many pawns wear it. Deleting a skin puts its wearers back on the default appearance.
- **Defaults**: Steve and Alex, drawn from the game assets the user imported in this browser (Settings → game assets). The server
  never stores or serves these textures; without an import the tiles have no preview and say how to get one. b1.8.1 has only
  `mob/char`, so its world views draw Alex as Steve.
- **Upload**: a PNG of 64×32 (b1.8.1 layout) or 64×64 (1.12.2 layout), at most 64 KiB. The browser checks the signature, header size
  and that it decodes, guesses slim arms from the empty arm column (the owner can change it), and shows a flat front preview before
  "Add to library" or "Upload and apply".
- **Look up**: copies a player's official skin into the library through the same server-side Mojang client as above (profile and
  session APIs, `textures.minecraft.net` only, no redirects, bounded responses). The browser never contacts Mojang. It answers 409
  while Administration → Player skins is off, so lookups stay optional.

Every pick is previewed (flat front view on a 2D canvas; no extra WebGL context) and applied with "Apply to <pawn>"; "Use default
appearance" clears it.

**Storage.** `<web data dir>/skins/pawn-skins.json` holds the library metadata (label, slim, size, source, who added it, when) and
the assignments (lower-cased account name → `{kind:"library", id}` or `{kind:"default", id:"steve"|"alex"}`); each skin is
`skins/<id>.png`, where the id is the first 24 hex digits of the SHA-256 of the stored PNG, so the same image is kept once. The
server re-validates every PNG (signature, chunk CRCs, IHDR 64×32/64×64, image data inflates to exactly the expected size) and keeps
only IHDR, PLTE, tRNS, IDAT and IEND, dropping text, EXIF and colour-profile metadata. The library holds at most 128 skins and
512 assignments. Choices survive reloads and web-service restarts.

**Routes** (Nest-native, never bridged): `GET /api/skins` (viewer) lists the library and assignments; `POST /api/skins` (upload),
`/api/skins/delete`, `/api/skins/assign` and `/api/skins/lookup` need the Story Mode role (or admin), CSRF and an allowed Origin,
and are audited as `story` with labels, ids and pawn names only (never image data).

**World views.** Pawn skins travel in the existing `GET /api/human-skins` feed beside official skins, served whether or not the
admin setting is on (they come from this server, not Mojang). A pawn's chosen skin wins over its official one. A default-skin row
has `base: "steve" | "alex"` and an empty `png`; the viewer then uses the pack's own texture and model (`entity/alex` with slim arms
in 1.12.2). The feed's epoch is `<initialization>.<setting>.<assignment generation>`, so any change restarts every view's skin
atlas; after "Apply" the Controller asks the viewer to poll at once instead of waiting for the five-second poll. Mock mode serves the
same feed from `mocks/skins.ts` (sessionStorage).

## AI vision boundary

Skins are a web-gateway-native resource at authenticated `GET /api/human-skins`. This route has no bridge or supervisor mapping. Skin bytes and URLs are never added to world/entity snapshots, SSE events, bot state, local texture packs, design files, audit entries or AI tools. The browser's optional shared skin atlas is configured only at the signed-in web application's lazy viewer boundary, and defaults to no skin provider in other consumers.

Bot orientation images still come from the separate C# `MineClaw.Render` / `EyeRenderer` / `VisionRenderer` pipeline. That pipeline has no dependency on this service and continues using the bundled/default player texture. The browser's `capturePng` call is used for designer previews, which contain design blocks and no player entities; it is not the AI vision endpoint. Human screenshots of a world view can contain skins, as expected, but are never substituted for the bot's orientation render.

Regression coverage checks pawn-skin validation, storage, restarts, auth and feed epochs (`apps/api/test/skin-library.test.ts`), the Skin Browser's checks and selection (`apps/web/test/skinBrowser*.test.ts*`, `e2e/storySkins.spec.ts`), default-off behavior, request deduplication, failure caching, initialization epochs, trust/size restrictions, authenticated native routing, admin step-up/CSRF/auditing, and browser disable/re-enable/restart behavior. `npm run test:skins -w @mineclaw/world-viewer` additionally renders slim 64×64 and legacy 64×32 synthetic skins in an isolated headless browser and verifies that disabling skins restores the default texture. Tests use synthetic images and fake responses, not committed player skins.
