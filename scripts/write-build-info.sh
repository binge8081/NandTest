#!/bin/sh
set -eu
version="${1:-0.1.0}"
root=$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)
api="$root/src/NandFlashTester.Api/build-info.json"
ui="$root/web/public/build-info.json"
if [ "${NAND_BUILD_INFO_LOCK:-}" = "1" ] && [ -f "$api" ] && [ -f "$ui" ]; then
  exit 0
fi
commit=$(git -C "$root" rev-parse HEAD 2>/dev/null || printf unknown)
stamp=$(date -u +%Y-%m-%dT%H:%M:%SZ)
mkdir -p "$root/web/public"
json=$(cat <<EOF
{
  "product": "领存NFA1000闪存测试仪",
  "version": "$version",
  "commit": "$commit",
  "buildTimeUtc": "$stamp",
  "packagedMode": "Production",
  "entry": "http://127.0.0.1:5080/"
}
EOF
)
printf '%s\n' "$json" > "$api"
printf '%s\n' "$json" > "$ui"
