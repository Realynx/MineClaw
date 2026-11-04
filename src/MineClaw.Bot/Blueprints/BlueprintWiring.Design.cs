using System.Globalization;
using System.Text.Json;
using MineClaw.Agent;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;

namespace MineClaw.Bot.Blueprints;

/// <summary>The brain's <c>design</c> tool: one handler per op (<see cref="Ops"/>).</summary>
public sealed partial class BlueprintWiring
{
    private const string NoDesign = "no design (id?)";

    /// <summary>One design call: its input, the speaker of the turn and the design it is about.</summary>
    private sealed class DesignCall(BlueprintWiring wiring, JsonElement input)
    {
        public JsonElement Input { get; } = input;
        public string? Speaker { get; } = TurnContext.Current?.Speaker;
        public string? IdText { get; } = BlueprintWiring.Id(input, "id");

        /// <summary>"my house" → the captured design it names (its remembered structure's link), before the store's name search;
        /// no id = the speaker's pending design, else the newest.</summary>
        public BlueprintRecord? Rec()
        {
            var r = IdText is { Length: > 0 } ? wiring.Owned?.Invoke(IdText, Speaker) ?? wiring.Service.Store.Find(IdText)
                : wiring.Service.Pending(Speaker) ?? wiring.Service.Store.List(1).FirstOrDefault();
            // A trusted player's turn working on a design opens it for editing (their requested digs may change it).
            if (r != null && wiring.Edits?.IsTrusted(Speaker) == true) wiring.Edits.Touch(r.Id, $"{Speaker} asked about it");
            return r;
        }
    }

    private static readonly Dictionary<string, Func<BlueprintWiring, DesignCall, ToolResult>> DesignOps = new()
    {
        ["log"] = (w, c) => w.LogOp(c),
        ["status"] = (w, c) => w.StatusOp(c),
        ["diff"] = (w, c) => w.DiffOp(c),
        ["commit"] = (w, c) => w.HistoryEdit(c, (h, rec) => h.Commit(rec, c.Speaker, c.Input.Str("what"))),
        ["revert"] = (w, c) => w.HistoryEdit(c, (h, rec) => h.Revert(rec, c.Input.Str("what"), c.Speaker)),
        ["checkout"] = (w, c) => w.HistoryEdit(c, (h, rec) => h.Revert(rec, c.Input.Str("what"), c.Speaker)),
        ["new"] = (w, c) => FromServiceText(w.Service.Design(c.Input.Str("what") ?? "", c.Input.Str("at"), c.Speaker)),
        ["revise"] = (w, c) => w.ReviseOp(c),
        ["build"] = (w, c) => w.BuildOp(c),
        ["show"] = (w, c) => w.ShowOp(c),
        // Read-only: memorize a standing build as a design (any speaker; rebuilding it still needs a trusted go).
        ["capture"] = (w, c) => w.CaptureOp?.Invoke(c.Input, c.Speaker) ?? ToolResult.Error("capture isn't wired"),
        ["list"] = (w, _) => w.ListOp(),
        ["cancel"] = (w, c) => c.Rec() is { } rec ? ToolResult.Success(w.Service.Cancel(rec)) : ToolResult.Error(NoDesign),
        ["undo"] = (w, c) => w.UndoOp(c),
        ["delete"] = (w, c) => w.DeleteOp(c),
    };

    private ToolResult Design(JsonElement input)
    {
        var op = DesignOpOf(input);
        return DesignOps.TryGetValue(op, out var run) ? run(this, new DesignCall(this, input)) : ToolResult.Error("op: " + string.Join("|", Ops));
    }

    /// <summary>The op named, else "revise" for an id with a wish, else "new".</summary>
    private static string DesignOpOf(JsonElement input) =>
        (input.Str("op") ?? (input.Str("id") != null && input.Str("what") != null ? "revise" : "new")).Trim().ToLowerInvariant();

    /// <summary>A service answer: "error: …" → an error without the prefix, else a success.</summary>
    private static ToolResult FromServiceText(string r) => r.StartsWith("error", StringComparison.Ordinal) ? ToolResult.Error(r[7..]) : ToolResult.Success(r);

