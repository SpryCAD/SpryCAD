#!/usr/bin/env bash
# Linux package build: publish the app for linux-x64 into ./publish/linux-x64, generate the demo parts, and pack
#   publish/SpryCAD-<version>-linux-x64.tar.gz
# with install.sh / uninstall.sh (per-user menu entry, icon and .sprt / .sasm file types; see installer/linux), and, where
# dpkg-deb exists, publish/sprycad_<version>_amd64.deb, and where rpmbuild exists too,
# publish/sprycad-<version>-<release>.x86_64.rpm.
# Runs on Linux, macOS or Git Bash on Windows (GNU tar sets the Linux file modes either way).
# Usage:  build/make-linux-package.sh
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
proj="$root/src/SpryCad.App/SpryCad.App.csproj"
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

# The oldest glibc the packages declare (Ubuntu 18.04, Debian 10, RHEL 8): what .NET's runtime (libcoreclr,
# libclrjit) needs, the most of any library shipped. Where objdump exists (Linux, CI), the build fails if a library
# needs more, as a .NET, Skia or HarfBuzz update could, so the packages never install somewhere they cannot run;
# then raise this and the oldest versions the download page names.
glibc_min=2.27
if command -v objdump >/dev/null; then
  need=$(for f in "$out"/SpryCAD "$out"/*.so; do objdump -T "$f" 2>/dev/null; done | grep -o 'GLIBC_[0-9.]*' |
    sed 's/GLIBC_//' | sort -V -u | tail -1)
  if [ "$(printf '%s\n%s\n' "$need" "$glibc_min" | sort -V | tail -1)" != "$glibc_min" ]; then
    echo "The build needs glibc $need, newer than glibc_min=$glibc_min: raise it (and the oldest systems named)." >&2
    exit 1
  fi
  echo "== needs glibc $need (packages declare $glibc_min) =="
fi

# 3) Generate the parametric demo parts fresh; fails on a demo with a feature that does not rebuild.
demos="$root/publish/demos"
rm -rf "$demos"
echo "== generating demo parts into $demos =="
dotnet run --project "$root/tests/SpryCad.Tests" -c Release -- demos "$demos"

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
# The app is self-contained (.NET included); it still needs the C/C++ runtime, ICU and, for Avalonia, X11,
# fontconfig and OpenGL. The oldest systems it runs on are those with glibc_min (2.27, checked after the publish):
# Ubuntu 18.04 and Mint 19, Debian 10, RHEL 8. ICU's package name carries its version, and .NET loads whichever is
# installed, so any from 80 down to 60 does: Ubuntu 18.04 (60), Debian 10 (63), 20.04 and Mint 20 (66), Debian 11
# (67), 22.04 (70), Debian 12 (72), 24.04 (74) and on. Older systems' C runtime is libgcc1 (Ubuntu 18.04, Debian 10).
icu=$(for v in $(seq 80 -1 60); do printf 'libicu%s | ' "$v"; done)
cat > "$pkg/DEBIAN/control" <<EOF
Package: sprycad
Version: $debver
Architecture: amd64
Maintainer: Kurt Jacobson <kcjengr@gmail.com>
Installed-Size: $(du -sk "$pkg/opt" | cut -f1)
Depends: libc6 (>= $glibc_min), libgcc-s1 | libgcc1, libstdc++6, ${icu% | }, libx11-6, libice6, libsm6, libfontconfig1, libgl1
Section: graphics
Priority: optional
Homepage: https://github.com/SpryCAD/SpryCAD
Description: Fast, lightweight parametric 3D CAD
 SpryCAD is open-source parametric 3D CAD with a SolidWorks-style workflow,
 its own exact B-rep geometry kernel, and STEP and STL export.
EOF
echo "== building $deb =="
dpkg-deb --root-owner-group -Zxz --build "$pkg" "$deb"
rm -rf "$pkg/DEBIAN"
echo "== .deb ready -> $deb =="

# 7) The .rpm (Fedora, RHEL, Rocky, Alma, ...), where rpmbuild exists, from the same tree as the .deb. Version
#    0.0.1, release 1 on the tag; a dev build is release 1.dev.3, which rpm sorts after 1 (and dev.10 after dev.9)
#    on every rpm version (the newer "^" would need rpm 4.15). Its requirements are named by library (soname), not
#    package, so they hold on any of those distributions; ICU is loaded by name at run time, so it is required by
#    package. Automatic requirements are off: the .NET runtime ships libraries for optional tracing (LTTng) that
#    nothing needs. As for the .deb, the menu, MIME and icon caches refresh through those packages' own triggers.
if command -v rpmbuild >/dev/null; then
  if [ "$dev" = 0 ]; then rpmrel=1; else rpmrel="1.dev.$dev"; fi
  rpm="$root/publish/sprycad-$base-$rpmrel.x86_64.rpm"
  top="$root/publish/rpm-build"
  rm -rf "$top" "$rpm"
  mkdir -p "$top/SPECS"
  cat > "$top/SPECS/sprycad.spec" <<EOF
Name: sprycad
Version: $base
Release: $rpmrel
Summary: Fast, lightweight parametric 3D CAD
License: MIT
URL: https://github.com/SpryCAD/SpryCAD
BuildArch: x86_64
AutoReqProv: no
Requires: libc.so.6(GLIBC_$glibc_min)(64bit), libstdc++.so.6()(64bit), libgcc_s.so.1()(64bit), libicu
Requires: libX11.so.6()(64bit), libICE.so.6()(64bit), libSM.so.6()(64bit), libfontconfig.so.1()(64bit), libGL.so.1()(64bit)
%global debug_package %{nil}
%global __os_install_post %{nil}
%global _build_id_links none

%description
SpryCAD is open-source parametric 3D CAD with a SolidWorks-style workflow,
its own exact B-rep geometry kernel, and STEP and STL export.

%install
cp -a "$pkg/." %{buildroot}/

%files
%defattr(-,root,root,-)
/opt/sprycad
/usr/bin/sprycad
/usr/share/applications/sprycad.desktop
/usr/share/icons/hicolor/256x256/apps/sprycad.png
/usr/share/icons/hicolor/256x256/mimetypes/application-x-sprycad-part.png
/usr/share/icons/hicolor/256x256/mimetypes/application-x-sprycad-assembly.png
/usr/share/mime/packages/sprycad.xml
%doc /usr/share/doc/sprycad
EOF
  echo "== building $rpm =="
  rpmbuild -bb --quiet --define "_topdir $top" --define "_rpmdir $top/RPMS" --define "_binary_payload w9.xzdio" \
    "$top/SPECS/sprycad.spec"
  mv "$top/RPMS/x86_64/$(basename "$rpm")" "$rpm"
  rm -rf "$top"
  echo "== .rpm ready -> $rpm =="
else
  echo "== rpmbuild not found: skipping the .rpm =="
fi
rm -rf "$pkg"
