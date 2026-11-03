using MineClaw.Agent.Chat;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Two ways a blocked command could get out anyway: vanilla accepts a namespace prefix on any command (<c>/minecraft:op</c>),
/// and <c>/execute ... run &lt;command&gt;</c> runs a command that someone else named. The name is compared without its
/// namespace, <c>/execute</c> is blocked by default, and whatever follows "run" is checked as well (recursively, so an
/// allowed /execute can't wrap a blocked one).
/// </summary>
public class CommandGateTests
{
    /// <summary>A gate whose owner is trusted: the strongest authorisation there is.</summary>
    private static CommandGate Gate(Action<CommandOptions>? tune = null)
    {
        var o = new CommandOptions();
        tune?.Invoke(o);
        return new CommandGate(o, () => ["poofimafox"]);
    }

    /// <summary>The owner types the exact command in chat: one grant for exactly that line.</summary>
    private static void OwnerAsks(CommandGate gate, string cmd)
    {
        var text = "maid chan run " + cmd;
        Assert.Equal(1, gate.Observe(new ChatMessage(ChatKind.Public, "poofimafox", text, "<poofimafox> " + text), mine: true, explicitAddress: true));
    }

    [Theory]
    [InlineData("/minecraft:op bob", "/op")]
    [InlineData("/MINECRAFT:STOP", "/stop")]
    [InlineData("/minecraft:deop bob", "/deop")]
    [InlineData("/minecraft:whitelist add bob", "/whitelist")]
    [InlineData("/minecraft:time set day", "/time")]
    [InlineData("/time set day", "/time")]
    [InlineData("/me waves", "/me")]
    public void A_command_is_named_without_its_namespace(string line, string name) =>
        Assert.Equal(name, CommandGate.NameOf(line));

    [Theory]
    [InlineData("/minecraft:op bob")]
    [InlineData("/MINECRAFT:OP bob")]
    [InlineData("/minecraft:stop")]
    [InlineData("/MINECRAFT:STOP")]
    [InlineData("/minecraft:deop bob")]
    [InlineData("/minecraft:pardon bob")]
    [InlineData("/minecraft:ban-ip bob")]
    [InlineData("/minecraft:whitelist add bob")]
    [InlineData("/minecraft:save-off")]
    [InlineData("/minecraft:kick bob")]
    public void A_namespaced_blocked_command_is_refused_even_when_the_owner_typed_it(string cmd)
    {
        var gate = Gate();
        OwnerAsks(gate, cmd);
        var d = gate.Authorize(cmd);
        Assert.False(d.Ok);
        Assert.Contains("blocked", d.Text);
    }

    [Theory]
    [InlineData("/execute as @a run op bob")]
    [InlineData("/execute run op bob")]
    [InlineData("/execute as @a run minecraft:op bob")]
    [InlineData("/execute run execute as @a run op bob")]
    [InlineData("/minecraft:execute as @a run stop")]
    [InlineData("/EXECUTE RUN STOP")]
    public void An_execute_wrapping_a_blocked_command_is_refused(string cmd)
    {
        var gate = Gate();
        OwnerAsks(gate, cmd);
        var d = gate.Authorize(cmd);
        Assert.False(d.Ok);
        Assert.Contains("blocked", d.Text);
    }

    [Fact]
    public void Execute_is_blocked_by_default_and_only_the_config_can_allow_it()
    {
        Assert.True(Gate().IsBlocked("/execute as @a run say hi"));
        Assert.False(Gate(o => o.Blocked = ["/op"]).IsBlocked("/execute as @a run say hi"));
    }

    [Fact]
    public void An_allowed_execute_cannot_wrap_a_blocked_command()
    {
        // The owner allowed /execute: the command it wraps still counts.
        var gate = Gate(o =>
        {
            o.Blocked = ["/op", "/stop", "/whitelist"];
            o.AlwaysAllowed = ["/me", "/tell", "/msg", "/execute"];
        });
        Assert.True(gate.IsBlocked("/execute as @a run op bob"));
        Assert.True(gate.IsBlocked("/minecraft:execute as @a run whitelist add bob"));
        Assert.False(gate.IsBlocked("/execute as @a run say hi"));
        OwnerAsks(gate, "/execute as @a run op bob");
        var d = gate.Authorize("/execute as @a run op bob");
        Assert.False(d.Ok);
        Assert.Contains("blocked", d.Text);
    }

