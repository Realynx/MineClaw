using MineClaw.Data;
using MineClaw.Protocol.Packets;

namespace MineClaw.Protocol.Release1122;

/// <summary>
/// Multi-version P2 of the 1.12.2 client connection: inventory windows, digging, placing and using, item entities and the
/// entity traffic the pickup logic and the observers read. Wire formats from the decompiled packet classes
/// (<c>readPacketData</c>/<c>writePacketData</c>), cross-checked with minecraft-data's protocol.json.
/// </summary>
public sealed partial class Release1122Connection
{
    private readonly Dictionary<int, Tracked> _entities = new();
    // Open container windows' canonical types (furnace properties are renumbered).
    private readonly Dictionary<int, byte> _windowTypes = new();

    private void ResetEntities()
    {
        _entities.Clear();
        _passengers.Clear();
        _windowTypes.Clear();
    }

    private IPacket? TranslatePlay2(int id, ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        switch (id)
        {
            // ---- windows ----
            case Ids1122.OpenWindow:
            {
                int wid = r.ReadUByte();
                var type = r.ReadString(32);
                var title = r.ReadString();
                int slots = r.ReadUByte();
                var (ct, n) = Codec1122.WindowType(type, slots);
                _windowTypes[wid] = ct;
                return new OpenWindowPacket { WindowId = (sbyte)wid, InventoryType = ct, Title = ChatComponent.ToPlain(title, Lang), SlotCount = (byte)Math.Min(n, 255) };
            }
            case Ids1122.CloseWindow:
            {
                int wid = r.ReadUByte();
                _windowTypes.Remove(wid);
                return new CloseWindowPacket { WindowId = (sbyte)wid };
            }
            case Ids1122.WindowItems:
            {
                int wid = r.ReadUByte();
                int n = r.ReadShort();
                if (n < 0 || n > r.Remaining) throw new ProtocolException($"window items count {n} out of range");
                var items = new ItemStack[n];
                for (int i = 0; i < n; i++) items[i] = Codec1122.ReadSlot(ref r);
                return new WindowItemsPacket { WindowId = (sbyte)wid, Items = items };
            }
            case Ids1122.SetSlot:
            {
                int wid = r.ReadSByte();
                int slot = r.ReadShort();
                var item = Codec1122.ReadSlot(ref r);
                if (wid == -2)
                {
                    // An InventoryPlayer index (handleSetSlot): the player window's slot.
                    wid = 0;
                    slot = Codec1122.InventoryIndexToWindowSlot(slot);
                    if (slot < 0) return null;
                }
                return new SetSlotPacket { WindowId = (sbyte)wid, Slot = (short)slot, Item = item };
            }
            case Ids1122.WindowProperty:
            {
                int wid = r.ReadUByte();
                int prop = r.ReadShort();
                short value = r.ReadShort();
                if (_windowTypes.TryGetValue(wid, out byte t) && t == 2)
                {
                    prop = Codec1122.FurnacePropertyToCanonical(prop);
                    if (prop < 0) return null;
                }
                return new UpdateProgressBarPacket { WindowId = (sbyte)wid, ProgressBar = (short)prop, Value = value };
            }
            case Ids1122.ConfirmTransaction:
                return new TransactionPacket { WindowId = r.ReadSByte(), ActionNumber = r.ReadShort(), Accepted = r.ReadBool() };

            // ---- blocks ----
            case Ids1122.BlockBreakAnim:
            {
                int eid = r.ReadVarInt();
                var (x, y, z) = r.ReadPosition();
                int stage = r.ReadSByte();
                return new BlockBreakAnimationMessage { EntityId = eid, X = x, Y = y, Z = z, Stage = stage };
            }
            case Ids1122.UpdateBlockEntity:
            {
                var (x, y, z) = r.ReadPosition();
                int action = r.ReadUByte();
                var tag = Nbt.ReadRoot(ref r);
                if (action != Codec1122.SignAction || tag is null || Codec1122.SignLines(tag, Lang) is not { } lines) return null;
                return new UpdateSignPacket { X = x, Y = (short)y, Z = z, Lines = lines };
            }

            default:
                return TranslateEntities(id, body); // P4: every entity kind, metadata, effects, attributes, passengers, experience
        }
    }

