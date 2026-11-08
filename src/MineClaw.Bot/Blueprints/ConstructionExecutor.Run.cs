using System.Globalization;
using MineClaw.Bot.Building;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.Blueprints;

public sealed partial class ConstructionExecutor
{
    /// <summary>
    /// One <see cref="RunAsync"/>: the run's state (report, plan, the part as built, the water column the body is in, the items
    /// short, someone else's blocks) and its steps, each one level of the loop: the preamble (claim, gate held/waiting), the
    /// passes over the plan (a re-plan or a gate correction starts a fresh pass), the step actions, the gate, the bookkeeping.
    /// </summary>
    private sealed partial class BuildRun
    {
        private readonly ConstructionExecutor _x;
        private readonly BlueprintOptions _o;
        private readonly long _id;
        private readonly VoxelPlan _design;
        private readonly BuildPart _part;
        private readonly IBuildBody _body;
        private readonly WorldState _w;
        private readonly CancellationToken _ct;
        private readonly int? _layer;
        private readonly bool _untilGate;
        private readonly BuildReport _report = new();
        private readonly BuildRecord _rec;
        private readonly TimeSpan _ttl;
        private readonly Dictionary<(int, int), string> _short = new();
        private readonly HashSet<BlockPos> _foreign = new();
        private BuildPlan _bp = null!;
        private VoxelPlan _voxel = null!;
        private BuildPart _vpart = null!;
        private DateTimeOffset _renewAt = DateTimeOffset.MinValue;
        private int _column = -1, _replans, _sinceCheckpoint;
        private bool _ruleHold;
        private int? _currentLayer;
        private HashSet<int>? _included;

        public BuildRun(ConstructionExecutor x, long id, VoxelPlan design, BuildPart part, IBuildBody body, CancellationToken ct, int? layer, bool untilGate)
        {
            (_x, _o, _id, _design, _part, _body, _w, _ct, _layer, _untilGate) = (x, x.Options0, id, design, part, body, body.World, ct, layer, untilGate);
            _rec = x.Store0?.GetBuild(id, part.Index) ?? new BuildRecord { BlueprintId = id, Part = part.Index };
            _rec.Parts = part.Count;
            _rec.Owner = body.Name;
            _rec.State = "building";
            _rec.Origin = design.Origin;
            _rec.Dim = _w.Dimension;
            _ttl = TimeSpan.FromSeconds(Math.Max(20, _o.ClaimSeconds));
        }

        private bool GateFirstPart => _x.GateOn && _part.Index == 0;
        private void Say(string line) => _x.Progress?.Invoke(line);

        public async Task<BuildReport> RunAsync(BuildPlan? previous)
        {
            Begin(previous);
            try
            {
                if (await EndsBeforeBuildingAsync().ConfigureAwait(false)) return Finish();
                await PrepareAsync().ConfigureAwait(false);
                await ResumeOreAsync().ConfigureAwait(false);
                while (await PassAsync().ConfigureAwait(false)) { }
                await AfterPassesAsync().ConfigureAwait(false);
                if (_bp.OreFrontier?.Count > 0 && _report.Stopped == null) _report.Stopped = "exposed ore waits for safe access or inventory room";
            }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested)
            {
                _report.Stopped = "cancelled";
            }
            await ExitAsync().ConfigureAwait(false);
            return Finish();
        }

        // ---------------------------------------------------------------- start

        private void Begin(BuildPlan? previous)
        {
            _bp = _x.PlanFor(_id, _design, _part, _body, save: false, previous: previous);
            (_voxel, _vpart) = Shape(_design, _part, _bp.Transform, _w.Dimension);
            _bp.State = "building";
            _report.Plan = _bp;
            _x.Plans?.Save(_bp);
            _report.Unreachable.AddRange(_bp.Infeasible.Select(i => i.Pos));
            if (_bp.Infeasible.Count == 0) return;
            var cant = BuildSkips.WithFloor(BuildSkips.Line(_bp.Infeasible.Select(i => (i.Pos, (string?)i.Why))), _bp.Foundation);
            Say($"can't place {_bp.Infeasible.Count}: {cant}");
            _x.Log0?.LogInformation("blueprint #{Id} {Part}: can't place {N}: {Why}", _id, _part.Label, _bp.Infeasible.Count, cant);
        }

        // Claim the part's area (hive: disjoint parts, so bodies never work in each other's cells).
        private async Task<string?> ClaimAsync()
        {
            if (_x.Claims0 == null) return null;
            var now = _x._time.GetUtcNow();
            if (now < _renewAt) return null;
            var c = await _x.Claims0.TryClaimAsync(_part.Claim, _ttl, $"blueprint #{_id} {_part.Label}", _ct).ConfigureAwait(false);
            _renewAt = now + _ttl / 2;
            return c.Ok ? null : $"build area {_part.Label} is claimed by {c.Holder}";
        }

        /// <summary>The run ends before building anything: the area is claimed, the gate is held, the gate-only phase has nothing
        /// to pass, or the gate asked (again) stops it.</summary>
        private async Task<bool> EndsBeforeBuildingAsync()
        {
            if (await ClaimAsync().ConfigureAwait(false) is { } held) { _report.Stopped = held; return true; }
            // A "no" at the gate holds the build for that player: only a fresh go (which clears it) builds on.
            if (_bp.Gate.HeldBy is { } heldBy)
            {
                _report.Stopped = $"held: {heldBy} said no at the confirmation gate; it needs a trusted player's fresh go (design build{{id={_id}}})"
                                  + (BuildGate.Placed(_bp, _id) is { } standing ? "; " + standing : "");
                _report.AtGate = true;
                await ExitAsync().ConfigureAwait(false);
                return true;
            }
            if (_untilGate && !GateToPass())
            {
                _report.GatePassed = true; // no gate to pass (off, approved before, or nothing to show)
                return true;
            }
            return await AskedBeforeAsync().ConfigureAwait(false);
        }

