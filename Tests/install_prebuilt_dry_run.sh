#!/bin/zsh
# scripts/install.sh from the release tarball (scripts/release.sh): prebuilt bundles in build/, no Makefile.
# It must install those bundles without building (no Xcode Command Line Tools on the target Mac); a `make`
# call would fail there for lack of a Makefile.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
make -s -C "$ROOT" all
DIR="$(mktemp -d /tmp/kef-prebuilt-test.XXXXXX)"
trap 'rm -rf "$DIR"' EXIT
mkdir -p "$DIR/build" "$DIR/scripts"
cp -R "$ROOT/build/KEFProxy.driver" "$ROOT/build/LSXIIVolumeFixer.app" "$DIR/build/"
cp "$ROOT/scripts/install.sh" "$DIR/scripts/"
out="$("$DIR/scripts/install.sh" all --dry-run)"
printf '%s\n' "$out" | grep -q "/KEFProxy.driver' '/Library/Audio/Plug-Ins/HAL/'" || { echo "FAIL: driver not staged" >&2; exit 1; }
printf '%s\n' "$out" | grep -q "/LSXIIVolumeFixer.app' '/Applications/LSXIIVolumeFixer.app'" || { echo "FAIL: app not staged" >&2; exit 1; }
echo "install_prebuilt_dry_run: passed"
