namespace Loupedeck.ClaudeWarpPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;

    // Where a pane lives: which Warp window and tab, and what that tab is called.
    public sealed class WarpPaneLocation
    {
        public Int32 WindowId { get; init; }

        public Int32 TabId { get; init; }

        public String TabTitle { get; init; } = "";

        // The pane's working directory, as Warp records it. Used to name a session that has not yet
        // reported one of its own.
        public String Cwd { get; init; } = "";

        // Creation order within the tab. Stable for the life of the pane, which is what keeps a tile
        // from hopping to another key while you are looking at it.
        public Int32 PaneOrdinal { get; init; }
    }

    // Reads Warp's own record of its tab/pane tree.
    //
    // This is an undocumented internal schema inside Warp's group container, so every failure here is
    // survivable by design: if the file moves, the schema changes, or sqlite3 is unavailable, we
    // return nothing and the caller falls back to a single ungrouped page. Status and focus keep
    // working; only the per-tab grouping is lost.
    public static class WarpTabs
    {
        private const String DbRelativePath =
            "Library/Group Containers/2BBY89MBSN.dev.warp/Library/Application Support/dev.warp.Warp-Stable/warp.sqlite";

        private const String Query =
            "SELECT lower(hex(tp.uuid)), w.id, t.id, COALESCE(tp.cwd,''), COALESCE(t.custom_title,'') " +
            "FROM terminal_panes tp " +
            "JOIN pane_leaves pl ON pl.pane_node_id = tp.id " +
            "JOIN pane_nodes  pn ON pn.id = tp.id " +
            "JOIN tabs        t  ON t.id  = pn.tab_id " +
            "JOIN windows     w  ON w.id  = t.window_id " +
            "ORDER BY w.id, t.id, tp.id;";

        public static String DatabasePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), DbRelativePath);

        public static Boolean IsAvailable => File.Exists(DatabasePath);

        // Returns pane uuid -> location. Empty when Warp's database cannot be read.
        public static Dictionary<String, WarpPaneLocation> Read()
        {
            var result = new Dictionary<String, WarpPaneLocation>(StringComparer.Ordinal);

            var db = DatabasePath;
            if (!File.Exists(db))
            {
                return result;
            }

            String output;
            try
            {
                // Shelling out to the system sqlite3 avoids shipping a native SQLite binding for a
                // query this small. -readonly keeps us off Warp's write path entirely.
                var psi = new ProcessStartInfo("/usr/bin/sqlite3")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-readonly");
                psi.ArgumentList.Add("-noheader");
                psi.ArgumentList.Add("-separator");
                psi.ArgumentList.Add("\u001f");
                psi.ArgumentList.Add(db);
                psi.ArgumentList.Add(Query);

                using var p = Process.Start(psi);
                if (p == null)
                {
                    return result;
                }

                output = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(2000))
                {
                    try
                    {
                        p.Kill(true);
                    }
                    catch
                    {
                        // Already gone.
                    }

                    return result;
                }

                if (p.ExitCode != 0)
                {
                    return result;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"Could not read Warp's tab database: {ex.Message}");
                return result;
            }

            var lastWindow = Int32.MinValue;
            var lastTab = Int32.MinValue;
            var ordinal = 0;

            foreach (var line in output.Split('\n'))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                // uuid US windowId US tabId US cwd US title
                var parts = line.Split('\u001f');
                if (parts.Length < 5
                    || parts[0].Length != 32
                    || !Int32.TryParse(parts[1], out var windowId)
                    || !Int32.TryParse(parts[2], out var tabId))
                {
                    continue;
                }

                if (windowId != lastWindow || tabId != lastTab)
                {
                    lastWindow = windowId;
                    lastTab = tabId;
                    ordinal = 0;
                }

                result[parts[0]] = new WarpPaneLocation
                {
                    WindowId = windowId,
                    TabId = tabId,
                    Cwd = parts[3],
                    TabTitle = parts[4].Trim('\r'),
                    PaneOrdinal = ordinal++,
                };
            }

            return result;
        }
    }
}
