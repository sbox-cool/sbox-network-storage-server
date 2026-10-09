# syntax=docker/dockerfile:1
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG VERSION=0.0.0-dev
WORKDIR /src
COPY Directory.Build.props global.json ./
COPY src/SboxNetworkStorage.Application/*.csproj src/SboxNetworkStorage.Application/
COPY src/SboxNetworkStorage.Contracts/*.csproj src/SboxNetworkStorage.Contracts/
COPY src/SboxNetworkStorage.Domain/*.csproj src/SboxNetworkStorage.Domain/
COPY src/SboxNetworkStorage.Infrastructure/*.csproj src/SboxNetworkStorage.Infrastructure/
COPY src/SboxNetworkStorage.Server/*.csproj src/SboxNetworkStorage.Server/
COPY src/SboxNetworkStorage.Storage/*.csproj src/SboxNetworkStorage.Storage/
COPY src/SboxNetworkStorage.Storage.Relational/*.csproj src/SboxNetworkStorage.Storage.Relational/
COPY src/SboxNetworkStorage.Storage.Sqlite/*.csproj src/SboxNetworkStorage.Storage.Sqlite/
COPY src/SboxNetworkStorage.Storage.Postgres/*.csproj src/SboxNetworkStorage.Storage.Postgres/
RUN dotnet restore src/SboxNetworkStorage.Server/SboxNetworkStorage.Server.csproj -a "$TARGETARCH"
COPY src/ src/
COPY install/ install/
RUN dotnet publish src/SboxNetworkStorage.Server/SboxNetworkStorage.Server.csproj \
    -c Release -a "$TARGETARCH" --self-contained false --no-restore \
    -p:Version="$VERSION" -o /app \
    && mkdir -p /runtime/config /runtime/data

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
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
COPY --from=build --chown=1654:1654 /runtime/ /
USER 1654
EXPOSE 8080
VOLUME ["/config", "/data"]
ENTRYPOINT ["/app/sbox-ns"]
CMD ["start"]
