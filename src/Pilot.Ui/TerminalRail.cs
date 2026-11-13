using DIR.Lib;

namespace Pilot.Ui;

/// <summary>How pilot-tui draws a page's glyph on its bar: the emoji where the terminal draws them two cells wide, else initials.</summary>
internal enum GlyphMode
{
    /// <summary>Emoji where the terminal is known to draw them two wide (see <see cref="TerminalRail.Detect"/>), else initials.</summary>
    Auto,
    Emoji,
    Initials,
}

/// <summary>
/// pilot-tui's page bar: one row of entries built from <see cref="PilotChrome{TSurface}.Pages"/>, so a page added there shows
/// in the terminal with no second list. Each entry is a glyph (the emoji, or initials where the terminal cannot be trusted to
/// draw an emoji two cells wide) and the key that reaches the page, a button whose hit shows the page. Every entry is
/// <see cref="EntryCells"/> cells wide in either mode, so the bar never shifts as a count or a glyph changes.
/// </summary>
internal static class TerminalRail
{
    /// <summary>The cells one entry takes: a space, the glyph's area, its key and a space.</summary>
    public const int EntryCells = 8;

    /// <summary>The cells the glyph has: a light bulb and "99+" is the longest.</summary>
    private const int GlyphCells = 5;

    /// <summary>The key of Spend: Ctrl+Shift+S is not told apart from Ctrl+S in most terminals, so it has a plain key.</summary>
    public const char SpendKey = '$';

    /// <summary>The environment variable with the same meaning as the <c>--glyphs</c> option.</summary>
    public const string GlyphsVariable = "PILOT_TUI_GLYPHS";

    /// <summary>An entry's action id, as a click on it reaches the host and as a test finds it.</summary>
    public static string Action(PilotPage page) => $"tui-rail:{page}";

    /// <summary>What a page is on the bar: its page, name and the key that reaches it, in the GUI rail's order.</summary>
    public static IReadOnlyList<(PilotPage Page, string Label, char Key)> Keys { get; } =
        [.. PilotChrome<DIR.Lib.RgbaImage>.Pages.Select(p => (p.Page, p.Label, KeyOf(p.Shortcut)))];

    private static char KeyOf(KeyChord chord) =>
        chord.Key is >= InputKey.D0 and <= InputKey.D9 ? (char)('0' + (chord.Key - InputKey.D0)) : SpendKey;

    /// <summary>The key a page has on the bar.</summary>
    public static char KeyFor(PilotPage page) => Keys.First(k => k.Page == page).Key;

    /// <summary>The page a key reaches: a digit by the GUI's Ctrl+digit order, and the dollar sign Spend; null for any other.</summary>
    public static PilotPage? PageFor(char key) => Keys.Where(k => k.Key == key).Select(k => (PilotPage?)k.Page).FirstOrDefault();

    /// <summary>The page a key press reaches: a digit key, or Shift+4 (the dollar sign on a US keyboard) for Spend.</summary>
    public static PilotPage? PageFor(InputKey key, InputModifier modifiers) =>
        key is >= InputKey.D0 and <= InputKey.D9
            ? ((modifiers & InputModifier.Shift) != 0 && key == InputKey.D4 ? PilotPage.Spend : PageFor((char)('0' + (key - InputKey.D0))))
            : null;

    /// <summary>The page an input event reaches by the table: a plain key-down of a digit, or Shift+4; null for any other event.</summary>
    public static PilotPage? PageFor(InputEvent evt) =>
        evt is InputEvent.KeyDown(var key, var modifiers) && (modifiers & ~InputModifier.Shift) == 0 ? PageFor(key, modifiers) : null;

    /// <summary>
    /// The mode a terminal gets from its environment: emoji where it is known to draw them two cells wide (Windows Terminal,
    /// VS Code's, WezTerm, iTerm2, kitty), else initials.
    /// </summary>
    public static GlyphMode Detect(Func<string, string?> env) =>
        env("WT_SESSION") is { Length: > 0 } || env("KITTY_WINDOW_ID") is { Length: > 0 }
        || env("TERM_PROGRAM") is "vscode" or "WezTerm" or "iTerm.app"
            ? GlyphMode.Emoji
            : GlyphMode.Initials;

