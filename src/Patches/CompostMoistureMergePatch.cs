using HarmonyLib;
using Vintagestory.API.Common;

#nullable disable

namespace CompostBin
{
    // Native implementation: https://github.com/anegostudios/vsapi/blob/master/Common/Collectible/Collectible.cs
    // TryMergeStacks averages native heat/progress; the prefix captures moisture
    // before stack counts change, and the postfix applies it only after a move.
    [HarmonyPatchCategory("compostbin")]
    [HarmonyPatch(typeof(CollectibleObject), nameof(CollectibleObject.TryMergeStacks))]
    public static class CompostMoistureMergePatch
    {
        public struct MergeState
        {
            public bool Tracked; public double Source, Sink; public int Count;
        }
        public static void Prefix(ItemStackMergeOperation op, out MergeState __state)
        {
            var source = op.SourceSlot.Itemstack;
            var sink = op.SinkSlot.Itemstack;
            bool tracked = source?.Attributes.GetTreeAttribute("temperature")?.HasAttribute("compostWater") == true
                || sink?.Attributes.GetTreeAttribute("temperature")?.HasAttribute("compostWater") == true;
            __state = new MergeState
            {
                Tracked = tracked,
                Source = CompostItemBehavior.WaterRatio(source),
                Sink = CompostItemBehavior.WaterRatio(sink),
                Count = op.SinkSlot.StackSize
            };
        }
        public static void Postfix(ItemStackMergeOperation op, MergeState __state)
        {
            if (__state.Tracked && op.MovedQuantity > 0 && op.SinkSlot.Itemstack != null)
                CompostItemBehavior.SetWater(op.SinkSlot.Itemstack,
                    (__state.Sink * __state.Count + __state.Source * op.MovedQuantity) / (__state.Count + op.MovedQuantity));
        }
    }
}
