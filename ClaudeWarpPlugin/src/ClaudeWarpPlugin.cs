namespace Loupedeck.ClaudeWarpPlugin
{
    using System;

    public class ClaudeWarpPlugin : Plugin
    {
        public override Boolean UsesApplicationApiOnly => true;

        // A deck of session tiles is for watching sessions WHILE working somewhere else, so it must
        // not be bound to an application - an application profile would switch away the moment you
        // focused Warp, which is the opposite of what a status display is for.
        public override Boolean HasNoApplication => true;

        public ClaudeWarpPlugin()
        {
            PluginLog.Init(this.Log);
            PluginResources.Init(this.Assembly);
        }

        public override void Load()
        {
            // Turns a silent, log-only failure into something the user can see and act on. Reactive
            // rather than checked up front: probing Accessibility at load would raise the system
            // prompt before the user has pressed anything that needs it.
            //
            // Qualified, because inside a Plugin the bare name resolves to this.PluginStatus - the
            // status message property - rather than to the enum.
            WarpInput.AccessibilityDenied += (_, _) => this.OnPluginStatusChanged(
                Loupedeck.PluginStatus.Error,
                "macOS blocked the keystroke. Grant Logi Plugin Service access under System Settings > "
                + "Privacy & Security > Accessibility, then press the key again.",
                "https://github.com/pffan91/claudewarp-keypad-mx#3-allow-typing-only-for-the-command-keys",
                "How to fix this");

            // Everything this plugin writes on load stays inside ~/.claude/keypad/, a directory it
            // owns. It does NOT wire itself into ~/.claude/settings.json here - that needs a
            // confirmed press on a Set up key. See HookWiring and docs/SUBMISSION.md for why.
            HookWiring.ExtractScript();
            HookWiring.SeedConfig();

            if (!HookWiring.IsWired)
            {
                PluginLog.Info("Claude Code hooks are not wired yet; press a Set up key to enable.");
            }

            // Warm the store off this thread so the first folder open is already populated. Doing it
            // synchronously is what previously blew Load's 10 second budget and unregistered the
            // plugin; Task.Run keeps Load itself instant.
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    _ = SessionStore.Instance.Groups;
                }
                catch (Exception ex)
                {
                    PluginLog.Warning($"Could not warm the session store: {ex.Message}");
                }
            });

            // Deliberately does no work. The host gives Load a 10 second budget and drops the plugin
            // entirely if it overruns - constructing the session store here (which shells out to
            // sqlite3 and probes process liveness) is what made the plugin vanish from Options+.
            // The store builds itself lazily, off this thread.
            PluginLog.Info(WarpTabs.IsAvailable
                ? "Warp tab database found; sessions will be grouped one page per tab."
                : $"Warp tab database not found at {WarpTabs.DatabasePath}; falling back to a single ungrouped page.");
        }

        public override void Unload()
        {
            // The config poll is a timer on a static field, and statics are per load context rather
            // than per process. Without this, every reload leaves its predecessor's timer running.
            KeypadConfig.Shutdown();
            HookWiring.Shutdown();
            WarpInput.Shutdown();
            SessionStore.Shutdown();
        }
    }
}
