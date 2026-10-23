#!/bin/zsh
# Install the KEFProxy HAL driver and the "LSX II Volume Fixer" menu bar agent for ALL macOS users.
# Driver -> /Library/Audio/Plug-Ins/HAL, app -> /Applications/LSXIIVolumeFixer.app, agent plist ->
# /Library/LaunchAgents (admin password via one GUI prompt). Every user's login session then
# starts its own instance, which only acts while that user owns the console.
#
# Usage: scripts/install.sh [driver|helper|all] [--pref key=value ...] [--dry-run]
#   --pref writes a system-wide default (/Library/Preferences/io.github.roberdorfer.lsxiivolumefixer.plist)
#   that applies to every user who has not overridden the key in their own defaults.
#   --dry-run prints the admin script instead of running it (nothing is installed).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WHAT=all
PREFS=()
DRY_RUN=0
while (( $# )); do
  case "$1" in
    driver|helper|all) WHAT="$1" ;;
    --pref) PREFS+=("$2"); shift ;;
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

cd "$ROOT"
make -s all

STAGE="$(mktemp -d /tmp/kefproxy.XXXXXX)"
trap 'rm -rf "$STAGE"' EXIT   # also when the admin prompt is cancelled or a step fails
ADMIN=""   # shell script run once with administrator privileges
PROMPT="Install KEF LSX II"

if [[ "$WHAT" == driver || "$WHAT" == all ]]; then
  cp -R "$ROOT/build/KEFProxy.driver" "$STAGE/"
  ADMIN+="rm -rf '$HAL_DIR/KEFProxy.driver' && cp -R '$STAGE/KEFProxy.driver' '$HAL_DIR/' && chown -R root:wheel '$HAL_DIR/KEFProxy.driver' && "
  PROMPT+=" – driver (restarts coreaudiod)"
fi

if [[ "$WHAT" == helper || "$WHAT" == all ]]; then
  cp -R "$ROOT/build/$APP_NAME.app" "$STAGE/"
  # No $HOME paths here: the same plist is loaded in every user's gui session. The app
  # writes its own log to ~/Library/Logs/$APP_NAME.log of the user it runs as.
  cat > "$STAGE/$AGENT_LABEL.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>Label</key><string>$AGENT_LABEL</string>
	<key>ProgramArguments</key><array><string>$APP_DIR/Contents/MacOS/$APP_NAME</string></array>
	<key>LimitLoadToSessionType</key><string>Aqua</string>
	<key>RunAtLoad</key><true/>
	<key>KeepAlive</key><true/>
	<key>ThrottleInterval</key><integer>5</integer>
</dict>
</plist>
PLIST
  ADMIN+="rm -rf '$APP_DIR' && cp -R '$STAGE/$APP_NAME.app' '$APP_DIR' && chown -R root:wheel '$APP_DIR' && cp '$STAGE/$AGENT_LABEL.plist' '$AGENT_PLIST' && chown root:wheel '$AGENT_PLIST' && chmod 644 '$AGENT_PLIST' && "
  PROMPT+=" – LSX II Volume Fixer (menu bar) for all users"
  # Restart the agent in every OTHER logged-in GUI session (fast user switching): an instance whose
  # bundle was replaced underneath keeps running the old code until that user logs out (such a
  # stale instance can also lose speaker contact). The current session is handled below after bootout.
  for uid in $(ps -axo uid=,comm= | awk -v me="$(id -u)" '$2 ~ /loginwindow$/ && $1 != me && $1 >= 500 {print $1}' | sort -u); do
    # bootout + bootstrap, not kickstart -k: after the bundle was replaced, kickstart left the job in
    # "spawn failed" (last exit code 78 EX_CONFIG). bootstrap can fail briefly after bootout.
    ADMIN+="launchctl bootout 'gui/$uid/$AGENT_LABEL' 2>/dev/null; for i in 1 2 3 4 5; do launchctl bootstrap 'gui/$uid' '$AGENT_PLIST' 2>/dev/null && break; sleep 1; done; "
  done
  # Stop the running agent in this session before the new one is loaded.
  (( DRY_RUN )) || launchctl bootout "gui/$(id -u)/$AGENT_LABEL" 2>/dev/null || true
fi

# Single-quote a value for the admin shell string (a literal ' becomes '\'').
shquote() { printf "'%s'" "$(printf '%s' "$1" | sed "s/'/'\\\\''/g")"; }
for kv in "${PREFS[@]}"; do
  ADMIN+="defaults write '$GLOBAL_PREFS' $(shquote "${kv%%=*}") $(shquote "${kv#*=}") && chmod 644 '$GLOBAL_PREFS.plist' && "
done

if [[ "$WHAT" == driver || "$WHAT" == all ]]; then
  ADMIN+="killall coreaudiod"
else
  ADMIN+="true"
fi

# The admin script is run from a file, not interpolated into the AppleScript string literal: a `"`
# or `\` in a --pref value (or any path) would otherwise end or escape that literal. $STAGE comes
# from mktemp and contains no quotes.
printf '%s\n' "$ADMIN" > "$STAGE/admin.sh"
chmod -R a+rX "$STAGE"
if (( DRY_RUN )); then
  echo "dry run; admin script that would run with administrator privileges:"
  cat "$STAGE/admin.sh"
  exit 0
fi
osascript -e "do shell script \"/bin/sh '$STAGE/admin.sh'\" with administrator privileges with prompt \"$PROMPT\""
[[ "$WHAT" == driver || "$WHAT" == all ]] && echo "driver installed; coreaudiod restarted"

if [[ "$WHAT" == helper || "$WHAT" == all ]]; then
  # Load into the current user's session now; other users get it at their next login.
  # bootout returns before the old job is fully gone; bootstrap can fail in that window.
  for i in 1 2 3 4 5; do
    launchctl bootstrap "gui/$(id -u)" "$AGENT_PLIST" 2>/dev/null && break
    sleep 1
  done
  launchctl print "gui/$(id -u)/$AGENT_LABEL" >/dev/null 2>&1 || { echo "helper agent failed to load" >&2; exit 1; }
  echo "LSX II Volume Fixer agent loaded (global, all users)"
fi
