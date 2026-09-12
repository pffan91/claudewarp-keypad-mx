namespace Loupedeck.ClaudeWarpPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Text;

    // Finds Claude Code sessions that are running but have not reported themselves.
    //
    // Hooks only fire on activity, so a session that was already running when the hooks were installed
    // - or that has simply been idle - has no state file and would be invisible. Each `claude` process
    // inherits its Warp pane's WARP_TERMINAL_SESSION_UUID, which is enough to place it on the right
    // tab. This runs once at startup; after that the hooks carry everything.
    public sealed class DiscoveredSession
    {
        public Int32 Pid { get; init; }

        // Present only for sessions started with `claude --resume <id>`, which put the id on the
        // command line. A plain `claude` does not, and those stay unnamed until their first hook.
        public String SessionId { get; init; } = "";
    }

    public static class ClaudeProcesses
    {
        // Returns pane uuid -> session for every live interactive Claude Code session.
        public static Dictionary<String, DiscoveredSession> Discover()
        {
            var result = new Dictionary<String, DiscoveredSession>(StringComparer.Ordinal);

            var pids = new List<Int32>();
            foreach (var line in Run("/bin/ps", "-axo", "pid=,comm=").Split('\n'))
            {
                var trimmed = line.Trim();
                var space = trimmed.IndexOf(' ');
                if (space <= 0 || !Int32.TryParse(trimmed.Substring(0, space), out var pid))
                {
                    continue;
                }

                var command = trimmed.Substring(space + 1).Trim();
                var slash = command.LastIndexOf('/');
                var name = slash >= 0 ? command.Substring(slash + 1) : command;

                if (name == "claude")
                {
                    pids.Add(pid);
                }
            }

            if (pids.Count == 0)
            {
                return result;
            }

            // One `ps` for all of them. `eww` is what makes it print each process's environment.
            var csv = new StringBuilder();
            foreach (var pid in pids)
            {
                if (csv.Length > 0)
                {
                    csv.Append(',');
                }

                csv.Append(pid);
            }

            foreach (var line in Run("/bin/ps", "eww", "-o", "pid=,command=", "-p", csv.ToString()).Split('\n'))
            {
                var trimmed = line.TrimStart();
                var space = trimmed.IndexOf(' ');
                if (space <= 0 || !Int32.TryParse(trimmed.Substring(0, space), out var pid))
                {
                    continue;
                }

                // Programmatic invocations (the SDK's stream-json mode) share their pane's uuid but are
                // not sessions anyone would want to jump to.
                if (trimmed.Contains("--output-format stream-json", StringComparison.Ordinal))
                {
                    continue;
                }

                const String Key = "WARP_TERMINAL_SESSION_UUID=";
                var at = trimmed.IndexOf(Key, StringComparison.Ordinal);
                if (at < 0 || at + Key.Length + 32 > trimmed.Length)
                {
                    continue;
                }

                var uuid = trimmed.Substring(at + Key.Length, 32);
                if (!IsHex32(uuid))
                {
                    continue;
                }

                var sessionId = "";
                var resumeAt = trimmed.IndexOf("--resume", StringComparison.Ordinal);
                if (resumeAt >= 0)
                {
                    var rest = trimmed.Substring(resumeAt + "--resume".Length).TrimStart();
                    var end = rest.IndexOf(' ');
                    var candidate = (end > 0 ? rest.Substring(0, end) : rest).Trim();
                    if (candidate.Length == 36 && candidate[8] == '-')
                    {
                        sessionId = candidate;
                    }
                }

                // Lowest pid wins: if a pane holds more than one, the first is the interactive one.
                if (!result.TryGetValue(uuid, out var existing) || pid < existing.Pid)
                {
                    result[uuid] = new DiscoveredSession { Pid = pid, SessionId = sessionId };
                }
            }

            return result;
        }

        private static Boolean IsHex32(String s)
        {
            if (s.Length != 32)
            {
                return false;
            }

            foreach (var c in s)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }

            return true;
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
                    return "";
                }

                var output = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(3000))
                {
                    try
                    {
                        p.Kill(true);
                    }
                    catch
                    {
                        // Already gone.
                    }

                    return "";
                }

                return output;
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"Could not enumerate Claude processes: {ex.Message}");
                return "";
            }
        }
    }
}