    // ------------------------------------------------------------------ sending (P2)

    private bool SendPlay2(IPacket packet)
    {
        switch (packet)
        {
            case WindowClickPacket c:
                SendWire(WireWriter.ForPacket(Ids1122.ClickWindow).WriteUByte((byte)c.WindowId).WriteShort(c.Slot).WriteSByte(c.EffectiveButton)
                    .WriteShort(c.ActionNumber).WriteVarInt(c.EffectiveMode).WriteSlot(c.ClickedItem), packet);
                return true;
            case TransactionPacket t:
                SendWire(WireWriter.ForPacket(Ids1122.ConfirmTransactionOut).WriteSByte(t.WindowId).WriteShort(t.ActionNumber).WriteBool(t.Accepted), packet);
                return true;
            case CloseWindowPacket cw:
                _windowTypes.Remove((byte)cw.WindowId);
                SendWire(WireWriter.ForPacket(Ids1122.CloseWindowOut).WriteUByte((byte)cw.WindowId), packet);
                return true;
            case PlayerDiggingPacket d:
            {
                // Drop / release / swap carry BlockPos.ORIGIN and DOWN (PlayerControllerMP, EntityPlayerSP.dropItem).
                bool atBlock = d.Status is PlayerDiggingPacket.StatusStarted or PlayerDiggingPacket.StatusCancelled or PlayerDiggingPacket.StatusFinished;
                var w = WireWriter.ForPacket(Ids1122.PlayerDigging).WriteVarInt(d.Status);
                if (atBlock) w.WritePosition(d.X, d.Y, d.Z).WriteUByte(d.Face > 5 ? (byte)0 : d.Face);
                else w.WritePosition(0, 0, 0).WriteUByte(0);
                SendWire(w, packet);
                return true;
            }
            case PlayerBlockPlacementPacket p when p.Direction == PlayerBlockPlacementPacket.NoDirection:
                SendWire(WireWriter.ForPacket(Ids1122.UseItem).WriteVarInt(p.Hand), packet); // CPacketPlayerTryUseItem
                return true;
            case PlayerBlockPlacementPacket p:
            {
                var (cx, cy, cz) = p.EffectiveCursor;
                SendWire(WireWriter.ForPacket(Ids1122.PlaceBlock).WritePosition(p.X, p.Y, p.Z).WriteVarInt(p.Direction).WriteVarInt(p.Hand)
                    .WriteFloat(cx).WriteFloat(cy).WriteFloat(cz), packet);
                return true;
            }
            case UseEntityPacket u:
            {
                // CPacketUseEntity.Action: 0 interact, 1 attack, 2 interact at (+ hit vector); interact and interact-at name a hand.
                var w = WireWriter.ForPacket(Ids1122.UseEntity).WriteVarInt(u.TargetEntityId);
                if (u.LeftClick) w.WriteVarInt(1);
                else if (u.InteractAt is { } at) w.WriteVarInt(2).WriteFloat(at.X).WriteFloat(at.Y).WriteFloat(at.Z).WriteVarInt(u.Hand);
                else w.WriteVarInt(0).WriteVarInt(u.Hand);
                SendWire(w, packet);
                return true;
            }
            case UpdateSignPacket sign:
            {
                var w = WireWriter.ForPacket(Ids1122.UpdateSign).WritePosition(sign.X, sign.Y, sign.Z);
                for (int i = 0; i < 4; i++) w.WriteString(i < sign.Lines.Length ? sign.Lines[i] : "", 384);
                SendWire(w, packet);
                return true;
            }
            default:
                return false;
        }
    }
}
