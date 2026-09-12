namespace Loupedeck.ClaudeWarpPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    // Every key in ~/.claude/keypad/config.json, published a second time as its own Options+ action
    // so it can be dropped on any home-page slot as well as sitting on the folder's bottom row.
    //
    // One class, many actions. AddParameter publishes an action per config entry and
    // ParametersChanged re-publishes when the file changes; a class per command is impossible here
    // because the host instantiates by reflection at load, and what the commands ARE is not known
    // until a user's config has been read.
    //
    // Unlike the folder's copy of the same key, this one never focuses a pane first. The folder
    // knows which tile you last pressed; a home-page key knows nothing, and silently bringing some
    // pane forward to type into would be worse than refusing. WarpInput declines unless Warp is
    // already frontmost, which is exactly the wanted behaviour here.
    public class ConfiguredKeyCommand : PluginDynamicCommand
    {
        private readonly EventHandler _onConfigChanged;

        private volatile IReadOnlyDictionary<String, KeyDef> _published = new Dictionary<String, KeyDef>();

        public ConfiguredKeyCommand()
            : base((DeviceType)DeviceTypeAliases.MxCreativeKeypad)
        {
            // Same reason as the other standalone keys: without this the host shrinks the image to
            // fit a title underneath, and a command key ends up as a stamp on a black field.
            this.IsWidget = true;

            // Held in a field so it can be unsubscribed. KeypadConfig.Changed is static, which means
            // it would otherwise keep this action alive past the plugin unload that discarded it.
            this._onConfigChanged = (_, _) => this.Publish();
        }

        protected override Boolean OnLoad()
        {
            KeypadConfig.Changed += this._onConfigChanged;
            this.Publish();
            return true;
        }

        protected override Boolean OnUnload()
        {
            KeypadConfig.Changed -= this._onConfigChanged;
            return true;
        }

        private void Publish()
        {
            var map = new Dictionary<String, KeyDef>(StringComparer.Ordinal);

            this.RemoveAllParameters();

            foreach (var key in KeypadConfig.Keys)
            {
                // Named after the label, not its position. Options+ stores this string inside the
                // user's profile, so an index would silently re-point an already-placed key the
                // moment someone reordered their config.
                var name = Unique(map, Slug(key.Label));
                map[name] = key;
                this.AddParameter(name, key.Label, "Commands");
            }

            this._published = map;
            this.ParametersChanged();
            PluginLog.Info($"published {map.Count} command action(s) for Options+");
        }

        protected override void RunCommand(String actionParameter)
        {
            if (actionParameter == null || !this._published.TryGetValue(actionParameter, out var key))
            {
                return;
            }

            if (key.IsEscape)
            {
                if (WarpInput.SendEscape())
                {
                    PluginLog.Info("sent Escape to the focused Warp pane");
                }

                return;
            }

            if (WarpInput.TypeText(key.Text, key.Submit))
            {
                PluginLog.Info($"typed \"{key.Text}\"{(key.Submit ? " + Return" : "")} into the focused Warp pane");
            }
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            actionParameter != null && this._published.TryGetValue(actionParameter, out var key)
                ? TileRenderer.Command(key.Label, null, imageSize)
                : TileRenderer.Blank(imageSize);

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => "";

        // A label is free text; an action name ends up in a saved profile, so keep it to characters
        // that survive a round trip through one.
        private static String Slug(String label)
        {
            var sb = new StringBuilder();
            foreach (var c in label ?? "")
            {
                sb.Append(Char.IsLetterOrDigit(c) ? Char.ToLowerInvariant(c) : '-');
            }

            var slug = sb.ToString().Trim('-');
            return slug.Length == 0 ? "key" : slug;
        }

        private static String Unique(IReadOnlyDictionary<String, KeyDef> taken, String name)
        {
            if (!taken.ContainsKey(name))
            {
                return name;
            }

            for (var n = 2; ; n++)
            {
                var candidate = $"{name}-{n}";
                if (!taken.ContainsKey(candidate))
                {
                    return candidate;
                }
            }
        }
    }
}
