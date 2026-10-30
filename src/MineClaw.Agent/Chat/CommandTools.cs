using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Chat;

/// <summary>
/// <c>run_command {command}</c> (brain only): sends a server command only if <see cref="CommandGate.Authorize"/> finds a
/// trusted player's exact, recent, unused authorisation for it. The model can't edit the command, reuse an authorisation,
/// or get past the blocklist; every execution is audited.
/// </summary>
public static class CommandTools
{
    public const string ToolName = "run_command";

    public static void Register(ToolRegistry tools, CommandGate gate, ChatRouter chat) =>
        tools.Register(ToolName,
            "Run a /command a trusted player asked for.",
            ToolSchema.Create().Str("command").Build(),
            async (input, ct) =>
            {
                var cmd = (input.ReqStr("command")).Trim();
                var d = gate.Authorize(cmd);
                if (!d.Ok) return ToolResult.Error(d.Text);
                if (!await chat.SendCommandAsync(cmd, ct).ConfigureAwait(false)) return ToolResult.Error("not sent (too long)");
                gate.Audit(cmd, d.AuthorizedBy);
                return ToolResult.Success("sent " + cmd);
            }, tiers: ToolTiers.Main);
}
