namespace Loupedeck.ClaudeWarpPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    // Shared machinery for the standalone home-page keys - the ones that live outside the sessions
    // folder and each answer a single question without opening anything.
    //
    // All of it is about being right while nobody is looking: subscribed for the plugin's lifetime
    // rather than only while a folder is open, and repainting only when the answer actually changes,
    // because the store re-reads every couple of seconds whether or not anything moved.
    public abstract class SessionKeyCommand : PluginDynamicCommand
    {
        private String _signature = "";

        // The pane the last press jumped to, so the next press can carry on from it.
        private volatile String _cursor;

        // The device type is not decoration. A PluginDynamicCommand bakes its image size in at
        // construction, and the default (DeviceType.All) resolves to Width90 - an 80px image drawn
        // inside a 90px button, which shows up as a key with a grey border round it. The keypad's
        // own size, Width116, is 116px in a 116px button: edge to edge. Folders escape this because
        // the host passes them the real device on every call.
        protected SessionKeyCommand(String displayName, String description)
            : base(displayName, description, "Claude", (DeviceType)DeviceTypeAliases.MxCreativeKeypad)
        {
            // The host's default layout for an action on a key is icon-above-title: it shrinks the
            // plugin's image to make room for the action name underneath. A widget owns the whole
            // key and draws it itself - which is also why these keys paint their own label.
            this.IsWidget = true;

            SessionStore.Instance.Changed += (_, _) => this.Refresh();

            // Setup state is part of what this key displays, so it repaints on the same terms as a
            // session count changing.
            HookWiring.Changed += (_, _) => this.ActionImageChanged();
        }

        protected static List<SessionInfo> AllSessions() =>
            SessionStore.Instance.Groups.SelectMany(g => g.Sessions).ToList();

        protected static Int32 Count(List<SessionInfo> all, String state) =>
            all.Count(s => s.State == state);

        // Oldest first, which is both the most useful order and a stable one: Since only changes
        // when a session changes state, and a session that changes state leaves this list anyway.
        protected static List<SessionInfo> InState(List<SessionInfo> all, String state) =>
            all.Where(s => s.State == state).OrderBy(s => s.Since).ToList();

        // Every session this key can jump to, in the order pressing it should walk them. Empty means
        // the press does nothing.
        protected abstract List<SessionInfo> Candidates();

        // What this key shows once it has something to count.
        protected abstract BitmapImage GetStateImage(PluginImageSize imageSize);

        // Until the hooks are wired there are no sessions to count and never will be, so a key
        // reporting a truthful zero would be indistinguishable from a broken one. It says what to do
        // instead.
        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            HookWiring.IsWired && HookWiring.Armed == SetupAction.None
                ? this.GetStateImage(imageSize)
                : TileRenderer.Setup(HookWiring.Armed, imageSize);

        // Called when the counts change, before the repaint - for a key that drives a timer off them.
        protected virtual void OnCountsChanged(List<SessionInfo> all)
        {
        }

        // Each press moves to the next one, wrapping at the end.
        //
        // A key that says "2" and can only ever reach one of them is telling you about a session it
        // will not show you, so the count and the press have to agree.
        protected override void RunCommand(String actionParameter)
        {
            // An armed key is answering a question, whatever it normally does.
            if (!HookWiring.IsWired || HookWiring.Armed != SetupAction.None)
            {
                HookWiring.Announce(this.Plugin, HookWiring.Press());
                return;
            }

            var candidates = this.Candidates();
            if (candidates.Count == 0)
            {
                PluginLog.Info($"{this.DisplayName}: nothing to jump to.");
                return;
            }

            // Where we are is remembered as a pane, not as a position. The list is rebuilt on every
            // press out of live state, so an index would point at a different session - or off the
            // end - as soon as one of them answered you. FindIndex returns -1 when the session we
            // last visited has since left the list, and -1 + 1 wraps to the first, which is exactly
            // the wanted behaviour: having dealt with one, start again from the most urgent.
            var at = candidates.FindIndex(s => s.WarpUuid == this._cursor);
            var next = candidates[(at + 1) % candidates.Count];

            this._cursor = next.WarpUuid;

            PluginLog.Info(
                $"{this.DisplayName}: {(at < 0 ? 1 : ((at + 1) % candidates.Count) + 1)} of {candidates.Count}");

            WarpFocus.Pane(next.WarpUuid);
        }

        // Returning empty stops the host writing the action's name across the key. The widget flag
        // is what actually gives us the whole face; the label is painted into the image instead.
        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => "";

        // Long press is the way back off. It only arms - the confirming press is a short one, so the
        // gesture that turns the hooks off is the same two-step as the one that turned them on.
        protected override Boolean ProcessButtonEvent2(String actionParameter, DeviceButtonEvent2 buttonEvent)
        {
            if (!HookWiring.IsWired || buttonEvent.EventType != DeviceButtonEventType.LongPress)
            {
                return false;
            }

            if (HookWiring.Armed == SetupAction.None)
            {
                HookWiring.Press();
            }

            return true;
        }

        private void Refresh()
        {
            var all = AllSessions();

            // One signature shared by every key of this kind: between them these four numbers cover
            // anything such a key can display, so each repaints exactly when something it might be
            // showing has moved. Cheaper than asking each subclass what it cares about, and wrong
            // only in the harmless direction - an occasional repaint of an identical image.
            var signature = $"{Count(all, "attention")}:{Count(all, "done")}:{Count(all, "busy")}:{all.Count}";

            if (signature == this._signature)
            {
                return;
            }

            this._signature = signature;
            this.OnCountsChanged(all);
            this.ActionImageChanged();
        }
    }
}