        private bool GateToPass() =>
            GateFirstPart && (_bp.Gate.State == GateState.Waiting || _bp.Gate.State == GateState.Pending && _bp.Steps.Any(s => s.Gate));

        /// <summary>A gate left asking (or its blocks up but never asked) is asked before anything else.</summary>
        private async Task<bool> AskedBeforeAsync() =>
            await AskAgainAsync().ConfigureAwait(false) || await AskUnaskedAsync().ConfigureAwait(false);

        // Stopped while asking (restart, death, stop): ask again, never carry on unasked.
        private async Task<bool> AskAgainAsync()
        {
            if (_bp.Gate.State != GateState.Waiting || !GateFirstPart) return false;
            var g = await GateAsync().ConfigureAwait(false);
            if (g == GateOutcome.Stop) { await ExitAsync().ConfigureAwait(false); return true; }
            if (!_untilGate || g != GateOutcome.Continue) return false;
            _report.GatePassed = true;
            return true;
        }

        // Gate blocks up but never asked (stopped right after the last one): ask before anything else.
        private async Task<bool> AskUnaskedAsync()
        {
            if (!_untilGate || _bp.Gate.State != GateState.Pending || !_bp.Steps.Where(s => s.Gate).All(s => s.Finished)) return false;
            var g = await GateAsync().ConfigureAwait(false);
            if (g == GateOutcome.Stop) return true;
            if (g != GateOutcome.Continue) return false;
            _report.GatePassed = true;
            return true;
        }

        private async Task PrepareAsync()
        {
            await PrefetchAsync().ConfigureAwait(false);
            // A shortfall: plan around the missing items (their blocks wait; nothing is planned to rest on them).
            if (_short.Count > 0)
            {
                _bp = Replan(_bp);
                _report.Plan = _bp;
                _included = null;
            }
            if (_voxel.Max.Y - _voxel.Min.Y >= 2 || _bp.Steps.Any(s => s.Action == StepAction.Scaffold)) _ = await _body.ClutchAsync(_ct).ConfigureAwait(false);
        }

        // ---------------------------------------------------------------- the passes

        /// <summary>What the pass does next: go on (with this step), skip it, stop the run's passes, or pass again (the plan changed).</summary>
        private enum Next { Go, Skip, Stop, Restart }

        /// <summary>One pass over the plan's steps in order; true = the plan changed (a re-plan, a gate correction): pass again.</summary>
        private async Task<bool> PassAsync()
        {
            if (_report.Stopped != null) return false;
            _included = Included(_bp);
            _report.Total = _bp.Steps.Count(s => s.Action == StepAction.Place && _included.Contains(s.Index));
            Tally();
            for (int i = 0; i < _bp.Steps.Count; i++)
            {
                var step = _bp.Steps[i];
                if (step.Finished || !_included.Contains(i)) continue;
                _ct.ThrowIfCancellationRequested();
                var next = await BeforeStepAsync(step, i).ConfigureAwait(false);
                if (next == Next.Skip) continue;
                if (next == Next.Go)
                {
                    NoteLayer(step);
                    var outcome = await StepAsync(step).ConfigureAwait(false);
                    next = await AfterStepAsync(step, outcome).ConfigureAwait(false);
                }
                if (next != Next.Go) return next == Next.Restart;
            }
            return false;
        }

        /// <summary>After a step: a stop ends the passes, a deviation re-plans (up to the limit), the last gate block asks the gate.</summary>
        private async Task<Next> AfterStepAsync(PlanStep step, StepOutcome outcome)
        {
            if (_report.Stopped != null) return Next.Stop;
            if (outcome == StepOutcome.Replan && _replans < _o.MaxReplans)
            {
                _replans++;
                _bp = Replan(_bp);
                _report.Plan = _bp;
                return Next.Restart;
            }
            if (GateDue(outcome, step))
            {
                var g = await GateAsync().ConfigureAwait(false);
                if (g == GateOutcome.Stop) return Next.Stop;
                if (g == GateOutcome.Replanned) return Next.Restart;
                if (_untilGate) { _report.GatePassed = true; return Next.Stop; }
            }
            CheckpointAfter(step);
            return Next.Go;
        }

        /// <summary>Whether this step runs now: the claim, a standing rule's hold, its dependencies, the first part's gate.</summary>
        private async Task<Next> BeforeStepAsync(PlanStep step, int i)
        {
            if (await ClaimAsync().ConfigureAwait(false) is { } lost) { _report.Stopped = lost; return Next.Stop; }
            if (_x.HoldNow(_id) is { } rule) { _report.Stopped = rule; _ruleHold = true; return Next.Stop; }
            if (WaitsForEarlier(step, i))
            {
                step.Note = "waits for an earlier step";
                return Next.Skip;
            }
            if (_part.Index > 0 && step.IsPlacement && !await OtherPartsGateAsync().ConfigureAwait(false))
            {
                _report.Stopped = "waiting for the first part's confirmation gate";
                return Next.Stop;
            }
            return Next.Go;
        }

        private bool WaitsForEarlier(PlanStep step, int i) =>
            step.Deps.Any(d => d >= 0 && d < _bp.Steps.Count && d != i && (!_bp.Steps[d].Finished || _bp.Steps[d].Status == StepStatus.Skipped));

        private void NoteLayer(PlanStep step)
        {
            if (step.Action != StepAction.Place || _currentLayer == step.Target.Y) return;
            if (_currentLayer is { } done) CheckLayer(done);
            _currentLayer = step.Target.Y;
        }

        private bool GateDue(StepOutcome outcome, PlanStep step) =>
            outcome == StepOutcome.Placed && step.Gate && GateFirstPart && _bp.Gate.State == GateState.Pending
            && _bp.Steps.Where(s => s.Gate).All(s => s.Finished);

