# syntax=docker/dockerfile:1

# Build on the native build platform and cross-publish for the target
# architecture, so multi-arch builds do not compile under emulation.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG TARGETARCH
ARG VERSION=0.0.0-dev
WORKDIR /src

COPY Directory.Build.props SboxNetworkStorage.sln ./
COPY src/ src/

RUN dotnet restore src/SboxNetworkStorage.Server/SboxNetworkStorage.Server.csproj -a "$TARGETARCH"

RUN dotnet publish src/SboxNetworkStorage.Server/SboxNetworkStorage.Server.csproj \
        -c Release \
        -a "$TARGETARCH" \
        --self-contained false \
        --no-restore \
        -p:Version="$VERSION" \
        -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
ARG VERSION=0.0.0-dev

LABEL org.opencontainers.image.title="sbox Network Storage Server" \
      org.opencontainers.image.description="Self-hosted s&box Network Storage game backend" \
      org.opencontainers.image.source="https://github.com/sbox-cool/sbox-network-storage-server" \
      org.opencontainers.image.licenses="AGPL-3.0-only" \
      org.opencontainers.image.version="$VERSION"

ENV NS_CONFIG_DIR=/config \
    NS_DATA_DIR=/data \
    NS_SERVER__LISTEN=0.0.0.0:8080 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

WORKDIR /app
COPY --from=build /app ./

# The aspnet image ships a non-root "app" user ($APP_UID).
RUN mkdir -p /config /data && chown "$APP_UID":"$APP_UID" /config /data

USER $APP_UID

EXPOSE 8080
VOLUME ["/config", "/data"]

ENTRYPOINT ["/app/sbox-ns"]
CMD ["start"]
