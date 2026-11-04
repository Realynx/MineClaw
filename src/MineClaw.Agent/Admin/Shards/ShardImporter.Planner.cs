using MineClaw.Agent.Rules;

namespace MineClaw.Agent.Admin.Shards;

public sealed partial class ShardImporter
{
    /// <summary>
    /// Computes an import <see cref="Plan"/> without writing anything, in the order the diff reads: refusal, per-record
    /// classification, removals, mode transition, vector reuse, scoped rebuilds, then the closing notes.
    /// </summary>
    private sealed class Planner
    {
        private readonly ShardImporter _imp;
        private readonly Shard _shard;
        private readonly ShardImportOptions _o;
        private readonly ShardManifest _m;
        private readonly KnowledgePackInfo _active;
        private readonly bool _isPack, _isMode, _modeOp;
        private readonly string? _mode;
        private readonly IReadOnlyList<string> _packFiles;
        private readonly ShardCollector _collector;
        private readonly List<string> _notes = new();
        private readonly List<ShardDiffItem> _items = new();
        private readonly List<PlanItem> _writes = new();
        private readonly List<ShardRecord> _removals = new();
        private readonly Dictionary<string, Dictionary<string, ShardRecord>> _existing = new(StringComparer.Ordinal);
        private HashSet<string> _shardIds = null!;
        private DirectiveStore _rules = null!;
        private string? _refused;
        private int _added, _changed, _conflicts, _unchanged, _skipped;

        public Planner(ShardImporter imp, Shard shard, ShardImportOptions o)
        {
            _imp = imp;
            _shard = shard;
            _o = o;
            _m = shard.Manifest;
            _active = KnowledgePack.Active(imp._ctx.Db);
            _isPack = _m.Kind == "pack";
            _mode = _isPack ? _m.Pack?.Mode ?? _m.Selection.PackMode : null;
            _isMode = _mode != null;
            _packFiles = PackFiles(_m);
            _modeOp = o.Mode is ShardImportMode.AddMode or ShardImportMode.RemoveMode;
            _collector = new ShardCollector(imp._ctx.Db);
        }

        private bool Replaces => _o.Mode is ShardImportMode.ReplaceSection or ShardImportMode.SwapPack or ShardImportMode.AddMode;

        public Plan Build()
        {
            _refused = Refusal();
            _rules = _imp.Rules();
            _shardIds = _shard.Records.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            // Removing a mode writes nothing: it only unloads the mode's chunks.
            if (_o.Mode != ShardImportMode.RemoveMode)
                foreach (var r in ShardFile.Canonical(_shard.Records)) Classify(r);
            CollectRemovals();
            var (modesFrom, modesTo) = ModeTransition();
            foreach (var r in _removals) _items.Add(new ShardDiffItem(r.Id, r.Kind, ShardChange.Removed, r.Label, true));

            bool reuse = ReuseVectors();
            var applied = _writes.Where(w => w.Apply).ToList();
            int reusable = reuse ? applied.Count(w => w.Record.DocHash is { } h && _shard.Embeddings.ContainsKey(h)) : 0;
            int embeddable = applied.Count(w => w.Record.Kind != ShardKinds.Trait);
            var (rebuild, estimate) = Rebuilds(applied, reusable);
            ClosingNotes(applied, rebuild);

            var diff = new ShardDiff
            {
                Shard = _m.Name, Mode = _o.Mode, Conflict = _o.Conflict, Items = _items,
                Added = _added, Changed = _changed, Conflicts = _conflicts, Removed = _removals.Count, Unchanged = _unchanged, Skipped = _skipped,
                Writes = applied.Count, Rebuild = rebuild, Estimate = estimate, ReusableVectors = reusable, ToEmbed = Math.Max(0, embeddable - reusable),
                PackFrom = _isPack ? _active.Version : null, PackTo = _isPack ? (_isMode ? _active.Version : _m.PackVersion) : null,
                ModesFrom = _isPack ? modesFrom : null, ModesTo = _isPack ? modesTo : null, Notes = _notes, Refused = _refused,
            };
            return new Plan(diff, applied, _removals, reuse);
        }

