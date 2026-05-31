#!/usr/bin/env bash
set -euo pipefail

limit="${VISUAL_RELAY_FILE_LINE_LIMIT:-300}"
failed=0

while IFS= read -r file; do
  lines="$(wc -l < "$file" | tr -d ' ')"
  if (( lines > limit )); then
    echo "file too large: $file has $lines lines (limit $limit)" >&2
    failed=1
  fi
done < <(find src tests tools \( -name '*.cs' -o -name '*.axaml' \) -not -path '*/bin/*' -not -path '*/obj/*' | sort)

exit "$failed"
