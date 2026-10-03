#!/bin/sh
# Fetches the ArcViewer web build (https://github.com/AllPoland/ArcViewer, GPL-3.0, by AllPoland)
# that ABeat serves at /arcviewer/. Pinned to the v0.8.1 deploy commit; not committed to this repo.
#   scripts/fetch-arcviewer.sh [dest]   (default: src/Abeat.Web/arcviewer)
set -eu
COMMIT=c776256497b66f7c91a74162cfcd943b0f45ee2e
DEST=${1:-"$(dirname "$0")/../src/Abeat.Web/arcviewer"}
BASE="https://raw.githubusercontent.com/AllPoland/ArcViewer/$COMMIT"
FILES="index.html
Build/ArcViewer.data
Build/ArcViewer.framework.js
Build/ArcViewer.loader.js
Build/ArcViewer.wasm
TemplateData/SFX/BadHitsounds/BloopBadHitsound.wav
TemplateData/SFX/BadHitsounds/FunkyBadHitsound.wav
TemplateData/SFX/BadHitsounds/RecordScratchBadHitsound.wav
TemplateData/SFX/BadHitsounds/VineBoomBadHitsound.wav
TemplateData/SFX/Hitsounds/ChromapperTick.wav
TemplateData/SFX/Hitsounds/GalxHitsound.wav
TemplateData/SFX/Hitsounds/OsuHitsound.wav
TemplateData/SFX/Hitsounds/RabbitViewerTick.wav
TemplateData/SFX/Hitsounds/ThumpyHitsound.wav
TemplateData/Scripts/oggdecode.js
TemplateData/favicon.ico
TemplateData/progress-bar-empty-dark.png
TemplateData/progress-bar-full-dark.png
TemplateData/style.css"

if [ -f "$DEST/.commit" ] && [ "$(cat "$DEST/.commit")" = "$COMMIT" ]; then
  echo "ArcViewer $COMMIT already in $DEST"; exit 0
fi
echo "fetching ArcViewer ($COMMIT, ~82 MB) into $DEST"
for f in $FILES; do
  mkdir -p "$DEST/$(dirname "$f")"
  curl -fsSL --retry 3 -o "$DEST/$f" "$BASE/$f"
done
printf '%s' "$COMMIT" > "$DEST/.commit"
echo "done"
