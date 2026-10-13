#!/bin/zsh
# Remove the KEFProxy HAL driver and the "LSX II Volume Fixer" menu bar agent installed by install.sh.
# Stops the agent in every logged-in GUI session, deletes the driver, the app, the agent plist and the
# system-wide prefs (one admin password prompt), and restarts coreaudiod so the "KEF LSX II" proxy
# device disappears; macOS then picks another default output.
#
# Usage: scripts/uninstall.sh [--purge] [--dry-run]
#   --purge also deletes the current user's own settings and log (other users keep theirs).
#   --dry-run prints what would run instead of running it (nothing is removed).
set -euo pipefail
PURGE=0
DRY_RUN=0
while (( $# )); do
  case "$1" in
    --purge) PURGE=1 ;;
    --dry-run) DRY_RUN=1 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
  shift
done

HAL_DIR=/Library/Audio/Plug-Ins/HAL
AGENT_LABEL=io.github.roberdorfer.lsxiivolumefixer
AGENT_PLIST="/Library/LaunchAgents/$AGENT_LABEL.plist"
APP_NAME=LSXIIVolumeFixer
APP_DIR="/Applications/$APP_NAME.app"
GLOBAL_PREFS="/Library/Preferences/$AGENT_LABEL"
USER_LOG="$HOME/Library/Logs/$APP_NAME.log"

STAGE="$(mktemp -d /tmp/kefproxy.XXXXXX)"
trap 'rm -rf "$STAGE"' EXIT

ADMIN=""   # shell script run once with administrator privileges
# Stop the agent in every logged-in GUI session (fast user switching), this one included.
for uid in $(ps -axo uid=,comm= | awk '$2 ~ /loginwindow$/ && $1 >= 500 {print $1}' | sort -u); do
  ADMIN+="launchctl bootout 'gui/$uid/$AGENT_LABEL' 2>/dev/null; "
done
# `defaults delete` goes through cfprefsd, which would otherwise keep serving (and could rewrite) a
# cached copy of the deleted system-wide plist. coreaudiod is only restarted when a driver was there.
ADMIN+="rm -f '$AGENT_PLIST' && rm -rf '$APP_DIR' && { defaults delete '$GLOBAL_PREFS' 2>/dev/null; rm -f '$GLOBAL_PREFS.plist'; } && "
ADMIN+="if [ -e '$HAL_DIR/KEFProxy.driver' ]; then rm -rf '$HAL_DIR/KEFProxy.driver' && killall coreaudiod; fi"

printf '%s\n' "$ADMIN" > "$STAGE/admin.sh"
chmod -R a+rX "$STAGE"
if (( DRY_RUN )); then
  echo "dry run; admin script that would run with administrator privileges:"
  cat "$STAGE/admin.sh"
  if (( PURGE )); then
    echo "then, as $(id -un): defaults delete $AGENT_LABEL; rm -f '$USER_LOG'"
  else
    echo "then, as $(id -un): defaults delete $AGENT_LABEL defaultOutputSetFor"
  fi
  exit 0
fi
osascript -e "do shell script \"/bin/sh '$STAGE/admin.sh'\" with administrator privileges with prompt \"Uninstall KEF LSX II (restarts coreaudiod)\""
echo "driver, app and agent removed"

if (( PURGE )); then
  defaults delete "$AGENT_LABEL" 2>/dev/null || true
  rm -f "$USER_LOG"
  echo "settings and log of $(id -un) removed"
else
  # Without this marker a later reinstall would not make the proxy the default output again.
  defaults delete "$AGENT_LABEL" defaultOutputSetFor 2>/dev/null || true
fi
echo "The app's entry under System Settings > Privacy & Security > Local Network stays; macOS offers no way to remove it, and it does nothing once the app is gone."
