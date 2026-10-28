using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Rag.Beliefs;
using MineClaw.Agent.Self;
using Microsoft.Extensions.Logging;

namespace MineClaw.Agent.Admin;

/// <summary>
/// Applies admin edits (add / update / delete) to one memory record through the owning store's own API, so FTS triggers, vector
/// invalidation and RAPTOR dirty flags stay exactly as consistent as for the bot's own writes: validate, one store call (one db
/// transaction), re-embed the changed text, sync the RAPTOR tree (or signal the live bot's maintenance loop), and write an audit
/// entry (ok or failed, record before → after) to the JSONL trail and the log. A read-only db rejects every edit untouched.
/// Edits on one context are serialized.
/// </summary>
public sealed class MemoryAdmin
{
    private readonly MemoryAdminContext _ctx;
    private readonly AdminAudit _audit;
    private readonly string _target;
    private readonly ILogger _log;

    public MemoryAdmin(MemoryAdminContext ctx, AdminAudit audit, string target)
    {
        _ctx = ctx;
        _audit = audit;
        _target = target;
        _log = ctx.Logger("MineClaw.Admin");
    }

    public string Target => _target;
    public AdminAudit Audit => _audit;

    private sealed class Rejected(string message) : Exception(message);

    private sealed record Outcome(string Message, string? Id, string? Before, string? After);