        // ---------------- refusal (first match wins)

        private string? Refusal() => SwapRefusal() ?? ModeRefusal() ?? PackRefusal();

        private string? SwapRefusal()
        {
            if (_o.Mode != ShardImportMode.SwapPack) return null;
            if (!_isPack) return "swap needs a knowledge-pack shard; this one is a " + _m.Kind + " shard";
            return _isMode ? $"this is the {_mode} mode pack: add or remove it; swap takes a version pack" : null;
        }

        private string? ModeRefusal()
        {
            if (_modeOp && !_isMode)
                return $"{ShardImportOptions.Name(_o.Mode)} needs a mode pack (e.g. skyblock); this one is a " + (_isPack ? "version pack: swap it" : _m.Kind + " shard");
            if (_isMode && !_modeOp)
                return $"the {_mode} mode pack is added or removed, not {ShardImportOptions.Name(_o.Mode)}: choose add mode or remove mode";
            return null;
        }

        private string? PackRefusal()
        {
            if (!_isPack) return null;
            if (_o.Mode == ShardImportMode.ReplaceSection) return "a knowledge pack is swapped, not section-replaced: choose swap";
            if (_o.Mode == ShardImportMode.Merge && _m.PackVersion is { } pv && !pv.Equals(_active.Version, StringComparison.OrdinalIgnoreCase)
                && _collector.All(ShardKinds.GameData).Count > 0)
                return $"this is a {pv} knowledge pack and the db holds {_active.Version}: merging would mix versions; swap it instead";
            return !KnowledgePack.ValidVersion(_m.PackVersion) ? "the knowledge pack has no valid version tag" : null;
        }

        // ---------------- records

