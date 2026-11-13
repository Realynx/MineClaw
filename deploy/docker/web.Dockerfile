# syntax=docker/dockerfile:1.7
# ghcr.io/realynx/mineclaw-web: the web explorer. Nest (web/apps/api) on 0.0.0.0:8080 in front of the Next standalone
# server (web/apps/web) on 127.0.0.1:3000; /app/start.mjs runs both and exits when either exits.
# It talks to the bridge on 127.0.0.1:8766, so it must share the server container's network namespace
# (docker-compose.yml: network_mode "service:server"; Helm: the same Pod).
#
# Build context: see server.Dockerfile (this one copies <ctx>/dist/<amd64|arm64>/mineclaw-web/ and <ctx>/start.mjs).
#   docker buildx build <ctx> -f <ctx>/web.Dockerfile --platform linux/amd64,linux/arm64 \
#     --build-arg VERSION=1.2.3 --build-arg COMMIT=<sha> -t ghcr.io/realynx/mineclaw-web:1.2.3
#
# Settings: any Nest key as env MINECLAW_WEB_<Section>__<Key> (deploy/viewer/web/README.md). The ones to set per install:
#   MINECLAW_BRIDGE_KEY                    the HMAC key shared with the bridge (>= 32 chars), or MINECLAW_BRIDGE_KEY_FILE
#   MINECLAW_WEB_Web__AllowedOrigins       every URL people browse to, comma-separated (CSRF/Origin check). start.mjs
#                                          defaults it to http://localhost:8080,http://127.0.0.1:8080 when unset.
#   MINECLAW_WEB_Web__TrustProxy=true + MINECLAW_WEB_Web__TrustedProxies=<proxy IPs/CIDRs>  behind a TLS reverse proxy, so
#                                          X-Forwarded-Proto/For count (CookieSecure "auto" then sets Secure cookies).

ARG BASE=node:22-bookworm-slim

# /var/lib/mineclaw-web owned by node (1000), made natively on the build platform (no QEMU) so a fresh volume is writable.
FROM --platform=$BUILDPLATFORM ${BASE} AS dirs
RUN mkdir -p /out/data && chown 1000:1000 /out/data && chmod 0700 /out/data

FROM ${BASE}
ARG TARGETARCH
ARG VERSION=0.0.0-dev
ARG COMMIT=unknown

LABEL org.opencontainers.image.title="MineClaw web" \
      org.opencontainers.image.description="MineClaw web explorer (Nest gateway + Next UI)" \
      org.opencontainers.image.source="https://github.com/Realynx/MineClaw" \
      org.opencontainers.image.url="https://github.com/Realynx/MineClaw" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${COMMIT}"

COPY --link dist/${TARGETARCH}/mineclaw-web/ /app/
COPY --link --chmod=0644 start.mjs /app/start.mjs
COPY --from=dirs --chown=1000:1000 /out/data/ /var/lib/mineclaw-web/

# Web data: web.db (sessions, login counters, audit), users.json (accounts; must persist), update cache, HTTPS settings.
ENV NODE_ENV=production \
    NEXT_TELEMETRY_DISABLED=1 \
    MINECLAW_INSTALL_KIND=docker \
    MINECLAW_VERSION=${VERSION} \
    MINECLAW_WEB_Web__DataDir=/var/lib/mineclaw-web \
    MINECLAW_WEB_Web__UsersFile=/var/lib/mineclaw-web/users.json \
    MINECLAW_WEB_Web__Bind=0.0.0.0 \
    MINECLAW_WEB_Web__Port=8080

VOLUME ["/var/lib/mineclaw-web"]
WORKDIR /app
USER 1000:1000
EXPOSE 8080
ENTRYPOINT ["node", "/app/start.mjs"]
