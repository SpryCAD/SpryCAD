#!/usr/bin/env bash
# Removes what install.sh added. Settings, recent files and autosave in ~/.config/FeatherCAD are kept.
set -euo pipefail
data="${XDG_DATA_HOME:-$HOME/.local/share}"

rm -rf "$data/feathercad"
[ -L "$HOME/.local/bin/feathercad" ] && rm -f "$HOME/.local/bin/feathercad"
rm -f "$data/applications/feathercad.desktop" \
      "$data/icons/hicolor/256x256/apps/feathercad.png" \
      "$data/icons/hicolor/256x256/mimetypes/application-x-feathercad-part.png" \
      "$data/icons/hicolor/256x256/mimetypes/application-x-feathercad-assembly.png" \
      "$data/mime/packages/feathercad.xml"

command -v update-mime-database >/dev/null && update-mime-database "$data/mime" || true
command -v update-desktop-database >/dev/null && update-desktop-database "$data/applications" || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q -t "$data/icons/hicolor" || true
echo "FeatherCAD removed."
