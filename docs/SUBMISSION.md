# Marketplace Submission

Path to a submittable `.lplug4` for the [Logitech Marketplace](https://marketplace.logitech.com/contribute),
per the [Actions SDK approval guidelines](https://logitech.github.io/actions-sdk-docs/marketplace-approval-guidelines/)
and the [Marketplace Developer Agreement](https://loupedeck.com/us/marketplace-developer-agreement/).

**Status:** `ClaudeWarp_1.0.0.lplug4` **builds, packs and verifies clean** (64 KB, 5 files, no host
assemblies, no symbols). Not submitted. Two things block submission, in this order:

1. **Push the repo.** `licenseUrl` points at `docs/EULA.md` on GitHub, which is a live **404** until
   the commit lands. Approval checks that every external link resolves.
2. **Test on the hardware.** Nothing below under Testing has been exercised on a real keypad.

Expect roughly **10 working days** for review, based on the turnaround another Claude Code plugin
reported for the same queue.

## The one thing that decides this submission

A Marketplace user installs a `.lplug4`. They never see this repository, so they never run
`../hooks/install-hooks.sh` — and without the hooks, every tile is blank and the plugin looks broken.

The tempting fix is `Plugin.Install()`. It exists and it works:

```
Loupedeck.Plugin :: virtual Boolean Install()
Loupedeck.Plugin :: virtual Boolean Uninstall()
Loupedeck.Plugin :: virtual Boolean InstallAdmin()
Loupedeck.Plugin :: virtual String[] GetProcessNamesToCloseBeforeInstall()
```

(Verified by reflection against the installed `PluginApi.dll` 6.4.1.3246. These are the
"installation methods" the approval guidelines say the package installer runs.)

**Do not use them to wire `~/.claude/settings.json`.** Logitech QA raised this against
[claude-console](https://github.com/rshankras/claude-console) as finding **#31**, and the resolution
recorded in [their submission doc](https://github.com/rshankras/claude-console/blob/main/SUBMISSION.md)
is unambiguous — *"The settings.json edit is opt-in, reversible and disclosed. Nothing is written on
install or load."*

`Install()` runs before the user has seen the plugin do anything: they clicked install on a listing,
not "please edit my Claude Code configuration". A press on a key they placed themselves is real
consent, and unlike an install-time side effect it has an obvious gesture to reverse it.

**What ClaudeWarp does instead:**

| Moment | `~/.claude/settings.json` |
|---|---|
| Package install | untouched |
| Plugin load | untouched — the hook script is extracted to `~/.claude/keypad/`, nothing more |
| First press on a **Set up** key | untouched — the key repaints to *Press again*, 15 s window |
| Second press within 15 s | hooks merged in, rolling backup taken, notice posted in Options+ |
| Long press on a wired key | the plugin's own entries removed, nothing else touched |

## Pre-submission checklist

### Package hygiene

- [x] **`<Private>false</Private>` on the `PluginApi` reference.** The host provides `PluginApi.dll`
      and its dependency closure at load time; shipping them is the single most common rejection —
      claude-console's 2.0.0 was bounced for exactly this. Verify before every submission:
      `unzip -l ClaudeWarp_1.0.0.lplug4 | grep -Ei 'PluginApi|Newtonsoft|YamlDotNet|Svg|ExCSS'`
      must return nothing.
- [x] `LoupedeckPackage.yaml` in `../ClaudeWarpPlugin/src/package/metadata/` with `Icon256x256.png` beside it.
- [x] `name: ClaudeWarp` — the immutable unique ID. `[a-zA-Z0-9_-]+`, and must not end in "Plugin".
      **Cannot be changed after publication.**
- [x] `version: 1.0.0` — must increase on every update.
- [x] `author: vsemenchenko` — displayed publicly on the listing.
- [x] `minimumLoupedeckVersion: 6.4` — the first Logi Plugin Service on .NET 10. A net10.0 DLL will
      not load on older .NET 8 services.
- [x] `supportedDevices: MxCreativeKeypad` only, matching reality.
- [x] **Universal plugin**: `HasNoApplication` in the yaml, and a `ClaudeWarpApplication` class that
      overrides nothing. The service requires the class to exist; the yaml makes it bind nothing.
      This is the shape Logitech directed claude-console toward (their #23).
- [x] No `.DS_Store` in the packaged tree (`src/package/` **and** the build output — `CopyPackage`
      copies the former, but a stale one in `bin/` survives rebuilds).
- [x] `pluginFolderWin` removed — the plugin is macOS-only (`warp://` deep links, `osascript`,
      Warp's sqlite file), and approval checks that claimed OS support matches reality.
- [x] Pack **Release**, not Debug. `DebugType=none` for Release, so no `.pdb` ships — it is weight
      and it leaks local source paths.
- [x] `logiplugintool` installed. It lives in `~/.dotnet/tools`, which is only on `PATH` after
      `source env.sh` — `which logiplugintool` fails without that and looks like it is missing.
- [x] **Packed and verified**: 5 files, 64 KB, `logiplugintool verify` OK.

### Legal

- [x] **MIT.** Only Apache 2.0 and MIT are accepted; GPL is rejected outright.
- [x] **EULA** written: [EULA.md](EULA.md) — names the developer as licensor, states Logitech is
      not a party and owes no support, and carries the MIT grant. `licenseUrl` points at it.
      **Not legally reviewed.** Developer Agreement §7.1: *"You must provide your own end user license agreement."*
      MIT does not satisfy this — the agreement wants a document naming you as licensor and stating
      Logitech is not a party. See `EULA.md`, and publish it at a reachable URL.
- [x] **Privacy policy** written: [PRIVACY.md](PRIVACY.md). Audited rather than assumed — the source
      has no `System.Net`/`HttpClient`/socket use, no external URLs, and launches only `open`,
      `osascript` and `sqlite3`. Required if the product collects personal data. ClaudeWarp collects none
      and transmits nothing, but it does read project paths, git branch names and Warp's session
      database — all locally. `PRIVACY.md` says so plainly; publish it too.
- [ ] Accept the Marketplace Developer Agreement (part of the first submission).

### Links

- [x] `homePageUrl` → 200. `supportPageUrl` → 200. The README `#setup` anchor → 200.
- [ ] **`licenseUrl` → 404.** `blob/main/docs/EULA.md` does not exist until the repo is pushed.
      **This is a submission blocker.**

Re-run this after pushing; all four must be 200:

```sh
for u in \
  https://github.com/pffan91/claudewarp-keypad-mx \
  https://github.com/pffan91/claudewarp-keypad-mx/issues \
  https://github.com/pffan91/claudewarp-keypad-mx/blob/main/docs/EULA.md \
  https://github.com/pffan91/claudewarp-keypad-mx/blob/main/docs/PRIVACY.md ; do
  printf '%s %s\n' "$(curl -s -o /dev/null -w '%{http_code}' -L "$u")" "$u"
done
```

> Note: claude-console's rule that *no user-facing link may point at github.com* is **theirs**,
  > not Logitech's — they were taking their repo private. This repo is public and MIT, so GitHub
  > links are fine.

### Behaviour

- [x] **Nothing is written outside the plugin's own data directory without a deliberate press.**
      See the table above.
- [x] The wiring is **additive and idempotent**: entries are identified by `keypad-hook.sh`, any
      previous copy is dropped before re-adding, `statusLine` is never touched, and other plugins'
      hook entries are preserved.
- [x] A **rolling backup** (`settings.json.claudewarp.bak`) is written before every change — rewritten
      each time rather than left as a stale snapshot.
- [x] Writes are atomic (temp file + rename), so an interrupted write cannot truncate the user's
      settings.
- [x] **No `python3` dependency.** On a Mac without Xcode Command Line Tools, `/usr/bin/python3` is a
      hard-linked CLT shim — same inode as `/usr/bin/git` and `/usr/bin/clang` — and invoking it pops
      the developer-tools install dialog instead of running. The JSON merge is done in C# for this
      reason; the shell installer in `hooks/` remains for people working from source.
- [x] The **off switch reaches users who never had the repo**: long press unwires, and the plugin
      owns both directions.

### Testing

- [ ] Tested on the supported hardware (MX Creative Keypad).
- [ ] **Clean-machine pass** — a fresh macOS user account with no dev tools, no Homebrew, no Xcode:
  - [ ] Install the `.lplug4` by double-clicking it.
  - [ ] Keys appear under the **Claude** group in Options+ and can be dragged onto a page.
  - [ ] Before setup: keys read **Set up**; `~/.claude/settings.json` is byte-identical to before.
  - [ ] One press → *Press again*. Wait 20 s → back to **Set up**, still no write.
  - [ ] Two presses → wired; Options+ shows the notice; the backup file exists; a `git diff` of
        `settings.json` shows **only** additions.
  - [ ] Start a Claude Code session in a Warp pane → a tile appears and turns coral.
  - [ ] Long press → unwired; the diff is back to empty.
  - [ ] Install over an existing install; confirm `~/.claude/keypad/config.json` survives untouched.
- [x] **Accessibility permission is reported, not silent.** The command keys drive Warp through
      `osascript`/System Events, and without the grant every typing key does nothing. `WarpInput`
      now detects the denial (System Events error `-1719`, or "not allowed assistive access") and
      raises an event the plugin turns into a `PluginStatus.Error` notice in Options+ with a link to
      the fix. Reactive rather than probed at load, so the system prompt is not raised before the
      user presses anything that needs it.
- [ ] **Confirm that notice actually appears.** The detection is reasoned from the documented error,
      not observed — Accessibility is granted on the dev machine, so the denial path has never run.
      Revoke it for Logi Plugin Service, press a command key, and check the Options+ strip.
- [ ] Verify with a second Claude Code plugin installed (ClaudeDesktop, ClaudeConsole) that all three
      coexist and no hook entries are lost.

### Build and pack

Verified working, in this order:

```sh
source env.sh                       # ~/.dotnet/tools on PATH, DOTNET_ROOT, roll-forward
dotnet build ClaudeWarpPlugin/src/ClaudeWarpPlugin.csproj -c Release
logiplugintool pack ./ClaudeWarpPlugin/bin/Release/ ./ClaudeWarp_1.0.0.lplug4
logiplugintool verify ./ClaudeWarp_1.0.0.lplug4
unzip -l ClaudeWarp_1.0.0.lplug4 | grep -Ei 'PluginApi|Newtonsoft|YamlDotNet|Svg|ExCSS|\.pdb'
```

The last line must print nothing.

> A Release build rewrites `ClaudeWarpPlugin.link` to point at `bin/Release`, which switches what the
> running service loads. Restore it afterwards if you want the Debug dev loop back:
> `echo "$PWD/ClaudeWarpPlugin/bin/Debug " > ~/Library/Application\ Support/Logi/LogiPluginService/Plugins/ClaudeWarpPlugin.link`

### Submit

- [ ] Push the repo, then re-run the link check above — all four must be 200.
- [ ] Local install test from the package, not the dev link:
      `logiplugintool install ./ClaudeWarp_1.0.0.lplug4`. Remove the `.link` file first, or the
      service will be loading the source tree instead of the package and the test proves nothing.
- [ ] Submit at <https://marketplace.logitech.com/contribute>

## Listing copy

Keep this in sync with the `description` in `LoupedeckPackage.yaml` and the README.

**Short:** Live status tiles for Claude Code sessions running in Warp panes — one keypad page per
Warp tab. Press a tile to jump straight to that pane.

**Requirements to state on the listing**, because all three are load-bearing and none are obvious:

- macOS, Warp terminal, Claude Code.
- Setup is one key press inside Options+; it adds hooks to `~/.claude/settings.json` and can be
  undone with a long press.
- The command keys need Accessibility permission for Logi Plugin Service.