    [Fact]
    public void A_trusted_request_with_a_command_example_keeps_the_plain_words_request_path()
    {
        var gate = Gate(o => o.Allowed = ["/tp"]);
        OwnerAsks(gate, "/tp it to 130 68 252");
        Assert.True(gate.Authorize("/tp maid_chan_109 130 68 252").Ok);
        Assert.True(gate.AllowOutgoing("/tp maid_chan_109 130 68 252"));
        Assert.True(gate.Authorize("/tp it to 130 68 252").Ok); // exact grant retained
        Assert.False(gate.Authorize("/give maid_chan diamond 64").Ok); // allowlist unchanged
        Assert.False(gate.Authorize("/op maid_chan").Ok); // blocklist unchanged
        var untrusted = Gate(o => o.Allowed = ["/tp"]);
        untrusted.Observe(new ChatMessage(ChatKind.Public, "stranger", "teleport /tp it", "<stranger> teleport /tp it"), true, true);
        Assert.False(untrusted.Authorize("/tp maid_chan_109 130 68 252").Ok);
    }

    [Fact]
    public void A_command_example_request_is_bounded_by_count_time_allowlist_and_blocklist()
    {
        var time = new ManualTime();
        var options = new CommandOptions { Allowed = ["/tp", "/time", "/op"] };
        Assert.Equal(8, options.RequestCommands);
        Assert.Equal(120, options.RequestSeconds);
        CommandGate Asked()
        {
            var gate = new CommandGate(options, () => ["poofimafox"], time);
            OwnerAsks(gate, "/tp it to 130 68 252");
            return gate;
        }
        var gate = Asked();
        // /op is on the allowlist here so this specifically tests the blocklist, before spending any request slots.
        Assert.False(gate.Authorize("/op maid_chan").Ok);
        Assert.False(gate.AllowOutgoing("/op maid_chan"));
        Assert.False(gate.Authorize("/give maid_chan diamond 64").Ok);
        Assert.False(gate.AllowOutgoing("/give maid_chan diamond 64"));
        for (int i = 0; i < options.RequestCommands; i++)
        {
            // None matches the example's exact grant; the request may authorize other allowlisted command kinds too.
            var cmd = i % 2 == 0 ? $"/tp maid_chan_{100 + i} 130 68 252" : $"/time set {1000 + i}";
            Assert.False(gate.AllowOutgoing(cmd));
            Assert.True(gate.Authorize(cmd).Ok, cmd);
            Assert.True(gate.AllowOutgoing(cmd));
            Assert.False(gate.AllowOutgoing(cmd)); // each authorization arms only one send
        }
        Assert.False(gate.Authorize("/time set day").Ok); // no ninth request command
        Assert.False(gate.AllowOutgoing("/time set day"));

        var expiring = Asked();
        time.Advance(TimeSpan.FromSeconds(options.RequestSeconds - 1));
        Assert.True(expiring.Authorize("/time set day").Ok);
        Assert.True(expiring.AllowOutgoing("/time set day"));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(expiring.Authorize("/time set night").Ok); // unused slots expire too
        Assert.False(expiring.AllowOutgoing("/time set night"));
    }

    [Fact]
    public void A_plain_command_still_runs_where_it_was_allowed_before()
    {
        var gate = Gate();
        Assert.False(gate.Authorize("/time set day").Ok); // nothing asked: refused, as before
        OwnerAsks(gate, "/time set day");
        Assert.True(gate.Authorize("/time set day").Ok);
        Assert.True(gate.AllowOutgoing("/time set day"));
        Assert.True(gate.AllowOutgoing("/me waves")); // always allowed
        Assert.True(gate.IsAlwaysAllowed("/me waves"));
        Assert.False(gate.IsBlocked("/time set day"));
        Assert.False(gate.IsBlocked("/minecraft:time set day"));
    }

