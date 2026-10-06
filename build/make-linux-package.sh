#!/usr/bin/env bash
# Linux package build: publish the app for linux-x64 into ./publish/linux-x64, generate the demo parts, and pack
#   publish/FeatherCAD-<version>-linux-x64.tar.gz
# with install.sh / uninstall.sh (per-user menu entry, icon and .fcad file type; see installer/linux), and, where
# dpkg-deb exists, publish/feathercad_<version>_amd64.deb.
# Runs on Linux, macOS or Git Bash on Windows (GNU tar sets the Linux file modes either way).
# Usage:  build/make-linux-package.sh [version]   (default: <Version> in the app's .csproj)
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
proj="$root/src/FeatherCad.App/FeatherCad.App.csproj"
rid=linux-x64

# 1) Version: argument, else <Version> in the app's .csproj.
ver="${1:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$proj" | head -n1)}"
[ -n "$ver" ] || { echo "No version: pass one or set <Version> in $proj" >&2; exit 1; }
echo "== version $ver =="

# 2) Publish self-contained and ReadyToRun (as publish.sh does) into a clean folder.
out="$root/publish/$rid"
rm -rf "$out"
echo "== publishing to $out =="
dotnet publish "$proj" -c Release -r "$rid" --self-contained true -p:PublishReadyToRun=true -p:DebugType=none \
  "-p:Version=$ver" -o "$out"

# 3) Generate the parametric demo parts fresh; fails on a demo with a feature that does not rebuild.
demos="$root/publish/demos"
rm -rf "$demos"
echo "== generating demo parts into $demos =="
dotnet run --project "$root/tests/FeatherCad.Tests" -c Release -- demos "$demos"

# 4) Stage: the app, samples, Linux desktop files and the license, in one top-level folder.
name="FeatherCAD-$ver-$rid"
stage="$root/publish/stage"
rm -rf "$stage"
mkdir -p "$stage/$name/samples/demos"
cp -R "$out/." "$stage/$name/"
cp "$root"/samples/*.fcad "$stage/$name/samples/"
cp "$demos"/*.fcad "$stage/$name/samples/demos/"
cp "$root"/installer/linux/{install.sh,uninstall.sh,feathercad.desktop,feathercad-mime.xml,feathercad.png} "$stage/$name/"
cp "$root/LICENSE" "$stage/$name/"

# 5) Pack with Linux modes: everything readable (folders searchable), then the programs again as executable.
#    Windows has no execute bit to carry over, and on extraction the later copy of a file wins.
tarball="$root/publish/$name.tar.gz"
tar="$root/publish/$name.tar"
rm -f "$tar" "$tarball"
opts=(--owner=0 --group=0 --numeric-owner -C "$stage")
tar "${opts[@]}" --mode='u=rwX,go=rX' -cf "$tar" "$name"
tar "${opts[@]}" --mode=755 -rf "$tar" "$name/FeatherCAD" "$name/install.sh" "$name/uninstall.sh" \
  $(cd "$stage" && ls "$name/createdump" 2>/dev/null)
gzip -9 "$tar"
rm -rf "$stage"

echo "== package ready -> $tarball =="

# 6) The .deb (Debian, Ubuntu, Mint, ...), where dpkg-deb exists (Linux, WSL, CI; not Git Bash): the app in
#    /opt/feathercad, the feathercad command in /usr/bin, and the menu entry, icon and .fcad file type in
#    /usr/share. The desktop, MIME and icon caches refresh through those packages' dpkg triggers, so the
#    package needs no install scripts.
if ! command -v dpkg-deb >/dev/null; then
  echo "== dpkg-deb not found: skipping the .deb (build it on Linux, WSL or CI) =="
  exit 0
fi
# Debian versions sort "-" as a revision separator: a pre-release (0.2.0-beta) is 0.2.0~beta, before 0.2.0
debver="${ver//-/\~}"
deb="$root/publish/feathercad_${debver}_amd64.deb"
pkg="$root/publish/deb-stage"
rm -rf "$pkg" "$deb"
mkdir -p "$pkg/DEBIAN" "$pkg/opt/feathercad/samples/demos" "$pkg/usr/bin" "$pkg/usr/share/applications" \
  "$pkg/usr/share/icons/hicolor/256x256/apps" "$pkg/usr/share/mime/packages" "$pkg/usr/share/doc/feathercad"
cp -R "$out/." "$pkg/opt/feathercad/"
cp "$root"/samples/*.fcad "$pkg/opt/feathercad/samples/"
cp "$demos"/*.fcad "$pkg/opt/feathercad/samples/demos/"
ln -s /opt/feathercad/FeatherCAD "$pkg/usr/bin/feathercad"
sed 's|@EXEC@|/opt/feathercad/FeatherCAD|' "$root/installer/linux/feathercad.desktop" > "$pkg/usr/share/applications/feathercad.desktop"
cp "$root/installer/linux/feathercad.png" "$pkg/usr/share/icons/hicolor/256x256/apps/feathercad.png"
cp "$root/installer/linux/feathercad-mime.xml" "$pkg/usr/share/mime/packages/feathercad.xml"
cp "$root/LICENSE" "$pkg/usr/share/doc/feathercad/copyright"
# modes: folders 755, files 644, the programs 755
find "$pkg" -type d -exec chmod 755 {} +
find "$pkg" -type f -exec chmod 644 {} +
chmod 755 "$pkg/opt/feathercad/FeatherCAD"
[ -f "$pkg/opt/feathercad/createdump" ] && chmod 755 "$pkg/opt/feathercad/createdump"
# The app is self-contained (.NET included); it still needs the C/C++ runtime, ICU (whose package name carries
# its version: newest first, back to Ubuntu 22.04) and, for Avalonia, X11, fontconfig and OpenGL.
cat > "$pkg/DEBIAN/control" <<EOF
Package: feathercad
Version: $debver
Architecture: amd64
Maintainer: Kurt Jacobson <kcjengr@gmail.com>
Installed-Size: $(du -sk "$pkg/opt" | cut -f1)
Depends: libc6, libgcc-s1, libstdc++6, libicu78 | libicu77 | libicu76 | libicu74 | libicu72 | libicu70, libx11-6, libice6, libsm6, libfontconfig1, libgl1
Section: graphics
Priority: optional
Homepage: https://github.com/KurtJacobson/FeatherCAD
Description: Fast, lightweight parametric 3D CAD
 FeatherCAD is open-source parametric 3D CAD with a SolidWorks-style workflow,
 its own exact B-rep geometry kernel, and STEP and STL export.
EOF
echo "== building $deb =="
dpkg-deb --root-owner-group -Zxz --build "$pkg" "$deb"
rm -rf "$pkg"
echo "== .deb ready -> $deb =="
