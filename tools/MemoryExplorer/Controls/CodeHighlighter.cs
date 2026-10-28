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
        "waitForDay", "guardArea", "body", "bodies", "spawn", "parallel", "crew", "main",
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
        var spans = new List<CodeSpan>();
        var kw = lua ? LuaKeywords : JsKeywords;
        int i = 0;
        void Add(int from, int to, CodeKind k)
        {
            if (to <= from) return;
            var text = s[from..to];
            if (spans.Count > 0 && spans[^1].Kind == k) spans[^1] = spans[^1] with { Text = spans[^1].Text + text };
            else spans.Add(new CodeSpan(text, k));
        }
        while (i < s.Length)
        {
            if (state == State.BlockComment || state == State.LuaBlock)
            {
                var close = state == State.BlockComment ? "*/" : "]]";
                int end = s.IndexOf(close, i, StringComparison.Ordinal);
                if (end < 0) { Add(i, s.Length, CodeKind.Comment); return spans; }
                Add(i, end + 2, CodeKind.Comment);
                i = end + 2;
                state = State.None;
                continue;
            }
            if (state == State.Template)
            {
                int end = s.IndexOf('`', i);
                if (end < 0) { Add(i, s.Length, CodeKind.String); return spans; }
                Add(i, end + 1, CodeKind.String);
                i = end + 1;
                state = State.None;
                continue;
            }
            char c = s[i];
            if (!lua && c == '/' && i + 1 < s.Length && s[i + 1] == '/') { Add(i, s.Length, CodeKind.Comment); break; }
            if (!lua && c == '/' && i + 1 < s.Length && s[i + 1] == '*') { state = State.BlockComment; Add(i, i + 2, CodeKind.Comment); i += 2; continue; }
            if (lua && c == '-' && i + 1 < s.Length && s[i + 1] == '-')
            {
                if (i + 3 < s.Length && s[i + 2] == '[' && s[i + 3] == '[') { state = State.LuaBlock; Add(i, i + 4, CodeKind.Comment); i += 4; continue; }
                Add(i, s.Length, CodeKind.Comment);
                break;
            }
            if (c is '"' or '\'' || (c == '`' && !lua))
            {
                if (c == '`') { state = State.Template; Add(i, i + 1, CodeKind.String); i++; continue; }
                int j = i + 1;
                while (j < s.Length && s[j] != c) j += s[j] == '\\' ? 2 : 1;
                j = Math.Min(s.Length, j + 1);
                Add(i, j, CodeKind.String);
                i = j;
                continue;
            }
            if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
            {
                int j = i + 1;
                while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '.' || s[j] == '_')) j++;
                Add(i, j, CodeKind.Number);
                i = j;
                continue;
            }
            if (char.IsLetter(c) || c is '_' or '$')
            {
                int j = i + 1;
                while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] is '_' or '$')) j++;
                var word = s[i..j];
                bool member = i > 0 && s[i - 1] == '.';
                int k = j;
                while (k < s.Length && s[k] == ' ') k++;
                bool call = k < s.Length && s[k] == '(';
                var kind = kw.Contains(word) && !member ? CodeKind.Keyword
                    : !member && SdkNames.Contains(word) ? CodeKind.Sdk
                    : call ? CodeKind.Call : CodeKind.Plain;
                Add(i, j, kind);
                i = j;
                continue;
            }
            Add(i, i + 1, char.IsWhiteSpace(c) ? CodeKind.Plain : CodeKind.Punct);
            i++;
        }
        return spans;
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
