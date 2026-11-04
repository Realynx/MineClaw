using System.Reflection;
using System.Runtime.CompilerServices;
using Acornima.Ast;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime.Debugger;

namespace MineClaw.Scripting.Runtime.Js;

/// <summary>
/// The debugger probe (<see cref="IScriptDebugProbe"/>). Jint's own debugger (DebugMode) would allocate a DebugInformation per
/// statement on every run, watched or not, so it stays off. Instead: the current statement is the engine's last-syntax-element
/// pointer (read off-thread: one reference load, no hook); the call stack is the text Jint builds at each host call anyway;
/// locals come from the execution-context stack's lexical environments through Jint's public <see cref="DebugScopes"/> (built
/// by reflection over its internal constructor; Jint is pinned, and any mismatch just turns locals off with a note).
/// </summary>
internal sealed partial class JsRuntime : IScriptDebugProbe
{
    private volatile string? _lastStack;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_lastSyntaxElement")]
    private static extern ref Node? LastNode(Engine engine);

    private static int _lastNodeBroken;

    public (string File, int Line, int Col)? LiveLocation()
    {
        var e = _e;
        if (e == null || Volatile.Read(ref _lastNodeBroken) != 0) return null;
        Node? n;
        try { n = LastNode(e); }
        catch (Exception) { Volatile.Write(ref _lastNodeBroken, 1); return null; }
        if (n == null) return null;
        var loc = n.Location;
        int line = loc.Start.Line, col = loc.Start.Column + 1;
        var file = loc.SourceFile ?? "";
        if (file == "script" && line == 1) col = Math.Max(1, col - WrapPrefix.Length);
        return (file, line, col);
    }

    public string? StackText()
    {
        try { return _lastStack = _e?.Advanced.StackTrace; }
        catch (Exception) { return _lastStack; }
    }

    public string? LastStackText() => _lastStack;

    // ------------------------------------------------------------------ locals

    private static readonly Lazy<LocalsAccess?> Access = new(LocalsAccess.Create);

    public (IReadOnlyList<ScriptLocal>? Locals, string? Note) Locals()
    {
        if (Access.Value is not { } acc) return (null, "locals unavailable with this Jint build (the reflection probe found no execution contexts)");
        var e = _e;
        if (e == null) return (null, "engine not started");
        var envs = acc.ScriptEnvironments(e);
        if (envs.Count == 0) return (null, "no script frame on the stack");
        var list = new List<ScriptLocal>();
        // the innermost script frame only (the first one whose scopes resolve)
        if (FirstScopes(acc, envs) is { } scopes && !CollectLocals(scopes, list)) return (list, "first 40 shown");
        return (list, list.Count == 0 ? "no locals in this frame" : null);
    }

    private static DebugScopes? FirstScopes(LocalsAccess acc, List<object> envs)
    {
        foreach (var env in envs)
            if (acc.Scopes(env) is { } scopes) return scopes;
        return null;
    }

