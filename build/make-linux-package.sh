#!/usr/bin/env bash
# Linux package build: publish the app for linux-x64 into ./publish/linux-x64, generate the demo parts, and pack
#   publish/SpryCAD-<version>-linux-x64.tar.gz
# with install.sh / uninstall.sh (per-user menu entry, icon and .sprt / .sasm file types; see installer/linux), and, where
# dpkg-deb exists, publish/sprycad_<version>_amd64.deb.
# Runs on Linux, macOS or Git Bash on Windows (GNU tar sets the Linux file modes either way).
# Usage:  build/make-linux-package.sh
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
proj="$root/src/FeatherCad.App/FeatherCad.App.csproj"
rid=linux-x64

# 1) Version, always from git: the latest v<x.y.z> tag, and how many commits HEAD is past it. On the tag: 0.0.1;
#    3 commits after it: 0.0.1-dev.3 (as make-installer.ps1).
desc="$(git -C "$root" describe --tags --match 'v[0-9]*' --long 2>/dev/null || true)"
if [[ ! "$desc" =~ ^v([0-9]+\.[0-9]+\.[0-9]+)-([0-9]+)-g[0-9a-f]+$ ]]; then
  echo "No version: no v<x.y.z> tag is reachable from HEAD (tag a commit, or fetch the tags / full history)." >&2; exit 1
fi
base="${BASH_REMATCH[1]}"; dev="${BASH_REMATCH[2]}"
if [ "$dev" = 0 ]; then ver="$base"; debver="$base"; else ver="$base-dev.$dev"; debver="$base+dev.$dev"; fi
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
name="SpryCAD-$ver-$rid"
stage="$root/publish/stage"
rm -rf "$stage"
mkdir -p "$stage/$name/samples/demos"
cp -R "$out/." "$stage/$name/"
cp "$root"/samples/*.sprt "$stage/$name/samples/"
cp "$demos"/*.sprt "$demos"/*.sasm "$stage/$name/samples/demos/"
cp "$root"/installer/linux/{install.sh,uninstall.sh,sprycad.desktop,sprycad-mime.xml,sprycad.png,application-x-sprycad-part.png,application-x-sprycad-assembly.png} "$stage/$name/"
cp "$root/LICENSE" "$stage/$name/"

# 5) Pack with Linux modes: everything readable (folders searchable), then the programs again as executable.
#    Windows has no execute bit to carry over, and on extraction the later copy of a file wins.
tarball="$root/publish/$name.tar.gz"
tar="$root/publish/$name.tar"
rm -f "$tar" "$tarball"
opts=(--owner=0 --group=0 --numeric-owner -C "$stage")
tar "${opts[@]}" --mode='u=rwX,go=rX' -cf "$tar" "$name"
tar "${opts[@]}" --mode=755 -rf "$tar" "$name/SpryCAD" "$name/install.sh" "$name/uninstall.sh" \
  $(cd "$stage" && ls "$name/createdump" 2>/dev/null)
gzip -9 "$tar"
rm -rf "$stage"

echo "== package ready -> $tarball =="

# 6) The .deb (Debian, Ubuntu, Mint, ...), where dpkg-deb exists (Linux, WSL, CI; not Git Bash): the app in
#    /opt/sprycad, the sprycad command in /usr/bin, and the menu entry, icon and .sprt / .sasm file types in
#    /usr/share. The desktop, MIME and icon caches refresh through those packages' dpkg triggers, so the
#    package needs no install scripts.
if ! command -v dpkg-deb >/dev/null; then
  echo "== dpkg-deb not found: skipping the .deb (build it on Linux, WSL or CI) =="
  exit 0
fi
# (debver, from step 1: a dev build is 0.0.1+dev.3, which Debian sorts after 0.0.1 as it should; "-" would read
#  as a Debian revision separator)
deb="$root/publish/sprycad_${debver}_amd64.deb"
pkg="$root/publish/deb-stage"
rm -rf "$pkg" "$deb"
mkdir -p "$pkg/DEBIAN" "$pkg/opt/sprycad/samples/demos" "$pkg/usr/bin" "$pkg/usr/share/applications" \
  "$pkg/usr/share/icons/hicolor/256x256/apps" "$pkg/usr/share/icons/hicolor/256x256/mimetypes" "$pkg/usr/share/mime/packages" "$pkg/usr/share/doc/sprycad"
cp -R "$out/." "$pkg/opt/sprycad/"
cp "$root"/samples/*.sprt "$pkg/opt/sprycad/samples/"
cp "$demos"/*.sprt "$demos"/*.sasm "$pkg/opt/sprycad/samples/demos/"
ln -s /opt/sprycad/SpryCAD "$pkg/usr/bin/sprycad"
sed 's|@EXEC@|/opt/sprycad/SpryCAD|' "$root/installer/linux/sprycad.desktop" > "$pkg/usr/share/applications/sprycad.desktop"
cp "$root/installer/linux/sprycad.png" "$pkg/usr/share/icons/hicolor/256x256/apps/sprycad.png"
cp "$root"/installer/linux/application-x-sprycad-{part,assembly}.png "$pkg/usr/share/icons/hicolor/256x256/mimetypes/"
cp "$root/installer/linux/sprycad-mime.xml" "$pkg/usr/share/mime/packages/sprycad.xml"
cp "$root/LICENSE" "$pkg/usr/share/doc/sprycad/copyright"
# modes: folders 755, files 644, the programs 755
find "$pkg" -type d -exec chmod 755 {} +
find "$pkg" -type f -exec chmod 644 {} +
chmod 755 "$pkg/opt/sprycad/SpryCAD"
[ -f "$pkg/opt/sprycad/createdump" ] && chmod 755 "$pkg/opt/sprycad/createdump"
# The app is self-contained (.NET included); it still needs the C/C++ runtime, ICU (whose package name carries
# its version: newest first, back to Ubuntu 22.04) and, for Avalonia, X11, fontconfig and OpenGL.
cat > "$pkg/DEBIAN/control" <<EOF
Package: sprycad
Version: $debver
Architecture: amd64
Maintainer: Kurt Jacobson <kcjengr@gmail.com>
Installed-Size: $(du -sk "$pkg/opt" | cut -f1)
Depends: libc6, libgcc-s1, libstdc++6, libicu78 | libicu77 | libicu76 | libicu74 | libicu72 | libicu70, libx11-6, libice6, libsm6, libfontconfig1, libgl1
Section: graphics
Priority: optional
Homepage: https://github.com/KurtJacobson/FeatherCAD
Description: Fast, lightweight parametric 3D CAD
 SpryCAD is open-source parametric 3D CAD with a SolidWorks-style workflow,
 its own exact B-rep geometry kernel, and STEP and STL export.
EOF
echo "== building $deb =="
dpkg-deb --root-owner-group -Zxz --build "$pkg" "$deb"
rm -rf "$pkg"
echo "== .deb ready -> $deb =="
