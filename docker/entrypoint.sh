#!/bin/bash
# ABeat container start: prepare the three bind-mountable folders, install the analysis runtime in the
# background (docker/provision.py; the web UI is up meanwhile and shows its progress), run the server.
#   /data     songs, analyses, generated maps, playlists, settings
#   /models   downloaded ML models (beat_this, Demucs, Whisper, BS-RoFormer)
#   /runtime  Python, the worker environment for the accelerator, ArcViewer, uv cache
# Runs as ABEAT_UID:ABEAT_GID (default: the image's "app" user) so files in bind mounts belong to you.
set -euo pipefail

uid=${ABEAT_UID:-$(id -u app)}
gid=${ABEAT_GID:-$(id -g app)}

for d in /data /models /runtime; do
  mkdir -p "$d"
  if [ "$(id -u)" = 0 ] && [ "$(stat -c %u "$d")" != "$uid" ]; then chown "$uid:$gid" "$d"; fi
done

as_user() {
  if [ "$(id -u)" = 0 ]; then
    # keep access to GPU device nodes (render/video groups of /dev/kfd and /dev/dri/*)
    groups=$(stat -c %g /dev/kfd /dev/dri/* 2>/dev/null | sort -u | paste -sd, - || true)
    setpriv --reuid "$uid" --regid "$gid" ${groups:+--groups "$groups"} ${groups:---clear-groups} "$@"
  else
    "$@"
  fi
}

export HOME=/runtime/home TORCH_HOME=/models/torch HF_HOME=/models/hf XDG_CACHE_HOME=/models/cache \
       ABEAT_SEPARATOR_MODELS=/models/separator NUMBA_CACHE_DIR=/runtime/numba \
       UV_PYTHON_INSTALL_DIR=/runtime/python UV_CACHE_DIR=/runtime/uv-cache UV_PYTHON_PREFERENCE=only-managed \
       ABEAT_RUNTIME_STATUS=/runtime/status.json ABEAT_ARCVIEWER_DIR=/runtime/arcviewer
as_user mkdir -p "$HOME" /runtime/arcviewer

rm -f /runtime/status.json
as_user uv run --no-project --python 3.12 /opt/abeat/docker/provision.py &

if [ "$(id -u)" = 0 ]; then
  exec setpriv --reuid "$uid" --regid "$gid" --clear-groups dotnet /opt/abeat/app/Abeat.Web.dll "$@"
fi
exec dotnet /opt/abeat/app/Abeat.Web.dll "$@"
