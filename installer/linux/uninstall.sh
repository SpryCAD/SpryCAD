#!/usr/bin/env bash
# Removes what install.sh added. Settings, recent files and autosave in ~/.config/SpryCAD are kept.
set -euo pipefail
data="${XDG_DATA_HOME:-$HOME/.local/share}"

rm -rf "$data/sprycad"
[ -L "$HOME/.local/bin/sprycad" ] && rm -f "$HOME/.local/bin/sprycad"
rm -f "$data/applications/sprycad.desktop" \
      "$data/icons/hicolor/256x256/apps/sprycad.png" \
      "$data/icons/hicolor/256x256/mimetypes/application-x-sprycad-part.png" \
      "$data/icons/hicolor/256x256/mimetypes/application-x-sprycad-assembly.png" \
      "$data/mime/packages/sprycad.xml"

command -v update-mime-database >/dev/null && update-mime-database "$data/mime" || true
command -v update-desktop-database >/dev/null && update-desktop-database "$data/applications" || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q -t "$data/icons/hicolor" || true
echo "SpryCAD removed."
