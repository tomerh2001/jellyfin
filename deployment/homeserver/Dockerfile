# syntax=docker/dockerfile:1
# Preserve the exact LinuxServer Jellyfin 12 runtime, web client, FFmpeg and GPU libraries.
FROM lscr.io/linuxserver/jellyfin@sha256:0f42497a69fa0441bfd5f9d6bba8694f2a656ec984e571d0ac04c6dd91250039 AS upstream
FROM mcr.microsoft.com/dotnet/sdk@sha256:4ea6fe75dd36706bb6d8c3c293d4c4315840f5d76ea28ac97def77e3ec487fa5 AS build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
WORKDIR /src
COPY . .
COPY --from=upstream /usr/lib/jellyfin/bin /baseline
RUN test ! -e artifacts && test ! -e .git \
    && test -z "$(find . -type d \( -name bin -o -name obj \) -print -quit)" \
    && dotnet test tests/Jellyfin.Naming.Tests/Jellyfin.Naming.Tests.csproj \
    --configuration Release --logger 'trx;LogFileName=naming.trx' --results-directory /verification
RUN dotnet publish deployment/homeserver/verify/NamingOverlayVerifier.csproj \
    --configuration Release --output /verifier \
    && dotnet /verifier/NamingOverlayVerifier.dll /baseline \
       /src/Emby.Naming/bin/Release/net10.0/Emby.Naming.dll > /verification/overlay.json

FROM scratch AS verification
COPY --from=build /verification /

FROM upstream AS runtime
LABEL org.opencontainers.image.title="Jellyfin with conservative episode version grouping" \
      org.opencontainers.image.source="https://github.com/tomerh2001/jellyfin" \
      org.opencontainers.image.version="12.0.0-homeserver.1" \
      org.opencontainers.image.licenses="GPL-3.0-only AND GPL-2.0-or-later" \
      org.opencontainers.image.base.name="lscr.io/linuxserver/jellyfin" \
      org.opencontainers.image.base.digest="sha256:0f42497a69fa0441bfd5f9d6bba8694f2a656ec984e571d0ac04c6dd91250039" \
      io.tomerh2001.jellyfin.upstream-revision="6c073e19ddf604b2369c638716164fdab4c952dc" \
      io.tomerh2001.jellyfin.upstream-patch="30eb0d7016441c13d9bf58a1958102321ea2f740"
COPY --from=build /src/Emby.Naming/bin/Release/net10.0/Emby.Naming.dll /usr/lib/jellyfin/bin/Emby.Naming.dll
COPY --from=build /src/Emby.Naming/bin/Release/net10.0/Emby.Naming.pdb /usr/lib/jellyfin/bin/Emby.Naming.pdb
COPY --from=build /verification/overlay.json /usr/share/jellyfin-homeserver/overlay-verification.json
COPY deployment/homeserver/README.md deployment/homeserver/provenance.json LICENSE /usr/share/jellyfin-homeserver/
