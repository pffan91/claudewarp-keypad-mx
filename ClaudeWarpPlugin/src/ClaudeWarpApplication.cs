namespace Loupedeck.ClaudeWarpPlugin
{
    using System;

    // The host appears to require a ClientApplication type even for a universal plugin, so this
    // exists but deliberately claims nothing: returning empty names is what keeps the plugin from
    // registering an application entry, alongside Plugin.HasNoApplication and the manifest capability.
    public class ClaudeWarpApplication : ClientApplication
    {
        public ClaudeWarpApplication()
        {
        }

        protected override String GetProcessName() => "";

        protected override String GetBundleName() => "";

        public override ClientApplicationStatus GetApplicationStatus() => ClientApplicationStatus.Unknown;
    }
}
