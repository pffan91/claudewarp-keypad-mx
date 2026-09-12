# Measured facts (Phase 0 spikes, 2026-09-11)

Anything here was observed on this machine, not inferred from docs.

## Device geometry — MX Creative Keypad

| Fact | Value | Evidence |
|---|---|---|
| `DeviceType` token | `Loupedeck70` | `GetButtonPressActionNames deviceType=Loupedeck70` |
| Manifest device token | `MxCreativeKeypad` | ClaudeDesktop's shipping manifest |
| Physical LCD keys | 9 | vendor spec |
| **Usable tiles per folder page** | **8** | see below |
| Button image size enum | `PluginImageSize.Width116` | every `GetCommandImage` call |

### Page size is 8, not 9

A `PluginDynamicFolder` returning 27 action names with `GetNavigationArea() => None` produced four
pages of 8 / 8 / 8 / 3. The host reserves one of the nine keys even when navigation is set to `None`
(ClaudeDesktop's manifest says the same of the Dialpad: "one of which the host takes for the way
back"). So a per-tab block must be **8**, and the plan's "9 names per tab" was wrong.

Image requests arrived in four bursts matching the ▶ presses, 12 s apart then ~0.4 s apart — so the
host renders **lazily, one page at a time**. Tabs you are not looking at cost nothing to draw.

```
18:09:21.4  tiles 1..8     page 1 (folder opened)
18:09:33.6  tiles 9..16    page 2  (▶)
18:09:34.0  tiles 17..24   page 3  (▶)
18:09:34.4  tiles 25..27   page 4  (▶, partial)
```

## Warp

- `open "warp://session/<uuid>"` **works**: activates Warp, switches tab, focuses the exact pane,
  within one poll iteration. Undocumented but real in `v0.2026.08.26`.
- `warp.sqlite` (Group Container `2BBY89MBSN.dev.warp`) reflects focus changes immediately on read,
  but **persists lazily** — a write can lag ~1 s behind the UI. Fine for tab grouping, not for
  instant feedback.
- Join: `terminal_panes.uuid` (BLOB → `lower(hex())`) → `pane_leaves.pane_node_id` → `pane_nodes.tab_id`
  → `tabs.window_id`. `tabs` has **no position column**.

## Toolchain

- Homebrew formula `dotnet` (10.0.400) installs without sudo; the `dotnet-sdk` cask needs it.
- `DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec` is required — Homebrew's location is non-standard.
- `LogiPluginTool` 6.1.4 is a net8.0 app; needs `DOTNET_ROLL_FORWARD=Major` to run on .NET 10.
- Generated template targets `net8.0`; must be `net10.0` because `PluginApi.dll` is .NETCoreApp v10.0.

## Two traps that cost real time

1. **Never ship `PluginApi.dll`.** The template copies it to the output by default, and the plugin
   then fails with a bare `Cannot load plugin from '<path>.dll'`. Set `<Private>false</Private>`.
   Neither shipping plugin (ClaudeDesktop, ClaudeConsole) ships it.
2. **`Cannot load plugin ... because plugin 'X' is already loaded` is BENIGN.** The service
   enumerates twice; `AppleMusic`, a stock working plugin, logs the identical pair. Only the
   `.dll`-path variant means a real failure. Do not chase this one.

Also: `open` is blocked by the Claude Code Bash sandbox. Anything using it must run with the sandbox
disabled, or a working command will look broken.
