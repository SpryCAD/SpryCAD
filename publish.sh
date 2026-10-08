#!/usr/bin/env bash
# Builds self-contained, ReadyToRun-compiled SpryCAD into ./publish/<rid>
# Usage: ./publish.sh [rid...]   e.g. ./publish.sh linux-x64 osx-arm64 win-x64
set -euo pipefail
cd "$(dirname "$0")"
rids=("$@")
if [ ${#rids[@]} -eq 0 ]; then
  case "$(uname -s)-$(uname -m)" in
    Darwin-arm64) rids=(osx-arm64) ;;
    Darwin-*) rids=(osx-x64) ;;
    *) rids=(linux-x64) ;;
  esac
fi
for rid in "${rids[@]}"; do
  echo "Publishing $rid ..."
  dotnet publish src/SpryCad.App/SpryCad.App.csproj -c Release -r "$rid" --self-contained true \
    -p:PublishReadyToRun=true \
    -p:DebugType=none -o "publish/$rid"
done
