namespace Loupedeck.ClaudeWarpPlugin
{
    using System;
    using System.Diagnostics;
    using System.Text.RegularExpressions;

    // Brings one Warp pane forward - window, tab and split - through Warp's own URL scheme.
    //
    // Undocumented, but Warp exports the matching WARP_FOCUS_URL into every pane's environment
    // itself, and it was measured working here before anything was built on it.
    public static class WarpFocus
    {
        private static readonly Regex HexUuid = new("^[0-9a-f]{32}$", RegexOptions.Compiled);

        public static Boolean Pane(String uuid)
        {
            // This value becomes part of a URL handed to /usr/bin/open. Warp writes 32 lowercase hex;
            // anything else is not ours and is not going anywhere near a shell-adjacent API.
            if (uuid == null || !HexUuid.IsMatch(uuid))
            {
                return false;
            }

            try
            {
                var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add($"warp://session/{uuid}");
                Process.Start(psi);
                PluginLog.Info($"opened warp://session/{uuid}");
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"Could not focus Warp pane {uuid}: {ex.Message}");
                return false;
            }
        }
    }
}
