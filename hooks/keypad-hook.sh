#!/bin/sh
# keypad-hook.sh <state> — records one Claude Code session's state for the ClaudeWarp keypad plugin.
#
# Called from Claude Code hooks. The hook payload arrives on stdin, but the hot path
# (PreToolUse/PostToolUse, which fire on every tool call) deliberately never parses it: the identity
# we key on, WARP_TERMINAL_SESSION_UUID, is already in the environment. Only `start` reads stdin.
#
#   SessionStart      -> start      writes meta + state=idle
#   UserPromptSubmit  -> prompt     refreshes meta, state=busy, resets the elapsed clock
#   PreToolUse        -> busy       hot path, state only, keeps the existing clock
#   PostToolUse       -> busy       ditto
#   PermissionRequest -> attention  Claude is blocked on a permission prompt
#   Notification      -> attention  ditto, matched to permission_prompt; see below
#   Stop              -> done       turn finished
#   SessionEnd        -> end        removes the session's files
#
# Always exits 0: a status display must never be able to break the session it is watching.

set -u
umask 077

ROOT="${CLAUDE_KEYPAD_ROOT:-$HOME/.claude/keypad}"
SESSIONS="$ROOT/sessions"

# Not running under Warp (or an older client that predates the variable): nothing to report.
[ -n "${WARP_TERMINAL_SESSION_UUID:-}" ] || exit 0

UUID="$WARP_TERMINAL_SESSION_UUID"
# Guard the value before it becomes a filename. Warp writes 32 lowercase hex; anything else is not ours.
case "$UUID" in
  *[!0-9a-f]* | "" ) exit 0 ;;
esac
[ ${#UUID} -eq 32 ] || exit 0

mkdir -p "$SESSIONS" 2>/dev/null || exit 0
# Refuse a root another local user created before we could.
[ -O "$ROOT" ] || exit 0
chmod 700 "$ROOT" 2>/dev/null

ACTION="${1:-done}"
STATE_FILE="$SESSIONS/$UUID.state.json"
META_FILE="$SESSIONS/$UUID.meta.json"
NOW="$(date +%s)"

if [ "$ACTION" = "end" ]; then
  rm -f "$STATE_FILE" "$META_FILE"
  exit 0
fi

# A backstop, not the main defence. Notification covers a family of unrelated events - a blocking
# permission prompt, but also the "waiting for your input" nudge a minute after a turn ends, plus
# auth and agent-lifecycle messages - and only the first is an alarm; the rest land on a tile that
# is already green, and green says "your move" without shouting about it.
#
# install-hooks.sh asks for the right one by matching permission_prompt, so in a correctly installed
# setup this check never fires. It stays because the hook has to survive being wired up by hand, or
# by an older Claude Code that does not honour the matcher: a permission prompt interrupts work in
# flight, so the state is busy, whereas every other notification can only arrive once the turn is
# over. Reading the state rather than the message text means a reworded notification cannot quietly
# break the alarm either.
if [ "$ACTION" = "attention" ]; then
  CURRENT=""
  [ -r "$STATE_FILE" ] && CURRENT="$(cat "$STATE_FILE" 2>/dev/null)"
  case "$CURRENT" in
    *'"state":"busy"'*) ;;   # something is blocked waiting on you
    *) exit 0 ;;             # nothing was running: leave the tile as it is
  esac
fi

case "$ACTION" in
  start)  STATE=idle ;;
  prompt) STATE=busy ;;
  busy)   STATE=busy ;;
  attention) STATE=attention ;;
  done)   STATE=done ;;
  *)      STATE=done ;;
esac

# Elapsed time is "how long in THIS state", so the clock only restarts when the state actually
# changes. Without this, every tool call would reset a busy tile's timer to 0:00.
SINCE="$NOW"
if [ "$ACTION" = "busy" ] && [ -r "$STATE_FILE" ]; then
  OLD="$(cat "$STATE_FILE" 2>/dev/null)"
  case "$OLD" in
    *'"state":"busy"'*)
      OLD_SINCE="${OLD##*\"since\":}"
      OLD_SINCE="${OLD_SINCE%%,*}"
      OLD_SINCE="${OLD_SINCE%%\}*}"
      case "$OLD_SINCE" in ''|*[!0-9]*) ;; *) SINCE="$OLD_SINCE" ;; esac
      ;;
  esac
fi

write_atomic() {  # write_atomic <file> <content>
  _t="$1.tmp.$$"
  printf '%s\n' "$2" > "$_t" 2>/dev/null && mv -f "$_t" "$1" 2>/dev/null || rm -f "$_t" 2>/dev/null
}

