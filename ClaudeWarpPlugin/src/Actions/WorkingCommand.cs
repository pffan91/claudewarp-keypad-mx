namespace Loupedeck.ClaudeWarpPlugin
{
    using System;

    // The companion to Needs me, and the opposite question.
    //
    // Needs me tells you whether YOU are the bottleneck. This one tells you whether anything is
    // still running - the thing you want to know before closing the laptop, and the one number the
    // sessions folder cannot give you at a glance while it is closed.
    public class WorkingCommand : SessionKeyCommand
    {
        public WorkingCommand()
            : base("Working", "Shows how many Claude sessions are working; press to jump to one")
        {
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            TileRenderer.Working(Count(AllSessions(), "busy"), imageSize);

        // Longest-running first, so the first press lands on the likeliest to be stuck, and pressing
        // again walks the rest.
        protected override List<SessionInfo> Candidates() => InState(AllSessions(), "busy");
    }
}
