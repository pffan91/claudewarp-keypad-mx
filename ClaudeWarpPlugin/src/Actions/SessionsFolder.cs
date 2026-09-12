namespace Loupedeck.ClaudeWarpPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading;

    // One keypad page per Warp tab.
    //
    // The host chunks a dynamic folder's action list into pages, and on the MX Creative Keypad it
    // takes the top-left key for its own Back button, leaving EIGHT usable tiles per page (measured,
    // not assumed - see FINDINGS.md). So emitting exactly eight names per Warp tab makes page N land
    // on tab N for free, and the number of pages equals the number of tabs that actually have
    // sessions - which is why pressing the right button past the last tab does nothing.
    public class SessionsFolder : PluginDynamicFolder
    {
        private const Int32 TilesPerPage = 8;

        private static readonly Regex HexUuid = new("^[0-9a-f]{32}$", RegexOptions.Compiled);

        private readonly Timer _tick;
        private volatile Boolean _open;
        private volatile String _lastFocused;
        private volatile Int32 _frame;

        // The tile whose long press has already been acted on, so its Release can be ignored.
        private volatile String _escaped;

        public SessionsFolder()
        {
            this.DisplayName = "Claude Sessions";
            this.GroupName = "Claude";

            // Animation only has to run while someone is looking at it.
            this._tick = new Timer(_ => this.OnTick(), null, Timeout.Infinite, Timeout.Infinite);
        }

        private static SessionStore Store => SessionStore.Instance;

        public override PluginDynamicFolderNavigation GetNavigationArea(DeviceType deviceType) =>
            PluginDynamicFolderNavigation.ButtonArea;

        public override Boolean Activate()
        {
            this._open = true;
            Store.Changed += this.OnSessionsChanged;
            KeypadConfig.Changed += this.OnConfigChanged;
            this._tick.Change(TileRenderer.TickMs, TileRenderer.TickMs);
            return base.Activate();
        }

        public override Boolean Deactivate()
        {
            this._open = false;
            Store.Changed -= this.OnSessionsChanged;
            KeypadConfig.Changed -= this.OnConfigChanged;
            this._tick.Change(Timeout.Infinite, Timeout.Infinite);
            return base.Deactivate();
        }

        // The number of command keys is part of the page layout, not just its contents, so a config
        // edit has to rebuild the pages the same way a new session does.
        private void OnConfigChanged(Object sender, EventArgs e)
        {
            this.ButtonActionNamesChanged();
            this.RepaintVisible();
        }

        private void OnSessionsChanged(Object sender, EventArgs e)
        {
            // A change in WHICH tiles exist changes the pages themselves; a change in what a tile says
            // does not. Rebuilding the page list on every state change would be visible churn.
            if (e is SessionsChangedEventArgs)
            {
                this.ButtonActionNamesChanged();
            }

            this.RepaintVisible();
        }

        // Advances the animation: the busy sweep and the attention blink.
        //
        // Only the tiles that are actually moving are repainted. Redrawing the whole page four times
        // a second would push eight images per tick to the device for the sake of one or two that
        // changed, and an all-idle deck would animate nothing at all yet still cost the traffic.
        private void OnTick()
        {
            if (!this._open)
            {
                return;
            }

            var animated = new List<String>();
            foreach (var g in Store.Groups)
            {
                foreach (var s in g.Sessions)
                {
                    if (s.State == "busy" || s.State == "attention")
                    {
                        animated.Add($"s:{s.WarpUuid}");
                    }
                }
            }

            if (animated.Count == 0)
            {
                return;
            }

            this._frame++;

            foreach (var name in animated)
            {
                this.CommandImageChanged(name);
            }
        }

        private void RepaintVisible()
        {
            foreach (var name in this.BuildParameters())
            {
                this.CommandImageChanged(name);
            }
        }

        // The page layout, as raw action parameters. Every tab contributes exactly TilesPerPage
        // entries per page so the host's chunking lines up with tab boundaries - which is why the
        // session tiles and the command keys have to be counted together rather than separately.
        private List<String> BuildParameters()
        {
            // Snapshotted once: the config file is re-read on a timer and must not change the split
            // halfway through building a page.
            var keys = KeypadConfig.Keys;
            var perPage = Math.Max(1, TilesPerPage - keys.Count);

            var list = new List<String>();

            foreach (var g in Store.Groups)
            {
                var pages = Math.Max(1, (g.Sessions.Count + perPage - 1) / perPage);

                for (var page = 0; page < pages; page++)
                {
                    for (var slot = 0; slot < perPage; slot++)
                    {
                        var index = (page * perPage) + slot;
                        list.Add(index < g.Sessions.Count
                            ? $"s:{g.Sessions[index].WarpUuid}"
                            : $"x:{g.WindowId}:{g.TabId}:{page}:{slot}");
                    }

                    // The window/tab/page suffix is not decoration: action parameters have to be
                    // unique across the whole list or the host collapses them into one tile.
                    for (var i = 0; i < keys.Count; i++)
                    {
                        list.Add($"k:{i}:{g.WindowId}:{g.TabId}:{page}");
                    }
                }
            }

            return list;
        }

        public override IEnumerable<String> GetButtonPressActionNames(DeviceType deviceType)
        {
            var groups = Store.Groups;
            var summary = String.Join(", ", groups.Select(g => $"w{g.WindowId}t{g.TabId}:{g.Sessions.Count}"));
            PluginLog.Info(
                $"pages built for {deviceType}: {groups.Count} tab group(s) [{summary}], " +
                $"{KeypadConfig.Keys.Count} command key(s)");

            var names = new List<String>();
            foreach (var p in this.BuildParameters())
            {
                names.Add(this.CreateCommandName(p));
            }

            if (names.Count == 0)
            {
                names.Add(this.CreateCommandName("x:none"));
            }

            return names;
        }

        // Long press on a session tile interrupts THAT session.
        //
        // Short press still focuses, through the default path. The value is not just a second action
        // per tile at no tile cost: the command-row escape acts on whatever is frontmost, which
        // depends on _lastFocused and a sleep, whereas this one names the pane it is interrupting.
        //
        // ProcessButtonEvent2 rather than ProcessButtonEvent - the older overload is [Obsolete] and
        // reduces the same information to two booleans. The enum distinguishes RepeatPress, which
        // the boolean form cannot, and holding a key long enough will produce those.
        public override Boolean ProcessButtonEvent2(String actionParameter, DeviceButtonEvent2 buttonEvent)
        {
            if (actionParameter == null || !actionParameter.StartsWith("s:", StringComparison.Ordinal))
            {
                return false;
            }

            PluginLog.Info($"tile event: {buttonEvent.EventType} after {buttonEvent.PressDuration}ms");

            switch (buttonEvent.EventType)
            {
                case DeviceButtonEventType.LongPress:
                    this._escaped = actionParameter;
                    this.EscapePane(actionParameter.Substring(2));
                    return true;

                // Holding past the threshold keeps producing these; one interrupt per hold is enough.
                case DeviceButtonEventType.RepeatPress:
                    return true;

                // Swallow the release that follows a long press, so a hold does not also focus.
                case DeviceButtonEventType.Release when this._escaped == actionParameter:
                    this._escaped = null;
                    return true;

                default:
                    return false;
            }
        }

        private void EscapePane(String uuid)
        {
            if (!HexUuid.IsMatch(uuid))
            {
                return;
            }

            WarpFocus.Pane(uuid);
            this._lastFocused = uuid;

            // The pane has to be in front before Escape can reach it, and focusing is a window
            // server round trip rather than a synchronous call.
            Thread.Sleep(350);

            if (WarpInput.SendEscape())
            {
                PluginLog.Info($"long press sent Escape to pane {uuid}");
            }
        }

        public override void RunCommand(String actionParameter)
        {
            if (actionParameter == null)
            {
                return;
            }

            if (actionParameter.StartsWith("k:", StringComparison.Ordinal))
            {
                this.RunConfiguredKey(actionParameter);
                return;
            }

            if (!actionParameter.StartsWith("s:", StringComparison.Ordinal))
            {
                return;
            }

            var uuid = actionParameter.Substring(2);
            PluginLog.Info($"focus requested for pane {uuid}");

            // This value becomes part of a URL handed to /usr/bin/open. Warp writes 32 lowercase hex;
            // anything else is not ours and is not going anywhere near a shell-adjacent API.
            if (!HexUuid.IsMatch(uuid))
            {
                return;
            }

            WarpFocus.Pane(uuid);
            this._lastFocused = uuid;
        }

        private void RunConfiguredKey(String actionParameter)
        {
            var key = Lookup(actionParameter);
            if (key == null)
            {
                // The config shrank between the page being built and the key being pressed.
                return;
            }

            if (key.IsEscape)
            {
                this.SendEscape();
                return;
            }

            this.SendCommand(key);
        }

        // "k:<index>:<window>:<tab>:<page>" back to the entry it was built from.
        private static KeyDef Lookup(String actionParameter)
        {
            var parts = actionParameter.Split(':');
            if (parts.Length < 2 || !Int32.TryParse(parts[1], out var index))
            {
                return null;
            }

            var keys = KeypadConfig.Keys;
            return index >= 0 && index < keys.Count ? keys[index] : null;
        }

        // Puts the pane the keys will act on in front.
        //
        // If Warp is already frontmost the keys act on whatever pane you are in, which respects
        // switching panes by hand. If it is not, the last tile you pressed is brought forward first,
        // so the intended flow - press a session, then press compact - lands in the right place.
        private Boolean EnsureWarpFocused()
        {
            if (WarpInput.IsWarpFrontmost())
            {
                return true;
            }

            var uuid = this._lastFocused;
            if (uuid == null)
            {
                PluginLog.Warning("Nothing sent: Warp is not in front and no session has been pressed yet.");
                return false;
            }

            WarpFocus.Pane(uuid);

            // Give the window server time to bring the pane forward before typing at it.
            Thread.Sleep(350);
            return true;
        }

        private void SendCommand(KeyDef key)
        {
            if (this.EnsureWarpFocused() && WarpInput.TypeText(key.Text, key.Submit))
            {
                PluginLog.Info(
                    $"typed \"{key.Text}\"{(key.Submit ? " + Return" : "")} into the focused Warp pane");
            }
        }

        private void SendEscape()
        {
            if (this.EnsureWarpFocused() && WarpInput.SendEscape())
            {
                PluginLog.Info("sent Escape to the focused Warp pane");
            }
        }

        public override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            if (actionParameter != null && actionParameter.StartsWith("s:", StringComparison.Ordinal))
            {
                var uuid = actionParameter.Substring(2);
                foreach (var g in Store.Groups)
                {
                    foreach (var s in g.Sessions)
                    {
                        if (s.WarpUuid == uuid)
                        {
                            return TileRenderer.Session(s, imageSize, this._frame);
                        }
                    }
                }

                return TileRenderer.Blank(imageSize);
            }

            if (actionParameter != null && actionParameter.StartsWith("k:", StringComparison.Ordinal))
            {
                var key = Lookup(actionParameter);
                return key != null
                    ? TileRenderer.Command(key.Label, null, imageSize)
                    : TileRenderer.Blank(imageSize);
            }

            return TileRenderer.Blank(imageSize);
        }

        public override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => "";
    }
}
