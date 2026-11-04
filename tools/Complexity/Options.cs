namespace MineClaw.Complexity;

/// <summary>Command-line options. See <see cref="Usage"/>.</summary>
internal sealed class Options
{
    public const string Usage = """
        Complexity - McCabe cyclomatic complexity of every C# method (report only, never edits code).

        usage: dotnet run --project tools/Complexity -- [paths...] [options]

          paths                        files or directories to scan (default: src/ and tools/, plus tests/ with --tests)
          --root <dir>                 repo root (default: nearest parent with MineClaw.sln)
          --format table|json          output format (default table)
          --threshold <n>              report functions with complexity > n (default 10)
          --all                        list every function, not only the ones over the threshold
          --top <n>                    table: show only the n worst rows (totals still cover everything)
          --out <file>                 write the report to a file instead of stdout
          --tests                      also scan test projects (tests/ and *.Tests directories)
          --inline-lambdas             fold lambdas into their enclosing member (VS Code Metrics style)
          --count-conditional-access   also count ?. and ?[] (ESLint parity; off by default)
          --exclude <path>             skip files under this repo-relative path (repeatable), e.g. src/MineClaw.Bot/Blueprints
          --fail-over <n>              exit 1 when any function exceeds n (default: always exit 0)
        """;

    public List<string> Paths { get; } = [];
    public List<string> Excludes { get; } = [];
    public string? Root { get; set; }
    public string Format { get; set; } = "table";
    public int Threshold { get; set; } = 10;
    public bool All { get; set; }
    public int? Top { get; set; }
    public string? Out { get; set; }
    public bool Tests { get; set; }
    public bool InlineLambdas { get; set; }
    public bool CountConditionalAccess { get; set; }
    public int? FailOver { get; set; }

    private static readonly Dictionary<string, Action<Options>> Flags = new()
    {
        ["--all"] = o => o.All = true,
        ["--tests"] = o => o.Tests = true,
        ["--inline-lambdas"] = o => o.InlineLambdas = true,
        ["--count-conditional-access"] = o => o.CountConditionalAccess = true,
    };

    private static readonly Dictionary<string, Action<Options, string>> Valued = new()
    {
        ["--root"] = (o, v) => o.Root = v,
        ["--format"] = (o, v) => o.Format = v,
        ["--threshold"] = (o, v) => o.Threshold = int.Parse(v),
        ["--top"] = (o, v) => o.Top = int.Parse(v),
        ["--out"] = (o, v) => o.Out = v,
        ["--exclude"] = (o, v) => o.Excludes.Add(v.Replace('\\', '/').TrimEnd('/')),
        ["--fail-over"] = (o, v) => o.FailOver = int.Parse(v),
    };

    /// <summary>Parses <paramref name="args"/>; throws <see cref="ArgumentException"/> on a bad flag.</summary>
    public static Options Parse(string[] args)
    {
        var options = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            i = options.Apply(args, i);
        }

        if (options.Format is not ("table" or "json"))
        {
            throw new ArgumentException($"unknown format '{options.Format}' (table|json)");
        }

        return options;
    }

    private int Apply(string[] args, int i)
    {
        var arg = args[i];
        if (Flags.TryGetValue(arg, out var flag))
        {
            flag(this);
            return i;
        }

        if (Valued.TryGetValue(arg, out var valued))
        {
            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"{arg} needs a value");
            }

            valued(this, args[i + 1]);
            return i + 1;
        }

        if (arg.StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"unknown option '{arg}'");
        }

        Paths.Add(arg);
        return i;
    }
}
