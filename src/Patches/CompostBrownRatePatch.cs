using HarmonyLib;
using Vintagestory.API.Common;

#nullable disable

namespace CompostBin
{
    // No behavior hook exists for collectible transition speed. Only gate our
    // added brown perish transition; native food rates remain untouched.
    // GetTransitionRateMul in the same native source. A prefix is necessary to
    // prevent our added brown transition outside the bin without changing food.
    [HarmonyPatchCategory("compostbin")]
    [HarmonyPatch(typeof(CollectibleObject), nameof(CollectibleObject.GetTransitionRateMul))]
    public static class CompostBrownRatePatch
    {
        public static bool Prefix(ItemSlot inSlot, EnumTransitionType transType, ref float __result)
        {
            if (transType != EnumTransitionType.Perish || !CompostItemBehavior.HasBinTransition(inSlot?.Itemstack))
                return true;
            if (inSlot is ItemSlotCompostBin slot && !slot.Owner.Sealed && !slot.Owner.IsBurning
                && (!CompostItemBehavior.IsBrown(inSlot.Itemstack) || slot.Owner.HasGreens))
                return true;
            __result = 0;
            return false;
        }
    }
}
