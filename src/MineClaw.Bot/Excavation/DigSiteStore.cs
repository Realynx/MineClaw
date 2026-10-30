using System.Text.Json;
using MineClaw.Agent.Rag;
using MineClaw.Skills;
using MineClaw.World;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.Excavation;

/// <summary>
/// Remembered excavations in the shared db (<c>dig_sites</c>): the stair pattern Fox taught (its rule, anchor and repeats dug),
/// the rooms off it, the strip mines. Every process reads the same rows, so a clone extending "spiral stair" continues where
/// main stopped, and at startup each loads every site's air into its world's must-be-air log (<see cref="KeepAirLog"/>).
/// Live 10-30 02:01 "remember this build so we can do it again elsewhere"; 02:31 "The stair is recorded at y38-60 only" (a frozen
/// capture). A site may carry the id of the design it was learned from: the design store stays as it is.
/// </summary>
public sealed class DigSiteStore : IDigSites
{
    private readonly AgentDb _db;
    private readonly ILogger? _log;

    public DigSiteStore(AgentDb db, ILogger? log = null)
    {
        _db = db;
        _log = log;
        lock (_db.Gate)
            _db.Exec("""
                CREATE TABLE IF NOT EXISTS dig_sites(key TEXT PRIMARY KEY, name TEXT NOT NULL, kind TEXT NOT NULL, design INTEGER,
                    body TEXT NOT NULL, at INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_dig_sites_design ON dig_sites(design);
                """);
    }

    public DigSite? Find(string name)
    {
        var key = DigSites.Key(name);
        var id = key.TrimStart('#').StartsWith("design ", StringComparison.Ordinal) ? key["design ".Length..] : key.TrimStart('#');
        try
        {
            lock (_db.Gate)
            {
                var rows = _db.Query("SELECT body FROM dig_sites WHERE key = $k", r => r.GetString(0), ("$k", key));
                if (rows.Count == 0 && int.TryParse(id, out int d))
                    rows = _db.Query("SELECT body FROM dig_sites WHERE design = $d ORDER BY at DESC LIMIT 1", r => r.GetString(0), ("$d", d));
                return rows.Count == 0 ? null : Decode(rows[0]);
            }
        }
        catch (Exception ex) { _log?.LogDebug(ex, "dig site {Name} unreadable", name); return null; }
    }

    public IReadOnlyList<DigSite> All()
    {
        try
        {
            lock (_db.Gate)
                return _db.Query("SELECT body FROM dig_sites ORDER BY at DESC", r => r.GetString(0)).Select(Decode).OfType<DigSite>().ToList();
        }
        catch (Exception ex) { _log?.LogDebug(ex, "dig sites unreadable"); return []; }
    }

    public void Save(DigSite site)
    {
        lock (_db.Gate)
            _db.Exec("""
                INSERT INTO dig_sites(key,name,kind,design,body,at) VALUES($k,$n,$kind,$d,$b,$t)
                ON CONFLICT(key) DO UPDATE SET name=$n, kind=$kind, design=$d, body=$b, at=$t
                """, ("$k", DigSites.Key(site.Name)), ("$n", site.Name), ("$kind", site.Kind), ("$d", (object?)site.Design),
                ("$b", Encode(site)), ("$t", AgentDb.ToUnixMs(site.At)));
        _log?.LogInformation("dig site saved: {Site}", site.Describe());
    }

    public bool Delete(string name)
    {
        lock (_db.Gate) return _db.Exec("DELETE FROM dig_sites WHERE key = $k", ("$k", DigSites.Key(name))) > 0;
    }

    // Cells as a flat x,y,z array: a strip mine is hundreds of cells, not hundreds of objects.
    private sealed record Row(string Name, string Kind, int Dim, string? Parent, string? Rule, int[] Anchor, int Repeats, int[] Cells,
        int? Design, string? Note, long At);

    private static string Encode(DigSite s) => JsonSerializer.Serialize(new Row(s.Name, s.Kind, s.Dimension, s.Parent, s.Rule,
        [s.Anchor.X, s.Anchor.Y, s.Anchor.Z], s.Repeats, s.Cells.SelectMany(c => new[] { c.X, c.Y, c.Z }).ToArray(), s.Design, s.Note,
        AgentDb.ToUnixMs(s.At)));

    private DigSite? Decode(string json)
    {
        try
        {
            var r = JsonSerializer.Deserialize<Row>(json);
            if (r is null) return null;
            var cells = new List<BlockPos>(r.Cells.Length / 3);
            for (int i = 0; i + 2 < r.Cells.Length; i += 3) cells.Add(new BlockPos(r.Cells[i], r.Cells[i + 1], r.Cells[i + 2]));
            return new DigSite
            {
                Name = r.Name, Kind = r.Kind, Dimension = r.Dim, Parent = r.Parent, Rule = r.Rule,
                Anchor = r.Anchor is { Length: 3 } a ? new BlockPos(a[0], a[1], a[2]) : default, Repeats = r.Repeats, Cells = cells,
                Design = r.Design, Note = r.Note, At = AgentDb.FromUnixMs(r.At),
            };
        }
        catch (Exception ex) { _log?.LogDebug(ex, "dig site row unreadable"); return null; }
    }
}
