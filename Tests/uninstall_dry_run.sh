#!/bin/zsh
# scripts/uninstall.sh --dry-run must list every installed part and remove nothing.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
before="$(print -l /tmp/kefproxy.*(N))"
out="$("$ROOT/scripts/uninstall.sh" --dry-run --purge)"
for want in "rm -f '/Library/LaunchAgents/io.github.roberdorfer.lsxiivolumefixer.plist'" \
            "rm -rf '/Applications/LSXIIVolumeFixer.app'" \
            "rm -rf '/Library/Audio/Plug-Ins/HAL/KEFProxy.driver'" \
            "rm -f '/Library/Preferences/io.github.roberdorfer.lsxiivolumefixer.plist'" \
            "defaults delete '/Library/Preferences/io.github.roberdorfer.lsxiivolumefixer'" \
            "killall coreaudiod" \
            "defaults delete io.github.roberdorfer.lsxiivolumefixer;"; do
  printf '%s\n' "$out" | grep -qF -- "$want" || { echo "FAIL: missing: $want" >&2; exit 1; }
done
# Without --purge only the default-output marker is reset, so a reinstall makes the proxy the default again.
"$ROOT/scripts/uninstall.sh" --dry-run | grep -qF "defaults delete io.github.roberdorfer.lsxiivolumefixer defaultOutputSetFor" \
  || { echo "FAIL: default-output marker not reset without --purge" >&2; exit 1; }
# The staging directory holding admin.sh must be removed (no new /tmp/kefproxy.* left behind).
after="$(print -l /tmp/kefproxy.*(N))"
[[ "$after" == "$before" ]] || { echo "FAIL: staging directory left behind" >&2; exit 1; }
echo "uninstall_dry_run: passed"
