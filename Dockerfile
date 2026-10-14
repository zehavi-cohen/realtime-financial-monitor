# Multi-stage build for the Rtfm API.
#
# Layer order is deliberate: project files are copied and restored before any
# source is copied, so an edit to a .cs file reuses the cached restore layer.
# Copying the whole tree first would invalidate the restore on every change and
# turn a five-second rebuild into a minute.

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Build configuration shared by every project, and the SDK pin.
COPY global.json Directory.Build.props .editorconfig ./

# Project files only - this layer changes when a dependency changes, not when code does.
COPY src/Rtfm.Core/Rtfm.Core.csproj                 src/Rtfm.Core/
COPY src/Rtfm.Infrastructure/Rtfm.Infrastructure.csproj src/Rtfm.Infrastructure/
COPY src/Rtfm.Api/Rtfm.Api.csproj                   src/Rtfm.Api/

RUN dotnet restore src/Rtfm.Api/Rtfm.Api.csproj

# Source. db/schema.sql is embedded into Rtfm.Infrastructure, so it is part of the
# build input rather than something to mount at runtime.
COPY db/ db/
COPY src/ src/

RUN dotnet publish src/Rtfm.Api/Rtfm.Api.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore

# ---------------------------------------------------------------------------
# Runtime
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime

# curl is here only so the Compose healthcheck can call /health/ready. Kubernetes
# probes are issued by the kubelet and need nothing in the image.
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish ./

# Non-root. APP_UID (1654) is defined by the base image.
USER $APP_UID

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Rtfm.Api.dll"]
