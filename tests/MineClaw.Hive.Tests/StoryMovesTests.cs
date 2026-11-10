using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// A formation in one request (<see cref="StoryColony.GiveMoves"/>): each pawn a move to its own cell, all or none, where the page
/// sent one orders request per pawn.
/// </summary>
public sealed class StoryMovesTests
{
    private static readonly P3 A = new(5, 64, 5), B = new(6, 64, 5), C = new(7, 64, 5);

    [Fact]
    public void Each_pawn_gets_its_own_target_and_the_answer_lists_the_orders_in_order()
    {
        var (c, h, _) = StoryRig.Make(3);
        foreach (var p in c.Pawns.Keys) c.Next(p); // each at a job: a move replaces it
        var (result, error) = c.GiveMoves([(c.P(0), A), (c.P(1), B), (c.P(2), C)], queue: false);
        Assert.Null(error);
        var ids = result!["ids"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(3, ids.Count);
        P3[] targets = [A, B, C];
        for (int i = 0; i < 3; i++)
        {
            var o = Assert.Single(c.Pawns[c.P(i)].Queue);
            Assert.Equal(("move", targets[i], ids[i]), (o.Kind, o.At, o.Id));
        }
        // Each pawn's job was cut like a move order's (the same supersede, so it heads off at once).
        Assert.Equal(3, h.Interrupted.Count(x => x.EndsWith(": new order", StringComparison.Ordinal)));
    }

    [Fact]
    public void An_unknown_pawn_refuses_the_whole_request_and_no_pawn_moves()
    {
        var (c, h, _) = StoryRig.Make(2);
        Assert.Null(c.GiveOrder([c.Pawns[c.P(0)]], new JsonObject { ["kind"] = "move", ["target"] = A.Json() }, queue: false).Error);
        long rev = c.Rev;
        int interrupts = h.Interrupted.Count;

        var (result, error) = c.GiveMoves([(c.P(0), B), (c.P(1), C), ("nobody", A)], queue: false);

        Assert.Null(result);
        Assert.Equal("no pawn 'nobody'", error);
        Assert.Equal(A, Assert.Single(c.Pawns[c.P(0)].Queue).At); // the first pawn's order stands
        Assert.Empty(c.Pawns[c.P(1)].Queue);
        Assert.Equal(rev, c.Rev);
        Assert.Equal(interrupts, h.Interrupted.Count);
    }

    [Fact]
    public void A_queued_formation_that_would_overfill_one_queue_refuses_all()
    {
        var (c, _, _) = StoryRig.Make(2);
        var second = c.Pawns[c.P(1)];
        for (int i = 0; i < 32; i++)
            Assert.Null(c.GiveOrder([second], new JsonObject { ["kind"] = "move", ["target"] = new P3(i, 64, 0).Json() }, queue: true).Error);

        Assert.Equal("queue full (32 orders)", c.GiveMoves([(c.P(0), A), (c.P(1), B)], queue: true).Error);
        Assert.Empty(c.Pawns[c.P(0)].Queue);
        Assert.Equal(32, second.Queue.Count);
        // Not queued, the same formation replaces each queue.
        Assert.Null(c.GiveMoves([(c.P(0), A), (c.P(1), B)], queue: false).Error);
        Assert.Equal(B, Assert.Single(second.Queue).At);
    }

    [Fact]
    public void Too_many_or_no_moves_are_refused()
    {
        var (c, _, _) = StoryRig.Make(1);
        Assert.Equal("moves required", c.GiveMoves([], queue: false).Error);
        var many = Enumerable.Range(0, StoryColony.MaxMoves + 1).Select(i => (c.P(0), new P3(i, 64, 0))).ToList();
        Assert.Equal("at most 512 moves", c.GiveMoves(many, queue: false).Error);
        Assert.Empty(c.Pawns[c.P(0)].Queue);
    }
}