write_atomic "$STATE_FILE" "{\"state\":\"$STATE\",\"since\":$SINCE,\"ts\":$NOW}"

# Metadata is comparatively expensive (a git call, a walk up the process tree, JSON parsing), so it is
# refreshed only on the events that can change it - plus whenever it is simply missing, which is how a
# session that started before this hook existed heals itself on its next tool call.
if [ "$ACTION" = "start" ] || [ "$ACTION" = "prompt" ] || [ ! -r "$META_FILE" ]; then
  PAYLOAD="$(cat 2>/dev/null)"

  SESSION_ID="$(printf '%s' "$PAYLOAD" | grep -o '"session_id":"[^"]*"' | head -1 | cut -d'"' -f4)"

  # Claude Code names each session with a kebab-case slug ("cap-dom-price-ladder-1000-cells") and
  # puts it in the terminal title, which is the text Warp shows on the tab. It is the only thing that
  # tells two sessions in the same folder apart - they share a checkout, so they share a branch.
  # Read from the tail: the slug is repeated on every transcript line and these files reach megabytes.
  TRANSCRIPT="$(printf '%s' "$PAYLOAD" | grep -o '"transcript_path":"[^"]*"' | head -1 | cut -d'"' -f4)"
  SLUG=""
  if [ -n "$TRANSCRIPT" ] && [ -r "$TRANSCRIPT" ]; then
    SLUG="$(tail -c 131072 "$TRANSCRIPT" 2>/dev/null | grep -o '"slug":"[^"]*"' | tail -1 | cut -d'"' -f4)"
  fi
  # Keep the previous slug rather than blanking a tile when a read happens to miss.
  if [ -z "$SLUG" ] && [ -r "$META_FILE" ]; then
    SLUG="$(sed -n 's/.*"slug":"\([^"]*\)".*/\1/p' "$META_FILE" 2>/dev/null)"
  fi
  CWD="$(printf '%s' "$PAYLOAD" | grep -o '"cwd":"[^"]*"' | head -1 | cut -d'"' -f4)"
  [ -n "$CWD" ] || CWD="$PWD"

  # In a repo the top level is the project and never drifts. Outside one, cwd follows the session
  # into subdirectories, so a tile would rename itself to things like "src" mid-session - keep the
  # name first recorded instead of letting it wander.
  TOPLEVEL="$(git -C "$CWD" rev-parse --show-toplevel 2>/dev/null)"
  if [ -n "$TOPLEVEL" ]; then
    PROJECT="$(basename "$TOPLEVEL")"
  else
    PROJECT=""
    if [ -r "$META_FILE" ]; then
      PROJECT="$(sed -n 's/.*"project":"\([^"]*\)".*/\1/p' "$META_FILE" 2>/dev/null)"
    fi
    [ -n "$PROJECT" ] || PROJECT="$(basename "$CWD")"
  fi
  BRANCH="$(git -C "$CWD" rev-parse --abbrev-ref HEAD 2>/dev/null)"
  [ "$BRANCH" = "HEAD" ] && BRANCH="$(git -C "$CWD" rev-parse --short HEAD 2>/dev/null)"

  # Find the claude process this hook descends from, for liveness checks by the plugin.
  PID=""
  p=$$
  n=0
  while [ "$n" -lt 10 ]; do
    c="$(ps -o comm= -p "$p" 2>/dev/null | sed 's|.*/||')"
    [ "$c" = "claude" ] && { PID="$p"; break; }
    p="$(ps -o ppid= -p "$p" 2>/dev/null | tr -d ' ')"
    { [ -z "$p" ] || [ "$p" -le 1 ]; } && break
    n=$((n + 1))
  done

  esc() { printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'; }

  if [ -r "$META_FILE" ]; then
    STARTED="$(sed -n 's/.*"started":\([0-9]*\).*/\1/p' "$META_FILE" 2>/dev/null)"
  fi
  [ -n "${STARTED:-}" ] || STARTED="$NOW"

  write_atomic "$META_FILE" "{\"schema\":1,\"warp_uuid\":\"$UUID\",\"focus_url\":\"warp://session/$UUID\",\"session_id\":\"$(esc "$SESSION_ID")\",\"pid\":${PID:-0},\"cwd\":\"$(esc "$CWD")\",\"project\":\"$(esc "$PROJECT")\",\"branch\":\"$(esc "$BRANCH")\",\"slug\":\"$(esc "$SLUG")\",\"transcript\":\"$(esc "$TRANSCRIPT")\",\"started\":$STARTED}"
fi

exit 0