    private static ToolResult Outcome(bool ok, string text) => ok ? ToolResult.Success(text) : ToolResult.Error(text);

    private ToolResult LogOp(DesignCall c)
    {
        if (c.Rec() is not { } rec || History is not { } h) return ToolResult.Error(NoDesign);
        // "r2 good" tags r2 (a name for revert/diff: revert{id, what:"good"}).
        var tagged = TagRevision(h, rec, c.Input.Str("what"));
        return ToolResult.Success(tagged + h.Log(rec));
    }

    /// <summary>"r2 good": tags r2 and returns the "tagged …; " prefix, else null.</summary>
    private static string? TagRevision(DesignHistory h, BlueprintRecord rec, string? spec)
    {
        if (spec is not { Length: > 0 } || spec.Split(' ', 2, StringSplitOptions.TrimEntries) is not [var rs, var tag]) return null;
        return h.Revisions.Resolve(rec.Id, rs) is { } tr && h.Revisions.TagRev(rec.Id, tr.Rev, tag) ? $"tagged r{tr.Rev} '{tag}'; " : null;
    }

    private ToolResult StatusOp(DesignCall c) =>
        c.Rec() is { } st && History is { } hs ? ToolResult.Success(hs.Status(st)) : ToolResult.Error(NoDesign);

    private ToolResult DiffOp(DesignCall c) =>
        c.Rec() is { } df && History is { } hd ? ToolResult.Success(hd.Diff(df, c.Input.Str("what"))) : ToolResult.Error(NoDesign);

    /// <summary>commit/revert: a new revision; the drift watcher re-reads HEAD.</summary>
    private ToolResult HistoryEdit(DesignCall c, Func<DesignHistory, BlueprintRecord, (bool Ok, string Text)> edit)
    {
        if (c.Rec() is not { } rec || History is not { } h) return ToolResult.Error(NoDesign);
        var (ok, text) = edit(h, rec);
        if (ok) Drift?.Refresh();
        return Outcome(ok, text);
    }

    private ToolResult ReviseOp(DesignCall c)
    {
        if (c.Rec() is not { } rec) return ToolResult.Error("no design to revise (id?)");
        return FromServiceText(Service.Revise(rec.Id.ToString(CultureInfo.InvariantCulture), c.Input.Str("what") ?? "", c.Speaker));
    }

    private ToolResult BuildOp(DesignCall c)
    {
        if (c.Rec() is not { } rec) return ToolResult.Error("no design to build (id?)");
        // Drawn for another version with blocks this world lacks: refused, the blocks named (never built wrong).
        if (Service.WorldRefusal(rec) is { } wrong) return ToolResult.Error(wrong);
        if (HeldRefusal(rec, c.Speaker) is { } held) return held;
        bool moving = c.Input.Str("at") is { Length: > 0 };
        var crew = CrewOf(c.Input);
        if (crew != null && !moving) Service.RequestCrew(rec.Id, crew);
        // Already accepted by a trusted player: resuming (after a material or night pause) needs no second go.
        if (ApprovedResume(rec, moving)) return Resume(rec, $"resuming; {rec.AcceptedBy} approved it");
        if (!Service.IsTrusted(c.Speaker)) return ToolResult.Error($"a build needs a trusted player's go ({string.Join("/", rt_Trusted())}); {c.Speaker ?? "nobody"} asked");
        return TrustedBuild(c, rec, moving, crew);
    }

    /// <summary>Held by a "no" at its gate: only a trusted player's fresh go lifts it (never a resume on its own).</summary>
    private ToolResult? HeldRefusal(BlueprintRecord rec, string? speaker)
    {
        if (Service.HeldBy(rec.Id) is not { } holder) return null;
        if (!Service.IsTrusted(speaker))
            return ToolResult.Error($"#{rec.Id} is held: {holder} said no at its gate; it needs a trusted player's fresh go ({speaker ?? "nobody"} asked)");
        Service.ClearHold(rec.Id);
        return null;
    }

