namespace Loupedeck.ClaudeWarpPlugin
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Encodings.Web;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Threading;

    // Whether the keypad is connected to Claude Code, and the only code that connects it.
    //
    // Claude Code reports session state through hooks in ~/.claude/settings.json, so the tiles stay
    // blank until that file names our script. Someone working from the repo runs
    // hooks/install-hooks.sh; someone who installed the .lplug4 from the Marketplace has no repo, so
    // the plugin has to be able to do it itself.
    //
    // It does NOT do it on install or on load, and that is a deliberate constraint rather than an
    // oversight. Plugin.Install() exists and would work, but it runs before the user has seen the
    // plugin do anything - they clicked install on a listing, not "please edit my Claude Code
    // configuration". Logitech's Marketplace review raised exactly this against another Claude Code
    // plugin. Writing here is therefore gated on a deliberate, confirmed, reversible key press; see
    // docs/SUBMISSION.md.
    public static class HookWiring
    {
        // The marker that makes an entry ours. Both directions key off it, so the two can never
        // disagree about which entries belong to this plugin.
        private const String Marker = "keypad-hook.sh";

        // How long a press stays armed. Long enough to be a decision, short enough that an armed key
        // left alone cannot be triggered by someone else later.
        private const Int32 ArmWindowMs = 15_000;

        // event -> the argument passed to keypad-hook.sh. Ordered, so a rewritten settings.json is
        // stable rather than reshuffled on every run.
        private static readonly (String Event, String Arg)[] Events =
        {
            ("SessionStart", "start"),
            ("UserPromptSubmit", "prompt"),
            ("PreToolUse", "busy"),
            ("PostToolUse", "busy"),
            ("PermissionRequest", "attention"),
            ("Notification", "attention"),
            ("Stop", "done"),
            ("SessionEnd", "end"),
        };

        // event -> matcher. PreToolUse/PostToolUse match every tool.
        //
        // Notification is the interesting one. Claude Code fires it for a whole family of unrelated
        // things - permission_prompt, idle_prompt, auth_success, agent_completed and more - and an
        // unmatched entry catches all of them, which is what used to turn a finished green tile red
        // about a minute later. Matching permission_prompt asks for the one that is actually an alarm.
        private static readonly Dictionary<String, String> Matchers = new(StringComparer.Ordinal)
        {
            ["PreToolUse"] = "*",
            ["PostToolUse"] = "*",
            ["Notification"] = "permission_prompt",
        };

        private static readonly Object Gate = new();
        private static readonly Timer Disarm = new(_ => Expire(), null, Timeout.Infinite, Timeout.Infinite);

        // Re-parsing settings.json on every repaint would be wasteful, and caching it outright would
        // miss a hand edit. The file's write time is one stat call and settles both.
        private static DateTime _stamp;
        private static Boolean _wired;

        private static SetupAction _armed;

        // Raised when the wiring changes or an armed press lapses, so the keys can repaint.
        public static event EventHandler Changed;

        public static String Root => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "keypad");

        public static String ScriptPath => Path.Combine(Root, "keypad-hook.sh");

        public static String SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

        private static String BackupPath => SettingsPath + ".claudewarp.bak";

        // What a press would do right now: nothing while armed, otherwise the opposite of the
        // current state.
        public static SetupAction Armed
        {
            get
            {
                lock (Gate)
                {
                    return _armed;
                }
            }
        }

        public static Boolean IsWired
        {
            get
            {
                try
                {
                    var stamp = File.Exists(SettingsPath) ? File.GetLastWriteTimeUtc(SettingsPath) : DateTime.MinValue;

                    lock (Gate)
                    {
                        if (stamp == _stamp)
                        {
                            return _wired;
                        }

                        _stamp = stamp;
                        _wired = ReadIsWired();
                        return _wired;
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Warning($"Could not read {SettingsPath}: {ex.Message}");
                    return false;
                }
            }
        }

        // Called from Plugin.Unload. Statics live per load context rather than per process, so
        // without this every reload leaves its predecessor's timer running and rooting the old context.
        public static void Shutdown()
        {
            Disarm.Dispose();
            Changed = null;
        }

        // Writes the hook script into the plugin's own data directory.
        //
        // This is the one thing that does happen on load, and it is deliberately confined to a
        // directory this plugin owns: ~/.claude/keypad/ holds the session state files the script
        // writes anyway. Nothing outside it is touched.
        //
        // Rewritten whenever the contents differ, so a plugin update ships a matching script; skipped
        // when identical, so loading does not churn the file every time.
        public static void ExtractScript()
        {
            try
            {
                var script = ReadEmbeddedScript();
                if (script == null)
                {
                    PluginLog.Warning("keypad-hook.sh is not embedded in the plugin; setup will not work.");
                    return;
                }

                Directory.CreateDirectory(Root);
                Restrict(Root);

                if (File.Exists(ScriptPath) && File.ReadAllText(ScriptPath) == script)
                {
                    return;
                }

                File.WriteAllText(ScriptPath, script);
                Restrict(ScriptPath);
                PluginLog.Info($"wrote {ScriptPath}");
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"Could not write {ScriptPath}: {ex.Message}");
            }
        }

        // Owner-only, and the script has to be executable because settings.json invokes it through sh.
        // Guarded rather than conditionally compiled: this plugin is macOS-only, but the API is
        // genuinely unsupported on Windows and the analyser is right to say so.
        private static void Restrict(String path)
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // A config file nobody knows about is not configurability, so a starter one is written next to
        // the script. Only ever when absent, and deliberately WITHOUT a "keys" block: leaving that out
        // means the built-in bottom row applies, so seeding this changes nothing about how the keypad
        // behaves.
        public static void SeedConfig()
        {
            try
            {
                var path = KeypadConfig.Path;
                if (File.Exists(path))
                {
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, StarterConfig);
                PluginLog.Info($"seeded {path}");
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"Could not seed {KeypadConfig.Path}: {ex.Message}");
            }
        }

        // First press arms, second press within the window acts.
        //
        // The press that arms writes nothing at all. A key labelled "Set up" is already an unambiguous
        // ask, but a reviewer pressing keys to find out what they do would otherwise have their
        // settings.json edited by the discovery - which is the complaint this whole design exists to
        // avoid. Returns what the press did, so the caller can repaint and report.
        public static SetupAction Press()
        {
            var wanted = IsWired ? SetupAction.Disable : SetupAction.Enable;

            lock (Gate)
            {
                if (_armed != wanted)
                {
                    _armed = wanted;
                    Disarm.Change(ArmWindowMs, Timeout.Infinite);
                    Raise();
                    return SetupAction.None;
                }

                _armed = SetupAction.None;
                Disarm.Change(Timeout.Infinite, Timeout.Infinite);
            }

            var ok = wanted == SetupAction.Enable ? Wire() : Unwire();

            lock (Gate)
            {
                // Force the next read past the cache: we just changed the file.
                _stamp = DateTime.MinValue;
            }

            Raise();
            return ok ? wanted : SetupAction.None;
        }

        // Says in Options+ what just happened to the user's settings file.
        //
        // A key press is a quiet way to edit a file that lives outside this plugin, so the edit has to
        // leave a mark somewhere the user will actually see. Warning rather than Normal is not alarmism
        // - it is the level the Options+ strip renders a visible badge for, and a notice nobody sees is
        // not disclosure.
        public static void Announce(Plugin plugin, SetupAction done)
        {
            if (plugin == null || done == SetupAction.None)
            {
                return;
            }

            var message = done == SetupAction.Enable
                ? $"Claude Code hooks added to {SettingsPath}. Long press the key to remove them. " +
                  $"Previous file saved as {BackupPath}."
                : $"Claude Code hooks removed from {SettingsPath}.";

            plugin.OnPluginStatusChanged(
                PluginStatus.Warning, message, "https://github.com/pffan91/claudewarp-keypad-mx#setup", "What changed");
        }

        private static void Expire()
        {
            lock (Gate)
            {
                if (_armed == SetupAction.None)
                {
                    return;
                }

                _armed = SetupAction.None;
            }

            PluginLog.Info("setup press lapsed; nothing was changed.");
            Raise();
        }

        private static void Raise() => Changed?.Invoke(null, EventArgs.Empty);

        private static Boolean Wire() => Rewrite(install: true);

        private static Boolean Unwire() => Rewrite(install: false);

        // The whole edit, in one pass over the eight events.
        //
        // Additive by construction: every entry that is not ours is copied through untouched, and
        // statusLine - which another Claude Code plugin owns - is never even looked at. Our own
        // previous entries are dropped before new ones are added, so running this twice cannot stack
        // duplicates.
        private static Boolean Rewrite(Boolean install)
        {
            var script = ScriptPath;

            // The path becomes a quoted argument inside a shell command string. Home directories do
            // not contain these, but a path that could break out of the quoting is not going into
            // someone's settings file.
            if (script.Contains('"') || script.Contains('\\'))
            {
                PluginLog.Warning($"Refusing to wire: {script} cannot be quoted safely.");
                return false;
            }

            if (install && !File.Exists(script))
            {
                PluginLog.Warning($"Refusing to wire: {script} does not exist.");
                return false;
            }

            try
            {
                JsonObject root;
                if (File.Exists(SettingsPath))
                {
                    // Strict parse, deliberately. Tolerating comments here would mean silently
                    // dropping them on the way out, and this is the user's file.
                    root = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject;
                    if (root == null)
                    {
                        PluginLog.Warning($"{SettingsPath} is not a JSON object; leaving it alone.");
                        return false;
                    }

                    File.Copy(SettingsPath, BackupPath, overwrite: true);
                }
                else if (install)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                    root = new JsonObject();
                }
                else
                {
                    return true;
                }

                if (root["hooks"] is not JsonObject hooks)
                {
                    hooks = new JsonObject();
                    root["hooks"] = hooks;
                }

                Int32 removed = 0, added = 0;

                foreach (var (name, arg) in Events)
                {
                    var kept = new JsonArray();

                    if (hooks[name] is JsonArray existing)
                    {
                        foreach (var entry in existing)
                        {
                            if (IsOurs(entry))
                            {
                                removed++;
                                continue;
                            }

                            // Detached, because a node still attached to the old array cannot be
                            // added to the new one.
                            kept.Add(entry?.DeepClone());
                        }
                    }

                    if (install)
                    {
                        var entry = new JsonObject
                        {
                            ["hooks"] = new JsonArray(new JsonObject
                            {
                                ["type"] = "command",
                                ["command"] = $"sh \"{script}\" {arg}",
                            }),
                        };

                        if (Matchers.TryGetValue(name, out var matcher))
                        {
                            entry["matcher"] = matcher;
                        }

                        kept.Add(entry);
                        added++;
                    }

                    if (kept.Count > 0)
                    {
                        hooks[name] = kept;
                    }
                    else
                    {
                        hooks.Remove(name);
                    }
                }

                Write(root);
                PluginLog.Info(
                    $"{(install ? "wired" : "unwired")} {SettingsPath}: removed {removed}, added {added}. " +
                    $"Backup at {BackupPath}");
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"Could not update {SettingsPath}: {ex.Message}");
                return false;
            }
        }

        private static void Write(JsonObject root)
        {
            // UnsafeRelaxedJsonEscaping so that whatever else lives in this file - non-ASCII paths,
            // '+' in a command - comes back out spelled the way the user wrote it, rather than
            // re-encoded into \uXXXX by the default encoder.
            var json = root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

            // Temp file plus rename, so an interrupted write cannot leave a truncated settings.json.
            var tmp = SettingsPath + ".claudewarp.tmp";
            File.WriteAllText(tmp, json + "\n");
            File.Move(tmp, SettingsPath, overwrite: true);
        }

        private static Boolean IsOurs(JsonNode entry) =>
            entry?["hooks"] is JsonArray commands
            && commands.Any(c => c?["command"]?.GetValue<String>()?.Contains(Marker, StringComparison.Ordinal) == true);

        private static Boolean ReadIsWired()
        {
            if (!File.Exists(SettingsPath))
            {
                return false;
            }

            if (JsonNode.Parse(File.ReadAllText(SettingsPath)) is not JsonObject root
                || root["hooks"] is not JsonObject hooks)
            {
                return false;
            }

            return Events.Any(e => hooks[e.Event] is JsonArray entries && entries.Any(IsOurs));
        }

        private static String ReadEmbeddedScript()
        {
            var assembly = typeof(HookWiring).Assembly;
            var name = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("keypad-hook.sh", StringComparison.Ordinal));

            if (name == null)
            {
                return null;
            }

            using var stream = assembly.GetManifestResourceStream(name);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        private const String StarterConfig = """
{
  "//": "Claude Code keypad settings. Re-read once a second - no restart, no rebuild.",

  "//label": "slug = the session name minus its random suffix. prompt = the prompt you typed.",
  "label": "slug",

  "//keys": "Add a \"keys\" array here to replace the bottom row (esc, /clear, /compact) with your",
  "//keys2": "own, and to publish each one as a draggable action in Options+. See config.example.json",
  "//keys3": "in the repo, or the Configuring section of the README."
}

""";
    }

    public enum SetupAction
    {
        None,
        Enable,
        Disable,
    }
}
