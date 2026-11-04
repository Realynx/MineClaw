using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Styling;

namespace MineClaw.MemoryExplorer.Controls;

public enum CodeKind { Plain, Keyword, String, Number, Comment, Call, Sdk, Punct }

/// <summary>One coloured run of a source line.</summary>
public sealed record CodeSpan(string Text, CodeKind Kind);

/// <summary>
/// A small, line-based highlighter for the bot's scripts: JavaScript (every new script) and Lua (the history of migrated ones).
/// Strings, numbers, comments (block comments and template strings carry over lines), keywords, calls, and the SDK's own
/// names. Good enough to read a script; it is not a parser.
/// </summary>
public static class CodeHighlighter
{
    private static readonly HashSet<string> JsKeywords = new(StringComparer.Ordinal)
    {
        "const", "let", "var", "function", "return", "if", "else", "for", "while", "do", "break", "continue", "switch", "case", "default",
        "try", "catch", "finally", "throw", "new", "typeof", "instanceof", "in", "of", "async", "await", "true", "false", "null", "undefined",
        "this", "class", "extends", "delete", "void", "yield",
    };

    private static readonly HashSet<string> LuaKeywords = new(StringComparer.Ordinal)
    {
        "local", "function", "return", "if", "then", "else", "elseif", "end", "for", "while", "do", "repeat", "until", "break", "in", "and",
        "or", "not", "true", "false", "nil", "goto",
    };

    /// <summary>SDK globals that read as "the bot's API" (the rest of the calls are coloured as plain calls).</summary>
    private static readonly HashSet<string> SdkNames = new(StringComparer.Ordinal)
    {
        "ok", "expect", "fail", "log", "prog", "wait", "waitFor", "say", "emote", "ask", "wake", "cp", "on", "skill", "params", "carry", "args",
        "me", "inv", "block", "near", "hostile", "where", "night", "time", "players", "retry", "upto", "until", "seq", "sel", "timeout", "room",
        "ensure", "machine", "after", "every", "when", "signal", "use", "policy", "keepFed", "fleeVoidEdges", "defendIfAttacked",
        "depositWhenFull", "replaceToolWhenBroken", "pauseAtNight", "store", "restock", "mineGen", "harvestFarm", "chopTrees", "deliver",
        "waitForDay", "guardArea", "body", "bodies", "spawn", "parallel", "crew", "main", "find", "item", "gather", "stash", "handTo",
        "makeRoom", "patrol", "quota", "forEach",
    };

    /// <summary>Highlights every line; block comments and template strings continue across lines.</summary>
    public static IReadOnlyList<IReadOnlyList<CodeSpan>> Lines(string? code, bool lua)
    {
        var result = new List<IReadOnlyList<CodeSpan>>();
        var state = State.None;
        foreach (var line in (code ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            result.Add(Line(line, lua, ref state));
        }
        return result;
    }

    private enum State { None, BlockComment, Template, LuaBlock }

    private static IReadOnlyList<CodeSpan> Line(string s, bool lua, ref State state)
    {
        var lexer = new LineLexer(s, lua, state);
        lexer.Run();
        state = lexer.State;
        return lexer.Spans;
    }

    /// <summary>One line's tokenizer: the cursor, the multi-line construct still open, and the spans so far.</summary>
    private sealed class LineLexer(string s, bool lua, State state)
    {
        private readonly HashSet<string> _keywords = lua ? LuaKeywords : JsKeywords;
        private int _i;

        public State State { get; private set; } = state;
        public List<CodeSpan> Spans { get; } = [];

        public void Run()
        {
            while (_i < s.Length)
            {
                bool more = State switch
                {
                    State.BlockComment or State.LuaBlock => CloseBlock(),
                    State.Template => CloseTemplate(),
                    _ => Token(),
                };
                if (!more) return;
            }
        }

        private void Add(int from, int to, CodeKind k)
        {
            if (to <= from) return;
            var text = s[from..to];
            if (Spans.Count > 0 && Spans[^1].Kind == k) Spans[^1] = Spans[^1] with { Text = Spans[^1].Text + text };
            else Spans.Add(new CodeSpan(text, k));
        }

        private bool At(int offset, char ch) => _i + offset < s.Length && s[_i + offset] == ch;

        /// <summary>Inside a block comment: up to its end, or the rest of the line (false).</summary>
        private bool CloseBlock()
        {
            var close = State == State.BlockComment ? "*/" : "]]";
            int end = s.IndexOf(close, _i, StringComparison.Ordinal);
            if (end < 0) { Add(_i, s.Length, CodeKind.Comment); return false; }
            Add(_i, end + 2, CodeKind.Comment);
            _i = end + 2;
            State = State.None;
            return true;
        }

        /// <summary>Inside a template string: up to its closing backtick, or the rest of the line (false).</summary>
        private bool CloseTemplate()
        {
            int end = s.IndexOf('`', _i);
            if (end < 0) { Add(_i, s.Length, CodeKind.String); return false; }
            Add(_i, end + 1, CodeKind.String);
            _i = end + 1;
            State = State.None;
            return true;
        }

        /// <summary>One token at the cursor; false when it took the rest of the line (a line comment).</summary>
        private bool Token()
        {
            char c = s[_i];
            if (lua ? c == '-' && At(1, '-') : c == '/' && (At(1, '/') || At(1, '*'))) return Comment();
            if (c is '"' or '\'' || (c == '`' && !lua)) Quoted(c);
            else if (char.IsDigit(c) || (c == '.' && _i + 1 < s.Length && char.IsDigit(s[_i + 1]))) Number();
            else if (char.IsLetter(c) || c is '_' or '$') Word();
            else { Add(_i, _i + 1, char.IsWhiteSpace(c) ? CodeKind.Plain : CodeKind.Punct); _i++; }
            return true;
        }

        /// <summary>At "//", "/*" (JS) or "--" (Lua): a block comment opens ("/*", "--[["), else the rest of the line is one.</summary>
        private bool Comment()
        {
            if (lua && At(2, '[') && At(3, '[')) return Open(State.LuaBlock, 4);
            if (!lua && At(1, '*')) return Open(State.BlockComment, 2);
            Add(_i, s.Length, CodeKind.Comment);
            return false;
        }

        private bool Open(State block, int length)
        {
            State = block;
            Add(_i, _i + length, CodeKind.Comment);
            _i += length;
            return true;
        }

        private void Quoted(char quote)
        {
            if (quote == '`') { State = State.Template; Add(_i, _i + 1, CodeKind.String); _i++; return; }
            int j = _i + 1;
            while (j < s.Length && s[j] != quote) j += s[j] == '\\' ? 2 : 1;
            j = Math.Min(s.Length, j + 1);
            Add(_i, j, CodeKind.String);
            _i = j;
        }

        private void Number()
        {
            int j = _i + 1;
            while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '.' || s[j] == '_')) j++;
            Add(_i, j, CodeKind.Number);
            _i = j;
        }

