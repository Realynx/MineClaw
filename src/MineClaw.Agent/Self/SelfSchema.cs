using MineClaw.Agent.Rag;

namespace MineClaw.Agent.Self;

/// <summary>
/// Personality-growth tables, created idempotently by the Self layer itself (not an <see cref="AgentDb"/> migration, so
/// several processes of a hive can open the same file in any order). Every statement is IF NOT EXISTS / column-probed and
/// runs inside one IMMEDIATE transaction, so two clones starting at once can't both ALTER.
/// </summary>
public static class SelfSchema
{
    public const string MainScope = "main", HiveScope = "hive";

    private const string Tables = """
        CREATE TABLE IF NOT EXISTS self_signals(
            id INTEGER PRIMARY KEY, at INTEGER NOT NULL, scope TEXT NOT NULL, clone TEXT NOT NULL, player TEXT,
            kind TEXT NOT NULL, subject TEXT, valence REAL NOT NULL, intensity REAL NOT NULL, weight REAL NOT NULL,
            episode TEXT, note TEXT, source TEXT NOT NULL DEFAULT 'episode');
        CREATE INDEX IF NOT EXISTS self_signals_scope ON self_signals(scope, at);
        CREATE INDEX IF NOT EXISTS self_signals_player ON self_signals(scope, player COLLATE NOCASE, at);
        CREATE INDEX IF NOT EXISTS self_signals_kind ON self_signals(scope, kind, subject);
        CREATE TABLE IF NOT EXISTS self_acc(
            scope TEXT NOT NULL, family TEXT NOT NULL, key TEXT NOT NULL, dim TEXT NOT NULL,
            sum REAL NOT NULL, n INTEGER NOT NULL, updated INTEGER NOT NULL,
            PRIMARY KEY(scope, family, key, dim)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS self_phrases(
            scope TEXT NOT NULL, player TEXT NOT NULL, kind TEXT NOT NULL, phrase TEXT NOT NULL,
            episodes INTEGER NOT NULL, last_episode TEXT, first_at INTEGER NOT NULL, last_at INTEGER NOT NULL,
            flagged INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(scope, player, kind, phrase)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS self_seeds(scope TEXT PRIMARY KEY, seeds TEXT NOT NULL, source TEXT NOT NULL, created INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS self_abstractions(
            scope TEXT NOT NULL, layer INTEGER NOT NULL, idx INTEGER NOT NULL, text TEXT NOT NULL, leaves INTEGER NOT NULL,
            built INTEGER NOT NULL, PRIMARY KEY(scope, layer, idx)) WITHOUT ROWID;
        """;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AgentDb, object> Done = new();

    public static void Ensure(AgentDb db)
    {
        if (Done.TryGetValue(db, out _)) return;
        db.Tx(() =>
        {
            db.Exec(Tables);
            AddColumn(db, "self_profile", "scope", "TEXT NOT NULL DEFAULT 'main'");
            AddColumn(db, "self_profile", "observations", "INTEGER NOT NULL DEFAULT 1");
            AddColumn(db, "self_profile_history", "scope", "TEXT NOT NULL DEFAULT 'main'");
            db.Exec("CREATE INDEX IF NOT EXISTS self_profile_scope ON self_profile(scope)");
            db.Exec("CREATE INDEX IF NOT EXISTS self_profile_history_scope ON self_profile_history(scope, version)");
        });
        Done.AddOrUpdate(db, new object());
    }

    private static void AddColumn(AgentDb db, string table, string column, string decl)
    {
        bool has = db.Query($"PRAGMA table_info({table})", r => r.GetString(1)).Any(c => string.Equals(c, column, StringComparison.OrdinalIgnoreCase));
        if (!has) db.Exec($"ALTER TABLE {table} ADD COLUMN {column} {decl}");
    }

    /// <summary>Meta key for per-scope bookkeeping (the main scope keeps the legacy key names).</summary>
    public static string MetaKey(string baseKey, string scope) => scope == MainScope ? baseKey : $"{baseKey}:{scope}";
}
