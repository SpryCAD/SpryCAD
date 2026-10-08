#!/usr/bin/env bash
# Installs SpryCAD for the current user (no root): the app into ~/.local/share/sprycad, a `sprycad`
# command in ~/.local/bin, and a menu entry, icon and .sprt / .sasm file types. Run it from the unpacked folder:
#   ./install.sh
# Settings, recent files and autosave live in ~/.config/SpryCAD and are kept by ./uninstall.sh.
set -euo pipefail
src="$(cd "$(dirname "$0")" && pwd)"
data="${XDG_DATA_HOME:-$HOME/.local/share}"
app="$data/sprycad"
bin="$HOME/.local/bin"

if [ ! -f "$src/SpryCAD" ]; then
  echo "Run install.sh from the unpacked SpryCAD folder (SpryCAD not found next to it)." >&2
  exit 1
fi

echo "Installing SpryCAD to $app"
# A clean app folder, so files a newer version no longer ships do not linger.
rm -rf "$app"
mkdir -p "$app" "$bin"
cp -R "$src/." "$app/"
chmod +x "$app/SpryCAD"
ln -sf "$app/SpryCAD" "$bin/sprycad"

mkdir -p "$data/applications" "$data/icons/hicolor/256x256/apps" "$data/icons/hicolor/256x256/mimetypes" "$data/mime/packages"
sed "s|@EXEC@|\"$app/SpryCAD\"|" "$src/sprycad.desktop" > "$data/applications/sprycad.desktop"
cp "$src/sprycad.png" "$data/icons/hicolor/256x256/apps/sprycad.png"
cp "$src"/application-x-sprycad-{part,assembly}.png "$data/icons/hicolor/256x256/mimetypes/"
cp "$src/sprycad-mime.xml" "$data/mime/packages/sprycad.xml"

# Refresh the menu, file type and icon caches where the tools exist; desktops pick the files up anyway on next login.
command -v update-mime-database >/dev/null && update-mime-database "$data/mime" || true
command -v update-desktop-database >/dev/null && update-desktop-database "$data/applications" || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q -t "$data/icons/hicolor" || true
command -v xdg-mime >/dev/null && xdg-mime default sprycad.desktop application/x-sprycad-part application/x-sprycad-assembly || true

echo "Done. SpryCAD is in your applications menu; from a terminal run: sprycad"
case ":$PATH:" in *":$bin:"*) ;; *) echo "(Add $bin to your PATH to use the sprycad command.)" ;; esac
