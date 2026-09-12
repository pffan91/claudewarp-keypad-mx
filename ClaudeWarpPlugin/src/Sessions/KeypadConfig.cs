namespace Loupedeck.ClaudeWarpPlugin
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using System.Threading;

    // One key on the folder's bottom row, or one action a user can drop on a home-page key.
    //
    // Everything the hardware can actually produce is expressible here: keystroke <text>, key code
    // 53 (Escape) and key code 36 (Return). An entry with empty Text and Submit true is a bare
    // Enter, which is what answers a plan or a question dialog.
    public sealed class KeyDef
    {
        public String Label { get; init; } = "";

        public String Text { get; init; } = "";

        // Press Return after typing. An explicit flag rather than a trailing-space convention:
        // whitespace is invisible in an editor and most JSON formatters strip it, so a file whose
        // behaviour hinges on a character you cannot see is a support problem.
        public Boolean Submit { get; init; }

        // "escape" for the interrupt key; null for an ordinary type-this key.
        public String Key { get; init; }

        public Boolean IsEscape => String.Equals(this.Key, "escape", StringComparison.OrdinalIgnoreCase);

        // Stable enough to name an Options+ action and to notice that the file changed.
        public String Signature => $"{this.Label}{this.Text}{this.Submit}{this.Key}";
    }

    // ~/.claude/keypad/config.json - the tile label and the command keys, changeable without a
    // rebuild and without restarting anything.
    //
    //   { "label": "prompt",
    //     "keys": [ { "label": "esc", "key": "escape" },
    //               { "label": "/clear", "text": "/clear" } ] }
    //
    // This is the ONLY way the folder can be configured. A PluginDynamicFolder has no Action Editor
    // surface at all - its whole public API is a string in and a bitmap out - so there is no GUI
    // path to what sits on its bottom row. Plugin.PluginPreferences is not a way round it either:
    // PluginPreferenceType has exactly two values, None and Account, and the only implementation is
    // an OAuth login. It is a sign-in pane, not a settings pane.
    public static class KeypadConfig
    {
        public const String LabelSlug = "slug";
        public const String LabelPrompt = "prompt";

        // At least one session tile must survive, or the folder stops being a session display.
        public const Int32 MaxKeys = 7;

        // Exactly what the bottom row did when it was hardcoded. Neither types Return: /compact
        // takes optional instructions and the point of not submitting is to let you add them.
        private static readonly IReadOnlyList<KeyDef> DefaultKeys = new[]
        {
            new KeyDef { Label = "esc", Key = "escape" },
            new KeyDef { Label = "/clear", Text = "/clear" },
            new KeyDef { Label = "/compact", Text = "/compact " },
        };

        private static readonly Object Gate = new();

        // Self-driving rather than re-read on property access: the Options+ action list is built
        // from this file too, and that consumer touches no property while the folder is closed. A
        // stat and a parse of a few hundred bytes once a second costs nothing.
        //
        // It must be shut down with the plugin. Each reload gets its own AssemblyLoadContext and so
        // its own copy of these statics, and a live timer roots the whole old context - three
        // reloads and three timers are re-reading this file forever.
        private static readonly Timer Poll = new(_ => Reread(), null, 1000, 1000);

        private static String _label = LabelSlug;
        private static IReadOnlyList<KeyDef> _keys = DefaultKeys;
        private static String _signature = Sign(LabelSlug, DefaultKeys);

        // Raised when the parsed contents actually differ, not merely when the file is touched.
        public static event EventHandler Changed;

        public static String Label
        {
            get { lock (Gate) { return _label; } }
        }

        public static IReadOnlyList<KeyDef> Keys
        {
            get { lock (Gate) { return _keys; } }
        }

        public static String Path => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "keypad", "config.json");

        // Called from Plugin.Unload so a reloaded plugin does not leave its predecessor's timer
        // running. Dropping the subscribers matters as much as the timer: a static event holds
        // every action that ever subscribed, across load contexts.
        public static void Shutdown()
        {
            Poll.Dispose();
            Changed = null;
        }

        private static String Sign(String label, IReadOnlyList<KeyDef> keys)
        {
            var parts = new List<String> { label };
            foreach (var k in keys)
            {
                parts.Add(k.Signature);
            }

            return String.Join("", parts);
        }

        private static void Reread()
        {
            String label;
            IReadOnlyList<KeyDef> keys;

            try
            {
                if (!File.Exists(Path))
                {
                    label = LabelSlug;
                    keys = DefaultKeys;
                }
                else
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(Path));
                    label = ReadLabel(doc.RootElement);
                    keys = ReadKeys(doc.RootElement);
                }
            }
            catch
            {
                // Malformed or unreadable: keep whatever last parsed cleanly rather than blanking
                // the deck halfway through someone's edit.
                return;
            }

            var signature = Sign(label, keys);

            lock (Gate)
            {
                if (signature == _signature)
                {
                    return;
                }

                _signature = signature;
                _label = label;
                _keys = keys;
            }

            PluginLog.Info($"config: label={label}, {keys.Count} command key(s)");
            Changed?.Invoke(null, EventArgs.Empty);
        }

        private static String ReadLabel(JsonElement root) =>
            root.TryGetProperty("label", out var v) && v.ValueKind == JsonValueKind.String && v.GetString() == LabelPrompt
                ? LabelPrompt
                : LabelSlug;

        private static IReadOnlyList<KeyDef> ReadKeys(JsonElement root)
        {
            // Absent means "I have not configured this", which keeps the row people already have.
            // An explicit empty array means "I want none", and gives all eight tiles to sessions.
            if (!root.TryGetProperty("keys", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return DefaultKeys;
            }

            var keys = new List<KeyDef>();
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object || keys.Count >= MaxKeys)
                {
                    continue;
                }

                var key = Str(e, "key");
                var text = Str(e, "text") ?? "";
                var submit = Bool(e, "submit");
                var label = Str(e, "label");

                // A key that neither types, interrupts, nor submits would be a dead tile.
                if (key == null && text.Length == 0 && !submit)
                {
                    continue;
                }

                keys.Add(new KeyDef
                {
                    Label = String.IsNullOrEmpty(label) ? (key ?? text.Trim()) : label,
                    Text = text,
                    Submit = submit,
                    Key = key,
                });
            }

            return keys;
        }

        private static String Str(JsonElement e, String name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static Boolean Bool(JsonElement e, String name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    }
}
