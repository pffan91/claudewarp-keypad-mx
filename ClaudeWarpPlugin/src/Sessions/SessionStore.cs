namespace Loupedeck.ClaudeWarpPlugin
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;

    public sealed class SessionInfo
    {
        public String WarpUuid { get; set; } = "";

        public String SessionId { get; set; } = "";

        public String Project { get; set; } = "";

        public String Branch { get; set; } = "";

        // Claude Code's own name for the session, e.g. "cap-dom-price-ladder-1000-cells". The only
        // thing that distinguishes sessions sharing a checkout, since those share a branch too.
        public String Slug { get; set; } = "";

        // The opening prompt, recovered from the transcript. Preferred over the slug because it is
        // what you actually typed.
        public String Title { get; set; } = "";

        // idle | busy | attention | done
        public String State { get; set; } = "idle";

        public Int64 Since { get; set; }

        public Int32 Pid { get; set; }

        public WarpPaneLocation Location { get; set; }

        public TimeSpan Elapsed =>
            TimeSpan.FromSeconds(Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - this.Since));
    }

    public sealed class TabGroup
    {
        public Int32 WindowId { get; init; }

        public Int32 TabId { get; init; }

        public String Title { get; init; } = "";

        public List<SessionInfo> Sessions { get; } = new();
    }

    // Owns everything the tiles read: the hook-written state files, Warp's tab tree, and liveness.
    public sealed class SessionStore : IDisposable
    {
        private static readonly Lazy<SessionStore> Lazy = new(() => new SessionStore(), true);

        // The host constructs dynamic folders itself, so they cannot be handed dependencies, and the
        // order of Plugin.Load versus folder construction is not guaranteed. A lazy singleton makes
        // that ordering irrelevant.
        public static SessionStore Instance => Lazy.Value;

        // Tool-call hooks fire constantly; without coalescing, the device would repaint per tool call.
        private const Int32 DebounceMs = 150;

        private readonly String _dir;
        private readonly FileSystemWatcher _watcher;
        private readonly Timer _debounce;
        private readonly Timer _poll;
        private readonly Object _gate = new();

        private List<TabGroup> _groups = new();

        // A timer callback can already be in flight when Dispose runs; this stops it doing work
        // against a torn-down watcher.
        private volatile Boolean _disposed;

        // Where each pane was last seen. Warp persists its pane tree lazily, so closing one pane makes
        // a still-live pane briefly vanish from the database - and treating that blink as fact moved
        // sessions into the "unplaced" group, which renumbered every page. A pane that was in a tab a
        // moment ago is still in it.
        private readonly ConcurrentDictionary<String, WarpPaneLocation> _lastLocation = new(StringComparer.Ordinal);

        // Discovered once: sessions already running when the plugin started, which have no state file
        // because hooks only fire on activity. After startup the hooks are authoritative.
        private Dictionary<String, DiscoveredSession> _seeded;
        private Boolean _seedAttempted;

        public event EventHandler Changed;

        public SessionStore()
        {
            this._dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "keypad", "sessions");

            Directory.CreateDirectory(this._dir);

            this._debounce = new Timer(_ => this.Reload(), null, Timeout.Infinite, Timeout.Infinite);

            // Warp's database is polled rather than watched: it lives in another app's group container
            // and is written through a WAL, so file events there are not a reliable change signal.
            // Liveness reaping rides along on the same tick.
            // Due immediately but on a pool thread: the first scan must not run on the caller's
            // thread, because that caller may be Plugin.Load, which the host kills after 10 seconds.
            this._poll = new Timer(_ => this.Reload(), null, 0, 2000);

            this._watcher = new FileSystemWatcher(this._dir, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };

            this._watcher.Changed += this.OnFileEvent;
            this._watcher.Created += this.OnFileEvent;
            this._watcher.Deleted += this.OnFileEvent;
            this._watcher.Renamed += this.OnFileEvent;
        }

        public IReadOnlyList<TabGroup> Groups
        {
            get
            {
                lock (this._gate)
                {
                    return this._groups;
                }
            }
        }

        private void OnFileEvent(Object sender, FileSystemEventArgs e)
        {
            if (this._disposed)
            {
                return;
            }

            try
            {
                this._debounce.Change(DebounceMs, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                // Raced with Dispose; there is nothing left to schedule.
            }
        }

        private void Reload()
        {
            if (this._disposed)
            {
                return;
            }

            List<TabGroup> groups;
            try
            {
                groups = this.Build();
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"Could not rebuild session list: {ex.Message}");
                return;
            }

            var changed = false;
            lock (this._gate)
            {
                if (!Fingerprint(this._groups).SequenceEqual(Fingerprint(groups)))
                {
                    this._groups = groups;
                    changed = true;
                }
                else
                {
                    // Same shape, but the tiles still need the fresh state/elapsed values.
                    this._groups = groups;
                }
            }

            // Only a change in the SET of tiles requires rebuilding the folder's pages; a state change
            // just repaints. The caller distinguishes via the flag.
            this.Changed?.Invoke(this, changed ? SessionsChangedEventArgs.Structural : EventArgs.Empty);
        }

        // Identity of the page layout: which tabs, which panes, in which order. Deliberately excludes
        // state and elapsed time so a busy tile does not count as a structural change.
        private static IEnumerable<String> Fingerprint(List<TabGroup> groups) =>
            groups.SelectMany(g => g.Sessions.Select(s => $"{g.WindowId}:{g.TabId}:{s.WarpUuid}"));

        private List<TabGroup> Build()
        {
            var locations = WarpTabs.Read();
            var sessions = new List<SessionInfo>();
            var reported = new HashSet<String>(StringComparer.Ordinal);

            if (!this._seedAttempted)
            {
                this._seedAttempted = true;
                this._seeded = ClaudeProcesses.Discover();
                PluginLog.Info($"seeded {this._seeded.Count} already-running session(s) from process list");
            }

            foreach (var stateFile in Directory.EnumerateFiles(this._dir, "*.state.json"))
            {
                var uuid = Path.GetFileName(stateFile);
                uuid = uuid.Substring(0, uuid.Length - ".state.json".Length);
                if (uuid.Length != 32)
                {
                    continue;
                }

                var s = new SessionInfo { WarpUuid = uuid };

                if (!TryReadJson(stateFile, out var state))
                {
                    continue;
                }

                s.State = GetString(state, "state", "idle");
                s.Since = GetInt64(state, "since");

                var metaFile = Path.Combine(this._dir, uuid + ".meta.json");
                if (TryReadJson(metaFile, out var meta))
                {
                    s.SessionId = GetString(meta, "session_id", "");
                    s.Project = GetString(meta, "project", "");
                    s.Branch = GetString(meta, "branch", "");
                    s.Slug = GetString(meta, "slug", "");
                    var facts = SessionTitles.For(s.SessionId, GetString(meta, "transcript", ""));
                    s.Title = facts.Title;
                    if (String.IsNullOrEmpty(s.Slug))
                    {
                        s.Slug = facts.Slug;
                    }
                    s.Pid = (Int32)GetInt64(meta, "pid");
                }

                // A session killed outright never fires SessionEnd, so the files outlive it. The
                // process is the source of truth for whether it is still there.
                if (s.Pid > 0 && !IsRunning(s.Pid))
                {
                    TryDelete(stateFile);
                    TryDelete(metaFile);
                    continue;
                }

                s.Location = this.Place(uuid, locations);
                reported.Add(uuid);
                sessions.Add(s);
            }

            // Anything still running that has not reported in yet. Shown with no clock, because we
            // know it exists but not how long it has been in its current state.
            if (this._seeded != null)
            {
                foreach (var (uuid, found) in this._seeded)
                {
                    var loc = this.Place(uuid, locations);
                    if (reported.Contains(uuid) || loc == null || !IsRunning(found.Pid))
                    {
                        continue;
                    }

                    var seededFacts = SessionTitles.For(found.SessionId, FindTranscript(found.SessionId));

                    sessions.Add(new SessionInfo
                    {
                        WarpUuid = uuid,
                        Pid = found.Pid,
                        SessionId = found.SessionId,
                        State = "idle",
                        Since = 0,
                        Project = ProjectFromPath(loc.Cwd),
                        Title = seededFacts.Title,
                        Slug = seededFacts.Slug,
                        Location = loc,
                    });
                }
            }

            var groups = new Dictionary<(Int32, Int32), TabGroup>();
            foreach (var s in sessions)
            {
                // Sessions whose pane Warp cannot place (database unreadable, or a pane it does not
                // know) collapse into one ungrouped page rather than disappearing.
                var win = s.Location?.WindowId ?? -1;
                var tab = s.Location?.TabId ?? -1;

                if (!groups.TryGetValue((win, tab), out var g))
                {
                    g = new TabGroup
                    {
                        WindowId = win,
                        TabId = tab,
                        Title = s.Location?.TabTitle ?? "",
                    };
                    groups[(win, tab)] = g;
                }

                g.Sessions.Add(s);
            }

            // Unplaced sessions sort LAST. They used to sort first, because -1 is less than 1, so a
            // single unplaceable session silently pushed tab 1 off page 1 and shifted every page.
            var ordered = groups.Values
                .OrderBy(g => g.WindowId < 0 ? Int32.MaxValue : g.WindowId)
                .ThenBy(g => g.TabId < 0 ? Int32.MaxValue : g.TabId)
                .ToList();

            foreach (var g in ordered)
            {
                // Pane creation order, which Warp assigns and never reshuffles, keeps a tile on the
                // same key for the life of the pane.
                g.Sessions.Sort((a, b) =>
                {
                    var ao = a.Location?.PaneOrdinal ?? Int32.MaxValue;
                    var bo = b.Location?.PaneOrdinal ?? Int32.MaxValue;
                    return ao != bo ? ao.CompareTo(bo) : String.CompareOrdinal(a.WarpUuid, b.WarpUuid);
                });
            }

            return ordered;
        }

        // Warp's answer if it has one, otherwise wherever this pane was last seen. Only a pane that has
        // never been placed is left unplaced.
        private WarpPaneLocation Place(String uuid, Dictionary<String, WarpPaneLocation> locations)
        {
            if (locations.TryGetValue(uuid, out var loc) && loc != null)
            {
                this._lastLocation[uuid] = loc;
                return loc;
            }

            return this._lastLocation.TryGetValue(uuid, out var remembered) ? remembered : null;
        }

        // Transcripts live at ~/.claude/projects/<encoded cwd>/<session id>.jsonl, but the encoded
        // directory is not worth reconstructing - searching for the file by name is exact.
        private static String FindTranscript(String sessionId)
        {
            if (String.IsNullOrEmpty(sessionId))
            {
                return "";
            }

            try
            {
                var root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

                if (!Directory.Exists(root))
                {
                    return "";
                }

                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    var candidate = Path.Combine(dir, sessionId + ".jsonl");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch
            {
                // Not fatal: the tile simply shows its project name.
            }

            return "";
        }

        private static String ProjectFromPath(String path)
        {
            if (String.IsNullOrEmpty(path))
            {
                return "";
            }

            var trimmed = path.TrimEnd('/');
            var slash = trimmed.LastIndexOf('/');
            return slash >= 0 ? trimmed.Substring(slash + 1) : trimmed;
        }

        private static Boolean IsRunning(Int32 pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return !p.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private static void TryDelete(String path)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Another writer holds it; it will be reaped on the next tick.
            }
        }

        private static Boolean TryReadJson(String path, out JsonElement element)
        {
            element = default;
            try
            {
                if (!File.Exists(path))
                {
                    return false;
                }

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var doc = JsonDocument.Parse(fs);
                element = doc.RootElement.Clone();
                return true;
            }
            catch
            {
                // A half-written file: the hook writes atomically, so this is transient.
                return false;
            }
        }

        private static String GetString(JsonElement e, String name, String fallback) =>
            e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : fallback;

        private static Int64 GetInt64(JsonElement e, String name) =>
            e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt64(out var n)
                ? n
                : 0;

        // Called from Plugin.Unload, via Shutdown.
        //
        // Nothing used to call this, and the cost was not a tidiness one. A plugin is reloaded on
        // every update, every enable/disable and every rebuild, and statics live per AssemblyLoadContext
        // rather than per process - so each reload left a live FileSystemWatcher and a 2 second poll
        // timer behind, still querying Warp's database forever and rooting its whole dead context.
        // Twenty reloads in a day took the host to 114% CPU and 664 threads, at which point the device
        // repaints slowly enough to look broken.
        public void Dispose()
        {
            this._disposed = true;
            this.Changed = null;
            this._watcher.EnableRaisingEvents = false;
            this._watcher.Dispose();
            this._debounce.Dispose();
            this._poll.Dispose();
        }

        // Disposes the singleton if it was ever built, and does not build one just to tear it down.
        //
        // Safe despite the instance being static: the next load gets a fresh context with its own
        // Lazy, so the disposed instance can never be handed out again.
        public static void Shutdown()
        {
            if (Lazy.IsValueCreated)
            {
                Lazy.Value.Dispose();
            }
        }
    }

    public sealed class SessionsChangedEventArgs : EventArgs
    {
        public static readonly SessionsChangedEventArgs Structural = new();
    }
}