    /// <summary>The mode an option's or the variable's text names (auto, emoji, initials); null when it names none.</summary>
    public static GlyphMode? Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "auto" => GlyphMode.Auto,
        "emoji" => GlyphMode.Emoji,
        "initials" => GlyphMode.Initials,
        _ => null,
    };

    /// <summary>Resolves Auto against the environment.</summary>
    public static GlyphMode Resolve(GlyphMode mode, Func<string, string?> env) => mode == GlyphMode.Auto ? Detect(env) : mode;

    /// <summary>
    /// The glyph an entry shows. Emoji mode is the page's emoji (the Background tasks one following its jobs, Ideas with its
    /// count); a one-char symbol (the plane, the cog) gets U+FE0F, which asks for the two-wide emoji form. Initials mode is the
    /// GUI night rail's letters, with ASCII ones of the terminal's own for Flights and Settings, whose night glyphs are the
    /// plane and the cog.
    /// </summary>
    public static string Glyph(PilotPage page, GlyphMode mode, JobsScreen? jobs, IdeasScreen? ideas, DateTimeOffset now)
    {
        var icon = PilotChrome<DIR.Lib.RgbaImage>.Pages.First(p => p.Page == page).Icon;
        var glyph = page switch
        {
            PilotPage.Jobs => PilotChrome<DIR.Lib.RgbaImage>.JobsGlyph(jobs, now).Glyph,
            PilotPage.Ideas => PilotChrome<DIR.Lib.RgbaImage>.IdeasGlyph(ideas).Glyph,
            _ => icon,
        };
        if (mode == GlyphMode.Emoji)
        {
            return glyph.Length == 1 ? glyph + "️" : glyph;
        }
        return page switch
        {
            PilotPage.Flights => "Fl",
            PilotPage.Settings => "Se",
            PilotPage.Ideas => (ideas?.Noted ?? 0) is > 0 and var n ? $"I{(n > 99 ? "99+" : n)}" : "I",
            _ => PilotChrome<DIR.Lib.RgbaImage>.NightGlyph(page, glyph),
        };
    }

    /// <summary>An entry's text, <see cref="EntryCells"/> chars (one cell each, as a cell holds one UTF-16 char).</summary>
    public static string EntryText(PilotPage page, GlyphMode mode, JobsScreen? jobs, IdeasScreen? ideas, DateTimeOffset now) =>
        $" {Glyph(page, mode, jobs, ideas, now).PadRight(GlyphCells)}{KeyFor(page)} ";

    /// <summary>
    /// What the bar remembers between frames to work round Console.Lib's CellBuffer, which diffs one UTF-16 char per cell: an
    /// emoji that changes to another sharing its high surrogate (the clipboard to the red dot) would flush only the low half, which
    /// the terminal draws as a replacement character. An entry whose glyph changes is drawn with its glyph blank for one frame,
    /// so the next frame writes the new glyph whole. <see cref="Settling"/> says a frame did so and the caller draws again at once.
    /// </summary>
    public sealed class Memory
    {
        private readonly Dictionary<PilotPage, string> _shown = [];
        private readonly HashSet<PilotPage> _blank = [];

        /// <summary>Whether the last <see cref="Tree"/> blanked an entry, so another frame is due.</summary>
        public bool Settling { get; internal set; }

        internal string Entry(PilotPage page, string text)
        {
            // The blank frame is over whatever follows it: forget it first, so an unchanged entry does not leave it standing.
            var wasBlank = _blank.Remove(page);
            if (!_shown.TryGetValue(page, out var before) || before == text || wasBlank)
            {
                _shown[page] = text;
                return text;
            }
            _shown[page] = text;
            _blank.Add(page);
            Settling = true;
            return $" {new string(' ', GlyphCells)}{KeyFor(page)} ";
        }
    }

    private static string Remember(Memory? memory, PilotPage page, string text) => memory?.Entry(page, text) ?? text;

    /// <summary>The bar's tree: one row of entries, the active page marked with a background, each a button that shows its page.</summary>
    public static Layout.Node Tree(PilotPage active, GlyphMode mode, JobsScreen? jobs, IdeasScreen? ideas, DateTimeOffset now, Action<PilotPage> show, Memory? memory = null)
    {
        if (memory is not null)
        {
            memory.Settling = false;
        }
        var palette = Palette.Current;
        var entries = Keys.Select(k =>
        {
            var on = k.Page == active;
            return Layout.Builder.Text(Remember(memory, k.Page, EntryText(k.Page, mode, jobs, ideas, now)), 12f, on ? palette.Bright : palette.Dim, TextAlign.Near, TextAlign.Center)
                .WFixed(EntryCells * 8f)
                .HFixed(16f)
                .Bg(on ? palette.RowFocusBg : palette.RowAltBg)
                .BgHover(palette.RowHoverBg)
                .WithTooltip($"{k.Label} ({k.Key})")
                .Follows(new HitResult.ButtonHit(Action(k.Page)), _ => show(k.Page));
        }).ToArray();
        return Layout.Builder.HStack(entries).HFixed(16f).Stretch();
    }
}
