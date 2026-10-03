# syntax=docker/dockerfile:1
# abeat web app: ASP.NET Core server + Python analysis worker in one image.
#   docker build -t abeat .                 (with beat_this + demucs, CPU torch; ~2.5 GB)
#   docker build -t abeat --build-arg ML=0 .  (librosa only; much smaller, less accurate beats)
#   docker run -p 8080:8080 -v abeat-data:/data abeat

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
COPY src/Abeat.Core/Abeat.Core.csproj src/Abeat.Core/
COPY src/Abeat.Web/Abeat.Web.csproj src/Abeat.Web/
RUN dotnet restore src/Abeat.Web/Abeat.Web.csproj
COPY src/Abeat.Core/ src/Abeat.Core/
COPY src/Abeat.Web/ src/Abeat.Web/
RUN dotnet publish src/Abeat.Web/Abeat.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG ML=1
COPY --from=ghcr.io/astral-sh/uv:latest /uv /usr/local/bin/uv
# JS runtime yt-dlp needs for YouTube links
COPY --from=denoland/deno:bin /deno /usr/local/bin/deno
ENV UV_PYTHON_INSTALL_DIR=/opt/python \
    UV_COMPILE_BYTECODE=1 \
    UV_LINK_MODE=copy \
    UV_NO_CACHE=1 \
    TORCH_HOME=/opt/abeat/torch \
    NUMBA_CACHE_DIR=/tmp/numba

WORKDIR /opt/abeat/analysis
COPY analysis/pyproject.toml analysis/uv.lock analysis/.python-version ./
# dependencies first (cached layer), then the worker source
RUN if [ "$ML" = "1" ]; then uv sync --frozen --no-install-project --extra ml; else uv sync --frozen --no-install-project; fi
COPY analysis/src ./src
RUN if [ "$ML" = "1" ]; then uv sync --frozen --extra ml; else uv sync --frozen; fi \
 && if [ "$ML" = "1" ]; then \
      .venv/bin/python -c "from beat_this.inference import Audio2Beats; Audio2Beats(checkpoint_path='final0', device='cpu', dbn=False)" \
      && .venv/bin/python -c "from demucs.pretrained import get_model; get_model('htdemucs')"; \
    fi \
 && mkdir -p /data /opt/abeat/torch && chown -R app:app /data /opt/abeat/torch

COPY --from=build /app /opt/abeat/app
COPY --from=ui-build /ui/dist /opt/abeat/app/wwwroot
ENV ABEAT_ANALYSIS_DIR=/opt/abeat/analysis \
    ABEAT_DATA=/data \
    ASPNETCORE_URLS=http://+:8080 \
    ABEAT_HTTP_PORT=8080 \
    ABEAT_HTTPS_PORT=8443
USER app
VOLUME /data
EXPOSE 8080 8443
WORKDIR /opt/abeat/app
ENTRYPOINT ["dotnet", "Abeat.Web.dll"]
