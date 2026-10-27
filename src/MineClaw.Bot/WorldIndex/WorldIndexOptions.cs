namespace MineClaw.Bot.WorldIndex;

/// <summary>World-indexer settings (bind from "Bot:WorldIndex"). Defaults keep a chunk-load burst to a few ms per pass.</summary>
public sealed class WorldIndexOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Background pass interval. All indexing work happens in passes, never on the network thread.</summary>
    public int PassIntervalMs { get; set; } = 1000;

    /// <summary>Full column scans per pass (nearest dirty columns first); the rest wait for the next pass.</summary>
    public int MaxColumnsPerPass { get; set; } = 24;

    /// <summary>Soft time budget for column scans in one pass.</summary>
    public double ScanBudgetMs { get; set; } = 15;

    /// <summary>Observations per <c>UpsertMany</c> transaction.</summary>
    public int MaxBatch { get; set; } = 200;

    /// <summary>An unchanged thing seen again is re-touched (last_seen/seen_count) at most this often.</summary>
    public int RefreshMinutes { get; set; } = 10;

    /// <summary>Minimum seconds between writes of one cluster (island/farm/tree/structure) whose text changed.</summary>
    public int ClusterMinWriteSeconds { get; set; } = 30;

    /// <summary>Minimum seconds between writes of an open container's contents while it stays open.</summary>
    public int ContainerMinWriteSeconds { get; set; } = 2;

    /// <summary>A dug generator cell that refills within this many seconds confirms the generator.</summary>
    public int GeneratorRegenSeconds { get; set; } = 12;

    /// <summary>Signs label the nearest indexed thing within this many blocks.</summary>
    public double SignAttachRadius { get; set; } = 4;

    /// <summary>"near X" in rendered text: nearest other indexed thing within this many blocks.</summary>
    public double NearRadius { get; set; } = 6;

    /// <summary>Player position sampling for hangout spots.</summary>
    public int PlayerSampleSeconds { get; set; } = 10;
    /// <summary>Hangout bucket size (blocks, x/z).</summary>
    public int HangoutBucket { get; set; } = 16;
    /// <summary>Minutes in one bucket before it becomes a hangout.</summary>
    public double HangoutMinMinutes { get; set; } = 2;
    public int HangoutMinWriteMinutes { get; set; } = 5;

    /// <summary>Block changes within this distance of a player (and nearer to them than to the bot) are attributed to them.</summary>
    public double ActivityRadius { get; set; } = 6;
    /// <summary>An activity burst is summarised once the player has been idle this long.</summary>
    public int ActivityIdleSeconds { get; set; } = 20;
    /// <summary>Bursts smaller than this are dropped.</summary>
    public int ActivityMinBlocks { get; set; } = 8;
    /// <summary>At most one activity summary per player per this many seconds (later bursts are merged in).</summary>
    public int ActivityCooldownSeconds { get; set; } = 120;

    /// <summary>Unknown structures need this many unusual (player-placed) blocks.</summary>
    public int StructureMinBlocks { get; set; } = 6;

    /// <summary>Islands smaller than this many columns are ignored (stray blocks, bridges' crumbs).</summary>
    public int IslandMinColumns { get; set; } = 4;
}
