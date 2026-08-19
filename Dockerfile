# Multi-stage build: the SDK image compiles and publishes, the runtime image ships. Only
# the published output crosses the boundary, so the final image carries no SDK, no NuGet
# cache, and no source.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Project files first, restore second, everything else third. Restore is the slow layer and
# it only depends on the csproj/props/config files, so editing source code does not
# invalidate it. global.json uses rollForward: latestFeature so a base image carrying a
# newer SDK patch than the pinned one still builds.
COPY global.json nuget.config Directory.Build.props ./
COPY src/NostrRelay.Core/NostrRelay.Core.csproj                               src/NostrRelay.Core/
COPY src/NostrRelay.Storage.Abstractions/NostrRelay.Storage.Abstractions.csproj src/NostrRelay.Storage.Abstractions/
COPY src/NostrRelay.Storage.Ef/NostrRelay.Storage.Ef.csproj                   src/NostrRelay.Storage.Ef/
COPY src/NostrRelay.Storage.Sqlite/NostrRelay.Storage.Sqlite.csproj           src/NostrRelay.Storage.Sqlite/
COPY src/NostrRelay.Storage.Postgres/NostrRelay.Storage.Postgres.csproj       src/NostrRelay.Storage.Postgres/
COPY src/NostrRelay.Server/NostrRelay.Server.csproj                           src/NostrRelay.Server/
RUN dotnet restore src/NostrRelay.Server/NostrRelay.Server.csproj

COPY src/ src/
RUN dotnet publish src/NostrRelay.Server/NostrRelay.Server.csproj \
      --configuration Release \
      --no-restore \
      --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# curl is here only so HEALTHCHECK below can hit /health. That endpoint exercises real
# storage connectivity, so a container reporting healthy has actually reached its database,
# not merely bound a port.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

# SQLite's database file lives on a volume rather than in the container's writable layer, so
# events survive a container replacement. Postgres deployments ignore this path entirely.
RUN mkdir -p /data && chown $APP_UID:$APP_UID /data
VOLUME /data

ENV ASPNETCORE_HTTP_PORTS=8080 \
    Storage__Provider=Sqlite \
    Storage__ConnectionString="Data Source=/data/relay.db"

EXPOSE 8080

# $APP_UID is a non-root user (1654) provided by the base image. Running as root would give
# a process that only ever needs to read its own files and write one database the ability to
# do considerably more than that.
USER $APP_UID

WORKDIR /app
COPY --from=build /app .

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
  CMD curl --fail --silent http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "NostrRelay.Server.dll"]
