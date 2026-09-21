using System;
using Vintagestory.API.Common;
using Vintagestory.Common;

#nullable disable

namespace CompostBin
{
    // Use native execution after validating the entire request. Both the block
    // packet route and native inventory packets reach these overloads.
    internal class CompostInventoryNetworkUtil : InventoryNetworkUtil
    {
        private readonly BECompostBin owner;
        private long nextWarning;
        internal const int MaxPacketBytes = 4096;

        public CompostInventoryNetworkUtil(InventoryBase inventory, BECompostBin owner, ICoreAPI api)
            : base(inventory, api)
        {
            this.owner = owner;
        }

        public override void HandleClientPacket(IPlayer player, int packetId, byte[] data)
        {
            if (!owner.CanPlayerAccess(player, true))
                return;
            if (data == null || data.Length == 0 || data.Length > MaxPacketBytes
                || !ValidWirePacket(data, packetId))
            {
                Reject(packetId);
                return;
            }

            Packet_Client packet;
            try
            {
                packet = Packet_ClientSerializer.DeserializeBuffer(data, data.Length, new Packet_Client());
            }
            catch (Exception)
            {
                // The game's generated decoder also throws plain Exception.
                // This catch surrounds decoding only, before any inventory change.
                Reject(packetId);
                return;
            }
            HandleClientPacket(player, packetId, packet);
        }

        public override void HandleClientPacket(IPlayer player, int packetId, Packet_Client packet)
        {
            if (!owner.CanPlayerAccess(player, true))
                return;
            if (!ValidRequest(player, packetId, packet))
            {
                Reject(packetId);
                return;
            }
            base.HandleClientPacket(player, packetId, packet);
            owner.Api.World.BlockAccessor?.GetChunkAtBlockPos(owner.Pos)?.MarkModified();
        }

        private bool ValidRequest(IPlayer player, int id, Packet_Client packet)
        {
            if (packet == null || packet.Id != id)
                return false;

            if (id == 7)
            {
                var action = packet.ActivateInventorySlot;
                return action != null && packet.MoveItemstack == null && packet.Flipitemstacks == null
                    && action.TargetInventoryId == inv.InventoryID
                    && ValidSlot(player, action.TargetInventoryId, action.TargetSlot)
                    && ValidSlot(player, "mouse-" + player.WorldData.PlayerUID, 0)
                    && action.TabIndex == 0 && action.TargetLastChanged >= 0
                    && action.Dir >= -1 && action.Dir <= 1
                    && ValidControls(action.MouseButton, action.Modifiers, action.Priority);
            }
            if (id == 8)
            {
                var move = packet.MoveItemstack;
                return move != null && packet.ActivateInventorySlot == null && packet.Flipitemstacks == null
                    && InvolvesBin(move.SourceInventoryId, move.TargetInventoryId)
                    && ValidSlot(player, move.SourceInventoryId, move.SourceSlot)
                    && ValidSlot(player, move.TargetInventoryId, move.TargetSlot)
                    && move.Quantity > 0 && move.TabIndex == 0
                    && move.SourceLastChanged >= 0 && move.TargetLastChanged >= 0
                    && ValidControls(move.MouseButton, move.Modifiers, move.Priority);
            }
            if (id == 9)
            {
                var flip = packet.Flipitemstacks;
                return flip != null && packet.ActivateInventorySlot == null && packet.MoveItemstack == null
                    && InvolvesBin(flip.SourceInventoryId, flip.TargetInventoryId)
                    && ValidSlot(player, flip.SourceInventoryId, flip.SourceSlot, flip.SourceTabIndex)
                    && ValidSlot(player, flip.TargetInventoryId, flip.TargetSlot, flip.TargetTabIndex)
                    && flip.SourceLastChanged >= 0 && flip.TargetLastChanged >= 0;
            }
            return false;
        }

        private bool InvolvesBin(string source, string target)
        {
            return source == inv.InventoryID || target == inv.InventoryID;
        }

