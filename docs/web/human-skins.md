# Human browser player skins

Administration → Player skins enables account skins in Controller and all browser world viewers. It is off by default. The setting applies without a bot restart. Accounts that cannot be resolved to a Minecraft profile keep the normal default player appearance, including offline bot account names with no matching profile.

The web gateway reads current player names on initialization, observes joins in entity snapshots and live events, and checks for additional players every 30 seconds even without a browser connected. It resolves each name once per web-service initialization, including unsuccessful lookups. Downloads run four at a time, with at most 512 distinct names, five-second request deadlines and 64 KiB response bounds. Only Mojang's profile/session APIs and HTTPS `textures.minecraft.net/texture/<hash>` are allowed; redirects are refused. Skins must be 64×32 or 64×64 PNGs.

Only the boolean setting is saved in `human-skins.json` in the web data directory. Skin bytes, profiles, failures and in-flight requests remain in memory. A web-service restart creates an empty cache and a new epoch. Disabling skins immediately stops the gateway from serving skin bytes; browser views revert within the next five-second poll. Re-enabling in the same initialization reuses the cache. API and browser requests use `no-store`; the PWA never caches API responses.

## AI vision boundary

Skins are a web-gateway-native resource at authenticated `GET /api/human-skins`. This route has no bridge or supervisor mapping. Skin bytes and URLs are never added to world/entity snapshots, SSE events, bot state, local texture packs, design files, audit entries or AI tools. The browser's optional shared skin atlas is configured only at the signed-in web application's lazy viewer boundary, and defaults to no skin provider in other consumers.

Bot orientation images still come from the separate C# `MineClaw.Render` / `EyeRenderer` / `VisionRenderer` pipeline. That pipeline has no dependency on this service and continues using the bundled/default player texture. The browser's `capturePng` call is used for designer previews, which contain design blocks and no player entities; it is not the AI vision endpoint. Human screenshots of a world view can contain skins, as expected, but are never substituted for the bot's orientation render.

Regression coverage checks default-off behavior, request deduplication, failure caching, initialization epochs, trust/size restrictions, authenticated native routing, admin step-up/CSRF/auditing, and browser disable/re-enable/restart behavior. `npm run test:skins -w @mineclaw/world-viewer` additionally renders slim 64×64 and legacy 64×32 synthetic skins in an isolated headless browser and verifies that disabling skins restores the default texture. Tests use synthetic images and fake responses, not committed player skins.
