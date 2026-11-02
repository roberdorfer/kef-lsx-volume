#!/bin/zsh
# Build the macOS release assets into build/release/:
#   kef-lsx-volume-macos.tar.gz  universal (arm64 + x86_64) driver and app, ad hoc signed, plus the
#                                install and uninstall scripts (install.sh skips the build without a Makefile)
#   install-macos.sh             one-line installer that downloads the tarball and runs install.sh
#   uninstall-macos.sh           scripts/uninstall.sh as is (it needs no other file)
# and the Windows ones (cross-built with the .NET 10 SDK; the SDK embeds the manifest into the exe here too):
#   kef-lsx-volume-windows-{x64,arm64}.zip  prebuilt windows\build\LSXIIVolumeFixer.exe plus the install and
#                                           uninstall scripts (install.ps1 skips the build without make.ps1)
#   install-windows.ps1, uninstall-windows.ps1  one-line installer and its uninstall twin
# Upload them to a GitHub release: gh release upload <tag> build/release/*
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
OUT=build/release
PKG="$OUT/pkg"
rm -rf "$OUT"
mkdir -p "$PKG/build" "$PKG/scripts"
for arch in arm64 x86_64; do make -s ARCH=$arch BUILD=build/$arch all; done

cp -R build/arm64/KEFProxy.driver build/arm64/LSXIIVolumeFixer.app "$PKG/build/"
for bin in KEFProxy.driver/Contents/MacOS/KEFProxy LSXIIVolumeFixer.app/Contents/MacOS/LSXIIVolumeFixer; do
  lipo -create "build/arm64/$bin" "build/x86_64/$bin" -output "$PKG/build/$bin"
done
codesign --force --sign - "$PKG/build/KEFProxy.driver"
codesign --force --sign - "$PKG/build/LSXIIVolumeFixer.app"
cp scripts/install.sh scripts/uninstall.sh "$PKG/scripts/"
cp LICENSE "$PKG/"
COPYFILE_DISABLE=1 tar -czf "$OUT/kef-lsx-volume-macos.tar.gz" -C "$PKG" LICENSE build scripts
rm -rf "$PKG"
cp scripts/install-macos.sh "$OUT/install-macos.sh"
cp scripts/uninstall.sh "$OUT/uninstall-macos.sh"

# Same dotnet publish as make.ps1's Invoke-Publish, for each RID instead of the native one; the RID in the
# intermediate path keeps the second RID from reusing the first one's apphost.
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
for rid in win-x64 win-arm64; do
  W="$OUT/${rid}"
  mkdir -p "$W/build" "$W/scripts"
  dotnet publish windows/Helper/LSXIIVolumeFixer.csproj -c Release -r $rid --self-contained false \
    -p:PublishSingleFile=true -p:EnableWindowsTargeting=true -p:AppendRuntimeIdentifierToOutputPath=true -nologo -o "$W/build" --disable-build-servers -v quiet
  cp windows/scripts/{install,uninstall}.{ps1,cmd} "$W/scripts/"
  cp windows/LICENSE "$W/"
  (cd "$W" && zip -qrX "../kef-lsx-volume-windows-${rid#win-}.zip" LICENSE build/LSXIIVolumeFixer.exe scripts)
  rm -rf "$W"
done
cp windows/scripts/install-windows.ps1 "$OUT/"
sed "s/^\$Action = 'install'\$/\$Action = 'uninstall'/" windows/scripts/install-windows.ps1 > "$OUT/uninstall-windows.ps1"
grep -q "^\$Action = 'uninstall'\$" "$OUT/uninstall-windows.ps1"
ls -l "$OUT"
