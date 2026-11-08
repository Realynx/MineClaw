using MineClaw.World;

namespace MineClaw.Bot.Blueprints;

public sealed partial class ConstructionExecutor
{
    private sealed partial class BuildRun
    {
        private MineOreWork Ore => new(_voxel, _bp, _body, () => _x.HoldNow(_id), Checkpoint,
            _x.Claims0, at => _foreign.Contains(at));

        private void ExposeOre(BlockPos dug, int family) => Ore.Expose(dug, family);

        private async Task ResumeOreAsync()
        {
            if (!_voxel.Blueprint.Mine || _untilGate) return;
            Ore.Resume(_vpart.Cells.Where(at => _layer == null || at.Y - _voxel.Origin.Y == _layer));
            await DrainOreAsync().ConfigureAwait(false);
        }

        private async Task DrainOreAsync()
        {
            _report.Cleared += await Ore.DrainAsync(_ct).ConfigureAwait(false);
            if (_w.Inventory.EmptySlotCount() == 0 && _bp.OreFrontier?.Count > 0)
                _report.Stopped = "inventory full; exposed ore saved for this mine";
        }
    }
}