        private void Word()
        {
            int j = _i + 1;
            while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] is '_' or '$')) j++;
            var word = s[_i..j];
            bool member = _i > 0 && s[_i - 1] == '.';
            Add(_i, j, WordKind(word, member, CallFollows(j)));
            _i = j;
        }

        /// <summary>A '(' after the word (spaces allowed).</summary>
        private bool CallFollows(int j)
        {
            while (j < s.Length && s[j] == ' ') j++;
            return j < s.Length && s[j] == '(';
        }

        private CodeKind WordKind(string word, bool member, bool call) =>
            _keywords.Contains(word) && !member ? CodeKind.Keyword
            : !member && SdkNames.Contains(word) ? CodeKind.Sdk
            : call ? CodeKind.Call : CodeKind.Plain;
    }

    /// <summary>The plain text of highlighted spans (tests, copy).</summary>
    public static string Text(IEnumerable<CodeSpan> spans)
    {
        var sb = new StringBuilder();
        foreach (var s in spans) sb.Append(s.Text);
        return sb.ToString();
    }

    private static readonly Dictionary<CodeKind, IBrush> Light = new()
    {
        [CodeKind.Keyword] = Brush("#8B3FD9"), [CodeKind.String] = Brush("#1F7A3A"), [CodeKind.Number] = Brush("#B5520B"),
        [CodeKind.Comment] = Brush("#7C8594"), [CodeKind.Call] = Brush("#1F5FBF"), [CodeKind.Sdk] = Brush("#0F7C8C"), [CodeKind.Punct] = Brush("#5A6270"),
    };

    private static readonly Dictionary<CodeKind, IBrush> Dark = new()
    {
        [CodeKind.Keyword] = Brush("#C792EA"), [CodeKind.String] = Brush("#A5D6A7"), [CodeKind.Number] = Brush("#F7B267"),
        [CodeKind.Comment] = Brush("#7F8796"), [CodeKind.Call] = Brush("#82AAFF"), [CodeKind.Sdk] = Brush("#5FD3D3"), [CodeKind.Punct] = Brush("#A9B1BF"),
    };

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex)).ToImmutable();

    /// <summary>The brush for a kind in the current theme (null = the text's own foreground).</summary>
    public static IBrush? BrushFor(CodeKind kind, bool dark) => (dark ? Dark : Light).GetValueOrDefault(kind);
}

/// <summary>A TextBlock that shows one highlighted source line (<see cref="Spans"/>).</summary>
public sealed class CodeLineView : TextBlock
{
    public static readonly StyledProperty<IReadOnlyList<CodeSpan>?> SpansProperty =
        AvaloniaProperty.Register<CodeLineView, IReadOnlyList<CodeSpan>?>(nameof(Spans));

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public CodeLineView() => ActualThemeVariantChanged += (_, _) => Rebuild();

    public IReadOnlyList<CodeSpan>? Spans
    {
        get => GetValue(SpansProperty);
        set => SetValue(SpansProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SpansProperty) Rebuild();
    }

    private void Rebuild()
    {
        var inlines = new InlineCollection();
        bool dark = ActualThemeVariant == ThemeVariant.Dark;
        foreach (var s in Spans ?? [])
        {
            var run = new Run(s.Text);
            if (CodeHighlighter.BrushFor(s.Kind, dark) is { } b) run.Foreground = b;
            if (s.Kind == CodeKind.Comment) run.FontStyle = FontStyle.Italic;
            inlines.Add(run);
        }
        Inlines = inlines;
    }
}
