#!/bin/sh
# Wires keypad-hook.sh into ~/.claude/settings.json, additively.
#
# Two other plugins already own hooks in this file (ClaudeDesktop's curl to :8765, ClaudeConsole's
# activity-hook.sh) and ClaudeConsole owns statusLine. Nothing here may disturb either: we only ever
# append our own entries, identified by the script name, and we never touch statusLine.
#
#   --uninstall   remove our entries and leave everything else alone
set -eu

HOOK="$(cd "$(dirname "$0")" && pwd)/keypad-hook.sh"
SETTINGS="$HOME/.claude/settings.json"
MODE="${1:-install}"

[ -f "$SETTINGS" ] || { echo "no $SETTINGS" >&2; exit 1; }
[ -x "$HOOK" ] || { echo "not executable: $HOOK" >&2; exit 1; }

BACKUP="$SETTINGS.claudewarp.bak"
cp "$SETTINGS" "$BACKUP"

HOOK="$HOOK" SETTINGS="$SETTINGS" MODE="$MODE" python3 - <<'PY'
import json, os, sys

hook     = os.environ["HOOK"]
settings = os.environ["SETTINGS"]
mode     = os.environ["MODE"]

# event -> argument passed to keypad-hook.sh.
EVENTS = {
    "SessionStart":      "start",
    "UserPromptSubmit":  "prompt",
    "PreToolUse":        "busy",
    "PostToolUse":       "busy",
    "PermissionRequest": "attention",
    "Notification":      "attention",
    "Stop":              "done",
    "SessionEnd":        "end",
}

# event -> matcher. PreToolUse/PostToolUse match every tool.
#
# Notification is the interesting one. Claude Code fires it for a whole family of unrelated things -
# permission_prompt, idle_prompt, auth_success, agent_completed and more - and an unmatched entry
# catches all of them, which is what used to turn a finished green tile red about a minute later.
# Matching permission_prompt asks for the one that is actually an alarm.
#
# PermissionRequest is hooked as well, and deliberately: it fires the moment the prompt appears,
# whereas the Notification for the same prompt is delayed several seconds. Both write the same
# state, so whichever arrives first wins and the other is a no-op. Our hook only observes - it
# writes a file and exits 0, never a permission decision - so it cannot interfere with
# ClaudeDesktop's blocking approval hook on the same event.
MATCHERS = {
    "PreToolUse":   "*",
    "PostToolUse":  "*",
    "Notification": "permission_prompt",
}
MARKER = "keypad-hook.sh"

with open(settings) as f:
    cfg = json.load(f)

hooks = cfg.setdefault("hooks", {})
removed = added = 0

for event, arg in EVENTS.items():
    entries = hooks.get(event, [])

    # Drop any previous entry of ours so re-running cannot stack duplicates.
    kept = []
    for entry in entries:
        cmds = entry.get("hooks", [])
        if any(MARKER in (c.get("command") or "") for c in cmds):
            removed += 1
            continue
        kept.append(entry)

    if mode == "install":
        new = {"hooks": [{"type": "command", "command": f'sh "{hook}" {arg}'}]}
        if event in MATCHERS:
            new["matcher"] = MATCHERS[event]
        kept.append(new)
        added += 1

    if kept:
        hooks[event] = kept
    else:
        hooks.pop(event, None)

tmp = settings + ".claudewarp.tmp"
with open(tmp, "w") as f:
    json.dump(cfg, f, indent=2)
    f.write("\n")
os.replace(tmp, settings)

print(f"{mode}: removed {removed} previous entr(ies), added {added}")
PY

echo "backup: $BACKUP"

# A config file nobody knows about is not configurability. Seed a starter one so the keypad's
# settings are discoverable from the machine rather than only from the repo - this is the only step
# every user runs, whether they cloned the source or installed the plugin from the Marketplace.
#
# Only ever written when absent, so it cannot overwrite someone's own, and deliberately WITHOUT a
# "keys" block: leaving it out means the built-in row applies, so seeding this changes nothing about
# how the keypad behaves. Uninstalling leaves it alone; it is the user's file by then.
CONFIG="$HOME/.claude/keypad/config.json"

if [ "$MODE" = "install" ] && [ ! -f "$CONFIG" ]; then
    mkdir -p "$(dirname "$CONFIG")"
    cat > "$CONFIG" <<'JSON'
{
  "//": "Claude Code keypad settings. Re-read once a second - no restart, no rebuild.",

  "//label": "slug = the session name minus its random suffix. prompt = the prompt you typed.",
  "label": "slug",

  "//keys": "Add a \"keys\" array here to replace the bottom row (esc, /clear, /compact) with your",
  "//keys2": "own, and to publish each one as a draggable action in Options+. See config.example.json",
  "//keys3": "in the repo, or the Configuring section of the README."
}
JSON
    echo "seeded $CONFIG"
fi
