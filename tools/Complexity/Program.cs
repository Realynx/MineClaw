using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MineClaw.Complexity;

// Cyclomatic-complexity report for the C# code. Report only: exit 0 unless --fail-over is given and exceeded (2 on bad usage).
var (options, exit) = ParseArgs(args);
if (options is null)
{
    return exit;
}

var root = Path.GetFullPath(options.Root ?? SourceScanner.FindRoot(Directory.GetCurrentDirectory()));
var scanner = new SourceScanner(root, options.Tests);
var files = scanner.Files(options.Paths.Count > 0 ? options.Paths : scanner.DefaultRoots())
    .Where(f => !options.Excludes.Any(x => SourceScanner.IsUnder(scanner.Relative(f), x)))
    .ToList();
var collector = new UnitCollector(new ComplexityCounter(options.CountConditionalAccess), options.InlineLambdas);
var parse = new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.None);

var measured = files.SelectMany(Measure).ToList();
var report = Report.Build(measured, files.Count, options.Threshold, options.All);
Emit(options.Format == "json" ? report.ToJson() : report.ToTable(options.Top));
return options.FailOver is { } max && measured.Any(f => f.Complexity > max) ? 1 : 0;

static (Options? Options, int Exit) ParseArgs(string[] args)
{
    if (args.Contains("--help") || args.Contains("-h"))
    {
        Console.WriteLine(Options.Usage);
        return (null, 0);
    }

    try
    {
        return (Options.Parse(args), 0);
    }
    catch (Exception e) when (e is ArgumentException or FormatException)
    {
        Console.Error.WriteLine(e.Message);
        Console.Error.WriteLine(Options.Usage);
        return (null, 2);
    }
}

void Emit(string text)
{
    if (options.Out is null)
    {
        Console.Write(text);
        return;
    }

    File.WriteAllText(options.Out, text);
    Console.Error.WriteLine($"wrote {options.Out}: {measured.Count(f => f.Complexity > options.Threshold)} functions over {options.Threshold}");
}

IEnumerable<FunctionReport> Measure(string file)
{
    var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), parse, file);
    var relative = scanner.Relative(file);
    var project = SourceScanner.ProjectOf(relative);
    return collector.Collect((CompilationUnitSyntax)tree.GetRoot()).Select(u => new FunctionReport(
        relative,
        u.Span.StartLinePosition.Line + 1,
        u.Span.EndLinePosition.Line + 1,
        u.Member,
        u.Kind,
        u.Complexity,
        u.Span.EndLinePosition.Line - u.Span.StartLinePosition.Line + 1,
        project)).ToList();
}