    [Theory]
    [InlineData("/summon rabbit ~ ~ ~")]
    [InlineData("/minecraft:summon rabbit")]
    [InlineData("/give player_one diamond 64")]
    [InlineData("/gamemode creative")]
    [InlineData("/kill @e")]
    public void With_an_allowlist_a_command_off_it_is_refused_even_when_the_owner_types_it(string cmd)
    {
        var gate = Gate(o => o.Allowed = ["/tp", "/time", "/weather"]);
        OwnerAsks(gate, cmd);
        Assert.True(gate.IsBlocked(cmd));
        Assert.False(gate.Authorize(cmd).Ok);
        Assert.False(gate.AllowOutgoing(cmd));
    }

    [Theory]
    [InlineData("/tp player_one")]
    [InlineData("/time set day")]
    [InlineData("/minecraft:weather clear")]
    public void With_an_allowlist_a_command_on_it_still_needs_and_gets_the_owners_word(string cmd)
    {
        var gate = Gate(o => o.Allowed = ["/tp", "/time", "/weather"]);
        Assert.False(gate.Authorize(cmd).Ok);           // nobody asked yet
        OwnerAsks(gate, cmd);
        Assert.True(gate.Authorize(cmd).Ok);
        Assert.True(gate.AllowOutgoing(cmd));
    }

    [Fact]
    public void With_an_allowlist_always_allowed_chat_commands_still_go_out()
    {
        var gate = Gate(o => o.Allowed = ["/tp"]);
        Assert.True(gate.Authorize("/me thumps her foot").Ok);
        Assert.True(gate.AllowOutgoing("/me thumps her foot"));
    }

    [Fact]
    public void With_an_allowlist_the_hives_managed_whitelist_line_still_goes_out()
    {
        var gate = Gate(o => o.Allowed = ["/tp"]);
        gate.ManagedAccount = n => n.StartsWith("maid_chan_", StringComparison.Ordinal);
        Assert.True(gate.ArmManagedWhitelist("/whitelist add maid_chan_101"));
        Assert.True(gate.AllowOutgoing("/whitelist add maid_chan_101"));
    }

    [Fact]
    public void Without_an_allowlist_an_unlisted_command_can_still_be_authorised() // the default stays foxy's behaviour
    {
        var gate = Gate();
        OwnerAsks(gate, "/summon rabbit");
        Assert.True(gate.Authorize("/summon rabbit").Ok);
    }

    // The web viewer path (ObserveRemote) records the raw command text as a grant; Authorize must still apply the same
    // namespace, /execute and allowlist rules to it as to an in-game grant.
    [Theory]
    [InlineData("/minecraft:op bob")]
    [InlineData("/execute as @a run op bob")]
    [InlineData("/execute as @a run minecraft:op bob")]
    public void A_viewer_grant_for_a_wrapped_or_namespaced_blocked_command_is_refused(string cmd)
    {
        var gate = Gate();
        Assert.Equal(1, gate.ObserveRemote("poofimafox", "maid chan run " + cmd, "viewer"));
        Assert.False(gate.Authorize(cmd).Ok);
    }

    [Fact]
    public void The_allowlist_applies_to_viewer_grants_and_viewer_requests()
    {
        var gate = Gate(o => o.Allowed = ["/tp"]);
        Assert.Equal(1, gate.ObserveRemote("poofimafox", "maid chan run /summon rabbit", "viewer"));
        Assert.False(gate.Authorize("/summon rabbit").Ok);

        // A plain-words request from the viewer lets the bot write commands, but only allowlisted ones.
        Assert.Equal(0, gate.ObserveRemote("poofimafox", "maid chan please give everyone some rabbits", "viewer"));
        Assert.False(gate.Authorize("/give poofimafox rabbit 64").Ok);
        Assert.True(gate.Authorize("/tp poofimafox 0 70 0").Ok);
    }
}