    public async Task<MemoryEditResult> ApplyAsync(MemoryEdit edit, CancellationToken ct = default)
    {
        var op = (edit.Op ?? "").Trim().ToLowerInvariant();
        var kind = MemoryKinds.Normalize(edit.Kind);
        var who = string.IsNullOrWhiteSpace(edit.Who) ? "admin" : edit.Who.Trim();
        MemoryEditResult result;
        await _ctx.WriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (kind == null) result = Fail($"unknown kind '{edit.Kind}' ({string.Join("|", MemoryKinds.All)})", edit.Id);
            else if (!MemoryEditSchema.Ops(kind).Contains(op)) result = Fail($"op must be {string.Join("|", MemoryEditSchema.Ops(kind))} for {kind}", edit.Id);
            else if (_ctx.ReadOnly) result = Fail("the database is open read-only; nothing was changed", edit.Id);
            else
            {
                Outcome? o = null;
                string? before = null;
                try
                {
                    var f = new Fields(kind, edit.Fields);
                    o = kind switch
                    {
                        MemoryKinds.Fact => Fact(op, edit.Id, f, b => before = b),
                        MemoryKinds.Place => Place(op, edit.Id, f, b => before = b),
                        MemoryKinds.Structure => Structure(op, edit.Id, f, who, b => before = b),
                        MemoryKinds.Claim => Claim(op, edit.Id, f, who, b => before = b),
                        MemoryKinds.Trait => Trait(op, edit.Id, f, edit.Reason, b => before = b),
                        MemoryKinds.Lesson => Lesson(op, edit.Id, f, b => before = b),
                        _ => Observation(op, edit.Id, f, b => before = b),
                    };
                    result = new MemoryEditResult(true, o.Message, o.Id, o.Before, o.After);
                }
                catch (Exception ex) when (ex is Rejected or ArgumentException or InvalidOperationException or FormatException or JsonException
                                               or Microsoft.Data.Sqlite.SqliteException or RegexParseException)
                {
                    result = Fail(ex is Microsoft.Data.Sqlite.SqliteException ? "database error: " + ex.Message : ex.Message, edit.Id, before);
                }
                if (result.Ok) result = await AfterWriteAsync(result, ct).ConfigureAwait(false);
            }
        }
        finally { _ctx.WriteGate.Release(); }
        _audit.Write(new AuditEntry(DateTimeOffset.UtcNow, who, _target, op.Length > 0 ? op : edit.Op ?? "", kind ?? edit.Kind ?? "", result.Id ?? edit.Id,
            result.Before, result.After, edit.Reason, result.Ok, result.Message));
        return result;
    }

    private static MemoryEditResult Fail(string message, string? id, string? before = null) => new(false, message, id, before);

    /// <summary>Re-embed changed text, then bring the RAPTOR tree up to date. The edit is committed already: failures here only
    /// add a note (the bot's own background embedding / maintenance retries).</summary>
    private async Task<MemoryEditResult> AfterWriteAsync(MemoryEditResult r, CancellationToken ct)
    {
        var notes = new List<string>();
        bool reembedded = false;
        try
        {
            if (_ctx.Rag.Embedder.IsAvailable) reembedded = await _ctx.Rag.FlushEmbeddingsAsync(ct).ConfigureAwait(false) > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { notes.Add("re-embed deferred: " + ex.Message); }
        try
        {
            if (_ctx.RaptorMaintenance != null) _ctx.RaptorMaintenance.Signal();
            else if (_ctx.Raptor is { Ready: true } raptor)
            {
                var s = await raptor.SyncAsync(ct).ConfigureAwait(false);
                if (s.Any) notes.Add($"tree +{s.Inserted} ~{s.Reinserted} -{s.Removed}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { notes.Add("tree sync deferred: " + ex.Message); }
        var msg = notes.Count == 0 ? r.Message : $"{r.Message} ({string.Join("; ", notes)})";
        return r with { Message = msg, Reembedded = reembedded };
    }

    // ======================= kinds =======================

    private static string Js(object? o) => o == null ? "null" : JsonSerializer.Serialize(o, AdminAudit.Json);

    private Outcome Fact(string op, string? id, Fields f, Action<string?> before)
    {
        var mem = _ctx.Memory;
        static object View(Note n) => new { id = n.Id, subject = n.Subject, text = n.Text };
        if (op == "add")
        {
            var subject = f.Req("subject");
            var text = f.Req("text");
            int had = mem.Read(d => d.Notes.Count);
            var n = mem.AddNote(subject, text);
            bool fresh = mem.Read(d => d.Notes.Count) > had;
            return new Outcome(fresh ? $"added fact #{n.Id}" : $"already known as fact #{n.Id}", n.Id.ToString(CultureInfo.InvariantCulture), null, Js(View(n)));
        }
        int nid = IntId(id, "fact");
        var old = mem.Read(d => d.Notes.FirstOrDefault(x => x.Id == nid) is { } x ? Js(View(x)) : null) ?? throw new Rejected($"no fact #{nid}");
        before(old);
        if (op == "delete")
        {
            mem.Forget("note", nid.ToString(CultureInfo.InvariantCulture));
            return new Outcome($"deleted fact #{nid}", nid.ToString(CultureInfo.InvariantCulture), old, null);
        }
        if (!f.Any) throw new Rejected("nothing to change");
        string? subj = f.Has("subject") ? f.Req("subject") : null, txt = f.Has("text") ? f.Req("text") : null;
        var e = mem.EditNote(nid, n => { if (subj != null) n.Subject = subj; if (txt != null) n.Text = txt; }) ?? throw new Rejected($"no fact #{nid}");
        return new Outcome($"updated fact #{nid}", nid.ToString(CultureInfo.InvariantCulture), old, Js(View(e)));
    }

    private Outcome Place(string op, string? id, Fields f, Action<string?> before)
    {
        var mem = _ctx.Memory;
        static object View(Waypoint w) => new { name = w.Name, x = w.X, y = w.Y, z = w.Z, dim = MemoryEditSchema.DimName(w.Dimension), note = w.Note };
        if (op == "add")
        {
            var name = f.Req("name");
            if (mem.GetWaypoint(name) != null) throw new Rejected($"a place named '{name}' already exists (use update)");
            var w = mem.SetWaypoint(name, f.ReqInt("x"), f.ReqInt("y"), f.ReqInt("z"), f.Opt("note") is { Length: > 0 } nt ? nt : null,
                MemoryEditSchema.ParseDim(f.Get("dim")) ?? 0);
            return new Outcome($"added place {w.Name}", w.Name, null, Js(View(w)));
        }
        var key = Req(id, "place name");
        var cur = mem.GetWaypoint(key) ?? throw new Rejected($"no place '{key}'");
        var old = Js(View(cur));
        before(old);
        if (op == "delete")
        {
            mem.Forget("waypoint", cur.Name);
            return new Outcome($"deleted place {cur.Name}", cur.Name, old, null);
        }
        if (!f.Any) throw new Rejected("nothing to change");
        string? name2 = f.Has("name") ? f.Req("name") : null;
        int? x = f.Int("x"), y = f.Int("y"), z = f.Int("z"), dim = MemoryEditSchema.ParseDim(f.Get("dim"));
        string? note = f.Opt("note");
        if (name2 != null && !name2.Equals(cur.Name, StringComparison.OrdinalIgnoreCase) && mem.GetWaypoint(name2) != null)
            throw new Rejected($"a place named '{name2}' already exists");
        var e = mem.EditWaypoint(cur.Name, w =>
        {
            if (name2 != null) w.Name = name2;
            if (x is { } vx) w.X = vx;
            if (y is { } vy) w.Y = vy;
            if (z is { } vz) w.Z = vz;
            if (dim is { } d) w.Dimension = d;
            if (note != null) w.Note = note.Length == 0 ? null : note;
        }) ?? throw new Rejected($"no place '{key}'");
        return new Outcome($"updated place {e.Name}", e.Name, old, Js(View(e)));
    }

    private Outcome Structure(string op, string? id, Fields f, string who, Action<string?> before)
    {
        var mem = _ctx.Memory;
        static object View(Structure s) => new
        {
            name = s.Name, kind = s.Kind, note = s.Note, confidence = s.Confidence, steps = s.Steps, min = s.Min?.ToString(), max = s.Max?.ToString(),
            dim = MemoryEditSchema.DimName(s.Dimension), key_blocks = s.KeyBlocks.Count, taught_by = s.TaughtBy,
        };
        string? name2 = f.Has("name") ? f.Req("name") : null, kind = f.Opt("kind"), note = f.Opt("note"), steps = f.Opt("steps");
        double? conf = f.Num("confidence");
        if (conf is < 0 or > 1) throw new Rejected("confidence must be 0..1");
        string? minS = f.Opt("min"), maxS = f.Opt("max");
        var min = MemoryEditSchema.ParsePos(minS, "min");
        var max = MemoryEditSchema.ParsePos(maxS, "max");
        int? dim = MemoryEditSchema.ParseDim(f.Get("dim"));
        void Apply(Structure s)
        {
            if (name2 != null) s.Name = name2;
            if (kind != null) s.Kind = kind.Length == 0 ? StructureKinds.Unknown : kind.Trim();
            if (note != null) s.Note = note.Length == 0 ? null : note.Trim();
            if (conf is { } c) s.Confidence = c;
            if (steps != null) s.Steps = steps.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (minS != null) s.Min = min;
            if (maxS != null) s.Max = max;
            if (dim is { } d) s.Dimension = d;
        }
        if (op == "add")
        {
            var name = f.Req("name");
            if (mem.GetStructure(name) != null) throw new Rejected($"a structure named '{name}' already exists (use update)");
            var s = mem.UpsertStructure(name, s =>
            {
                Apply(s);
                s.Name = name.Trim();
                if (dim == null) s.Dimension = 0;
                s.TaughtBy ??= who;
            });
            return new Outcome($"added structure {s.Name}", s.Name, null, Js(View(s)));
        }
        var key = Req(id, "structure name");
        var cur = mem.GetStructure(key) ?? throw new Rejected($"no structure '{key}'");
        var old = Js(View(cur));
        before(old);
        if (op == "delete")
        {
            mem.Forget("structure", cur.Name);
            return new Outcome($"deleted structure {cur.Name}", cur.Name, old, null);
        }
        if (!f.Any) throw new Rejected("nothing to change");
        if (name2 != null && !name2.Equals(cur.Name, StringComparison.OrdinalIgnoreCase) && mem.GetStructure(name2) != null)
            throw new Rejected($"a structure named '{name2}' already exists");
        var e = mem.EditStructure(cur.Name, Apply) ?? throw new Rejected($"no structure '{key}'");
        return new Outcome($"updated structure {e.Name}", e.Name, old, Js(View(e)));
    }

    private Outcome Claim(string op, string? id, Fields f, string who, Action<string?> before)
    {
        var b = _ctx.Beliefs;
        static object View(BeliefClaim c) => new
        {
            id = c.Id, claimant = c.Claimant, claimants = c.Claimants, text = c.Text, status = BeliefRanks.Name(c.Status),
            confidence = Math.Round(c.Confidence, 3), reason = c.Reason, category = BeliefRanks.Name(c.Category),
        };
        ClaimStatus? status = f.Get("status") is { Length: > 0 } st
            ? Enum.TryParse<ClaimStatus>(st.Trim(), true, out var ps) ? ps : throw new Rejected($"status must be {string.Join("|", Enum.GetNames<ClaimStatus>().Select(n => n.ToLowerInvariant()))}")
            : null;
        double? conf = f.Num("confidence");
        if (conf is < 0 or > 1) throw new Rejected("confidence must be 0..1");
        string? reason = f.Opt("reason");
        if (op == "add")
        {
            var r = b.Ingest(f.Req("claimant"), f.Req("text"));
            if (!r.Stored || r.Claim == null) throw new Rejected(r.Text);
            var c = r.Claim;
            if (status != null || conf != null || reason != null)
                c = b.AdminSet(c.Id, status ?? c.Status, conf, reason is { Length: > 0 } ? reason : null, who) ?? c;
            return new Outcome($"added claim#{c.Id}: {r.Text}", c.Id.ToString(CultureInfo.InvariantCulture), null, Js(View(c)));
        }
        long cid = LongId(id, "claim");
        var cur = b.Get(cid) ?? throw new Rejected($"no claim#{cid}");
        var old = Js(View(cur));
        before(old);
        if (op == "delete")
        {
            if (!b.AdminDelete(cid)) throw new Rejected($"no claim#{cid}");
            return new Outcome($"deleted claim#{cid}", cid.ToString(CultureInfo.InvariantCulture), old, null);
        }
        if (status == null && conf == null && reason == null) throw new Rejected("nothing to change (status, confidence, reason)");
        if (cur.Directive) throw new Rejected("an instruction (directive) is never a fact; delete it instead");
        var after = b.AdminSet(cid, status ?? cur.Status, conf ?? (status == null ? cur.Confidence : null), reason is { Length: > 0 } ? reason : null, who) ?? throw new Rejected($"no claim#{cid}");
        return new Outcome($"claim#{cid} {BeliefRanks.Name(after.Status)} ({after.Confidence:0.##})", cid.ToString(CultureInfo.InvariantCulture), old, Js(View(after)));
    }

    private Outcome Trait(string op, string? id, Fields f, string? editReason, Action<string?> before)
    {
        var self = _ctx.Self ?? throw new Rejected("the self profile is off (Agent:Self:Enabled)");
        static object View(SelfEntry e) => new
        {
            id = e.Id, section = SelfProfile.Name(e.Section), text = e.Text, player = e.Player, strength = Math.Round(e.Strength, 3), reason = e.Reason,
        };
        string? reason = f.Opt("reason") is { Length: > 0 } fr ? fr : editReason;
        SelfChangeResult res;
        if (op == "add")
        {
            if (!SelfProfile.TryParseSection(f.Req("section"), out var section)) throw new Rejected($"unknown section '{f.Get("section")}'");
            res = self.Apply(new SelfChange("add", section, f.Req("text"), f.Opt("player") is { Length: > 0 } p ? p : null, Reason: reason), SelfProfile.SourceAdmin);
            if (!res.Ok) throw new Rejected(res.Message);
            var added = self.Entries.FirstOrDefault(e => e.Id == res.EntryId);
            return new Outcome(res.Message, res.EntryId?.ToString(CultureInfo.InvariantCulture), null, added == null ? null : Js(View(added)));
        }
        long eid = LongId(id, "trait");
        var cur = self.Entries.FirstOrDefault(e => e.Id == eid) ?? throw new Rejected($"no self-profile entry #{eid}");
        var old = Js(View(cur));
        before(old);
        if (op == "delete")
        {
            res = self.Apply(new SelfChange("remove", cur.Section, Id: eid, Reason: reason), SelfProfile.SourceAdmin);
            if (!res.Ok) throw new Rejected(res.Message);
            return new Outcome(res.Message, eid.ToString(CultureInfo.InvariantCulture), old, null);
        }
        if (!f.Any) throw new Rejected("nothing to change");
        if (f.Get("section") is { Length: > 0 } sec && (!SelfProfile.TryParseSection(sec, out var s2) || s2 != cur.Section))
            throw new Rejected("an entry's section can't change; delete it and add a new one");
        var player = f.Opt("player");
        if (player is { Length: 0 } && cur.Player != null) throw new Rejected("the player can't be cleared; delete the entry and add a new one");
        var text = f.Has("text") ? f.Req("text") : cur.Text;
        res = self.Apply(new SelfChange("adjust", cur.Section, text, player is { Length: > 0 } ? player : null, Id: eid, Reason: reason), SelfProfile.SourceAdmin);
        if (!res.Ok) throw new Rejected(res.Message);
        var after = self.Entries.FirstOrDefault(e => e.Id == eid);
        return new Outcome(res.Message, eid.ToString(CultureInfo.InvariantCulture), old, after == null ? null : Js(View(after)));
    }

    private Outcome Lesson(string op, string? id, Fields f, Action<string?> before)
    {
        var db = _ctx.Db;
        var log = _ctx.Logger("MineClaw.Rag");
        static object View(KnowledgeCorrection c, string? source) => new
        {
            id = c.Id, title = c.Title, correction = c.Correction, patterns = PatternLines(c), source,
        };
        KnowledgeCorrection? Active(string lid) =>
            _ctx.Rag.Corrections?.Items.FirstOrDefault(c => c.Id.Equals(lid, StringComparison.OrdinalIgnoreCase))
            ?? KnowledgeCorrections.LoadAdmin(db, log).FirstOrDefault(c => c.Id.Equals(lid, StringComparison.OrdinalIgnoreCase));
        void Save(KnowledgeCorrection c) => db.Tx(() =>
        {
            KnowledgeCorrections.SaveAdmin(db, c);
            KnowledgeCorrections.Apply(_ctx.Rag, _ctx.Options.KnowledgeDirectory, log);
        });

        if (op == "add")
        {
            var title = f.Req("title");
            var lid = f.Opt("id") is { Length: > 0 } given ? Slug(given) : Slug(title);
            if (lid.Length == 0) throw new Rejected("lesson id is empty");
            if (Active(lid) != null || KnowledgeCorrections.SourceOf(db, lid) != null) throw new Rejected($"a lesson '{lid}' already exists (use update)");
            var (clauses, whole) = ParsePatterns(f.Req("patterns"));
            var c = new KnowledgeCorrection(lid, title, f.Req("correction"), Short(f.Req("correction")), clauses, whole);
            Save(c);
            return new Outcome($"added lesson {lid}", lid, null, Js(View(c, KnowledgeCorrections.AdminSource)));
        }
        var key = Req(id, "lesson id");
        var cur = Active(key) ?? throw new Rejected($"no lesson '{key}'");
        var source = KnowledgeCorrections.SourceOf(db, cur.Id) ?? KnowledgeCorrections.Source;
        var old = Js(View(cur, source));
        before(old);
        if (op == "delete")
        {
            if (source != KnowledgeCorrections.AdminSource)
                throw new Rejected($"'{cur.Id}' is a built-in/file lesson, rebuilt at every start; update it instead (an admin edit overrides it)");
            db.Tx(() =>
            {
                KnowledgeCorrections.DeleteAdmin(db, cur.Id);
                KnowledgeCorrections.Apply(_ctx.Rag, _ctx.Options.KnowledgeDirectory, log);
            });
            var back = _ctx.Rag.Corrections?.Items.FirstOrDefault(c => c.Id.Equals(cur.Id, StringComparison.OrdinalIgnoreCase));
            return new Outcome(back == null ? $"deleted lesson {cur.Id}" : $"deleted the admin override of {cur.Id}; the built-in lesson is back",
                cur.Id, old, back == null ? null : Js(View(back, KnowledgeCorrections.Source)));
        }
        if (!f.Any) throw new Rejected("nothing to change");
        var patterns = f.Opt("patterns");
        var (cl, wh) = patterns is { Length: > 0 } ? ParsePatterns(patterns) : (cur.Clauses, cur.Whole);
        var corr = f.Has("correction") ? f.Req("correction") : cur.Correction;
        var updated = new KnowledgeCorrection(cur.Id, f.Has("title") ? f.Req("title") : cur.Title, corr, Short(corr), cl, wh);
        Save(updated);
        return new Outcome($"updated lesson {cur.Id}", cur.Id, old, Js(View(updated, KnowledgeCorrections.AdminSource)));
    }

    private Outcome Observation(string op, string? id, Fields f, Action<string?> before)
    {
        var obs = _ctx.Observations;
        static object View(ObservationRow r) => new
        {
            id = r.Id, kind = r.Kind, key = r.Key, dim = r.Dimension, text = r.Text, annotation = r.Annotation,
            source = ObservationStore.Source(r.Source), removed = r.Removed,
        };
        var row = FindObservation(Req(id, "observation id or ref")) ?? throw new Rejected($"no observation '{id}'");
        var old = Js(View(row));
        before(old);
        if (op == "delete")
        {
            obs.Remove(row.Kind, row.Key, tombstone: false, by: ObservationSource.Taught, dimension: row.Dimension);
            return new Outcome($"deleted observation #{row.Id} ({row.Kind} {row.Key})", row.Id.ToString(CultureInfo.InvariantCulture), old, null);
        }
        string? text = f.Opt("text"), annotation = f.Opt("annotation");
        if (text == null && annotation == null) throw new Rejected("nothing to change (text, annotation)");
        if (text is { Length: 0 }) throw new Rejected("observation text can't be empty");
        var after = obs.AdminEdit(row.Id, text, annotation) ?? throw new Rejected($"no observation #{row.Id}");
        return new Outcome($"updated observation #{row.Id}", row.Id.ToString(CultureInfo.InvariantCulture), old, Js(View(after)));
    }

    /// <summary>By id ("42") or doc ref ("obs:chest:1,64,2", "obs:d-1:portal:3,70,4").</summary>
    private ObservationRow? FindObservation(string id)
    {
        if (long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return _ctx.Observations.GetById(n);
        if (!id.StartsWith("obs:", StringComparison.OrdinalIgnoreCase)) return null;
        var rest = id[4..];
        int dim = 0;
        var m = Regex.Match(rest, @"^d(-?\d+):");
        if (m.Success)
        {
            dim = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            rest = rest[m.Length..];
        }
        int colon = rest.IndexOf(':');
        if (colon <= 0) return null;
        return _ctx.Observations.Get(rest[..colon], rest[(colon + 1)..], includeRemoved: true, dimension: dim);
    }

    // ======================= helpers =======================

    private static string Req(string? id, string what) =>
        string.IsNullOrWhiteSpace(id) ? throw new Rejected($"{what} required") : id.Trim();

    private static int IntId(string? id, string what) =>
        int.TryParse(Req(id, what + " id").TrimStart('#'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : throw new Rejected($"{what} id must be a number");

    private static long LongId(string? id, string what) =>
        long.TryParse(Req(id, what + " id").TrimStart('#'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : throw new Rejected($"{what} id must be a number");

    private static string Slug(string s)
    {
        var x = Regex.Replace(s.Trim().ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
        return x.Length > 60 ? x[..60].TrimEnd('_') : x;
    }

    private static string Short(string correction) => correction.Length <= 180 ? correction : correction[..177] + "...";

    /// <summary>One rule per line: regexes joined by "&amp;&amp;" all match one sentence, optionally "!!" then regexes that must not;
    /// a line starting with "*" is matched against the whole text.</summary>
    internal static (IReadOnlyList<ClauseRule> Clauses, IReadOnlyList<Regex>? Whole) ParsePatterns(string text)
    {
        var clauses = new List<ClauseRule>();
        var whole = new List<Regex>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('*')) { whole.Add(KnowledgeCorrections.Pattern(line[1..].Trim())); continue; }
            var parts = line.Split("!!", 2);
            var all = parts[0].Split("&&").Select(p => p.Trim()).Where(p => p.Length > 0).Select(KnowledgeCorrections.Pattern).ToList();
            if (all.Count == 0) throw new Rejected($"pattern line '{line}' has no regex to match");
            var none = parts.Length > 1 ? parts[1].Split("&&").Select(p => p.Trim()).Where(p => p.Length > 0).Select(KnowledgeCorrections.Pattern).ToList() : null;
            clauses.Add(new ClauseRule(all, none is { Count: > 0 } ? none : null));
        }
        if (clauses.Count == 0 && whole.Count == 0) throw new Rejected("a lesson needs at least one pattern (the memories it supersedes)");
        return (clauses, whole.Count > 0 ? whole : null);
    }

    internal static string PatternLines(KnowledgeCorrection c) => string.Join("\n",
        c.Clauses.Select(r => string.Join(" && ", r.AllOf.Select(x => x.ToString())) + (r.NoneOf is { Count: > 0 } n ? " !! " + string.Join(" && ", n.Select(x => x.ToString())) : ""))
            .Concat(c.Whole?.Select(w => "* " + w) ?? []));

    /// <summary>Case-insensitive view of the edit's fields: absent/null = keep; "" = clear (optional fields).</summary>
    private sealed class Fields
    {
        private readonly Dictionary<string, string?> _d;

        public Fields(string kind, IReadOnlyDictionary<string, string?>? fields)
        {
            _d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var allowed = MemoryEditSchema.For(kind).Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in fields ?? new Dictionary<string, string?>())
            {
                if (!allowed.Contains(k)) throw new Rejected($"unknown field '{k}' for {kind} ({string.Join(", ", allowed)})");
                _d[k] = v;
            }
        }

        public bool Any => _d.Values.Any(v => v != null);
        public bool Has(string n) => _d.TryGetValue(n, out var v) && v != null;
        public string? Get(string n) => _d.TryGetValue(n, out var v) ? v : null;
        /// <summary>Trimmed value, "" when cleared, null when absent.</summary>
        public string? Opt(string n) => Get(n)?.Trim();
        public string Req(string n) => Get(n) is { } v && v.Trim().Length > 0 ? v.Trim() : throw new Rejected($"{n} is required");

        public int? Int(string n) => Opt(n) is { Length: > 0 } s
            ? int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : throw new Rejected($"{n} must be an integer")
            : null;

        public int ReqInt(string n) => Int(n) ?? throw new Rejected($"{n} is required");

        public double? Num(string n) => Opt(n) is { Length: > 0 } s
            ? double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : throw new Rejected($"{n} must be a number")
            : null;
    }
}
