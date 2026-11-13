# syntax=docker/dockerfile:1.7
# ghcr.io/realynx/mineclaw-server: the bot / control server (/app/server) AND the viewer bridge (/app/bridge).
# Default command: the bot, headless. `bridge` as the command runs the bridge instead (docker-compose.yml, the Helm chart).
#
# Nothing is compiled here: the image only copies the per-arch release trees the release workflow already built, so a
# multi-arch build needs no RUN under QEMU. Build context (deploy/release/stage-docker.sh lays it out):
#
#   <ctx>/server.Dockerfile  web.Dockerfile  entrypoint.sh  start.mjs     (copied from deploy/docker/)
#   <ctx>/dist/amd64/mineclaw/          linux-x64 server tree   (out/stage/linux-x64/mineclaw)
#   <ctx>/dist/amd64/mineclaw-bridge/   linux-x64 bridge tree   (out/stage/linux-x64/mineclaw-bridge)
#   <ctx>/dist/amd64/mineclaw-web/      linux-x64 web tree      (out/stage/linux-x64/mineclaw-web)
#   <ctx>/dist/arm64/...                the same for linux-arm64
#
# The executables (mineclaw/MineClaw.App, mineclaw-bridge/MineClaw.ViewerBridge) must carry their exec bit in the context
# (artifact up/downloads drop it: chmod +x when staging).
#
#   docker buildx build <ctx> -f <ctx>/server.Dockerfile --platform linux/amd64,linux/arm64 \
#     --build-arg VERSION=1.2.3 --build-arg COMMIT=<sha> -t ghcr.io/realynx/mineclaw-server:1.2.3

ARG BASE=mcr.microsoft.com/dotnet/runtime-deps:10.0

# The writable dirs, owned by app (1654), made on the BUILD platform (native, no QEMU) and copied in: Docker seeds a fresh
# named volume from the image, so the bot can write to /var/lib/mineclaw and /var/log/mineclaw from the first start.
FROM --platform=$BUILDPLATFORM ${BASE} AS dirs
RUN mkdir -p /out/data /out/log /out/config \
 && chown 1654:1654 /out/data /out/log \
 && chmod 0750 /out/data /out/log \
 && chmod 0755 /out/config

FROM ${BASE}
ARG TARGETARCH
ARG VERSION=0.0.0-dev
ARG COMMIT=unknown

LABEL org.opencontainers.image.title="MineClaw server" \
      org.opencontainers.image.description="MineClaw bot / control server and viewer bridge (headless)" \
      org.opencontainers.image.source="https://github.com/Realynx/MineClaw" \
      org.opencontainers.image.url="https://github.com/Realynx/MineClaw" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${COMMIT}"

COPY --link dist/${TARGETARCH}/mineclaw/ /app/server/
COPY --link dist/${TARGETARCH}/mineclaw-bridge/ /app/bridge/
COPY --link --chmod=0755 entrypoint.sh /app/entrypoint.sh
COPY --from=dirs --chown=1654:1654 /out/data/ /var/lib/mineclaw/
COPY --from=dirs --chown=1654:1654 /out/log/ /var/log/mineclaw/
COPY --from=dirs /out/config/ /etc/mineclaw/

# MINECLAW_Agent__Rag__SeedFile: the clean-state seed ships in the image; the bot bootstraps mineclaw.db from it on the
# first start (an existing db is never touched). The Bridge keys only matter to the bridge (the bot ignores that section).
ENV MINECLAW_INSTALL_KIND=docker \
    MINECLAW_VERSION=${VERSION} \
    MINECLAW_Agent__KnowledgeDirectory=/app/server/docs/knowledge \
    MINECLAW_Agent__Rag__SeedFile=/app/server/seed/mineclaw-seed.db \
    MINECLAW_Bridge__DataDir=/var/lib/mineclaw \
    MINECLAW_Bridge__PacksDir=/app/bridge/shards/defaults \
    MINECLAW_Bridge__AuditLog=/var/log/mineclaw/bridge/audit.jsonl \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=

# ASPNETCORE_HTTP_PORTS: the .NET base image sets it to 8080, which is the web's port in the shared network namespace; the
# bridge listens only where Bridge:Bind/Port say (127.0.0.1:8766).

# /var/lib/mineclaw: db, seed copy, usage, and settings.override.json (what the web's Administration page saves).
# /etc/mineclaw: optional appsettings.json (read-only is fine). /var/log/mineclaw: text logs, usage-*.jsonl, bridge audit.
VOLUME ["/var/lib/mineclaw", "/etc/mineclaw", "/var/log/mineclaw"]
WORKDIR /var/lib/mineclaw
USER 1654:1654
# The Supervisor (8765) and the bridge (8766) stay on loopback; only the web image's 8080 is published (shared netns).
ENTRYPOINT ["/app/entrypoint.sh"]
CMD ["server"]
