using Vintagestory.API.Common;

#nullable disable

namespace CompostBin
{
    public class ItemSlotCompostBin : ItemSlotSurvival
    {
        internal BECompostBin Owner
        {
            get;
        }
        public ItemSlotCompostBin(InventoryBase inventory, BECompostBin owner) : base(inventory) { Owner = owner; }

        private void PrepareTransfer(ItemSlot source)
        {
            if (source?.Itemstack == null || inventory.Api == null)
                return;
            // Freeze brown elapsed time before entering. Native merges handle
            // transitioned-hours averaging; never strip portable native state.
            if (CompostItemBehavior.HasBinTransition(source.Itemstack))
                source.Itemstack.Collectible.UpdateAndGetTransitionStates(inventory.Api.World, source);
        }
        public override bool CanHold(ItemSlot source)
        {
            PrepareTransfer(source);
            return base.CanHold(source) && Accepted(source);
        }
        public override bool CanTakeFrom(ItemSlot source, EnumMergePriority priority = EnumMergePriority.AutoMerge)
        {
            PrepareTransfer(source);
            return base.CanTakeFrom(source, priority) && Accepted(source);
        }
        public override ItemStack TakeOutWhole()
        {
            PrepareTransfer(this);
            return base.TakeOutWhole();
        }
        public override ItemStack TakeOut(int quantity)
        {
            PrepareTransfer(this);
            return base.TakeOut(quantity);
        }
        private bool Accepted(ItemSlot source)
        {
            var stack = source?.Itemstack;
            if (stack == null)
                return false;
            if (CompostItemBehavior.IsPeat(stack))
                return true;
            if (stack.Collectible.Code?.Path is "rot" or "compost")
                return true;
            if (System.Array.IndexOf(BECompostBin.DryOfferingCodes, stack.Collectible.Code?.Path) >= 0)
                return true;
            var props = stack.Collectible.GetTransitionableProperties(inventory.Api.World, stack, null);
            if (props != null)
                foreach (var prop in props)
                    if (prop.Type == EnumTransitionType.Perish)
                        return true;
            return false;
        }
        internal static void StripBinAttributes(ItemStack stack)
        {
            foreach (string key in new[] { "compostBinDecomposeProgress", "compostBinLastTickHours", "compostBinInsertedHours", "compostBinPauseStartHours" })
                stack?.Attributes.RemoveAttribute(key);
        }
    }
}
