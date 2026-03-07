using Vintagestory.API.Common;

#nullable disable

namespace CompostBin
{
    /// <summary>
    /// A vessel that accepts only that which rots, that which has rotted,
    /// that which is born of rot — or those few dry offerings
    /// that the bin deigns to receive.
    /// </summary>
    public class ItemSlotCompostBin : ItemSlotSurvival
    {
        public ItemSlotCompostBin(InventoryBase inventory) : base(inventory)
        {
        }

        /// <summary>
        /// Determines whether the offered item may enter this vessel.
        /// </summary>
        public override bool CanHold(ItemSlot sourceSlot)
        {
            if (!base.CanHold(sourceSlot)) return false;
            return IsAcceptedItem(sourceSlot);
        }

        /// <summary>
        /// Determines whether this slot will accept a transfer from the source.
        /// </summary>
        public override bool CanTakeFrom(ItemSlot sourceSlot, EnumMergePriority priority = EnumMergePriority.AutoMerge)
        {
            if (!base.CanTakeFrom(sourceSlot, priority)) return false;
            return IsAcceptedItem(sourceSlot);
        }

        /// <summary>
        /// When an offering departs the vessel, strip the bin's tracking attributes
        /// so the stack may rejoin its kin in the world without impediment.
        /// </summary>
        public override ItemStack TakeOutWhole()
        {
            ItemStack stack = base.TakeOutWhole();
            StripBinAttributes(stack);
            return stack;
        }

        /// <inheritdoc />
        public override ItemStack TakeOut(int quantity)
        {
            ItemStack stack = base.TakeOut(quantity);
            StripBinAttributes(stack);
            return stack;
        }

        /// <summary>
        /// Removes the compost bin's internal tracking attributes from a stack,
        /// restoring it to a form that will merge cleanly with untouched stacks.
        /// </summary>
        private static void StripBinAttributes(ItemStack stack)
        {
            if (stack?.Attributes == null) return;
            stack.Attributes.RemoveAttribute("compostBinDecomposeProgress");
            stack.Attributes.RemoveAttribute("compostBinLastTickHours");
        }

        /// <summary>
        /// The warding test: does this item bear the mark of decay,
        /// is it the product of decay, or is it among the named dry offerings?
        /// Dry offering codes are sourced from BECompostBin — one truth, one list.
        /// </summary>
        private bool IsAcceptedItem(ItemSlot sourceSlot)
        {
            if (sourceSlot?.Itemstack == null) return false;

            var collectible = sourceSlot.Itemstack.Collectible;
            if (collectible == null) return false;

            string code = collectible.Code?.Path;
            if (code != null)
            {
                // Accept rot and compost by name
                if (code == "rot" || code == "compost") return true;

                // Accept dry offerings — the canonical list lives in BECompostBin
                var dryOfferings = BECompostBin.DryOfferingCodes;
                for (int i = 0; i < dryOfferings.Length; i++)
                {
                    if (code == dryOfferings[i]) return true;
                }
            }

            // Accept items that bear the Perish transition — they carry the seed of rot
            var transProps = collectible.GetTransitionableProperties(
                inventory.Api.World, sourceSlot.Itemstack, null
            );
            if (transProps != null)
            {
                for (int i = 0; i < transProps.Length; i++)
                {
                    if (transProps[i].Type == EnumTransitionType.Perish) return true;
                }
            }

            return false;
        }
    }
}
