#!/usr/bin/env bash
# Installs FeatherCAD for the current user (no root): the app into ~/.local/share/feathercad, a `feathercad`
# command in ~/.local/bin, and a menu entry, icon and .fcad file type. Run it from the unpacked folder:
#   ./install.sh
# Settings, recent files and autosave live in ~/.config/FeatherCAD and are kept by ./uninstall.sh.
set -euo pipefail
src="$(cd "$(dirname "$0")" && pwd)"
data="${XDG_DATA_HOME:-$HOME/.local/share}"
app="$data/feathercad"
bin="$HOME/.local/bin"

if [ ! -f "$src/FeatherCAD" ]; then
  echo "Run install.sh from the unpacked FeatherCAD folder (FeatherCAD not found next to it)." >&2
  exit 1
fi

echo "Installing FeatherCAD to $app"
# A clean app folder, so files a newer version no longer ships do not linger.
rm -rf "$app"
mkdir -p "$app" "$bin"
cp -R "$src/." "$app/"
chmod +x "$app/FeatherCAD"
ln -sf "$app/FeatherCAD" "$bin/feathercad"

mkdir -p "$data/applications" "$data/icons/hicolor/256x256/apps" "$data/mime/packages"
sed "s|@EXEC@|\"$app/FeatherCAD\"|" "$src/feathercad.desktop" > "$data/applications/feathercad.desktop"
cp "$src/feathercad.png" "$data/icons/hicolor/256x256/apps/feathercad.png"
cp "$src/feathercad-mime.xml" "$data/mime/packages/feathercad.xml"

# Refresh the menu, file type and icon caches where the tools exist; desktops pick the files up anyway on next login.
command -v update-mime-database >/dev/null && update-mime-database "$data/mime" || true
command -v update-desktop-database >/dev/null && update-desktop-database "$data/applications" || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q -t "$data/icons/hicolor" || true
command -v xdg-mime >/dev/null && xdg-mime default feathercad.desktop application/x-feathercad-part || true

echo "Done. FeatherCAD is in your applications menu; from a terminal run: feathercad"
case ":$PATH:" in *":$bin:"*) ;; *) echo "(Add $bin to your PATH to use the feathercad command.)" ;; esac
