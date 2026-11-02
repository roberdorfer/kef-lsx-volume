#!/bin/zsh
# One-line installer (release asset): downloads the prebuilt driver and app of the latest release and
# runs their scripts/install.sh, so no Xcode Command Line Tools are needed. Files fetched with curl
# carry no quarantine flag, so the ad hoc signed bundles load like ones built on this Mac.
#
#   zsh -c "$(curl -fsSL https://github.com/roberdorfer/kef-lsx-volume/releases/latest/download/install-macos.sh)"
# Arguments for install.sh follow a placeholder for $0, e.g. ... )" _ --pref maxSpeakerVolume=60
set -euo pipefail
URL=https://github.com/roberdorfer/kef-lsx-volume/releases/latest/download/kef-lsx-volume-macos.tar.gz
DIR="$(mktemp -d /tmp/kef-lsx-volume.XXXXXX)"
trap 'rm -rf "$DIR"' EXIT
echo "Downloading $URL"
curl -fsSL "$URL" | tar -xzf - -C "$DIR"
"$DIR/scripts/install.sh" "$@"