        private static bool ValidSlot(IPlayer player, string id, int slot, int? tabIndex = null)
        {
            // Never resolve arbitrary world inventory IDs from an untrusted packet.
            var inventories = player.InventoryManager.Inventories;
            if (string.IsNullOrEmpty(id) || id.Length > 256 || inventories == null
                || !inventories.TryGetValue(id, out var inventory) || inventory is not InventoryBase other
                || slot < 0 || !other.CanPlayerModify(player, player.Entity.Pos))
                return false;

            if (other is InventoryPlayerCreative creative)
            {
                if (player.WorldData.CurrentGameMode != EnumGameMode.Creative)
                    return false;
                // Flip changes both tabs; move uses the peer's current tab. Check
                // the requested tab without mutating it during validation.
                IInventory tab = creative.CurrentTab?.Inventory;
                if (tabIndex.HasValue)
                {
                    tab = null;
                    foreach (var candidate in creative.CreativeTabs.Tabs)
                        if (candidate.Index == tabIndex.Value)
                            tab = candidate.Inventory;
                }
                return tab != null && (slot == 99999 || slot < tab.Count);
            }
            return (!tabIndex.HasValue || tabIndex.Value == 0) && slot < other.Count;
        }

        private static bool ValidControls(int button, int modifiers, int priority)
        {
            return Enum.IsDefined(typeof(EnumMouseButton), button)
                && (modifiers & ~7) == 0 && priority >= 0 && priority <= 2;
        }

        private void Reject(int packetId)
        {
            long now = Environment.TickCount64;
            if (now < nextWarning)
                return;
            nextWarning = now + 10000;
            owner.Api?.Logger?.Warning("[Compost Bin] Rejected invalid inventory packet {0} at {1} (warnings limited to one per bin per 10 seconds).",
                packetId, owner.Pos);
        }

        // Preflight the 1.22 inventory protobuf schema before the native decoder
        // can allocate strings or traverse nested messages. No unknown messages,
        // duplicate fields, groups, truncated lengths or unbounded varints.
        private static bool ValidWirePacket(byte[] data, int packetId)
        {
            ulong bodyKey = packetId switch
            {
                7 => 154,
                8 => 114,
                9 => 122,
                _ => 0
            };
            if (bodyKey == 0)
                return false;
            int offset = 0;
            bool seenId = false, seenBody = false;
            while (offset < data.Length)
            {
                // Native CitoMemoryStream.ToArray returns its backing buffer,
                // including unused zero-filled capacity after the message.
                if (data[offset] == 0 && seenId && seenBody)
                {
                    while (offset < data.Length)
                        if (data[offset++] != 0)
                            return false;
                    return true;
                }
                if (!ReadVarint(data, ref offset, data.Length, out ulong key))
                    return false;
                if (key == 8 && !seenId)
                {
                    if (!ReadVarint(data, ref offset, data.Length, out ulong id) || id != (ulong)packetId)
                        return false;
                    seenId = true;
                }
                else if (key == bodyKey && !seenBody)
                {
                    if (!ReadVarint(data, ref offset, data.Length, out ulong length) || length > (ulong)(data.Length - offset))
                        return false;
                    int end = offset + (int)length;
                    if (!ValidBody(data, ref offset, end, packetId))
                        return false;
                    seenBody = true;
                }
                else
                    return false;
            }
            return seenId && seenBody;
        }

        private static bool ValidBody(byte[] data, ref int offset, int end, int packetId)
        {
            int seen = 0;
            while (offset < end)
            {
                if (!ReadVarint(data, ref offset, end, out ulong key) || key > 88)
                    return false;
                int field = (int)(key >> 3);
                int maxField = packetId == 8 ? 11 : 8;
                if (field < 1 || field > maxField || (seen & (1 << field)) != 0)
                    return false;
                seen |= 1 << field;
                bool isString = field == 2 || (packetId != 7 && field == 1);
                if ((key & 7) != (isString ? 2ul : 0ul)
                    || !ReadVarint(data, ref offset, end, out ulong value))
                    return false;
                if (isString)
                {
                    if (value == 0 || value > 256 || value > (ulong)(end - offset))
                        return false;
                    offset += (int)value;
                }
                else
                {
                    bool isTimestamp = packetId switch
                    {
                        7 => field == 5,
                        8 => field == 6 || field == 7,
                        _ => field == 5 || field == 6
                    };
                    if (!isTimestamp && value > uint.MaxValue)
                        return false;
                }
            }
            return offset == end;
        }

        private static bool ReadVarint(byte[] data, ref int offset, int end, out ulong value)
        {
            value = 0;
            for (int i = 0; i < 10 && offset < end; i++)
            {
                byte part = data[offset++];
                if (i == 9 && part > 1)
                    return false;
                value |= (ulong)(part & 127) << (7 * i);
                if ((part & 128) == 0)
                    return i == 0 || part != 0;
            }
            return false;
        }
    }
}
