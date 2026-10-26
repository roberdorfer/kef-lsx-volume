#!/bin/zsh
# Regression test for scripts/install.sh --pref quoting: the value must reach `defaults write`
# unchanged after the admin script is parsed by /bin/sh (it used to be interpolated into an
# AppleScript string literal, where `"` and `\` ended or escaped the literal).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
value='a"b\c'"'"'d $x `y`'
out="$("$ROOT/scripts/install.sh" helper --dry-run --pref "speakerHost=$value")"
line="$(printf '%s\n' "$out" | grep -o "defaults write [^&]*speakerHost[^&]*")"
[[ -n "$line" ]] || { echo "FAIL: no defaults write line in dry-run output" >&2; exit 1; }
# Replace the command with printf and let /bin/sh parse the quoting exactly as the admin script would.
got="$(/bin/sh -c "${line/#defaults write/printf '%s\\n'}" | sed -n 3p)"
[[ "$got" == "$value" ]] || { echo "FAIL: value changed: got <$got> want <$value>" >&2; exit 1; }
printf '%s\n' "$out" | grep -q "rm -rf '/Applications/LSXIIVolumeFixer.app'" || { echo "FAIL: admin script incomplete" >&2; exit 1; }
# This run's staging directory (quoted in the admin script) must be gone; other runs' dirs are not ours.
stage="$(printf '%s\n' "$out" | grep -o "'/tmp/kefproxy\.[^/']*" | head -n 1 | tr -d "'")"
[[ -n "$stage" ]] || { echo "FAIL: staging directory not found in admin script" >&2; exit 1; }
[[ ! -e "$stage" ]] || { echo "FAIL: staging directory left behind: $stage" >&2; exit 1; }
echo "install_dry_run: passed"
