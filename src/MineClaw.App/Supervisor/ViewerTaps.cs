using System.Runtime.CompilerServices;
using MineClaw.Agent;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Rag;
using MineClaw.Bot.Bodies;
using MineClaw.Client;

namespace MineClaw.App.Supervisor;

/// <summary>
/// Feeds the web viewer (<see cref="ViewerFeed"/>, <see cref="ViewerMemoryActivity"/>) from every brain in this process: main
/// and each in-process hive attach their runtime here once; the events are read only while the viewer is on (<see cref="Feed"/>
/// set by the host), so attaching costs nothing otherwise. A hive in its own process attaches too, but its lines stay in that
/// process (main still sees its public chat through the server, and its muted text as hive progress).
/// </summary>
public static class ViewerTaps
{
    private static readonly ConditionalWeakTable<object, string> Brains = new();
    private static int _memoryHooked;

    /// <summary>The process's viewer feed (null = viewer off).</summary>
    public static ViewerFeed? Feed { get; private set; }
    /// <summary>The process's memory-activity ring (null = viewer off).</summary>
    public static ViewerMemoryActivity? Memory { get; private set; }

    /// <summary>The host turns the viewer on (Supervisor:Viewer:Enabled).</summary>
    public static void Enable(ViewerFeed feed, ViewerMemoryActivity memory)
    {
        Feed = feed;
        Memory = memory;
        if (Interlocked.Exchange(ref _memoryHooked, 1) == 0) MemoryAccess.Accessed += OnMemory;
    }

    /// <summary>
    /// One brain's runtime: its router's chat (<paramref name="mainHears"/>: main hears public chat; a hive only its whispers),
    /// its own speech (Said, InternalSpeech, Muted) and its memory reads.
    /// </summary>
    public static void Attach(string brain, AgentRuntime rt, MinecraftClient? client, bool mainHears)
    {
        Register(rt.Rag, brain);
        Register(rt.Memory.Index, brain);
        if (rt.Retriever != null) Register(rt.Retriever, brain);
        var chat = rt.Chat;
        chat.Said += text => Feed?.Add(brain, "said", brain, text, "bot");
        rt.Agent.Muted += text => Feed?.Add(brain, "muted", brain, text, "bot");
        rt.Agent.InternalSpeech += text => Feed?.Add(brain, "internal", brain, text, "bot");
        if (client != null) client.ChatReceived += raw => OnServerLine(brain, chat, raw, mainHears);
    }

    /// <summary>A retriever/index instance belongs to <paramref name="brain"/> (its reads are that brain's).</summary>
    public static void Register(object owner, string brain) => Brains.AddOrUpdate(owner, brain);

    /// <summary>A server chat line as the feed shows it (tests call it directly).</summary>
    public static void OnServerLine(string brain, ChatRouter chat, string raw, bool mainHears)
    {
        if (Feed is not { } feed) return;
        try
        {
            var msg = chat.Parse(raw);
            bool self = msg.Player != null && msg.Player.Equals(chat.Username, StringComparison.OrdinalIgnoreCase);
            if (msg.Kind == ChatKind.Whisper && !self) { feed.Add(brain, "whisper", msg.Player, msg.Text, "game"); return; }
            if (!mainHears || self) return; // our own echo is already "said"; a hive hears only its whispers
            bool hive = msg.Player != null && chat.IsHiveBot?.Invoke(msg.Player) == true;
            switch (msg.Kind)
            {
                case ChatKind.Public or ChatKind.Emote when hive:
                    feed.Add(msg.Player!, "said", msg.Player, msg.Text, "game"); // a hive bot in another process (deduped against its Said)
                    break;
                case ChatKind.Public:
                    feed.Add(brain, "chat", msg.Player, msg.Text, "game");
                    break;
                case ChatKind.Emote:
                    feed.Add(brain, "chat", msg.Player, "* " + msg.Player + " " + msg.Text, "game");
                    break;
                case ChatKind.Join or ChatKind.Leave or ChatKind.System when !hive:
                    feed.Add(brain, "system", null, msg.Text, "game");
                    break;
            }
        }
        catch (Exception) { /* the feed never breaks chat */ }
    }

    private static void OnMemory(object owner, string source, IReadOnlyList<MemoryAccessHit> hits)
    {
        if (Memory is not { } memory || !Brains.TryGetValue(owner, out var brain)) return;
        memory.Add(brain, BodyScope.Current?.Name, source, hits);
    }
}
