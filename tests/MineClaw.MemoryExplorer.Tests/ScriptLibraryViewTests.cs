using MineClaw.Agent.Routines;
using MineClaw.MemoryExplorer.Controls;
using MineClaw.MemoryExplorer.Services;
using MineClaw.MemoryExplorer.ViewModels;

namespace MineClaw.MemoryExplorer.Tests;

/// <summary>The Scripts page's diff between versions (a migrated script's Lua original vs its JS), highlighting and offline lint.</summary>
public class ScriptLibraryViewTests
{
    private const string Lua = """
        -- purpose: mine the gen
        for i = 1, 4 do
          gen("4,64,6")
        end
        deposit()
        """;

    private const string Js = """
        // purpose: mine the gen
        const p = params({n: 4, cell: '4,64,6'})
        for (let i = 0; i < p.n; i++) {
          gen(p.cell)
        }
        deposit()
        """;

    [Fact]
    public void Diff_lines_mark_added_removed_and_unchanged_with_both_line_numbers()
    {
        var lines = ScriptDiff.Lines("a\nb\nc\nd", "a\nB\nc\nd\ne");
        Assert.Equal(
            ["  1 1 a", "- 2 _ b", "+ _ 2 B", "  3 3 c", "  4 4 d", "+ _ 5 e"],
            lines.Select(l => $"{l.Prefix} {l.OldNo?.ToString() ?? "_"} {l.NewNo?.ToString() ?? "_"} {l.Text}"));
        Assert.Equal((2, 1), ScriptDiff.Stats(lines));
        // Trailing whitespace and CRLF don't count as a change.
        Assert.All(ScriptDiff.Lines("x  \r\ny", "x\ny"), l => Assert.Equal(DiffKind.Same, l.Kind));
    }

    [Fact]
    public void Hunks_fold_unchanged_runs_and_the_unified_text_has_hunk_headers()
    {
        var old = string.Join("\n", Enumerable.Range(1, 20).Select(i => "line " + i));
        var neu = old.Replace("line 10", "line ten");
        var lines = ScriptDiff.Lines(old, neu);
        var hunks = ScriptDiff.Hunks(lines, context: 2);
        Assert.Equal(DiffKind.Gap, hunks[0].Kind);
        Assert.Equal("7 unchanged lines", hunks[0].Text);
        Assert.Equal(["line 8", "line 9", "line 10", "line ten", "line 11", "line 12"], hunks.Skip(1).Take(6).Select(l => l.Text));
        Assert.Equal(DiffKind.Gap, hunks[^1].Kind);
        var unified = ScriptDiff.Unified(lines, "x v1", "x v2", context: 2);
        Assert.StartsWith("--- x v1", unified);
        Assert.Contains("@@ -8,5 +8,5 @@", unified);
        Assert.Contains("-line 10", unified);
        Assert.Contains("+line ten", unified);
    }

    [Fact]
    public void Lua_to_js_diff_rows_highlight_each_side_in_its_own_language()
    {
        var rows = ScriptDiffRow.Build(Lua, oldLua: true, Js, newLua: false, hunksOnly: false);
        // The Lua comment line is removed (highlighted as a Lua comment), the JS comment line added (a JS comment).
        var luaComment = rows.First(r => r.Line.Kind == DiffKind.Removed && r.Line.Text.StartsWith("--"));
        Assert.Equal(CodeKind.Comment, luaComment.Spans.Single().Kind);
        var jsComment = rows.First(r => r.Line.Kind == DiffKind.Added && r.Line.Text.StartsWith("//"));
        Assert.Equal(CodeKind.Comment, jsComment.Spans.Single().Kind);
        // "deposit()" is the same in both: kept, numbered on both sides, coloured as a call.
        var same = rows.Single(r => r.Line.Kind == DiffKind.Same);
        Assert.Equal(("5", "6"), (same.Line.OldText, same.Line.NewText));
        Assert.Contains(same.Spans, s => s is { Text: "deposit", Kind: CodeKind.Call });
        // Every row's spans give back its text.
        Assert.All(rows, r => Assert.Equal(r.Line.Text, CodeHighlighter.Text(r.Spans)));
    }

    [Fact]
    public void Js_highlighting_knows_keywords_strings_numbers_comments_and_the_sdk()
    {
        var lines = CodeHighlighter.Lines("const p = params({n: 4}) // tune\nsay(`a\nb`) /* x\ny */ mineGen('A')", lua: false);
        var l1 = lines[0];
        Assert.Contains(l1, s => s is { Text: "const", Kind: CodeKind.Keyword });
        Assert.Contains(l1, s => s is { Text: "params", Kind: CodeKind.Sdk });
        Assert.Contains(l1, s => s is { Text: "4", Kind: CodeKind.Number });
        Assert.Contains(l1, s => s is { Text: "// tune", Kind: CodeKind.Comment });
        Assert.Contains(lines[1], s => s.Kind == CodeKind.String && s.Text.StartsWith('`'));   // template string runs on...
        Assert.Contains(lines[2], s => s is { Text: "b`", Kind: CodeKind.String });            // ...to the next line
        Assert.Contains(lines[3], s => s is { Text: "y */", Kind: CodeKind.Comment });         // block comment ends here
        Assert.Contains(lines[3], s => s is { Text: "mineGen", Kind: CodeKind.Sdk });
        Assert.Contains(lines[3], s => s is { Text: "'A'", Kind: CodeKind.String });
    }

    [Fact]
    public void Source_rows_carry_lint_marks_on_their_lines()
    {
        var a = OfflineLint.Analyze("const x = 1\nnotDeclared.y = 2\neval('1')", lua: false);
        Assert.Equal("errors", a.Lint.Status);
        var rows = CodeLineRow.Build("const x = 1\nnotDeclared.y = 2\neval('1')", false, a.Lint.Findings);
        Assert.False(rows[0].HasLint);
        Assert.True(rows[2].HasLint);
        Assert.True(rows[2].LintError);
        Assert.Contains("offline", a.Lint.Note);
        Assert.Equal([new ScriptParamInfo("n", "4"), new ScriptParamInfo("cell", "'4,64,6'")], OfflineLint.Analyze(Js, false).Params);
    }
}
