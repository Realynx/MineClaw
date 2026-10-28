using System.Text.Json;
using MineClaw.Scripting.Runtime.Js;

namespace MineClaw.Scripting.Sdk;

/// <summary>One game function a JS script calls as a global: <c>chop_tree</c> and <c>chopTree</c> both work.</summary>
/// <param name="Kind">alias (a short name such as <c>go</c>), action (a game tool) or skill (a catalog skill).</param>
public sealed record ScriptGlobal(string Name, string Camel, string Target, string Kind, IReadOnlyList<string> Params)
{
    /// <summary>"chopTree(near, n) / chop_tree"; params positional in this order, or one {name: value} object.</summary>
    public string Signature => Camel != Name ? $"{Camel}({string.Join(", ", Params)}) / {Name}" : $"{Name}({string.Join(", ", Params)})";
}

/// <summary>
/// The game functions the JS runtime defines as globals for a host, in the runtime's own order and with its own rules (aliases, then
/// action tools, then catalog skills; SDK names win; camelCase added): what <c>JsRuntime</c> binds, listed without running a script.
/// </summary>
public static class ScriptGlobals
{
    public static IReadOnlyList<ScriptGlobal> List(ScriptHost host)
    {
        var taken = new HashSet<string>(SdkModules.Globals, StringComparer.Ordinal);
        var byName = host.Actions.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        IReadOnlyCollection<string> skills;
        try { skills = host.Skills(); } catch (Exception) { skills = []; }
        var list = new List<ScriptGlobal>();
        bool Free(string n) => System.Text.RegularExpressions.Regex.IsMatch(n, @"^[A-Za-z_$][\w$]*$") && !JsRuntime.Reserved.Contains(n) && !taken.Contains(n);
        void Add(string global, string target, string kind, IReadOnlyList<string> ps)
        {
            if (!Free(global)) return;
            taken.Add(global);
            var camel = JsRuntime.Camel(global);
            if (camel != global && Free(camel)) taken.Add(camel); else camel = global;
            list.Add(new ScriptGlobal(global, camel, target, kind, ps));
        }
        IReadOnlyList<string> SkillParams(string skill)
        {
            JsonElement? schema = null;
            try { schema = host.SkillSchema(skill); } catch (Exception) { }
            return schema is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object
                ? props.EnumerateObject().Select(p => p.Name).ToList() : [];
        }
        foreach (var (alias, target) in ScriptSession.Aliases)
        {
            if (byName.TryGetValue(target, out var a)) Add(alias, target, "alias", a.Params);
            else if (skills.Contains(target)) Add(alias, target, "alias", SkillParams(target));
        }
        foreach (var a in host.Actions) Add(a.Name, a.Name, "action", a.Params);
        foreach (var sk in skills) Add(sk, sk, "skill", SkillParams(sk));
        return list;
    }
}
