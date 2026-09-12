namespace Loupedeck.ClaudeWarpPlugin
{
    using System;
    using System.Collections.Concurrent;
    using System.IO;
    using System.Text.Json;

    // Recovers the opening prompt of a session from its transcript, to use as the tile's label.
    //
    // Claude Code's slug is what Warp shows on the tab, but it appends a random adjective+noun to
    // nearly every one ("do-a-deep-review-snazzy-garden") and sometimes produces nothing else at all
    // ("goofy-yawning-falcon"). The prompt is what the session is actually about.
    public sealed class TranscriptFacts
    {
        public static readonly TranscriptFacts None = new();

        public String Title { get; init; } = "";

        public String Slug { get; init; } = "";
    }

    public static class SessionTitles
    {
        // Transcripts reach megabytes and the opening prompt is at the top, so reading stops early.
        private const Int32 MaxLinesScanned = 400;

        private static readonly ConcurrentDictionary<String, TranscriptFacts> Cache = new(StringComparer.Ordinal);

        // Messages carrying one of these are machinery, not something the user typed.
        private static readonly String[] MachineryTags =
        {
            "<task-notification>", "<system-reminder>", "<local-command-caveat>",
            "<user-prompt-submit-hook>", "<command-output>", "<local-command-stdout>",
        };

        public static TranscriptFacts For(String sessionKey, String transcriptPath)
        {
            if (String.IsNullOrEmpty(sessionKey) || String.IsNullOrEmpty(transcriptPath))
            {
                return TranscriptFacts.None;
            }

            return Cache.GetOrAdd(sessionKey, _ => Read(transcriptPath));
        }

        // A session can be renamed by its next prompt only if we forget the old answer.
        public static void Forget(String sessionKey)
        {
            if (!String.IsNullOrEmpty(sessionKey))
            {
                Cache.TryRemove(sessionKey, out _);
            }
        }

        private static TranscriptFacts Read(String path)
        {
            var slug = "";
            try
            {
                if (!File.Exists(path))
                {
                    return TranscriptFacts.None;
                }

                // A session often opens with a slash command (/effort, /mcp). That names the command
                // but not the work, so it is only used if no real prompt follows.
                var commandFallback = "";

                using var reader = new StreamReader(path);
                for (var i = 0; i < MaxLinesScanned; i++)
                {
                    var line = reader.ReadLine();
                    if (line == null)
                    {
                        break;
                    }

                    if (slug.Length == 0)
                    {
                        // The slug is repeated on every line, so the first one that has it will do.
                        var at = line.IndexOf("\"slug\":\"", StringComparison.Ordinal);
                        if (at >= 0)
                        {
                            var start = at + 8;
                            var end = line.IndexOf('"', start);
                            if (end > start)
                            {
                                slug = line.Substring(start, end - start);
                            }
                        }
                    }

                    if (line.Length < 2 || !line.Contains("\"type\":\"user\"", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var text = Clean(ExtractText(line));
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    if (text[0] == '/')
                    {
                        if (commandFallback.Length == 0)
                        {
                            commandFallback = text;
                        }

                        continue;
                    }

                    return new TranscriptFacts { Title = text, Slug = slug };
                }

                return new TranscriptFacts { Title = commandFallback, Slug = slug };
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"Could not read session title: {ex.Message}");
            }

            return new TranscriptFacts { Title = "", Slug = slug };
        }

        private static String ExtractText(String line)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("message", out var message)
                    || !message.TryGetProperty("content", out var content))
                {
                    return "";
                }

                if (content.ValueKind == JsonValueKind.String)
                {
                    return content.GetString();
                }

                if (content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.ValueKind == JsonValueKind.Object
                            && part.TryGetProperty("type", out var t)
                            && t.GetString() == "text"
                            && part.TryGetProperty("text", out var v))
                        {
                            return v.GetString();
                        }
                    }
                }
            }
            catch
            {
                // Not a line we can read; the caller moves on to the next.
            }

            return "";
        }

        private static String Clean(String raw)
        {
            if (String.IsNullOrWhiteSpace(raw))
            {
                return "";
            }

            foreach (var tag in MachineryTags)
            {
                if (raw.Contains(tag, StringComparison.OrdinalIgnoreCase))
                {
                    return "";
                }
            }

            // A slash command invocation: name the command rather than showing its XML.
            var nameAt = raw.IndexOf("<command-name>", StringComparison.OrdinalIgnoreCase);
            if (nameAt >= 0)
            {
                var close = raw.IndexOf("</command-name>", nameAt, StringComparison.OrdinalIgnoreCase);
                if (close > nameAt)
                {
                    var start = nameAt + "<command-name>".Length;
                    var name = raw.Substring(start, close - start).Trim().TrimStart('/');
                    return name.Length > 0 ? "/" + name : "";
                }
            }

            if (raw.Contains("<command-message>", StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }

            // Drop any remaining markup, then collapse whitespace so a multi-line prompt reads as one.
            var sb = new System.Text.StringBuilder(raw.Length);
            var depth = 0;
            foreach (var c in raw)
            {
                if (c == '<')
                {
                    depth++;
                }
                else if (c == '>')
                {
                    if (depth > 0)
                    {
                        depth--;
                    }
                }
                else if (depth == 0)
                {
                    sb.Append(Char.IsWhiteSpace(c) ? ' ' : c);
                }
            }

            var text = sb.ToString();
            while (text.Contains("  ", StringComparison.Ordinal))
            {
                text = text.Replace("  ", " ", StringComparison.Ordinal);
            }

            return text.Trim();
        }
    }
}
