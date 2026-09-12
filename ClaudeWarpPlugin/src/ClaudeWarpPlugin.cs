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
        }
    }
}