    private static bool ApprovedResume(BlueprintRecord rec, bool moving) =>
        !moving && rec.Status is DesignStatus.Accepted or DesignStatus.Building && rec.AcceptedBy != null;

    private ToolResult Resume(BlueprintRecord rec, string how) =>
        Service.StartBuild(rec.Id, null) is { } why ? ToolResult.Error(why) : ToolResult.Success(Service.WithRules(rec.Id, $"building #{rec.Id} \"{rec.Name}\" ({how})"));

    /// <summary>A trusted go: a built/captured design (or one moved elsewhere) is reused, an accepted one resumes, a draft is accepted.</summary>
    private ToolResult TrustedBuild(DesignCall c, BlueprintRecord rec, bool moving, CrewRequest? crew)
    {
        if (rec.Status is DesignStatus.Built or DesignStatus.Captured || moving && rec.Status is not DesignStatus.Review)
            return FromServiceText(Service.Reuse(rec, c.Input.Str("at"), c.Speaker!, crew).Text);
        if (rec.Status is DesignStatus.Accepted or DesignStatus.Building) return Resume(rec, "resuming");
        var a = Service.Accept(rec, c.Speaker!, crew);
        return a.StartsWith("building", StringComparison.Ordinal) ? ToolResult.Success(a) : ToolResult.Error(a);
    }

    private ToolResult ShowOp(DesignCall c)
    {
        if (c.Rec() is not { } rec) return ToolResult.Error(NoDesign);
        var head = History?.Ensure(rec);
        var res = ToolResult.Success(Service.Describe(rec) + (head != null ? $"; at r{head.Rev} (log/status/diff)" : ""));
        if (c.Input.Bool("image") == true && Service.Store.Picture(rec.Id) is { } png && Png(png) is { } img)
            res = res with { Image = new LlmImage(png, img.W, img.H, $"[design #{rec.Id} preview]") };
        return res;
    }

    private ToolResult ListOp()
    {
        var list = Service.Store.List(6);
        return ToolResult.Success(list.Count == 0 ? "no designs yet" : string.Join("; ", list.Select(Service.ListLine)));
    }

    private static string BlockCount(int n) => $"{n} block{(n == 1 ? "" : "s")}";

    /// <summary>The build's own ledger (gate blocks included), dug top-first as the body's job; nothing else is touched.</summary>
    private ToolResult UndoOp(DesignCall c)
    {
        if (c.IdText is not { Length: > 0 } || Service.Store.Find(c.IdText) is not { } rec) return ToolResult.Error("undo needs the design's id");
        int n = Service.PlacedCount(rec.Id);
        if (n == 0) return ToolResult.Error($"#{rec.Id} \"{rec.Name}\" has no placed blocks to take down");
        return Service.StartUndo(rec.Id) is { } no ? ToolResult.Error($"can't undo #{rec.Id}: {no}")
            : ToolResult.Success($"taking down #{rec.Id} \"{rec.Name}\": the {BlockCount(n)} it placed, top first; a [task] reports the end");
    }

    /// <summary>A trusted player's word, like a build's go: the blocks come down, then its plans and the record go.</summary>
    private ToolResult DeleteOp(DesignCall c)
    {
        if (c.IdText is not { Length: > 0 } || Service.Store.Find(c.IdText) is not { } rec) return ToolResult.Error("delete needs the design's id");
        if (rec.Status == DesignStatus.Deleted) return ToolResult.Error($"#{rec.Id} is already deleted");
        if (!Service.IsTrusted(c.Speaker)) return ToolResult.Error($"deleting a design needs a trusted player's word ({string.Join("/", rt_Trusted())}); {c.Speaker ?? "nobody"} asked");
        int n = Service.PlacedCount(rec.Id);
        return Service.StartDelete(rec.Id, c.Speaker!) is { } no ? ToolResult.Error($"can't delete #{rec.Id}: {no}")
            : ToolResult.Success($"deleting #{rec.Id} \"{rec.Name}\": taking down the {BlockCount(n)} it placed, then its plan; a [task] names what came down");
    }
}
