namespace Loupedeck.ClaudeWarpPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Threading;

    // A single key, for a home-page slot rather than the sessions folder: how many Claude sessions
    // are waiting on you, and one press to jump to the one that has waited longest.
    //
    // Deliberately NOT a tile inside the folder. Eight tiles is the whole page and they are already
    // spoken for, and in the folder this would be redundant anyway - the waiting session is right
    // there to press. Its value is entirely in being visible while the folder is closed and you are
    // working in something else.
    public class NeedsMeCommand : SessionKeyCommand
    {
        private readonly Timer _blink;
        private volatile Int32 _frame;

        public NeedsMeCommand()
            : base("Needs me", "Shows how many Claude sessions are waiting for you; press to jump to one")
        {
            this._blink = new Timer(_ => this.OnBlink(), null, Timeout.Infinite, Timeout.Infinite);
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            var all = AllSessions();
            return TileRenderer.NeedsMe(
                Count(all, "attention"), Count(all, "done"), Count(all, "busy"), all.Count,
                imageSize, this._frame);
        }

        // The sessions waiting on you, longest first, with prompts you have to answer beating turns
        // that merely finished.
        //
        // One group or the other, never both at once, so that pressing walks exactly the sessions the
        // number on the key is counting. Mixing them would make a key reading "2" take four presses
        // to come back round.
        protected override List<SessionInfo> Candidates()
        {
            var all = AllSessions();
            var blocked = InState(all, "attention");

            return blocked.Count > 0 ? blocked : InState(all, "done");
        }

        // The blink runs only while something is genuinely waiting, so a quiet deck costs nothing.
        protected override void OnCountsChanged(List<SessionInfo> all)
        {
            var blinking = Count(all, "attention") > 0;

            this._blink.Change(
                blinking ? TileRenderer.TickMs : Timeout.Infinite,
                blinking ? TileRenderer.TickMs : Timeout.Infinite);
        }

        private void OnBlink()
        {
            this._frame++;
            this.ActionImageChanged();
        }
    }
}
