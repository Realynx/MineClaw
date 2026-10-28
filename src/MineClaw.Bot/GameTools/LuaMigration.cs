using MineClaw.Agent.Authoring;
using MineClaw.Agent.Routines;
using MineClaw.Scripting;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// The one-time translation of saved Lua scripts to JavaScript (<see cref="ScriptOptions.MigrateLua"/>). For each saved script
/// whose active version is Lua (most used first): the Opus author gets the Lua and the SDK reference and writes the JS; the
/// draft must lint clean and survive the dry run (the same check as new scripts); the result is saved as the script's next
/// version with the Lua kept in its history (rollback still works; auto-revert compares their reliability). Capped at
/// <see cref="ScriptOptions.MigrateMaxUsdPerScript"/> per script and <see cref="ScriptOptions.MigrateMaxUsd"/> in total. Every
/// script gets a verdict on its row (<see cref="RoutineScript.Migration"/>), so the migration never repeats.
/// </summary>
public sealed class LuaMigration(ScriptBridge bridge, ILogger? log = null)
{
    public sealed record Item(string Name, int FromVersion, int? ToVersion, string Verdict, decimal Usd);

    /// <summary>Saved scripts still on Lua with no verdict yet (what a run would translate), most used first.</summary>
    public IReadOnlyList<Agent.Memory.Routine> Pending() =>
        bridge.Library.Scripts()
            .Where(r => r.Script is { Migration: null, DuplicateOf: null } sc && ScriptLibrary.Active(sc).IsLua && sc.Lang != "js")
            .OrderByDescending(r => r.Script!.Runs).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();

    public async Task<IReadOnlyList<Item>> RunAsync(CancellationToken ct)
    {
        var o = bridge.Options;
        var done = new List<Item>();
        if (bridge.Author is not { } author) return done;
        if (author.Refusal() is { } no) { log?.LogInformation("lua migration: skipped ({Why}); retried next start", no); return done; }
        var pending = Pending();
        if (pending.Count == 0) return done;
        log?.LogInformation("lua migration: {N} saved Lua script(s) to translate (cap ${Each:0.00} each, ${Total:0.00} total)", pending.Count,
            o.MigrateMaxUsdPerScript, o.MigrateMaxUsd);
        decimal spent = 0;
        foreach (var r in pending)
        {
            ct.ThrowIfCancellationRequested();
            var sc = r.Script!;
            var from = ScriptLibrary.Active(sc);
            var left = o.MigrateMaxUsd - spent;
            if (left <= 0.01m) { log?.LogInformation("lua migration: total cap reached; {Name} and the rest wait for the next start", r.Name); break; }
            var job = new AuthorJob
            {
                Kind = "migrate",
                System = ScriptPrompts.AuthorSystem,
                Brief = ScriptPrompts.MigrationBrief(r.Name, from.Code, bridge.AuthorFacts(r.Name), o.MaxCodeChars),
                Check = bridge.CheckDraft,
                MaxUsd = Math.Min(o.MigrateMaxUsdPerScript, left),
            };
            AuthorOutcome outcome;
            try { outcome = await author.RunAsync(job, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log?.LogWarning(ex, "lua migration: {Name} failed", r.Name);
                continue; // transient (network): no verdict, retried next start
            }
            spent += outcome.Usd;
            if (!outcome.Ok || outcome.Artifact is not { } js)
            {
                var why = $"failed: {outcome.Blocker} ({outcome.Cost})";
                bridge.Library.MarkMigration(r.Name, why);
                done.Add(new Item(r.Name, from.V, null, why, outcome.Usd));
                log?.LogWarning("lua migration: {Name} v{V} not translated: {Why}", r.Name, from.V, why);
                continue;
            }
            var save = bridge.Library.SaveVersion(r.Name, js, author: $"migration/{outcome.Model}", purpose: ScriptLint.Purpose(js) ?? sc.Purpose,
                argNames: ScriptLint.ArgNames(js), lang: "js");
            var to = save.Routine?.Script?.Version;
            var verdict = $"ok v{to} from Lua v{from.V} ({outcome.Cost})";
            bridge.Library.MarkMigration(r.Name, verdict);
            done.Add(new Item(r.Name, from.V, to, verdict, outcome.Usd));
            log?.LogInformation("lua migration: {Name} v{From} (Lua, {LuaChars} chars) -> v{To} (JS, {JsChars} chars), {Cost}", r.Name, from.V,
                from.Code.Length, to, js.Length, outcome.Cost);
        }
        log?.LogInformation("lua migration: {Ok}/{N} translated, ${Usd:0.00} spent", done.Count(d => d.ToVersion != null), done.Count, spent);
        return done;
    }
}
