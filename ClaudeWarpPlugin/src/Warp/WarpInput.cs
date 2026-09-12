namespace Loupedeck.ClaudeWarpPlugin
{
    using System;
    using System.Diagnostics;

    // Types text into the focused Warp pane.
    //
    // There is no API for this - Warp's URL scheme can focus a pane but not send it input - so this
    // goes through System Events, which means Logi Plugin Service needs Accessibility permission.
    // macOS prompts for that the first time and the key does nothing until it is granted.
    public static class WarpInput
    {
        public const String WarpBundleId = "dev.warp.Warp-Stable";

        // Raised the first time macOS refuses a keystroke for want of Accessibility permission.
        //
        // Without this the failure is a line in a log file, which is to say invisible: every typing
        // key silently does nothing and the plugin looks broken rather than unpermitted. The plugin
        // subscribes once and turns it into a notice in Options+.
        public static event EventHandler AccessibilityDenied;

        // One notice per spell of being denied, rather than one per press. Reset on the next success,
        // so granting permission and later revoking it reports again.
        private static Boolean _reported;

        // Refuses to type unless Warp is genuinely frontmost. Without this check a mistimed press
        // would send "/clear" into whatever application happened to be in front.
        //
        // Text and Return go through one script rather than two runs, because two runs means two
        // osascript launches with a gap in between - long enough for the pane to lose focus and for
        // the Return to land somewhere else.
        private const String Script = @"
on run argv
    tell application ""System Events""
        set frontId to bundle identifier of first application process whose frontmost is true
        if frontId is not ""dev.warp.Warp-Stable"" then return ""not-warp""
        set theText to item 1 of argv
        if theText is not """" then keystroke theText
        if (item 2 of argv) is ""1"" then key code 36
    end tell
    return ""ok""
end run";

        public static Boolean IsWarpFrontmost()
        {
            var result = Run("/usr/bin/osascript", "-e",
                "tell application \"System Events\" to get bundle identifier of first application process whose frontmost is true");
            return result.Trim() == WarpBundleId;
        }

        // Types text into the focused pane, optionally following it with Return.
        //
        // Empty text with submit is a bare Return - which is how a key answers a plan approval or a
        // question dialog, the commonest keystroke this plugin can save.
        //
        // Returns false when Warp was not frontmost, so nothing was typed.
        public static Boolean TypeText(String text, Boolean submit)
        {
            text ??= "";

            if (text.Length == 0 && !submit)
            {
                return false;
            }

            // Passed as an argument rather than interpolated into the script, so the text can never
            // be read as AppleScript. This is what makes user-supplied command text safe.
            var result = Run("/usr/bin/osascript", "-e", Script, text, submit ? "1" : "0").Trim();

            return Check(result, "type into Warp");
        }

        // Interrupts whatever the session is doing. Escape is a key code rather than a character,
        // so it cannot go through `keystroke` like the slash commands do - hence a second script.
        private const String EscapeScript = @"
on run argv
    tell application ""System Events""
        set frontId to bundle identifier of first application process whose frontmost is true
        if frontId is not ""dev.warp.Warp-Stable"" then return ""not-warp""
        key code 53
    end tell
    return ""ok""
end run";

        public static Boolean SendEscape()
        {
            var result = Run("/usr/bin/osascript", "-e", EscapeScript).Trim();

            return Check(result, "send Escape to Warp");
        }

        // Called from Plugin.Unload. A static event holding a handler that captures the plugin would
        // keep the whole previous load context alive across a reload.
        public static void Shutdown() => AccessibilityDenied = null;

        private static Boolean Check(String result, String what)
        {
            if (result == "ok")
            {
                _reported = false;
                return true;
            }

            if (result == "not-warp")
            {
                PluginLog.Warning($"Did not {what}: Warp is not the frontmost application.");
                return false;
            }

            PluginLog.Warning($"Could not {what}: {result}");

            // -1719 is what System Events returns when the calling process is not trusted for
            // Accessibility; the message accompanies it in most macOS versions, so match either.
            var denied = result.Contains("-1719", StringComparison.Ordinal)
                || result.Contains("not allowed assistive access", StringComparison.OrdinalIgnoreCase);

            if (denied && !_reported)
            {
                _reported = true;
                AccessibilityDenied?.Invoke(null, EventArgs.Empty);
            }

            return false;
        }

        private static String Run(String exe, params String[] args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                foreach (var a in args)
                {
                    psi.ArgumentList.Add(a);
                }

                using var p = Process.Start(psi);
                if (p == null)
                {
                    return "no-process";
                }

                var output = p.StandardOutput.ReadToEnd();
                var error = p.StandardError.ReadToEnd();

                // osascript can block on a permission prompt, so this must not wait forever.
                if (!p.WaitForExit(5000))
                {
                    try
                    {
                        p.Kill(true);
                    }
                    catch
                    {
                        // Already gone.
                    }

                    return "timeout";
                }

                return p.ExitCode == 0 ? output : error;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }
}
