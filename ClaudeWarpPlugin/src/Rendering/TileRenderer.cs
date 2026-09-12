namespace Loupedeck.ClaudeWarpPlugin
{
    using System;

    public static class TileRenderer
    {
        // The whole tile carries the state colour, so these are chosen for contrast against white
        // text rather than for vividness. Measured WCAG ratios against #FFFFFF:
        //   busy 4.78 : done 4.83 : attention 5.89 : idle 7.15  - all clear of the 4.5 threshold.
        // Brighter versions of the same hues (the original #D97757 coral, for instance) only reach
        // ~3.1 and made the text noticeably harder to read once it filled the key.
        private static readonly BitmapColor Busy = new(0xB8, 0x55, 0x35);
        private static readonly BitmapColor Done = new(0x3E, 0x7F, 0x4E);
        private static readonly BitmapColor Attention = new(0xB3, 0x3A, 0x32);
        private static readonly BitmapColor Idle = new(0x55, 0x58, 0x5C);
        private static readonly BitmapColor Neutral = new(0x3A, 0x3D, 0x42);
        private static readonly BitmapColor Empty = new(0x14, 0x16, 0x18);

        // Animation is driven by a frame counter the folder advances on its tick; the renderer stays
        // a pure function of (session, frame) so nothing has to be remembered between paints.
        public const Int32 TickMs = 250;

        // One full left-to-right sweep of the busy bar, in frames. Ten at 250 ms reads as continuous
        // motion without asking the device to redraw faster than it comfortably can.
        private const Int32 SweepFrames = 10;

        // Frames per half-cycle of the attention blink: 2 -> on 500 ms, off 500 ms.
        private const Int32 BlinkFrames = 2;

        public static BitmapColor StateColor(String state) => state switch
        {
            "busy" => Busy,
            "done" => Done,
            "attention" => Attention,
            _ => Idle,
        };

        public static BitmapImage Session(SessionInfo s, PluginImageSize size, Int32 frame)
        {
            var bg = StateColor(s.State);

            // Colour alone is a poor alarm: it is static, and a red tile in a grid of coral ones is
            // easy to look straight past. Dimming the whole tile on and off makes it move, and
            // movement is what peripheral vision actually picks up.
            if (s.State == "attention" && (frame / BlinkFrames) % 2 == 1)
            {
                bg = Shade(Attention, 0.55);
            }

            using var b = new BitmapBuilder(size);
            var w = b.Width;
            var h = b.Height;

            b.Clear(bg);

            var fg = Foreground(bg);
            var secondary = Tint(bg, 0.88);

            var project = Truncate(String.IsNullOrEmpty(s.Project) ? "—" : s.Project, 14);
            var what = Describe(s);

            if (what.Length > 0)
            {
                // The project is context; what the session IS gets the space and the weight.
                b.DrawText(project, 2, (Int32)(h * 0.04), w - 4, (Int32)(h * 0.18), secondary, 11);

                // Three lines at 13px fits ~42 characters, which covers most slugs outright instead
                // of truncating them. Stops short of the bottom strip the busy bar lives in, so text
                // does not shift position when a session starts working.
                b.DrawText(what, 3, (Int32)(h * 0.24), w - 6, (Int32)(h * 0.64), fg, 13);
            }
            else
            {
                b.DrawText(project, 2, (Int32)(h * 0.28), w - 4, (Int32)(h * 0.38), fg, 16);
            }

            if (s.State == "busy")
            {
                DrawSweep(b, w, h, bg, frame);
            }

            return b.ToImage();
        }

        // An indeterminate progress bar across the bottom edge: a segment that slides in from the
        // left, crosses, and slides out to the right.
        //
        // Drawn rather than animated in text. A row of cycling ASCII dots at this size is a few
        // pixels of movement that vanishes at arm's length, whereas a bar spanning the whole key is
        // legible across a room - which is the entire point of putting session status on a keypad.
        private static void DrawSweep(BitmapBuilder b, Int32 w, Int32 h, BitmapColor bg, Int32 frame)
        {
            var barH = Math.Max(4, (Int32)(h * 0.055));
            var y = h - barH;

            b.FillRectangle(0, y, w, barH, Shade(bg, 0.35));

            var segW = Math.Max(12, (Int32)(w * 0.30));
            var travel = w + segW;
            var x = (Int32)(((frame % SweepFrames) / (Double)SweepFrames) * travel) - segW;

            // Clamped rather than relying on the canvas to clip a negative origin.
            var x0 = Math.Max(0, x);
            var x1 = Math.Min(w, x + segW);
            if (x1 > x0)
            {
                b.FillRectangle(x0, y, x1 - x0, barH, Tint(bg, 0.75));
            }
        }

        // Turns "we-need-to-fix-async-newt" into "fix async newt".
        //
        // Slugs are generated from the opening prompt, so they routinely start with filler ("we need
        // to", "please help me") and the distinguishing words sit behind it. Dropping the filler is
        // what makes two tiles in the same project tell themselves apart at a glance.
        private static readonly String[] Filler =
        {
            "i", "we", "you", "let", "s", "lets", "please", "help", "me", "my", "need", "needs",
            "to", "the", "a", "an", "is", "are", "this", "that", "it", "can", "could", "would",
            "and", "for", "of", "on", "in", "here", "there", "now", "also", "but", "so", "do",
        };

        private static String Describe(SessionInfo s)
        {
            var preferSlug = KeypadConfig.Label == KeypadConfig.LabelSlug;

            if (!preferSlug && !String.IsNullOrWhiteSpace(s.Title))
            {
                // Shown as written - no filler stripping - because a prompt already reads as language
                // and editing it would only make it look wrong.
                return TruncateEnd(s.Title.Trim(), 42);
            }

            var fromSlug = FromSlug(s.Slug);
            if (fromSlug.Length > 0)
            {
                return fromSlug;
            }

            if (!String.IsNullOrWhiteSpace(s.Title))
            {
                return TruncateEnd(s.Title.Trim(), 42);
            }

            // Nothing named this session yet: the branch beats an empty tile.
            return s.Branch ?? "";
        }

        private static String FromSlug(String slug)
        {
            if (String.IsNullOrWhiteSpace(slug))
            {
                return "";
            }

            var words = slug.Split('-', StringSplitOptions.RemoveEmptyEntries);

            // Claude Code appends a random adjective+noun to almost every slug
            // ("do-a-deep-review-snazzy-garden"). It is always the last two words, and nothing marks
            // it as random - so this drops them positionally, and will also clip two real words from
            // a slug that never had a suffix.
            var end = words.Length > 3 ? words.Length - 2 : words.Length;

            var start = 0;
            while (start < end - 1 && Array.IndexOf(Filler, words[start].ToLowerInvariant()) >= 0)
            {
                start++;
            }

            if (start >= end)
            {
                return "";
            }

            return TruncateEnd(String.Join(" ", words, start, end - start), 42);
        }

        // A command key. Deliberately unlike a session tile - dark and monochrome - so it never reads
        // as "a session that happens to be dark", which matters when it sits in the same grid.
        public static BitmapImage Command(String label, String subtitle, PluginImageSize size)
        {
            using var b = new BitmapBuilder(size);
            return Command(b, label, subtitle);
        }

        // The Action Editor hands out pixel dimensions rather than a PluginImageSize, so the same
        // key has to be drawable from either.
        public static BitmapImage Command(String label, String subtitle, Int32 width, Int32 height)
        {
            using var b = new BitmapBuilder(width, height);
            return Command(b, label, subtitle);
        }

        private static BitmapImage Command(BitmapBuilder b, String label, String subtitle)
        {
            var w = b.Width;
            var h = b.Height;

            b.Clear(Neutral);

            var fg = Foreground(Neutral);

            if (String.IsNullOrEmpty(subtitle))
            {
                b.DrawText(label, 2, (Int32)(h * 0.30), w - 4, (Int32)(h * 0.40), fg, 16);
            }
            else
            {
                b.DrawText(label, 2, (Int32)(h * 0.22), w - 4, (Int32)(h * 0.34), fg, 18);
                b.DrawText(subtitle, 2, (Int32)(h * 0.58), w - 4, (Int32)(h * 0.24), Tint(Neutral, 0.55), 11);
            }

            return b.ToImage();
        }

        // The key before the plugin is connected to Claude Code, and the confirmation that connects it.
        //
        // Two lines rather than one, because "Set up" alone does not say set up what, and this key can
        // be sitting on a home page among thirty others. The armed state changes the colour as well as
        // the words: the difference between "nothing has happened yet" and "the next press edits a
        // file" is too important to rest on reading two small words.
        public static BitmapImage Setup(SetupAction armed, PluginImageSize size)
        {
            using var b = new BitmapBuilder(size);

            var bg = armed switch
            {
                SetupAction.Enable => Busy,
                SetupAction.Disable => Attention,
                _ => Neutral,
            };

            var (line, note) = armed switch
            {
                SetupAction.Enable => ("Press again", "to enable"),
                SetupAction.Disable => ("Press again", "to turn off"),
                _ => ("Set up", "Claude Code"),
            };

            b.Clear(bg);
            b.DrawText(line, 2, (Int32)(b.Height * 0.20), b.Width - 4, (Int32)(b.Height * 0.34), Foreground(bg), 15);
            b.DrawText(note, 2, (Int32)(b.Height * 0.56), b.Width - 4, (Int32)(b.Height * 0.26), Tint(bg, 0.55), 10);

            return b.ToImage();
        }

        // The standalone key, meant for a home-page slot rather than this folder: how many sessions
        // are waiting on you, answered without opening anything.
        //
        // One number, edge to edge. The state is carried entirely by the colour - the same palette
        // the session tiles use - because at a glance across a desk a digit filling the key reads
        // from much further away than a digit sharing it with two words. Which number is shown is
        // the first of these that is non-zero, so the key always answers the most urgent question
        // rather than the loudest one.
        public static BitmapImage NeedsMe(
            Int32 attention, Int32 done, Int32 busy, Int32 total, PluginImageSize size, Int32 frame)
        {
            BitmapColor bg;
            String count;

            if (attention > 0)
            {
                // Blinks for the same reason the tiles do, and more so: this key earns its slot by
                // catching your eye from a screen you are not looking at.
                bg = (frame / BlinkFrames) % 2 == 1 ? Shade(Attention, 0.55) : Attention;
                count = attention.ToString();
            }
            else if (done > 0)
            {
                bg = Done;
                count = done.ToString();
            }
            else if (busy > 0)
            {
                bg = Busy;
                count = busy.ToString();
            }
            else if (total > 0)
            {
                bg = Idle;
                count = total.ToString();
            }
            else
            {
                bg = Empty;
                count = "–";
            }

            return CountKey(count, "Needs me", bg, size);
        }

        // The companion key: how many sessions are working right now.
        //
        // Unlike Needs me this has only one thing to say, so the colour never changes meaning -
        // coral while anything is running, near-black when nothing is. "Everything has stopped" is
        // then as readable from across the room as a number is.
        public static BitmapImage Working(Int32 busy, PluginImageSize size) =>
            busy > 0
                ? CountKey(busy.ToString(), "Working", Busy, size)
                : CountKey("–", "Working", Empty, size);

        // One number over a small label, filling the key.
        //
        // These keys carry their own label because the host does not: marking the action a widget is
        // what gives it the whole face, and that also removes the name the host would otherwise draw
        // underneath. The label is kept small so the count is still what reads from across the room.
        private static BitmapImage CountKey(String count, String label, BitmapColor bg, PluginImageSize size)
        {
            using var b = new BitmapBuilder(size);

            b.Clear(bg);

            var h = b.Height;

            DrawCentred(b, count, (Int32)(h * 0.42), (Int32)(h * 0.46), Foreground(bg));
            DrawCentred(b, label, (Int32)(h * 0.84), Math.Max(10, (Int32)(h * 0.125)), Tint(bg, 0.80));

            return b.ToImage();
        }

        // Draws text with its INK centred on centreY.
        //
        // BitmapBuilder.DrawText centres the text's baseline in the rectangle it is given, not the
        // glyphs, so anything large ends up sitting high by half its own height - measured across
        // 20-63px fonts on a 116px key, the ink's bottom edge stays pinned about 6px below the
        // rectangle's centre however big the font is. Digits and a lowercase-free short label have
        // no descender and fill the cap height, so their ink is close to 0.73 of the font size:
        // dropping the rectangle by half that, less the 6px the baseline already sits low, puts the
        // text where it was asked for. Verified to land within a pixel of centre.
        private static void DrawCentred(
            BitmapBuilder b, String text, Int32 centreY, Int32 fontSize, BitmapColor color)
        {
            var y = centreY + (Int32)Math.Round((0.365 * fontSize) - 6) - (b.Height / 2);
            b.DrawText(text, 0, y, b.Width, b.Height, color, fontSize);
        }

        public static BitmapImage Blank(PluginImageSize size)
        {
            using var b = new BitmapBuilder(size);
            b.Clear(Empty);
            return b.ToImage();
        }

        // Picks whichever of white/near-black reads better on the given background. The palette above
        // is already tuned for white, but this keeps the tiles correct if a colour is ever changed.
        private static BitmapColor Foreground(BitmapColor bg) =>
            Luminance(bg) > 0.35 ? new BitmapColor(0x11, 0x11, 0x11) : BitmapColor.White;

        // A lighter wash of the background, for text that should recede without losing contrast.
        private static BitmapColor Tint(BitmapColor bg, Double amount) => new(
            (Byte)(bg.R + ((255 - bg.R) * amount)),
            (Byte)(bg.G + ((255 - bg.G) * amount)),
            (Byte)(bg.B + ((255 - bg.B) * amount)));

        // The opposite of Tint: toward black, for the blink's dark phase and the bar's track.
        private static BitmapColor Shade(BitmapColor c, Double amount) => new(
            (Byte)(c.R * (1 - amount)),
            (Byte)(c.G * (1 - amount)),
            (Byte)(c.B * (1 - amount)));

        private static Double Luminance(BitmapColor c)
        {
            static Double Channel(Byte v)
            {
                var s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }

            return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
        }

        // Keeps the start and drops the tail. For anything sentence-like the meaning is front-loaded,
        // so this is the right shape - unlike Truncate below, which is for names.
        private static String TruncateEnd(String value, Int32 max)
        {
            if (String.IsNullOrEmpty(value) || value.Length <= max)
            {
                return value ?? "";
            }

            // Prefer cutting at a word boundary so the last word is not left as a stump.
            var cut = value.LastIndexOf(' ', max - 1);
            if (cut < max / 2)
            {
                cut = max - 1;
            }

            return value.Substring(0, cut).TrimEnd() + "…";
        }

        // Middle-truncation keeps both ends, which is what distinguishes names differing only at the
        // tail (acme-ios vs acme-web) - plain clipping renders those identical.
        private static String Truncate(String value, Int32 max)
        {
            if (String.IsNullOrEmpty(value) || value.Length <= max)
            {
                return value ?? "";
            }

            var keep = max - 1;
            var head = (keep + 1) / 2;
            var tail = keep - head;
            return value.Substring(0, head) + "…" + value.Substring(value.Length - tail);
        }
    }
}
