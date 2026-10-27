using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.World;

namespace MineClaw.Bot.WorldIndex;

/// <summary>
/// Per-entity client signals the activity tracker listens to. When a source can't raise some of them the tracker
/// falls back to polling <see cref="EntityTracker"/> state (held item, vehicle, crouch, item entities disappearing).
/// </summary>
public interface IPlayerEventSource
{
    /// <summary>0x12 (entity id, animation): 1 = arm swing, 2 = hurt.</summary>
    event Action<int, int>? EntityAnimated;
    /// <summary>0x36 (block, data1, data2): chest lids on servers that send them, note blocks, pistons.</summary>
    event Action<BlockPos, int, int>? BlockAction;
    event Action<string>? ChatReceived;
    event Action<string>? PlayerJoined;
    event Action<string>? PlayerLeft;

    /// <summary>0x16 Collect Item (collected entity id, collector entity id).</summary>
    event Action<int, int>? ItemCollected;
    /// <summary>0x05 Entity Equipment (entity id, slot 0 held / 1-4 armor, item id or -1, damage).</summary>
    event Action<int, int, int, int>? EquipmentChanged;
    /// <summary>0x26 Entity Status (entity id, status: 2 hurt, 3 dead).</summary>
    event Action<int, int>? EntityStatus;
    /// <summary>0x11 Use Bed (entity id, bed head).</summary>
    event Action<int, BlockPos>? UsedBed;
    /// <summary>0x27 Attach Entity (entity id, vehicle id or -1).</summary>
    event Action<int, int>? EntityAttached;
    /// <summary>0x82 Update Sign (position, 4 lines). Signs are also polled via the indexer.</summary>
    event Action<BlockPos, string[]>? SignUpdated;
}

/// <summary>
/// Forwards client events: the IGameClient ones always, the per-entity ones (0x16/0x05/0x26/0x11/0x27/0x82) from the
/// real <see cref="MineClaw.Client.MinecraftClient"/> when the game client wraps one.
/// </summary>
public sealed class GameClientPlayerEvents : IPlayerEventSource, IDisposable
{
    private readonly IGameClient _client;
    private readonly List<Action> _unsub = new();

    public GameClientPlayerEvents(IGameClient client)
    {
        _client = client;
        if (client is not MinecraftGameClient { Inner: var c }) return;
        Action<int, int, MineClaw.Data.ItemStack?> collected = (collector, item, _) => ItemCollected?.Invoke(item, collector);
        Action<int, int, int, int> equip = (e, slot, item, dmg) => EquipmentChanged?.Invoke(e, slot, item, dmg);
        Action<int, int> status = (e, st) => EntityStatus?.Invoke(e, st);
        Action<int, int, int, int> bed = (e, x, y, z) => UsedBed?.Invoke(e, new BlockPos(x, y, z));
        Action<int, int> attach = (e, v) => EntityAttached?.Invoke(e, v);
        Action<int, int, int, string[]> sign = (x, y, z, lines) => SignUpdated?.Invoke(new BlockPos(x, y, z), lines);
        c.ItemCollected += collected;
        c.EquipmentChanged += equip;
        c.EntityStatus += status;
        c.EntityUsedBed += bed;
        c.EntityAttached += attach;
        c.SignUpdated += sign;
        _unsub.Add(() =>
        {
            c.ItemCollected -= collected;
            c.EquipmentChanged -= equip;
            c.EntityStatus -= status;
            c.EntityUsedBed -= bed;
            c.EntityAttached -= attach;
            c.SignUpdated -= sign;
        });
    }

    public event Action<int, int>? EntityAnimated { add => _client.EntityAnimated += value; remove => _client.EntityAnimated -= value; }
    public event Action<BlockPos, int, int>? BlockAction { add => _client.BlockAction += value; remove => _client.BlockAction -= value; }
    public event Action<string>? ChatReceived { add => _client.ChatReceived += value; remove => _client.ChatReceived -= value; }
    public event Action<string>? PlayerJoined { add => _client.PlayerJoined += value; remove => _client.PlayerJoined -= value; }
    public event Action<string>? PlayerLeft { add => _client.PlayerLeft += value; remove => _client.PlayerLeft -= value; }
    public event Action<int, int>? ItemCollected;
    public event Action<int, int, int, int>? EquipmentChanged;
    public event Action<int, int>? EntityStatus;
    public event Action<int, BlockPos>? UsedBed;
    public event Action<int, int>? EntityAttached;
    public event Action<BlockPos, string[]>? SignUpdated;

    public void Dispose()
    {
        foreach (var u in _unsub) u();
        _unsub.Clear();
    }
}

/// <summary>Test/replay source: raise anything by hand.</summary>
public sealed class ManualPlayerEvents : IPlayerEventSource
{
    public event Action<int, int>? EntityAnimated;
    public event Action<BlockPos, int, int>? BlockAction;
    public event Action<string>? ChatReceived;
    public event Action<string>? PlayerJoined;
    public event Action<string>? PlayerLeft;
    public event Action<int, int>? ItemCollected;
    public event Action<int, int, int, int>? EquipmentChanged;
    public event Action<int, int>? EntityStatus;
    public event Action<int, BlockPos>? UsedBed;
    public event Action<int, int>? EntityAttached;
    public event Action<BlockPos, string[]>? SignUpdated;

    public void Swing(int entityId) => EntityAnimated?.Invoke(entityId, 1);
    public void Hurt(int entityId) => EntityAnimated?.Invoke(entityId, 2);
    public void Action(BlockPos p, int a, int b) => BlockAction?.Invoke(p, a, b);
    public void Chat(string line) => ChatReceived?.Invoke(line);
    public void Join(string name) => PlayerJoined?.Invoke(name);
    public void Leave(string name) => PlayerLeft?.Invoke(name);
    public void Collect(int item, int collector) => ItemCollected?.Invoke(item, collector);
    public void Equip(int entity, int slot, int item, int damage = 0) => EquipmentChanged?.Invoke(entity, slot, item, damage);
    public void Status(int entity, int status) => EntityStatus?.Invoke(entity, status);
    public void Bed(int entity, BlockPos p) => UsedBed?.Invoke(entity, p);
    public void Attach(int entity, int vehicle) => EntityAttached?.Invoke(entity, vehicle);
    public void Sign(BlockPos p, string[] lines) => SignUpdated?.Invoke(p, lines);
}