        private void CheckpointAfter(PlanStep step)
        {
            if (step.Action is not (StepAction.PourWater or StepAction.PickupWater or StepAction.Scaffold or StepAction.RemoveScaffold)
                && ++_sinceCheckpoint < Math.Max(1, _o.CheckpointEvery)) return;
            _sinceCheckpoint = 0;
            Checkpoint();
        }

        private async Task AfterPassesAsync()
        {
            if (_untilGate && !_report.GatePassed && _report.Stopped == null) _report.Stopped = "couldn't place the confirmation gate blocks";
            if (_report.Stopped == null && _column >= 0) await LeaveColumnAsync().ConfigureAwait(false);
            if (_currentLayer is { } lastLayer && _report.Stopped == null) CheckLayer(lastLayer);
            if (_report.Stopped == null && _short.Count > 0) _report.Stopped = ShortfallStop();
        }

        private string ShortfallStop() =>
            _short.Values.First() + (_short.Count > 1 ? $" (+{_short.Count - 1} more items)" : "") + "; built everything else, paused until I have them";

        // Never strand the body: a run that ends (done, gate, materials, a rule's time limit) with it up on the build and no walk
        // home comes down first: its own pillar, else a stair. A cancel (a stop, the danger reflex) is not ours to move after.
        private async Task ExitAsync()
        {
            if (!MayExit()) return;
            try
            {
                if (_column >= 0 && !await LeaveColumnAsync().ConfigureAwait(false)) return;
                _report.Exit = await LeaveSafelyAsync(_body, _bp, _voxel, _rec.Scaffold, _ct, _x.Progress, _x.Log0).ConfigureAwait(false);
                if (_ruleHold) await WalkBackToStartAsync().ConfigureAwait(false);
                if (_report.Exit != null) Checkpoint();
            }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested) { }
        }

        private bool MayExit()
        {
            if (_ct.IsCancellationRequested || _report.GatePassed || !_body.Available) return false;
            return _report.Stopped is not { } st || !(st.Contains("void", StringComparison.OrdinalIgnoreCase) || st.Contains("danger", StringComparison.OrdinalIgnoreCase));
        }

        // A rule's time limit (dusk for a day-only build): home before dark, where the build started.
        private async Task WalkBackToStartAsync()
        {
            if (_bp.Home is not { } start || StartCell(_w) == start || _w.Player.Position.ToBlockPos().DistanceSquaredTo(start) <= 4) return;
            var g = await _body.GotoAsync(start, _ct).ConfigureAwait(false);
            var line = g.Ok ? $"walked back to {start.X},{start.Y},{start.Z} (where the build started)" : $"couldn't walk back to where the build started: {Short(g.Message)}";
            Say(line);
            _report.Exit = _report.Exit == null ? line : _report.Exit + "; " + line;
        }

        // ---------------------------------------------------------------- one step

        private Task<StepOutcome> StepAsync(PlanStep step) => step.Action switch
        {
            StepAction.Clear => ClearAsync(step),
            StepAction.Place or StepAction.Scaffold => PlaceStepAsync(step),
            StepAction.RemoveScaffold => RemoveScaffoldAsync(step),
            StepAction.PourWater => PourAsync(step),
            StepAction.PickupWater => PickupAsync(step),
            _ => Task.FromResult(StepOutcome.Done),
        };

        private async Task<StepOutcome> ClearAsync(PlanStep step)
        {
            int id = _w.GetBlock(step.Target);
            if (id == 0 || BlockShapes.IsLiquid(id)) { step.Status = StepStatus.Verified; return StepOutcome.Done; }
            if (_foreign.Contains(step.Target)) { Skip(step, "someone else's block: left"); return StepOutcome.Done; }
            if (_body.IsProtected(step.Target, id))
            {
                _report.Deviations.Add($"{BuildBlocks.Name(id, 0)} at {_voxel.Local(step.Target)} is protected: left");
                Skip(step, "protected");
                return StepOutcome.Done;
            }
            if (!await MoveAsync(step, needStand: false).ConfigureAwait(false)) return StepOutcome.Done;
            var r = await DigAsync(step.Target, step.Column >= 0).ConfigureAwait(false);
            if (r.Ok)
            {
                _report.Cleared++;
                step.Status = StepStatus.Verified;
                _bp.ScaffoldUp.Remove(step.Target);
                _rec.Scaffold.Remove(step.Target);
                if (_voxel.Blueprint.Mine)
                {
                    ExposeOre(step.Target, OreIdentity.Canonical(id, _w.Inventory.Model.ModernClicks));
                    await DrainOreAsync().ConfigureAwait(false);
                }
            }
            else Deviate(step, $"couldn't clear {_voxel.Local(step.Target)}: {Short(r.Message)}", r);
            return StepOutcome.Done;
        }

        private Task<ActionResult> DigAsync(BlockPos p, bool here) => here ? _body.DigHereAsync(p, _ct) : _body.DigAsync(p, _ct);

        /// <summary>A failed action: the deviation reported, the step skipped with the short reason.</summary>
        private void Deviate(PlanStep step, string deviation, ActionResult r)
        {
            _report.Deviations.Add(deviation);
            Skip(step, Short(r.Message));
        }

        private async Task<StepOutcome> RemoveScaffoldAsync(PlanStep step)
        {
            int id = _w.GetBlock(step.Target);
            if (id == 0 || _voxel.Blocks.ContainsKey(step.Target)) { _bp.ScaffoldUp.Remove(step.Target); step.Status = StepStatus.Verified; return StepOutcome.Done; }
            // Only our own placement comes down (protection rule 1: self-placed is always minable).
            if (!IsOurScaffold(step.Target, id)) { Skip(step, "not ours"); return StepOutcome.Done; }
            // From the planned stand (it keeps a way down: a pillar is dug from on top of it, a stair from the step below); a cell
            // no stand sees is left to the client's own aim.
            bool planned = step.Column >= 0 || step.Stand != default && step.Note?.StartsWith("no stand sees it", StringComparison.Ordinal) != true;
            if (!await MoveAsync(step, needStand: planned).ConfigureAwait(false)) return StepOutcome.Done;
            bool here = step.Column >= 0 || planned && StartCell(_w) == step.Stand;
            var r = await DigAsync(step.Target, here).ConfigureAwait(false);
            if (r.Ok)
            {
                _report.ScaffoldRemoved++;
                _bp.ScaffoldUp.Remove(step.Target);
                _rec.Scaffold.Remove(step.Target);
                step.Status = StepStatus.Verified;
            }
            else Deviate(step, $"scaffold at {_voxel.Local(step.Target)} left: {Short(r.Message)}", r);
            return StepOutcome.Done;
        }

        private bool IsOurScaffold(BlockPos p, int id) =>
            _w.Placed.IsPlaced(p, _w.Dimension, id) || _bp.ScaffoldUp.Contains(p) || _rec.Scaffold.Contains(p);

        private async Task<StepOutcome> PourAsync(PlanStep step)
        {
            if (!await MoveAsync(step, needStand: true).ConfigureAwait(false)) return StepOutcome.Done;
            var r = await _body.PourWaterAsync(step.Target, step.Face ?? BlockFace.Top, _ct).ConfigureAwait(false);
            if (!r.Ok) { Deviate(step, $"water column at {_voxel.Local(step.Target)}: {Short(r.Message)}", r); return StepOutcome.Done; }
            step.Status = StepStatus.Done;
            Say($"poured a water column to build down ({step.Note})");
            return StepOutcome.Done;
        }

        private async Task<StepOutcome> PickupAsync(PlanStep step)
        {
            if (!await MoveAsync(step, needStand: true).ConfigureAwait(false)) return StepOutcome.Done;
            var r = await _body.PickupWaterAsync(step.Target, _ct).ConfigureAwait(false);
            if (r.Ok)
            {
                step.Status = StepStatus.Verified;
                foreach (var c in _bp.Columns.Where(c => c.Source == step.Target)) c.Removed = true;
            }
            else Deviate(step, $"water at {_voxel.Local(step.Target)} left: {Short(r.Message)}", r);
            return StepOutcome.Done;
        }

        private async Task<StepOutcome> PlaceStepAsync(PlanStep step)
        {
            bool scaffold = step.Action == StepAction.Scaffold;
            if (AlreadyStands(step, scaffold)) return StepOutcome.Done;
            if (Obstructed(step)) return StepOutcome.Replan;
            if (await ItemForAsync(step, scaffold).ConfigureAwait(false) is not { } got) return StepOutcome.Done;
            var (item, dmg) = got;
            if (!await MoveAsync(step, needStand: true).ConfigureAwait(false)) return StepOutcome.Done;
            var r = await PutAsync(step, scaffold, item, dmg).ConfigureAwait(false);
            if (!r.Ok) return PlaceFailed(step, scaffold, r);
            Ledger(step, scaffold);
            if (scaffold)
            {
                ScaffoldPlaced(step);
                return StepOutcome.Done;
            }
            await DesignPlacedAsync(step, item, dmg).ConfigureAwait(false);
            return StepOutcome.Placed;
        }

        private void ScaffoldPlaced(PlanStep step)
        {
            _report.Scaffolded++;
            if (!_bp.ScaffoldUp.Contains(step.Target)) _bp.ScaffoldUp.Add(step.Target);
            if (!_rec.Scaffold.Contains(step.Target)) _rec.Scaffold.Add(step.Target);
            step.Status = StepStatus.Verified;
        }

        /// <summary>A design block went in: counted, a gate block noted, verified (a turned stair or door re-placed once).</summary>
        private async Task DesignPlacedAsync(PlanStep step, int item, int dmg)
        {
            _report.Placed++;
            if (step.Gate) _bp.Gate.Blocks.Add(step.Target);
            if (Verify(step) || Turned(step) && await RefaceAsync(step, item, dmg).ConfigureAwait(false)) step.Status = StepStatus.Verified;
        }

        /// <summary>The planned block already stands there (a scaffold step: anything solid does).</summary>
        private bool AlreadyStands(PlanStep step, bool scaffold)
        {
            if (!scaffold && _voxel.Blocks.TryGetValue(step.Target, out var want) && DesignChecks.SameState(want, _w.GetBlock(step.Target), _w.GetMeta(step.Target)))
            { _report.Already++; step.Status = StepStatus.Verified; return true; }
            if (scaffold && BuildBlocks.Supports(_w.GetBlock(step.Target))) { step.Status = StepStatus.Verified; return true; }
            return false;
        }

        /// <summary>Someone built there (or it's the wrong block): never dig a player's block on our own; re-plan around it.</summary>
        private bool Obstructed(PlanStep step)
        {
            int existing = _w.GetBlock(step.Target);
            if (Aiming.IsReplaceable(_w, existing)) return false;
            bool ours = _bp.Placed.Contains(step.Target) || _w.Placed.IsPlaced(step.Target, _w.Dimension, existing);
            _report.Deviations.Add($"{_voxel.Local(step.Target)} holds {BuildBlocks.Name(existing, _w.GetMeta(step.Target))}{(ours ? "" : " (not mine: someone built there)")}");
            if (!ours) _foreign.Add(step.Target);
            Skip(step, ours ? "wrong block" : "someone built there");
            return true;
        }

        /// <summary>The item to place (scaffold: a clutch block); null = none (a scaffold shortfall stops the run, a design item
        /// leaves the step pending while everything that doesn't need it goes on).</summary>
        private async Task<(int Item, int Damage)?> ItemForAsync(PlanStep step, bool scaffold)
        {
            if (scaffold)
            {
                int item = ScaffoldItem(_body, _bp);
                if (item != 0) return (item, 0);
                _report.Stopped = "missing scaffold blocks (cobblestone or planks)";
                return null;
            }
            if (_body.Count(step.Item, step.Damage) == 0 && !await HaveAsync(step.Item, step.Damage).ConfigureAwait(false))
            {
                step.Note = "missing " + Name(step.Item, step.Damage);
                return null;
            }
            return (step.Item, step.Damage);
        }

        private Task<ActionResult> PutAsync(PlanStep step, bool scaffold, int item, int dmg) =>
            scaffold && step.Technique == Technique.Pillar
                ? _body.PillarAsync(step.Target, item, _ct)
                : _body.PlaceAsync(new BuildStep(scaffold ? StepKind.Scaffold : StepKind.Place, step.Target, scaffold ? item : step.Id, scaffold ? 0 : step.Meta,
                    null, step.Face, step.HiddenFace, step.Yaw), item, dmg, _ct);

        private StepOutcome PlaceFailed(PlanStep step, bool scaffold, ActionResult r)
        {
            _report.Failed++;
            _report.Deviations.Add($"{(scaffold ? "scaffold" : BuildBlocks.Name(step.Id, step.Meta))} at {_voxel.Local(step.Target)}: {Short(r.Message)}");
            if (r.Message.Contains("void", StringComparison.OrdinalIgnoreCase) || r.Message.Contains("danger", StringComparison.OrdinalIgnoreCase))
            { _report.Stopped = r.Message; return StepOutcome.Done; }
            step.Note = Short(r.Message);
            return StepOutcome.Replan; // re-plan from where I am now (another stand/face/technique)
        }

        /// <summary>A placement goes in the plan's ledger (a door's upper half too).</summary>
        private void Ledger(PlanStep step, bool scaffold)
        {
            step.Status = StepStatus.Done;
            if (!_bp.Placed.Contains(step.Target)) _bp.Placed.Add(step.Target);
            if (BuildBlocks.KindOf(step.Id) == BuildKind.Door && !scaffold && !_bp.Placed.Contains(step.Target.Up())) _bp.Placed.Add(step.Target.Up());
        }

        /// <summary>To the step's stand: out of (or into, down to the depth of) a water column first.</summary>
        private async Task<bool> MoveAsync(PlanStep step, bool needStand)
        {
            if (step.Column >= 0 && step.Column < _bp.Columns.Count) return await IntoColumnAsync(step).ConfigureAwait(false);
            if (_column >= 0 && !await LeaveColumnAsync().ConfigureAwait(false)) return false;
            if (!needStand || step.Stand == default) return true;
            if (StartCell(_w) == step.Stand) return true;
            var g = await _body.GotoAsync(step.Stand, _ct).ConfigureAwait(false);
            if (!g.Ok) step.Note = "goto: " + Short(g.Message);
            return true; // the action itself says whether it worked from here
        }

        private async Task<bool> IntoColumnAsync(PlanStep step)
        {
            var c = _bp.Columns[step.Column];
            if (_column >= 0 && _column != step.Column) await LeaveColumnAsync().ConfigureAwait(false);
            var r = await _body.SwimToDepthAsync(c.Swim, c.Entry, step.Stand.Y, _ct).ConfigureAwait(false);
            if (!r.Ok) { _report.Stopped = $"water column at {_voxel.Local(c.Source)}: {Short(r.Message)}"; return false; }
            _column = step.Column;
            return true;
        }

        private async Task<bool> LeaveColumnAsync()
        {
            if (_column < 0) return true;
            var c = _bp.Columns[_column];
            var r = await _body.SwimOutAsync(c.Swim, c.Entry, _ct).ConfigureAwait(false);
            if (!r.Ok) { _report.Stopped = $"couldn't climb out of the water column: {Short(r.Message)}"; return false; }
            _column = -1;
            return true;
        }

        // ---------------------------------------------------------------- materials

        private async Task PrefetchAsync()
        {
            var needs = _bp.Steps.Where(s => s.Action == StepAction.Place && !s.Finished && (_included ??= Included(_bp)).Contains(s.Index))
                .GroupBy(s => (s.Item, s.Damage)).Select(g => (g.Key.Item, g.Key.Damage, Count: g.Count())).ToList();
            foreach (var (item, dmg, count) in needs)
            {
                if (_body.Count(item, dmg) >= count) continue;
                var why = await _body.EnsureAsync(item, dmg, Math.Min(count, _o.FetchMax), _ct).ConfigureAwait(false);
                if (why != null && _body.Count(item, dmg) == 0) _short[(item, dmg)] = why;
            }
            await PrefetchScaffoldAsync(needs).ConfigureAwait(false);
        }

        // Scaffold (climbing pillars and stairs included) comes out of the same cobblestone: fetched on top of what the design's own
        // cobblestone takes (best effort: planks or another clutch block do too).
        private async Task PrefetchScaffoldAsync(List<(int Item, int Damage, int Count)> needs)
        {
            int scaffold = _bp.Steps.Count(s => s.Action == StepAction.Scaffold && !s.Finished && (_included ??= Included(_bp)).Contains(s.Index));
            if (scaffold <= 0) return;
            int cobble = needs.Where(n => n.Item == Blocks.Cobblestone).Sum(n => n.Count) + scaffold;
            if (_body.Count(Blocks.Cobblestone, 0) < cobble)
                _ = await _body.EnsureAsync(Blocks.Cobblestone, 0, Math.Min(cobble, _o.FetchMax), _ct).ConfigureAwait(false);
        }

        /// <summary>Gets more of an item when the inventory ran out (once per item per run when it can't: no repeated asks).</summary>
        private async Task<bool> HaveAsync(int item, int dmg)
        {
            if (_short.ContainsKey((item, dmg)) && _body.Count(item, dmg) <= 0) return false;
            int left = _bp.Steps.Count(s => s.Action == StepAction.Place && !s.Finished && s.Item == item && s.Damage == dmg);
            var why = await _body.EnsureAsync(item, dmg, Math.Max(1, Math.Min(left, _o.FetchMax)), _ct).ConfigureAwait(false);
            if (_body.Count(item, dmg) > 0) { _short.Remove((item, dmg)); return true; }
            _short[(item, dmg)] = why ?? $"missing {Name(item, dmg)} (need {left}, have 0)";
            return false;
        }

        // ---------------------------------------------------------------- gate

        private async Task<GateOutcome> GateAsync()
        {
            if (_bp.Gate.Correction is { } pendingFix) return await CorrectAsync(pendingFix).ConfigureAwait(false);
            var q = _x.GateQuestion?.Invoke(_bp, _voxel) ?? BuildGate.Question(_bp, _voxel, _voxel.Blueprint.Name, _ => null, null, null);
            _bp.Gate.State = GateState.Waiting;
            _bp.Gate.Question = q;
            _bp.Gate.AskedAt = _x._time.GetUtcNow();
            if (_bp.Gate.Blocks.Count == 0) _bp.Gate.Blocks = _bp.Steps.Where(s => s.Gate && s.Finished).Select(s => s.Target).ToList();
            Checkpoint();
            if (_column >= 0) await LeaveColumnAsync().ConfigureAwait(false);
            await _x.Ask!(q).ConfigureAwait(false);
            Say("paused at the confirmation gate: " + q);
            var reply = await _x.Gates!.WaitAsync(_id, TimeSpan.FromMinutes(Math.Max(0.01, _o.GateMinutes)), _ct).ConfigureAwait(false);
            // A restart (or another process) may have recorded the answer in the plan meanwhile.
            if (reply == null && _x.Plans?.Get(_id, _part.Index) is { Gate.State: GateState.Approved } fresh) { _bp.Gate = fresh.Gate; return GateOutcome.Continue; }
            return await AnsweredAsync(reply).ConfigureAwait(false);
        }

        private async Task<GateOutcome> AnsweredAsync(GateReply? reply)
        {
            switch (reply?.Verdict)
            {
                case null:
                    _report.Stopped = "paused at the confirmation gate: no answer yet (no default: say 'keep going' or what to change)";
                    _report.AtGate = true;
                    return GateOutcome.Stop;
                case GateVerdict.Hold:
                    return Held(reply);
                case GateVerdict.Approve:
                    return Approved(reply);
                case GateVerdict.Undo:
                    var undone = await UndoPlacedAsync(_body, _bp, _w, _ct).ConfigureAwait(false);
                    _bp.State = "undone";
                    _report.Stopped = $"undone: took down {undone} block{(undone == 1 ? "" : "s")}";
                    return GateOutcome.Stop;
                default:
                    return await CorrectAsync(reply.Correction!).ConfigureAwait(false);
            }
        }

        private GateOutcome Held(GateReply reply)
        {
            _bp.Gate.HeldBy = reply.By;
            Checkpoint();
            _report.Stopped = $"paused at the confirmation gate: {reply.By} said \"{Short(reply.Text)}\"; tell me what to change"
                              + (BuildGate.Placed(_bp, _id) is { } blocks ? "; " + blocks : "");
            _report.AtGate = true;
            return GateOutcome.Stop;
        }

        private GateOutcome Approved(GateReply reply)
        {
            _bp.Gate.State = GateState.Approved;
            _bp.Gate.ApprovedBy = reply.By;
            _bp.Gate.HeldBy = null;
            Checkpoint();
            Say($"{reply.By} confirmed the first blocks: carrying on");
            return GateOutcome.Continue;
        }

        private async Task<GateOutcome> CorrectAsync(BuildTransform fix)
        {
            // The gate blocks come down (self-placed, so allowed), the plan turns/mirrors/moves, the gate repeats.
            if (_column >= 0) await LeaveColumnAsync().ConfigureAwait(false);
            await TakeGateBlocksDownAsync().ConfigureAwait(false);
            _bp.Transform = _bp.Transform.Then(fix);
            _bp.Gate.Correction = null;
            _bp.Gate.State = GateState.Pending;
            _bp.Gate.Rounds++;
            _bp.Gate.Blocks.Clear();
            _bp.Gate.Question = null;
            (_voxel, _vpart) = Shape(_design, _part, _bp.Transform, _w.Dimension);
            _bp = Stamp(BuildPlanner.Plan(_voxel, _w.PlanningView, StartCell(_w), _vpart, _x.Options(_body, _part, _bp), _bp));
            _report.Plan = _bp;
            Checkpoint();
            Say($"re-planned {_bp.Transform.Text}: placing the first blocks again to check");
            return GateOutcome.Replanned;
        }

        private async Task TakeGateBlocksDownAsync()
        {
            foreach (var p in _bp.Gate.Blocks.Concat(_bp.Placed).Distinct().OrderByDescending(p => p.Y).ToList())
            {
                if (_w.GetBlock(p) == 0) continue;
                var r = await _body.DigAsync(p, _ct).ConfigureAwait(false);
                if (r.Ok) { _bp.Placed.Remove(p); _report.Placed = Math.Max(0, _report.Placed - 1); }
                else _report.Deviations.Add($"couldn't take down gate block {p.X},{p.Y},{p.Z}: {Short(r.Message)}");
            }
        }

        private async Task<bool> OtherPartsGateAsync()
        {
            if (!_x.GateOn || _x.Plans == null) return true;
            for (int k = 0; k < Math.Max(1, _o.GateMinutes * 30); k++)
            {
                var first = _x.Plans.Get(_id, 0);
                if (first == null || first.Gate.State is GateState.Approved or GateState.Off) return true;
                await Task.Delay(TimeSpan.FromSeconds(2), _x._time, _ct).ConfigureAwait(false);
            }
            return false;
        }

        // ---------------------------------------------------------------- bookkeeping

        /// <summary>A fresh slice of this design: its id, version, dimension, building.</summary>
        private BuildPlan Stamp(BuildPlan next)
        {
            next.BlueprintId = _id;
            next.Version = _design.Hash();
            next.Dim = _w.Dimension;
            next.State = "building";
            return next;
        }

        private BuildPlan Replan(BuildPlan current)
        {
            var next = Stamp(BuildPlanner.Plan(_voxel, _w.PlanningView, StartCell(_w), _vpart,
                _x.Options(_body, _part, current, _foreign, _short.Keys.Select(k => (k.Item1, k.Item2)).ToHashSet()), current));
            // Someone else's blocks stay: a block that appeared in a planned cell since we started (not ours) is reported, not dug.
            foreach (var s in next.Steps.Where(s => s.Action == StepAction.Clear && !s.Finished)) LeaveIfForeign(next, s);
            _x.Plans?.Save(next);
            Say($"re-planned the rest ({next.Steps.Count(s => !s.Finished)} steps) after a deviation");
            return next;
        }

        private void LeaveIfForeign(BuildPlan next, PlanStep s)
        {
            int id = _w.GetBlock(s.Target);
            bool ours = next.Placed.Contains(s.Target) || _w.Placed.IsPlaced(s.Target, _w.Dimension, id);
            if (!_foreign.Contains(s.Target) && (ours || !_voxel.Blocks.ContainsKey(s.Target))) return;
            if (_foreign.Add(s.Target) || !_report.Deviations.Any(d => d.Contains(_voxel.Local(s.Target))))
                _report.Deviations.Add($"{_voxel.Local(s.Target)} holds {BuildBlocks.Name(id, _w.GetMeta(s.Target))} (not mine: someone built there)");
            s.Status = StepStatus.Skipped;
            s.Note = "someone built there";
        }

        private HashSet<int> Included(BuildPlan p)
        {
            // Gate-only: the steps up to the last gate block (obstructions, the gate blocks and what they stand on).
            if (_untilGate && _layer == null) return Enumerable.Range(0, p.Steps.Where(s => s.Gate).Select(s => s.Index + 1).DefaultIfEmpty(0).Max()).ToHashSet();
            if (_layer is not { } ly) return Enumerable.Range(0, p.Steps.Count).ToHashSet();
            var set = LayerWithDeps(p, ly);
            bool top = ly >= _voxel.Blueprint.H - 1;
            foreach (var s in p.Steps.Where(s => s.Action is StepAction.RemoveScaffold or StepAction.PickupWater))
                if (top || _rec.Scaffold.Contains(s.Target)) set.Add(s.Index);
            return set;
        }

        /// <summary>A layer's placements and every step they depend on, transitively.</summary>
        private HashSet<int> LayerWithDeps(BuildPlan p, int ly)
        {
            var set = new HashSet<int>();
            var q = new Queue<int>(p.Steps.Where(s => s.Action == StepAction.Place && s.Target.Y - _voxel.Origin.Y == ly).Select(s => s.Index));
            while (q.Count > 0)
            {
                int k = q.Dequeue();
                if (!set.Add(k)) continue;
                foreach (var d in p.Steps[k].Deps) if (d >= 0 && d < p.Steps.Count) q.Enqueue(d);
            }
            return set;
        }

        private static void Skip(PlanStep s, string why)
        {
            s.Status = StepStatus.Skipped;
            s.Note = why;
        }

        private void Checkpoint()
        {
            _x.Plans?.Save(_bp);
            Tally();
            _x.Store0?.SaveBuild(_rec);
        }

        // The record counts one unit on both sides: the part's design blocks, and how many of them stand right now (never
        // scaffold, gate rounds or re-placements; live 10-28 "20/14 placed" mixed this session's steps with every placement ever).
        private void Tally()
        {
            var cells = _voxel.Blocks.Values.Where(b => !b.UpperDoor && _vpart.Cells.Contains(b.Pos)).ToList();
            _rec.Total = cells.Count;
            _rec.Placed = cells.Count(b => _w.IsLoaded(b.Pos.X, b.Pos.Z) && DesignChecks.SameState(b, _w.GetBlock(b.Pos), _w.GetMeta(b.Pos)));
        }

        // A cell with no step and no slicer reason: its item is held back (a shortfall), or the run stopped before planning it.
        private string? Unplanned(BlockPos p)
        {
            if (_voxel.Blocks.TryGetValue(p, out var b) && _short.Keys.Any(k => (k.Item1, k.Item2) == BuildBlocks.ItemFor(b.Id, b.Meta)))
                return "missing " + Name(BuildBlocks.ItemFor(b.Id, b.Meta).Item, BuildBlocks.ItemFor(b.Id, b.Meta).Damage);
            return _report.Stopped is { } st ? "not reached: " + st : null;
        }

        // The right block facing the wrong way (a stair or door turned): it's ours, so take it back and place it again once.
        private bool Turned(PlanStep s)
        {
            if (!_voxel.Blocks.TryGetValue(s.Target, out var want) || _w.GetBlock(s.Target) != want.Id) return false;
            int meta = _w.GetMeta(s.Target);
            return want.Kind == BuildKind.Stairs && (meta & 3) != (want.Meta & 3) || want.Kind == BuildKind.Door && (meta & 1) != (want.Meta & 1);
        }

        // Live 10-30: two oak stairs of the arch bridge faced east and north instead of north and south, and the rerun counted
        // them as done ("rerun placed 0, nothing missing"). One retry: dig it (our own block) and place it again with the plan's yaw.
        private async Task<bool> RefaceAsync(PlanStep s, int item, int dmg)
        {
            var dug = await _body.DigHereAsync(s.Target, _ct).ConfigureAwait(false);
            if (!dug.Ok) return false;
            var again = await _body.PlaceAsync(new BuildStep(StepKind.Place, s.Target, s.Id, s.Meta, null, s.Face, s.HiddenFace, s.Yaw), item, dmg, _ct).ConfigureAwait(false);
            if (!again.Ok) { _report.Deviations.Add($"{BuildBlocks.Name(s.Id, s.Meta)} at {_voxel.Local(s.Target)}: re-placing it facing right failed: {Short(again.Message)}"); return false; }
            bool ok = Verify(s);
            if (ok) _report.Deviations.RemoveAll(d => d.Contains($"at {_voxel.Local(s.Target)} go up") || d.Contains($"door at {_voxel.Local(s.Target)} turned"));
            return ok;
        }

        private bool Verify(PlanStep s)
        {
            if (!_voxel.Blocks.TryGetValue(s.Target, out var want)) return true;
            if (VerifyIssue(s.Target, want) is not { } issue) return true;
            _report.Deviations.Add(issue);
            return false;
        }

        /// <summary>Why the block standing at <paramref name="p"/> isn't the planned one; null = it is.</summary>
        private string? VerifyIssue(BlockPos p, PlannedBlock want)
        {
            int id = _w.GetBlock(p), meta = _w.GetMeta(p);
            // A variant is part of the block: a stone slab (44:0) in a cobblestone-slab (44:3) cell is wrong, not done.
            if (id != want.Id || !DesignChecks.SameState(want, id, meta))
                return $"{_voxel.Local(p)} is {BuildBlocks.Name(id, meta)}, planned {BuildBlocks.Name(want.Id, want.Meta)}";
            if (want.Kind == BuildKind.Stairs && (meta & 3) != (want.Meta & 3))
                return $"stairs at {_voxel.Local(p)} go up {BuildBlocks.StairsAscend(meta).Name()}, planned {BuildBlocks.StairsAscend(want.Meta).Name()}";
            if (want.Kind == BuildKind.Door && (meta & 1) != (want.Meta & 1))
                return $"door at {_voxel.Local(p)} turned across the wall";
            return null;
        }

        private void CheckLayer(int y)
        {
            var cells = _voxel.Blocks.Values.Where(b => b.Pos.Y == y && _vpart.Cells.Contains(b.Pos)).ToList();
            var miss = cells.Where(b => !DesignChecks.SameState(b, _w.GetBlock(b.Pos), _w.GetMeta(b.Pos))).ToList();
            var line = string.Create(CultureInfo.InvariantCulture, $"layer {y - _voxel.Origin.Y}: {cells.Count - miss.Count}/{cells.Count}") +
                       (miss.Count > 0 ? $", missing {string.Join(" ", miss.Take(3).Select(b => _voxel.Local(b.Pos)))}" : "");
            Say(line);
            _x.Log0?.LogInformation("blueprint #{Id} {Part}: {Line}", _id, _part.Label, line);
        }

        // ---------------------------------------------------------------- the end

        private BuildReport Finish()
        {
            CollectMissing();
            bool partDone = _report.Stopped == null && _report.Missing.Count == 0 && _bp.ScaffoldUp.Count == 0 && _layer == null;
            if (_report.GatePassed && !partDone) return FinishGatePassed();
            _rec.State = RecordState(partDone);
            _rec.Scaffold = _bp.ScaffoldUp.ToList();
            Tally();
            _rec.Note = _report.Text(_part.Label);
            if (_bp.State != "undone") _bp.State = PlanState();
            _bp.Note = _report.Stopped;
            _x.Plans?.Save(_bp);
            _x.Store0?.SaveBuild(_rec);
            _report.Plan = _bp;
            if (partDone || _report.Stopped != null) ReleaseClaim();
            return _report;
        }

        // A finished part is done; anything else stays "building" (resumable: unplaced cells, leftover scaffold, the gate).
        private string RecordState(bool partDone) =>
            _bp.State == "undone" ? "undone" : partDone || (_layer == null && _report.Stopped == null && _report.Missing.Count == 0) ? "done" : "building";

        private string PlanState() => _rec.State == "done" ? "done" : _report.AtGate ? "gate" : "paused";

        /// <summary>The part's cells (this layer's, for a layer run) that aren't standing as planned, and why the cells left are left.</summary>
        private void CollectMissing()
        {
            foreach (var b in _voxel.Blocks.Values.Where(b => _vpart.Cells.Contains(b.Pos) && (_layer is not { } ly || b.Pos.Y - _voxel.Origin.Y == ly)))
                if (_w.IsLoaded(b.Pos.X, b.Pos.Z) && !DesignChecks.SameState(b, _w.GetBlock(b.Pos), _w.GetMeta(b.Pos))) _report.Missing.Add(b.Pos);
            if (_report.Missing.Count == 0 && !_bp.Steps.Any(s => s.Status == StepStatus.Skipped)) { _report.Skips = null; return; }
            var items = BuildSkips.Of(_bp, _report.Missing, Unplanned);
            // The design's whole footprint (not just this part's), the cells still empty under it as the world is now.
            _report.Skips = BuildSkips.WithFloor(BuildSkips.Line(items), BuildSkips.FloorHint(items, BuildSkips.Footprint(_voxel.Blocks.Keys), _w));
        }

        // The lanes carry on from here: the plan stays "building", the area isn't held for one body.
        private BuildReport FinishGatePassed()
        {
            _rec.State = "building";
            Tally();
            _rec.Note = _report.Text(_part.Label + " (gate)");
            _bp.State = "building";
            _x.Plans?.Save(_bp);
            _x.Store0?.SaveBuild(_rec);
            _report.Plan = _bp;
            ReleaseClaim();
            return _report;
        }

        private void ReleaseClaim()
        {
            if (_x.Claims0 == null) return;
            try { _x.Claims0.ReleaseAsync(_part.Claim, CancellationToken.None).GetAwaiter().GetResult(); } catch (Exception) { }
        }
    }
}
