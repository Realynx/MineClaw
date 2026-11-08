using MineClaw.Data;

namespace MineClaw.Bot.GameTools;

/// <summary>Chest transfers can name an exact variant even when its ordinary name means the whole item family.</summary>
internal static class TransferItemSelector
{
    internal static (ItemInfo Item, int Damage)? Resolve(string name)
    {
        var parts = name.Split(':');
        if (parts.Length == 2 && int.TryParse(parts[0], out var id) && int.TryParse(parts[1], out var meta))
            return Items.Exists(id) && meta is >= 0 and <= short.MaxValue ? (Items.Get(id), meta) : null;
        return Items.ByNameWithDamage(name);
    }
}
