# Privacy Policy — ClaudeWarp

**Last updated:** 2026-09-12

ClaudeWarp shows the status of Claude Code sessions on an MX Creative Keypad. It runs entirely on
your machine.

## Nothing is collected and nothing is transmitted

ClaudeWarp has no network code. It opens no sockets, makes no HTTP requests, contacts no server,
and has no analytics, telemetry, crash reporting or update check. There is no account, no sign-in
and no identifier of any kind.

The developer receives no data from your use of this plugin — none, ever, including anonymised or
aggregated data.

## What it reads on your machine

| What | Why |
|---|---|
| `~/.claude/keypad/sessions/*.json` | the session state files its own hook writes — project name, git branch, state, timestamps |
| `~/.claude/keypad/config.json` | your keypad settings |
| `~/.claude/settings.json` | to know whether its hooks are installed, and to add or remove them when you confirm |
| Warp's session database (read-only) | to group sessions by Warp tab. `~/Library/Group Containers/2BBY89MBSN.dev.warp/…/warp.sqlite`, opened read-only, never written |
| The process table | to notice when a Claude Code session has exited so its tile can be cleared |

The project names and git branch names shown on the keys come from the directory your session is
running in. They are written to files under `~/.claude/keypad/` (owner-readable only, mode 0700) and
are displayed on the keypad attached to your computer. They go nowhere else.

## What it writes

- `~/.claude/keypad/` — session state, a copy of its hook script, and a starter `config.json`.
- `~/.claude/settings.json` — **only** when you confirm a setup press on a key, and only entries
  tagged `keypad-hook.sh`. The previous version of the file is saved as
  `settings.json.claudewarp.bak` first. A long press removes those entries again. Nothing is written
  to this file when the plugin is installed or loaded.

## What it controls

When you press a command key, the plugin asks macOS System Events to send keystrokes to Warp — this
is what typing `/clear` or sending Escape does, and it is why macOS asks you to grant Accessibility
permission to the Logi Plugin Service. Keystrokes are only ever sent to Warp, only in response to a
key you pressed, and nothing is recorded.

## Third parties

None. ClaudeWarp bundles no third-party SDKs or services. It interacts with Claude Code, Warp and
the Logi Plugin Service, all already installed on your machine and each governed by its own privacy
policy.

## Removing your data

Uninstall the plugin in Logi Options+, then delete `~/.claude/keypad/`. If the hooks are still
wired, long press a key first (or run `hooks/install-hooks.sh --uninstall`) to take them out of
`~/.claude/settings.json`.

## Contact

Questions: <https://github.com/pffan91/claudewarp-keypad-mx/issues>
