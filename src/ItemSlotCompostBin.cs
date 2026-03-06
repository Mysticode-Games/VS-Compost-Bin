using System.Linq;
using Vintagestory.API.Common;

#nullable disable

namespace CompostBin
{
    /// <summary>
    /// A vessel that accepts only that which rots, that which has rotted,
    /// that which is born of rot — or those few dry offerings
    /// that the bin deigns to receive: grass, cattail tops, and thatch.
    /// </summary>
    public class ItemSlotCompostBin : ItemSlotSurvival
    {
        // The sigils of items accepted by name:
        // dry offerings (will decompose inside the bin),
        // rot (the product of decay), and compost (the fruit of the rite)
        private static readonly string[] AcceptedByCodes = new string[]
        {
            "drygrass",
            "cattailtops",
            "papyrustops",
            "thatch",
            "rot",
            "compost"
        };

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
        /// The warding test: does this item bear the mark of decay,
        /// is it the product of decay, or is it among the named dry offerings?
        /// </summary>
        private bool IsAcceptedItem(ItemSlot sourceSlot)
        {
            if (sourceSlot?.Itemstack == null) return false;

            var collectible = sourceSlot.Itemstack.Collectible;
            if (collectible == null) return false;

            // Accept items by their code — rot, compost, and dry offerings
            string code = collectible.Code?.Path;
            if (code != null)
            {
                for (int i = 0; i < AcceptedByCodes.Length; i++)
                {
                    if (code == AcceptedByCodes[i]) return true;
                }
            }

            // Accept items that bear the Perish transition — they carry the seed of rot
            var transProps = collectible.GetTransitionableProperties(
                inventory.Api.World, sourceSlot.Itemstack, null
            );
            if (transProps != null && transProps.Any(p => p.Type == EnumTransitionType.Perish))
            {
                return true;
            }

            return false;
        }
    }
}
