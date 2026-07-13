#!/bin/bash
set -euo pipefail

# Export a physical Unraid disk map for Transcoder storage-aware scheduling.
# Run this on Unraid. The Transcoder server can then import the TSV over the normal media SMB mount.
#
# Defaults assume your user share is /mnt/user/media and per-disk paths are /mnt/disk*/media.
# Override when needed, e.g.:
#   MEDIA_ROOT_NAME=media OUT=/mnt/user/media/.transcoder/storage-map.tsv ./scripts/unraid-export-storage-map.sh

MEDIA_ROOT_NAME="${MEDIA_ROOT_NAME:-media}"
OUT="${OUT:-/mnt/user/${MEDIA_ROOT_NAME}/.transcoder/storage-map.tsv}"
TMP="${OUT}.tmp"

mkdir -p "$(dirname "$OUT")"
{
  printf 'relative_path\tstorage_key\tsize_bytes\tmtime_unix\n'

  for ROOT in /mnt/disk* /mnt/cache; do
    [ -d "$ROOT/$MEDIA_ROOT_NAME" ] || continue
    STORAGE_KEY="$(basename "$ROOT")"

    find "$ROOT/$MEDIA_ROOT_NAME" -type f \( \
      -iname "*.mkv" -o \
      -iname "*.mp4" -o \
      -iname "*.avi" -o \
      -iname "*.m2ts" -o \
      -iname "*.ts" -o \
      -iname "*.webm" \
    \) -print0 | while IFS= read -r -d '' FILE; do
      REL="${FILE#"$ROOT/$MEDIA_ROOT_NAME/"}"
      SIZE="$(stat -c '%s' "$FILE" 2>/dev/null || echo 0)"
      MTIME="$(stat -c '%Y' "$FILE" 2>/dev/null || echo 0)"
      printf '%s\t%s\t%s\t%s\n' "$REL" "$STORAGE_KEY" "$SIZE" "$MTIME"
    done
  done
} > "$TMP"

mv "$TMP" "$OUT"
echo "Wrote $OUT"
