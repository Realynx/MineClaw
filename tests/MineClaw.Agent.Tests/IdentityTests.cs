using System.Text.Json;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Tests;

public class IdentityTests
{
    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

    private static ChatRouter Router(ChatOptions? o = null) =>
        new(o ?? new ChatOptions { LineDelayMs = 0 }, new EventQueue(), (_, _) => Task.CompletedTask);

    [Fact]
    public void Defaults_LoginVsChatName()
    {
        var r = Router();
        r.SetOnlinePlayers(["Alice", "Bob"]);
        bool A(string line) => r.IsAddressed(r.Parse(line));
        Assert.Equal("maid_chan", r.Username);
        Assert.Equal("claudechan", r.DisplayName);
        Assert.True(A("<Alice> hi claudechan"));
        Assert.True(A("<Alice> claude chan come here"));
        Assert.True(A("<Alice> Claude, look"));
        Assert.True(A("<Alice> maid_chan?"));
        Assert.False(A("<Alice> bob look"));

        // Own lines (vanilla "<maid_chan> message") are ignored.
        var q = new EventQueue();
        var r2 = new ChatRouter(new ChatOptions(), q, (_, _) => Task.CompletedTask);
        r2.OnServerChat("<maid_chan> hello claudechan");
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public async Task Rename_IsImmediate_Persisted_AndReappliedAtStartup()
    {
        using var dir = new TempDir();
        using var mem = new MemoryStore(dir.File("mineclaw.db"));
        var chat = Router();
        chat.SetOnlinePlayers(["Alice", "Bob"]);
        var reg = new ToolRegistry();
        IdentityTools.RegisterAll(reg, mem, chat);

        var r = await reg.ExecuteAsync("identity", J(new { op = "rename", name = "Mochi", by = "Alice" }), default);
        Assert.True(r.Ok, r.Text);
        Assert.Equal("Mochi", chat.DisplayName);
        Assert.Contains("claudechan", chat.Aliases); // old name kept as alias
        Assert.True(chat.IsAddressed(chat.Parse("<Bob> mochi wanna help?")));

        Assert.True((await reg.ExecuteAsync("identity", J(new { op = "add_alias", name = "mo" }), default)).Ok);
        Assert.True(chat.IsAddressed(chat.Parse("<Bob> mo!")));
        Assert.True((await reg.ExecuteAsync("identity", J(new { op = "remove_alias", name = "claude" }), default)).Ok);
        Assert.False(chat.IsAddressed(chat.Parse("<Bob> claude is an ai")));
        Assert.False((await reg.ExecuteAsync("identity", J(new { op = "remove_alias", name = "Mochi" }), default)).Ok);
        Assert.False((await reg.ExecuteAsync("identity", J(new { op = "rename", name = "§c" }), default)).Ok);

        // New process: identity comes back from memory over config defaults.
        var chat2 = Router();
        using var mem2 = new MemoryStore(dir.File("mineclaw.db"));
        IdentityTools.ApplyPersisted(mem2, chat2);
        Assert.Equal("Mochi", chat2.DisplayName);
        Assert.Contains("mo", chat2.Aliases);
        Assert.DoesNotContain("claude", chat2.Aliases);
    }

    [Fact]
    public async Task Rename_ChangesOnlyUncachedTail_OfSystemPrompt()
    {
        var (rt, llm, _) = TestRuntime.Create();
        llm.ThenTool("identity", new { op = "rename", name = "Mochi" }).ThenText("mochi it is!").ThenText("hi");
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("Alice", "clawd your name is now mochi")]), default);
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("Alice", "hey mochi")]), default);

        var before = llm.Requests[0].System;
        var sameTurn = llm.Requests[1].System;
        var after = llm.Requests[2].System;
        Assert.Equal(before[0], after[0]);                 // cached static block unchanged
        Assert.True(before[0].Cache);
        Assert.True(before[1].Cache);                      // breakpoint 2: identity/self block (small rewrite on change)
        Assert.Equal("5m", before[1].Ttl);
        Assert.Equal(before[1], sameTurn[1]);              // stable within the turn
        Assert.Contains("Clawd", before[1].Text);
        Assert.Contains("Your name in chat is Mochi", after[1].Text);
        Assert.DoesNotContain("Mochi", after[0].Text);
    }
}
