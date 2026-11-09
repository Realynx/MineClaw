using MineClaw.Bot;
using MineClaw.Bot.Bodies;
using MineClaw.Client;
using MineClaw.Hive.Story;
using MineClaw.Scripting;
using MineClaw.World;

namespace MineClaw.App.Supervisor;

/// <summary>Human ownership pauses only this pawn. Its role, schedule, draft state and colony options
/// are untouched; the director replans the interrupted job when the browser releases it.</summary>
public sealed class LivePawnControlBody(Body body, StoryColony colony) : IPawnControlBody
{
    private MinecraftClient Client => body.Client ?? throw new InvalidOperationException("No game client for this pawn.");
    private bool _begun, _restartScript;
    private StoryHumanControlClaim? _claim;
    public string Name => body.Name;
    public bool Online => Client.IsPlaying && (_begun
        ? Client.HumanControlled && _claim is { Active: true } && colony.Meta.Active
        : colony.CanHumanControl(Name));

    public static IPawnControlBody? Resolve(BotHost bot, StoryColonies? colonies, string name)
    {
        if (bot.Bodies?.Find(name) is not Body { IsMain: false, Client: not null } body) return null;
        var colony = colonies?.All.FirstOrDefault(c => c.Meta.Active && c.HasPawn(body.Name));
        return colony == null ? null : new LivePawnControlBody(body, colony);
    }

    public PawnControlState State()
    {
        var world = Client.World;
        var player = world.Player;
        var p = player.Position;
        return new(p.X, p.Y, p.Z, player.Physics.Yaw, player.Physics.Pitch, world.Dimension,
            player.Health, player.Food, player.HeldSlot, player.Physics.OnGround, world.Inventory.ActiveWindow.Id);
    }

    public PawnControlInventory Inventory()
    {
        var (window, items, cursor) = Client.World.Inventory.WindowSnapshot();
        var slots = items.Select((s, i) => (s, i)).Where(x => !x.s.IsEmpty)
            .Select(x => new PawnControlSlot(x.i, x.s.Id, x.s.Damage, x.s.Count)).ToArray();
        return new(window.Id, window.Kind ?? window.Type.ToString().ToLowerInvariant(), window.Title, window.SlotCount,
            window.ContainerSlots, window.MainStart, window.HotbarStart, slots,
            cursor.IsEmpty ? null : new PawnControlItem(cursor.Id, cursor.Damage, cursor.Count));
    }

    public void Begin()
    {
        if (_begun) return;
        if (!Client.IsPlaying || colony.BeginHumanControl(Name) is not { } claim) throw new PawnControlException(409, "The pawn is no longer available.");
        _claim = claim;
        _begun = true;
        _restartScript = body.Scripts?.Current is not { Status: ScriptStatus.Paused };
        // End the lane so its old in-flight primitive cannot replay after the lease ends. The
        // director has released its reservations and will choose a fresh job on return.
        if (_restartScript) body.Scripts?.Cancel("controlled by player");
        body.Activities.Cancel("controlled by player");
        Client.BeginHumanControl();
    }

    public void Input(PawnControlInput input) => Client.SetHumanInput(new MoveInput(input.Forward, input.Strafe, input.Jump,
        input.Sneak, input.Sprint, input.Yaw, input.Pitch), input.Hotbar);

    public Task<ActionResult> ActionAsync(PawnControlAction action, CancellationToken ct, CancellationToken holdRelease = default) => Client.HumanActionAsync(action.Kind,
        action.Target is { } p ? new BlockPos(p.X, p.Y, p.Z) : null, action.EntityId, action.Face, action.WindowId,
        action.Slot, action.Button ?? 0, action.Shift, action.WholeStack, ct, holdRelease);

    public void End()
    {
        if (!_begun) return;
        _begun = false;
        try { Client.EndHumanControl(); }
        finally
        {
            if (_claim is { } claim)
            {
                claim.Revoke();
                // Colony reconciliation can hold its lock for seconds. It must never hold the global browser
                // session lock or delay this client's key release. Claim identity protects a later takeover.
                _ = Task.Run(() => colony.CompleteHumanControl(claim, () =>
                {
                    if (_restartScript && Client.IsPlaying) colony.Host.StartPawn(Name, colony.Id);
                }));
            }
        }
    }
}