        private Dictionary<string, ShardRecord> Target(string kind)
        {
            if (!_existing.TryGetValue(kind, out var map))
                _existing[kind] = map = _collector.All(kind).GroupBy(r => r.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            return map;
        }

        private string? SkipReason(ShardRecord r)
        {
            if (ShardKinds.IsWorld(r.Kind) && !_o.IncludeWorld) return "world memory not chosen";
            if (r.Kind == ShardKinds.Rule) return RuleSkipReason(r);
            return r.Kind == ShardKinds.Trait && _imp._ctx.Self == null ? "the self profile is off in the target" : null;
        }

        private string? RuleSkipReason(ShardRecord r)
        {
            if (!_o.IncludeRules) return "standing rules not chosen";
            return !_rules.Trusted(r.Get("player")) ? $"{r.Get("player")} isn't a trusted player here" : null;
        }

        private void Classify(ShardRecord r)
        {
            if (SkipReason(r) is { } skip)
            {
                _skipped++;
                _items.Add(new ShardDiffItem(r.Id, r.Kind, ShardChange.Skipped, r.Label, false, skip));
                return;
            }
            var mine = Target(r.Kind).GetValueOrDefault(r.Id);
            if (mine == null) Added(r);
            else if (mine.Hash == r.Hash) Unchanged(r);
            else if (r.Updated > mine.Updated) Changed(r, mine);
            else Conflict(r, mine);
        }

        private void Added(ShardRecord r)
        {
            _added++;
            _writes.Add(new PlanItem(r, null, ShardChange.Added, true));
            _items.Add(new ShardDiffItem(r.Id, r.Kind, ShardChange.Added, r.Label, true));
        }

        private void Unchanged(ShardRecord r)
        {
            _unchanged++;
            if (_unchanged <= MaxUnchangedItems) _items.Add(new ShardDiffItem(r.Id, r.Kind, ShardChange.Unchanged, r.Label, false));
        }

        private void Changed(ShardRecord r, ShardRecord mine)
        {
            _changed++;
            bool apply = Replaces || _o.Conflict != ShardConflictPolicy.KeepMine;
            _writes.Add(new PlanItem(r, mine, ShardChange.Changed, apply));
            _items.Add(new ShardDiffItem(r.Id, r.Kind, ShardChange.Changed, r.Label, apply, "was: " + mine.Label));
        }

        private void Conflict(ShardRecord r, ShardRecord mine)
        {
            _conflicts++;
            bool apply = Replaces || _o.Conflict == ShardConflictPolicy.PreferShard;
            _writes.Add(new PlanItem(r, mine, ShardChange.Conflict, apply));
            _items.Add(new ShardDiffItem(r.Id, r.Kind, ShardChange.Conflict, r.Label, apply,
                (apply ? "shard wins over the newer local: " : "keeping the newer local: ") + mine.Label));
        }

        // ---------------- removals

        private void CollectRemovals()
        {
            if (_refused != null) return;
            if (_o.Mode == ShardImportMode.ReplaceSection) ReplaceSectionRemovals();
            else if (_o.Mode == ShardImportMode.SwapPack) SwapRemovals();
            else if (_modeOp) ModeRemovals();
        }

        private void ReplaceSectionRemovals()
        {
            var sel = _m.Selection with
            {
                IncludeRules = _m.Selection.IncludeRules && _o.IncludeRules,
                IncludeWorld = _m.Selection.IncludeWorld && _o.IncludeWorld,
            };
            if (sel.Mode == "subtree")
            {
                _notes.Add("a RAPTOR subtree has no section in another db: its records are replaced, nothing else is removed");
                return;
            }
            if (sel.Semantic && !string.IsNullOrWhiteSpace(sel.Query)) _notes.Add("the semantic selection is re-applied here as a text filter");
            try { _removals.AddRange(_collector.Select(sel with { Semantic = false }).Where(r => !_shardIds.Contains(r.Id))); }
            catch (ArgumentException ex) { _refused = ex.Message; }
        }

        private void SwapRemovals()
        {
            _removals.AddRange(_collector.All(ShardKinds.GameData).Where(r => !_shardIds.Contains(r.Id)));
            // The outgoing version pack's docs and the incoming one's files: chunks the new pack doesn't carry go.
            var files = _packFiles.Concat(_active.Files ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count > 0) _removals.AddRange(_collector.All(ShardKinds.Knowledge).Where(r => ShardCollector.InFiles(r, files) && !_shardIds.Contains(r.Id)));
            else if (_m.Selection.IncludeKnowledgeDocs) _notes.Add("knowledge chunks in the pack are merged; docs/knowledge files re-index them when they change");
        }

        private void ModeRemovals() =>
            _removals.AddRange(_collector.All(ShardKinds.Knowledge)
                .Where(r => (ShardCollector.InFiles(r, _packFiles) || _shardIds.Contains(r.Id)) && (_o.Mode == ShardImportMode.RemoveMode || !_shardIds.Contains(r.Id))));

        // ---------------- modes, vectors, rebuilds, notes

        /// <summary>Active modes before / after (a legacy db has no list: every mode doc on disk counted until now).</summary>
        private (List<string>? From, List<string>? To) ModeTransition()
        {
            var modesFrom = _active.Modes?.Select(x => x.Mode).ToList();
            if (!_isMode || _refused != null || !_modeOp) return (modesFrom, modesFrom);
            bool add = _o.Mode == ShardImportMode.AddMode;
            var list = (modesFrom ?? []).Where(x => !x.Equals(_mode, StringComparison.OrdinalIgnoreCase)).ToList();
            if (add) list.Add(_mode!);
            ModeNotes(add, legacy: modesFrom == null, list);
            return (modesFrom, list);
        }

        private void ModeNotes(bool add, bool legacy, List<string> modesTo)
        {
            if (legacy)
                _notes.Add($"this db had no mode list (every mode doc on disk counted); after this its modes are exactly: {Modes(modesTo)}");
            if (add && _m.Pack?.Version is { } mv && !KnowledgePack.Matches(mv, _active.Version))
                _notes.Add($"the {_mode} pack was written for {mv}; this db plays {_active.Version}, so its docs stay out until the versions match");
            _notes.Add(add
                ? $"mode {_mode} on: its core lines join the cached prompt and its {_packFiles.Count} doc(s) join retrieval"
                : $"mode {_mode} off: its core lines leave the cached prompt and its chunks leave retrieval");
        }

        /// <summary>Vectors are reused when the target embeds with the shard's model + dims (or, offline, when its stored vectors are those).</summary>
        private bool ReuseVectors()
        {
            if (_m.Embeddings is not { } ei || _shard.Embeddings.Count == 0) return false;
            var emb = _imp._ctx.Embedder;
            bool reuse = emb.IsAvailable
                ? emb.Model == ei.Model && emb.Dimensions == ei.Dims
                : _imp._ctx.Db.ScalarLong("SELECT count(*) FROM vectors WHERE model <> $m OR dims <> $d", ("$m", ei.Model), ("$d", ei.Dims)) == 0;
            if (!reuse) _notes.Add($"the shard's vectors are {ei.Model}@{ei.Dims}, the target embeds with {emb.Model}@{emb.Dimensions}: its docs are re-embedded");
            return reuse;
        }

        /// <summary>Scoped rebuilds: only tree kinds that actually change.</summary>
        private (List<RebuildRequest> Rebuild, RebuildEstimate? Estimate) Rebuilds(List<PlanItem> applied, int reusable)
        {
            var rebuild = new List<RebuildRequest>();
            var ctx = _imp._ctx;
            if (ctx.Raptor == null)
            {
                if (applied.Count > 0) _notes.Add("no RAPTOR tree here (" + (ctx.RaptorNote ?? "off") + "): nothing to rebuild");
                return (rebuild, null);
            }
            if (_refused != null) return (rebuild, null);
            var kinds = applied.Select(w => w.Record.Kind).Concat(_removals.Select(r => r.Kind)).Distinct()
                .Select(ShardKinds.RebuildKind).OfType<string>().Distinct().ToList();
            rebuild.AddRange(kinds.Select(k => new RebuildRequest("kind", Kind: k, MaxUsd: _o.RebuildMaxUsd, Who: _o.Who)));
            return (rebuild, rebuild.Count > 0 ? _imp.Estimate(rebuild, applied, reusable) : null);
        }

        private void PackNotes()
        {
            if (!_isPack) return;
            _notes.Add($"knowledge pack {_m.PackVersion}: the db holds {_active.Version}" + (_o.Mode == ShardImportMode.SwapPack ? $" → {_m.PackVersion} after the swap" : ""));
            if (_imp._ctx.IsLive && _o.Mode is ShardImportMode.SwapPack or ShardImportMode.AddMode or ShardImportMode.RemoveMode)
                _notes.Add("retrieval switches now; the bot's cached prompt (core lines, version) follows after its next restart");
        }

        private void ClosingNotes(List<PlanItem> applied, List<RebuildRequest> rebuild)
        {
            PackNotes();
            if (_m.Pack is { NeedsEmbedding: > 0 } np && _o.Mode != ShardImportMode.RemoveMode)
                _notes.Add($"{np.NeedsEmbedding} pack doc(s) carry no vector: they are embedded on import");
            if (_m.Summaries is { Count: > 0 } && rebuild.Count > 0) _notes.Add($"{_m.Summaries.Count} cached summaries: identical clusters are reused without a model call");
            if (applied.Count == 0 && _removals.Count == 0 && _refused == null) _notes.Add("identical to what's here: nothing to write, no rebuild");
        }
    }
}