    /// <summary>The frame's own function and its blocks, plus one closure; false once the 40-local cap is hit.</summary>
    private static bool CollectLocals(DebugScopes scopes, List<ScriptLocal> list)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scope in scopes)
        {
            if (scope.ScopeType is DebugScopeType.Global or DebugScopeType.Script or DebugScopeType.With or DebugScopeType.Module) break;
            if (!AddScopeLocals(scope, ScopeKind(scope.ScopeType), seen, list)) return false;
            if (scope.ScopeType == DebugScopeType.Closure) break;
        }
        return true;
    }

    private static string ScopeKind(DebugScopeType type) => type switch
    {
        DebugScopeType.Local => "local",
        DebugScopeType.Closure => "closure",
        DebugScopeType.Catch => "catch",
        _ => "block",
    };

    private static bool AddScopeLocals(DebugScope scope, string kind, HashSet<string> seen, List<ScriptLocal> list)
    {
        foreach (var name in scope.BindingNames)
        {
            if (name is "arguments" or "this" || !seen.Add(name)) continue;
            JsValue? v;
            try { v = scope.GetBindingValue(name); } catch (Exception) { v = null; }
            if (v is Function) continue; // helper functions are code, not state
            list.Add(new ScriptLocal(name, v == null ? "(uninitialized)" : Preview(v, 0), kind));
            if (list.Count >= 40) return false;
        }
        return true;
    }

    /// <summary>A short JS-looking preview that never runs script code (data properties only; getters show as "get").</summary>
    internal static string Preview(JsValue v, int depth)
    {
        try { return PreviewScalar(v, depth) ?? PreviewComposite(v, depth); }
        catch (Exception) { return "?"; }
    }

    private static string? PreviewScalar(JsValue? v, int depth)
    {
        if (v is null) return null;
        if (v.IsUndefined()) return "undefined";
        if (v.IsNull()) return "null";
        if (v.IsBoolean()) return v.AsBoolean() ? "true" : "false";
        if (v.IsNumber()) return ScriptSession.Fmt(v.AsNumber());
        if (v.IsString()) return "'" + ScriptSession.Short(v.AsString().Replace("\n", "\\n"), depth == 0 ? 80 : 24) + "'";
        return null;
    }

    private static string PreviewComposite(JsValue? v, int depth) => v switch
    {
        Function f => "ƒ " + JsConvert.Text(f.Get("name")) + "()",
        JsArray a => PreviewArray(a, depth),
        ObjectInstance o => PreviewObject(o, depth),
        _ => ScriptSession.Short(v?.ToString() ?? "?", 60),
    };

    private static string PreviewArray(JsArray a, int depth)
    {
        var n = (int)Math.Min(a.Length, int.MaxValue);
        if (depth > 0) return $"Array({n})";
        var items = new List<string>();
        for (uint i = 0; i < Math.Min(n, 6); i++)
            items.Add(Preview(a[i], depth + 1));
        return "[" + string.Join(", ", items) + (n > 6 ? $", … {n} items" : "") + "]";
    }

    private static string PreviewObject(ObjectInstance o, int depth)
    {
        if (depth > 0) return "{…}";
        var parts = new List<string>();
        int count = 0;
        foreach (var (key, desc) in o.GetOwnProperties())
        {
            if (!key.IsString()) continue;
            if (++count > 6) continue;
            var val = desc.IsAccessorDescriptor() ? "get" : desc.Value is { } dv ? Preview(dv, depth + 1) : "?";
            parts.Add(key.AsString() + ": " + val);
        }
        return "{" + string.Join(", ", parts) + (count > 6 ? $", … {count} keys" : "") + "}";
    }

    /// <summary>A value as JSON reading data properties only (never a getter or a proxy trap), ≤ 4 levels, ≤ 64 keys/items each.</summary>
    internal static System.Text.Json.Nodes.JsonNode? SafeNode(JsValue v, int depth)
    {
        if (TrySafeScalar(v, out var scalar)) return scalar;
        if (depth >= 4) return System.Text.Json.Nodes.JsonValue.Create("…");
        if (v is JsArray a) return SafeArray(a, depth);
        if (v is ObjectInstance o) return SafeObject(o, depth);
        return System.Text.Json.Nodes.JsonValue.Create(v.ToString());
    }

    /// <summary>undefined/null, booleans, numbers (non-finite as text), short strings and functions (by name).</summary>
    private static bool TrySafeScalar(JsValue v, out System.Text.Json.Nodes.JsonNode? node)
    {
        node = null;
        if (v.IsUndefined() || v.IsNull()) return true;
        if (v.IsBoolean()) node = System.Text.Json.Nodes.JsonValue.Create(v.AsBoolean());
        else if (v.IsNumber()) node = SafeNumber(v.AsNumber());
        else if (v.IsString()) node = System.Text.Json.Nodes.JsonValue.Create(ScriptSession.Short(v.AsString(), 200));
        else if (v is Function f) node = System.Text.Json.Nodes.JsonValue.Create("ƒ " + JsConvert.Text(f.Get("name")));
        return node != null;
    }

    private static System.Text.Json.Nodes.JsonNode SafeNumber(double d) => double.IsFinite(d)
        ? System.Text.Json.Nodes.JsonValue.Create(d)
        : System.Text.Json.Nodes.JsonValue.Create(d.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static System.Text.Json.Nodes.JsonArray SafeArray(JsArray a, int depth)
    {
        var arr = new System.Text.Json.Nodes.JsonArray();
        for (uint i = 0; i < Math.Min(a.Length, 64u); i++) arr.Add(SafeNode(a[i], depth + 1));
        return arr;
    }

    private static System.Text.Json.Nodes.JsonObject SafeObject(ObjectInstance o, int depth)
    {
        var obj = new System.Text.Json.Nodes.JsonObject();
        foreach (var (key, desc) in o.GetOwnProperties())
        {
            if (!key.IsString() || obj.Count >= 64) continue;
            obj[key.AsString()] = desc.IsAccessorDescriptor() ? System.Text.Json.Nodes.JsonValue.Create("(getter)") : desc.Value is { } dv ? SafeNode(dv, depth + 1) : null;
        }
        return obj;
    }

    /// <summary>Reflection over Jint internals, resolved once: the execution-context stack and the DebugScopes constructor.</summary>
    private sealed class LocalsAccess
    {
        private FieldInfo _contexts = null!, _stack = null!, _array = null!, _size = null!, _lexical = null!, _function = null!;
        private ConstructorInfo _scopes = null!;

        public static LocalsAccess? Create()
        {
            try
            {
                const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var a = new LocalsAccess();
                a._contexts = typeof(Engine).GetField("_executionContexts", any) ?? throw new MissingFieldException("_executionContexts");
                a._stack = a._contexts.FieldType.GetField("_stack", any) ?? throw new MissingFieldException("_stack");
                a._array = a._stack.FieldType.GetField("_array", any) ?? throw new MissingFieldException("_array");
                a._size = a._stack.FieldType.GetField("_size", any) ?? throw new MissingFieldException("_size");
                var ctxType = a._array.FieldType.GetElementType() ?? throw new MissingFieldException("ExecutionContext");
                a._lexical = ctxType.GetField("LexicalEnvironment", any) ?? throw new MissingFieldException("LexicalEnvironment");
                a._function = ctxType.GetField("Function", any) ?? throw new MissingFieldException("Function");
                var envType = a._lexical.FieldType;
                a._scopes = typeof(DebugScopes).GetConstructor(any, [envType]) ?? throw new MissingMethodException("DebugScopes(Environment)");
                return a;
            }
            catch (Exception) { return null; }
        }

        /// <summary>The lexical environments of the script-sourced contexts, innermost first.</summary>
        public List<object> ScriptEnvironments(Engine e)
        {
            var list = new List<object>();
            var stackObj = _stack.GetValue(_contexts.GetValue(e));
            if (stackObj == null || _array.GetValue(stackObj) is not Array arr) return list;
            int size = Math.Min((int)(_size.GetValue(stackObj) ?? 0), arr.Length);
            for (int i = size - 1; i >= 0 && list.Count < 4; i--)
                if (ScriptLexical(arr.GetValue(i)) is { } env) list.Add(env);
            return list;
        }

        /// <summary>A context's lexical environment when it runs a script-sourced function, else null.</summary>
        private object? ScriptLexical(object? ctx)
        {
            if (ctx == null) return null;
            if (_function.GetValue(ctx) is not Function f) return null;
            if (f.FunctionDeclaration is not Node decl || decl.Location.SourceFile != "script") return null;
            return _lexical.GetValue(ctx);
        }

        public DebugScopes? Scopes(object env)
        {
            try { return (DebugScopes)_scopes.Invoke([env]); }
            catch (Exception) { return null; }
        }
    }
}
