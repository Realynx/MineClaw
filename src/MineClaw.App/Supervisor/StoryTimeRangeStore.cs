using MineClaw.Agent.Rag;

namespace MineClaw.App.Supervisor;

/// <summary>An allowed arc of the Minecraft clock: start included, end excluded, possibly across midnight/dawn.</summary>
public sealed record StoryTimeRange(int Start, int End)
{
    public bool Valid => Start is >= 0 and <= 23999 && End is >= 0 and <= 23999 && Start != End;
    public bool Contains(long ticks)
    {
        var t = (ticks % 24000 + 24000) % 24000;
        return Start < End ? t >= Start && t < End : t >= Start || t < End;
    }
}

/// <summary>World-wide time policy, independent of any colony or open browser.</summary>
public sealed class StoryTimeRangeStore(AgentDb db)
{
    private void Ensure() => db.Exec("CREATE TABLE IF NOT EXISTS story_time_range(id INTEGER PRIMARY KEY CHECK(id=1), start INTEGER NOT NULL, end INTEGER NOT NULL)");

    public StoryTimeRange? Load()
    {
        Ensure();
        return db.Query("SELECT start, end FROM story_time_range WHERE id=1", r => new StoryTimeRange(r.GetInt32(0), r.GetInt32(1)))
            .FirstOrDefault(r => r.Valid);
    }

    public void Save(StoryTimeRange? range)
    {
        Ensure();
        if (range == null) db.Exec("DELETE FROM story_time_range WHERE id=1");
        else db.Exec("INSERT OR REPLACE INTO story_time_range(id, start, end) VALUES (1, $s, $e)", ("$s", range.Start), ("$e", range.End));
    }
}
