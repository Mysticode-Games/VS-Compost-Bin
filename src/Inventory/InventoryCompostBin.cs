using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

#nullable disable

namespace CompostBin
{
    public class InventoryCompostBin : InventoryGeneric
    {
        private readonly BECompostBin owner;

        public InventoryCompostBin(BECompostBin owner)
            : base(BECompostBin.SlotCount, null, null,
                (id, inventory) => new ItemSlotCompostBin(inventory, owner))
        {
            this.owner = owner;
            // LateInitialize supplies the API without replacing this utility.
            InvNetworkUtil = new CompostInventoryNetworkUtil(this, owner, null);
        }

        public override bool CanPlayerAccess(IPlayer player, EntityPos position)
        {
            return Api?.Side == EnumAppSide.Server
                ? owner.CanPlayerAccess(player, false)
                : base.CanPlayerAccess(player, position);
        }

        public override bool CanPlayerModify(IPlayer player, EntityPos position)
        {
            return Api?.Side == EnumAppSide.Server
                ? owner.CanPlayerAccess(player, true)
                : base.CanPlayerModify(player, position);
        }

        public override object Open(IPlayer player)
        {
            if (player == null || (Api?.Side == EnumAppSide.Server && !owner.CanPlayerAccess(player, false)))
                return null;

            return base.Open(player);
        }

        internal bool HasOpenSession(IPlayer player)
        {
            return player != null && base.HasOpened(player);
        }

        public override bool HasOpened(IPlayer player)
        {
            return HasOpenSession(player)
                && (Api?.Side != EnumAppSide.Server || owner.CanPlayerAccess(player, false));
        }

        public override object ActivateSlot(int slotId, ItemSlot sourceSlot, ref ItemStackMoveOperation op)
        {
            if (slotId < 0 || slotId >= Count || op == null)
                return null;

            if (Api?.Side == EnumAppSide.Server
                && (op.ActingPlayer == null || !owner.CanPlayerAccess(op.ActingPlayer, true)))
                return null;

            return base.ActivateSlot(slotId, sourceSlot, ref op);
        }

        public override WeightedSlot GetBestSuitedSlot(ItemSlot sourceSlot, ItemStackMoveOperation op = null,
            List<ItemSlot> skipSlots = null)
        {
            // Player shift transfers can reach this inventory through another
            // inventory's packets. Operations without a player are automation.
            if (Api?.Side == EnumAppSide.Server && op?.ActingPlayer != null
                && !owner.CanPlayerAccess(op.ActingPlayer, true))
                return new WeightedSlot();

            return base.GetBestSuitedSlot(sourceSlot, op, skipSlots);
        }
    }
}
