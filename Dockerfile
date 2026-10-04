# syntax=docker/dockerfile:1
# ABeat: ASP.NET Core server + React UI + the analysis worker's source. One small image for every machine:
# the heavy parts are installed on first start into bind-mountable folders, not baked in.
#   /data     songs, analyses, generated maps, playlists, settings
#   /models   ML models (beat_this, Demucs, Whisper, BS-RoFormer), downloaded on first use
#   /runtime  Python + the worker environment with the torch build for the detected accelerator
#             (cpu, cuda, cuda12, rocm, xpu), ArcViewer, uv cache
# Build:  docker build -t abeat .
# Run:    docker compose up -d          (GPU: see docker-compose.<nvidia|rocm|intel>.yml)

# ── React UI ──
FROM node:22-alpine AS ui-build
WORKDIR /ui
COPY src/abeat-ui/package*.json ./
RUN npm ci
COPY src/abeat-ui/ ./
COPY VERSION /VERSION
RUN npx tsc -b && npx vite build --outDir ./dist --emptyOutDir

# ── .NET server ──
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY VERSION ./
COPY src/Abeat.Core/Abeat.Core.csproj src/Abeat.Core/
COPY src/Abeat.Web/Abeat.Web.csproj src/Abeat.Web/
RUN dotnet restore src/Abeat.Web/Abeat.Web.csproj
COPY src/Abeat.Core/ src/Abeat.Core/
COPY src/Abeat.Web/ src/Abeat.Web/
RUN dotnet publish src/Abeat.Web/Abeat.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# uv installs Python and the worker environment at start (docker/provision.py)
COPY --from=ghcr.io/astral-sh/uv:latest /uv /usr/local/bin/uv
COPY analysis/pyproject.toml analysis/uv.lock analysis/.python-version /opt/abeat/analysis/
COPY analysis/src /opt/abeat/analysis/src
COPY docker/entrypoint.sh docker/provision.py scripts/fetch-arcviewer.sh /opt/abeat/docker/
COPY --from=build /app /opt/abeat/app
COPY --from=ui-build /ui/dist /opt/abeat/app/wwwroot
RUN chmod +x /opt/abeat/docker/entrypoint.sh
ENV ABEAT_ANALYSIS_DIR=/opt/abeat/analysis \
    ABEAT_DATA=/data \
    ABEAT_ACCEL=auto \
    ABEAT_EXTRAS= \
    ASPNETCORE_URLS=http://+:8080 \
    ABEAT_HTTP_PORT=8080 \
    ABEAT_HTTPS_PORT=8443
VOLUME ["/data", "/models", "/runtime"]
EXPOSE 8080 8443
WORKDIR /opt/abeat/app
# starts as root only to hand the bind mounts to ABEAT_UID:ABEAT_GID, then drops to that user
ENTRYPOINT ["/opt/abeat/docker/entrypoint.sh"]
