using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Input;

namespace MineClaw.MemoryExplorer.Tests;

/// <summary>
/// A deterministic text dump of an object's public state (properties sorted by name; collections item by item; nested
/// Explorer objects one level deep), for golden tests that pin a view model after a scripted sequence of inputs.
/// </summary>
internal static class StateDump
{
    public static string Of(object? o, int depth = 2)
    {
        var sb = new StringBuilder();
        Write(sb, o, depth, "");
        return sb.ToString();
    }

    private static bool Plain(Type t) =>
        t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(DateTimeOffset)
        || t == typeof(TimeSpan) || t == typeof(Guid);

    private static bool Ours(Type t) => t.Namespace?.StartsWith("MineClaw", StringComparison.Ordinal) == true;

    private static void Write(StringBuilder sb, object? o, int depth, string indent)
    {
        switch (o)
        {
            case null: sb.Append("null"); return;
            case string s: sb.Append('"').Append(s.Replace("\n", "\\n")).Append('"'); return;
            case IFormattable f when Plain(o.GetType()): sb.Append(f.ToString(null, CultureInfo.InvariantCulture)); return;
            case JsonNode j: sb.Append(j.ToJsonString()); return;
            case ICommand: sb.Append("cmd"); return;
        }
        var t = o.GetType();
        if (Plain(t)) { sb.Append(o); return; }
        if (o is IEnumerable e)
        {
            sb.Append('[');
            int n = 0;
            foreach (var item in e)
            {
                sb.Append('\n').Append(indent).Append("  ").Append(n++).Append(": ");
                Write(sb, item, depth - 1, indent + "  ");
            }
            sb.Append(n > 0 ? "\n" + indent + "]" : "]");
            return;
        }
        if (!Ours(t) || depth <= 0) { sb.Append(o); return; }
        sb.Append(t.Name).Append(" {");
        foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => p.GetIndexParameters().Length == 0).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (typeof(ICommand).IsAssignableFrom(p.PropertyType)) continue;
            sb.Append('\n').Append(indent).Append("  ").Append(p.Name).Append(" = ");
            object? v;
            try { v = p.GetValue(o); }
            catch (TargetInvocationException ex) { sb.Append("throws ").Append(ex.InnerException?.GetType().Name); continue; }
            Write(sb, v, depth - 1, indent + "  ");
        }
        sb.Append('\n').Append(indent).Append('}');
    }
}
