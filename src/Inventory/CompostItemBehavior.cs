using HarmonyLib;
using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

#nullable disable

namespace CompostBin
{
    // Portable moisture shares the native temperature subtree (ignored for stack
    // equality). Preserve it on splits and explicitly average it on native merges.
    public class CompostItemBehavior : CollectibleBehavior
    {
        public bool Brown
        {
            get; private set;
        }
        public bool Peat
        {
            get; private set;
        }
        // The client registry constructs behaviors with this exact signature.
        public CompostItemBehavior(CollectibleObject item) : base(item) { }
        public CompostItemBehavior(CollectibleObject item, bool brown, bool peat = false) : this(item)
        {
            Initialize(JsonObject.FromJson("{\"brown\":" + (brown ? "true" : "false")
                + ",\"peat\":" + (peat ? "true" : "false") + "}"));
        }
        public override void Initialize(JsonObject properties)
        {
            base.Initialize(properties);
            Brown = properties["brown"].AsBool(false);
            Peat = properties["peat"].AsBool(false);
        }
        public static bool IsBrown(ItemStack stack) => stack?.Collectible?.GetBehavior<CompostItemBehavior>()?.Brown == true;
        public static bool IsPeat(ItemStack stack) => stack?.Collectible?.Code?.Domain == "game"
            && stack.Collectible.Code.Path == "peatbrick";
        public static bool HasPeatTransition(ItemStack stack) => stack?.Collectible?.GetBehavior<CompostItemBehavior>()?.Peat == true;
        public static bool HasBinTransition(ItemStack stack) => IsBrown(stack) || HasPeatTransition(stack);
        public static double WaterRatio(ItemStack stack)
        {
            double initial = IsBrown(stack) || IsPeat(stack) ? 0.12 : 0.8;
            double value = stack?.Attributes.GetTreeAttribute("temperature")?.GetDouble("compostWater", initial) ?? initial;
            return double.IsFinite(value) ? Math.Clamp(value, 0, 4) : initial;
        }
        public static void SetWater(ItemStack stack, double ratio) =>
            stack?.Attributes.GetOrAddTreeAttribute("temperature").SetDouble("compostWater", double.IsFinite(ratio) ? Math.Clamp(ratio, 0, 4) : WaterRatio(stack));
        public override TransitionState[] UpdateAndGetTransitionStates(IWorldAccessor world, ItemSlot slot, ref EnumHandling handling)
        {
            if ((Brown || Peat) && slot.Itemstack != null &&
                (slot is not ItemSlotCompostBin compostSlot || !compostSlot.Owner.AllowBrownAdvance
                    || (Brown && !compostSlot.Owner.HasGreens)))
                slot.Itemstack.Attributes.GetTreeAttribute("transitionstate")?.SetDouble("lastUpdatedTotalHours", world.Calendar.TotalHours);
            return null;
        }
        public override ItemStack OnTransitionNow(ItemSlot slot, TransitionableProperties props, ref EnumHandling handling)
        {
            if (!Peat || props.Type != EnumTransitionType.Perish || slot is not ItemSlotCompostBin compostSlot)
                return null;
            var output = props.TransitionedStack?.ResolvedItemstack;
            if (output == null)
                return null;
            handling = EnumHandling.PreventSubsequent;
            int count = GameMath.RoundRandom(slot.Inventory.Api.World.Rand,
                (float)(slot.StackSize * compostSlot.Owner.Settings.PeatRotYield));
            var result = output.Clone();
            result.StackSize = count;
            return result;
        }
    }
}
