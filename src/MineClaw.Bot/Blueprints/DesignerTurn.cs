using MineClaw.Agent.Llm;

namespace MineClaw.Bot.Blueprints;

/// <summary>What one Design Studio message did: the reply, the ops applied (in order), cost and usage.</summary>
/// <param name="Refusal">Why the budget refused the message (nothing ran).</param>
/// <param name="Stopped">Why the message ended before the model was done (a limit, a cut-off, a stuck model, a failed call); its
/// edits so far are kept. Already said in <see cref="Reply"/>.</param>
public sealed record DesignerTurnResult(string Reply, IReadOnlyList<DesignerOp> Ops, decimal CostUsd, LlmUsage Usage, string Model, int Calls,
    string? Refusal = null, string? Stopped = null);

/// <summary>
/// One Design Studio message as it runs: the canvas ops, usage, cost and calls; the model's last words; why it ended early, if it
/// did; and the run of rounds whose tool calls all failed (a stuck model is stopped instead of paying for more of the same).
/// </summary>
public sealed class DesignerTurn(string model, int maxFailedRounds)
{
    public List<DesignerOp> Ops { get; } = [];
    public LlmUsage Usage { get; private set; } = LlmUsage.Zero;
    public decimal Cost { get; private set; }
    public int Calls { get; private set; }
    public string Model { get; private set; } = model;
    /// <summary>The model's newest non-empty text.</summary>
    public string Reply { get; private set; } = "";
    /// <summary>Why the turn ended early (the first reason wins); null = the model finished.</summary>
    public string? Stopped { get; private set; }

    private int _failedRounds;
    private string _lastError = "";

    /// <summary>A completed call: its usage, cost and text.</summary>
    public void Record(LlmResponse resp, decimal usd)
    {
        Calls++;
        Model = resp.Model;
        Spend(resp.Usage, usd);
        if (resp.Text.Trim() is { Length: > 0 } t) Reply = t;
    }

    /// <summary>Usage of a call that didn't complete (aborted at the cost cap).</summary>
    public void Spend(LlmUsage usage, decimal usd)
    {
        Usage = Usage.Add(usage);
        Cost += usd;
    }

    /// <summary>Ends the turn early with why (said with the reply); a reason already given stays.</summary>
    public void Stop(string why) => Stopped ??= why;

    /// <summary>
    /// Counts one round's tool results; true = the last <c>maxFailedRounds</c> rounds failed every call (the turn is stopped, with
    /// the last error, so the user sees what went wrong).
    /// </summary>
    public bool Stuck(IReadOnlyCollection<LlmToolResult> results)
    {
        if (results.Count == 0 || results.Any(r => !r.IsError))
        {
            _failedRounds = 0;
            return false;
        }
        _failedRounds++;
        _lastError = results.Last().Content;
        if (_failedRounds < Math.Max(1, maxFailedRounds)) return false;
        Stop($"I stopped after {_failedRounds} failed attempts in a row (last: {DesignerText.Clip(_lastError, 160)})");
        return true;
    }

    public DesignerTurnResult Result()
    {
        var reply = Reply.Length > 0 ? Reply
            : Ops.Count > 0 ? "Done: " + string.Join("; ", Ops.Select(o => o.Label).Distinct().Take(4)) + "."
            : Stopped == null ? "(no changes)" : "";
        if (Stopped != null) reply = (reply.Length > 0 ? reply + "\n\n" : "") + "(" + Stopped + (Ops.Count > 0 ? "; the edits so far are kept" : "") + ")";
        return new DesignerTurnResult(reply, Ops, Cost, Usage, Model, Calls, Stopped: Stopped);
    }
}
