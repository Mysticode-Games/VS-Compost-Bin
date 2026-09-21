using System;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

#nullable disable

namespace CompostBin
{
    public partial class BECompostBin : ITemperatureSensitive
    {
        public bool IsHot => !Sealed && !IsBurning && pileTemperature > 25;
        public void CoolNow(float amountRel, OnStackToCool onStackToCoolCallback)
        {
            if (Api?.Side != EnumAppSide.Server || !IsHot)
                return;
            // Use the pile's water/energy balance, not the generic metal-quenching
            // callback as well (which would apply cooling a second time).
            AddWater(Math.Clamp(amountRel * 0.2 * Settings.EnvironmentalWaterMultiplier, 0, 4));
        }
        internal bool AllowBrownAdvance;
        private bool updatingPhysics, physicsLoaded, ignitionRequested;
        private double oxygen = 0.8, brownBurnRemainder, transitionHours, brownTransitionHours;
        private ITreeAttribute legacyDecomposition;
        public bool IsSmoldering
        {
            get; private set;
        }
        private double smolderConsumptionRemainder;
        private int smolderSlotCursor;
        public double Moisture => ReadPhysicsState().Moisture;
        // Inactive compost still spoils normally. Keep biological heat tied to
        // actual activity, and retain the high-temperature perish cutoff.
        public float DecompositionRate => Sealed || IsBurning || IsSmoldering || pileTemperature >= 75 ? 0
            : (float)(Settings.DecompositionSpeedMultiplier * Math.Max(1,
                BasePerishSpeedMul * 2 * CompostPhysics.Activity(ReadPhysicsState(), Settings))
                * CompostPhysics.PeatDecompositionMultiplier(ReadPhysicsState(), Settings));
        public float BrownDecompositionRate => HasGreens
            ? DecompositionRate * (float)Settings.BrownDecompositionSpeedMultiplier : 0;
        public bool Overheating => !Sealed && !IsBurning && pileTemperature >= Settings.OverheatingTemperature;
        public double GetTemperature() => pileTemperature;
        public static double StackMass(ItemStack stack) => stack == null ? 0 :
            stack.StackSize / 64.0;

        private void InitializePhysics()
        {
            if (!physicsLoaded)
                pileTemperature = Api.World.BlockAccessor.GetClimateAt(Pos, EnumGetClimateMode.NowValues)?.Temperature ?? 20;
            updatingPhysics = true;
            try
            {
                for (int i = 0; i < inventory.Count; i++)
                {
                    var slot = inventory[i];
                    if (slot.Empty)
                        continue;
                    double progress = legacyDecomposition?.GetTreeAttribute(i.ToString())?.GetDouble("progress")
                        ?? slot.Itemstack.Attributes.GetDouble("compostBinDecomposeProgress");
                    if (progress <= 0 && slot.Itemstack.Attributes.HasAttribute("compostBinInsertedHours"))
                    {
                        double end = slot.Itemstack.Attributes.GetDouble("compostBinPauseStartHours");
                        if (end <= 0)
                            end = Api.World.Calendar.TotalHours;
                        progress = Math.Max(0, end - slot.Itemstack.Attributes.GetDouble("compostBinInsertedHours")) * 1.5;
                    }
                    if (CompostItemBehavior.IsBrown(slot.Itemstack))
                    {
                        slot.Itemstack.Collectible.UpdateAndGetTransitionStates(Api.World, slot);
                        if (progress > 0)
                            slot.Itemstack.Collectible.SetTransitionState(slot.Itemstack, EnumTransitionType.Perish, (float)Math.Min(143.99, progress));
                    }
                    ItemSlotCompostBin.StripBinAttributes(slot.Itemstack);
                    slot.Itemstack.Collectible.SetTemperature(Api.World, slot.Itemstack, (float)pileTemperature, false);
                    CompostItemBehavior.SetWater(slot.Itemstack, CompostItemBehavior.WaterRatio(slot.Itemstack));
                }
                legacyDecomposition = null;
                physicsLoaded = true;
            }
            finally { updatingPhysics = false; }
        }

        internal CompostPhysics.State ReadPhysicsState()
        {
            var state = new CompostPhysics.State
            {
                Temperature = pileTemperature,
                Oxygen = oxygen,
                Sealed = Sealed || IsBurning,
                HasBeenTurned = hasBeenTurned,
                TurnAgeHours = Math.Max(0, (Api?.World?.Calendar?.TotalHours ?? lastTurnedTotalHours) - lastTurnedTotalHours)
            };
            foreach (var slot in inventory)
            {
                if (slot.Empty)
                    continue;
                double mass = StackMass(slot.Itemstack);
                state.DryMass += mass;
                state.Water += mass * CompostItemBehavior.WaterRatio(slot.Itemstack);
                double stacks = slot.StackSize / (double)Math.Max(1, slot.Itemstack.Collectible.MaxStackSize);
                if (CompostItemBehavior.IsPeat(slot.Itemstack))
                    state.Peat += stacks;
                else if (IsDryOffering(slot.Itemstack))
                {
                    state.Browns += mass;
                    state.OrganicStacks += stacks;
                }
                else if (IsGreenOffering(slot.Itemstack))
                {
                    state.Greens += mass;
                    state.OrganicStacks += stacks;
                }
            }
            // Chemical consumption accrues fractionally before a whole item is removed.
            state.Water *= Math.Max(0, state.DryMass - brownBurnRemainder) / Math.Max(0.001, state.DryMass);
            state.Browns = Math.Max(0, state.Browns - brownBurnRemainder);
            state.DryMass = Math.Max(0, state.DryMass - brownBurnRemainder);
            return state;
        }

        private void MixContents()
        {
            if (Api?.World == null || !physicsLoaded)
                return;
            double energy = 2 * pileTemperature, capacity = 2;
            foreach (var slot in inventory)
            {
                if (slot.Empty)
                    continue;
                double c = StackMass(slot.Itemstack) * (1.5 + 4.2 * CompostItemBehavior.WaterRatio(slot.Itemstack));
                float t = slot.Itemstack.Collectible.HasTemperature(slot.Itemstack)
                    ? slot.Itemstack.Collectible.GetTemperature(Api.World, slot.Itemstack)
                    : Api.World.BlockAccessor.GetClimateAt(Pos, EnumGetClimateMode.NowValues)?.Temperature ?? 20;
                energy += c * t;
                capacity += c;
            }
            pileTemperature = energy / capacity;
            SyncStackHeat(null);
        }

        private void SyncStackHeat(double? waterRatio)
        {
            foreach (var slot in inventory)
            {
                if (slot.Empty)
                    continue;
                slot.Itemstack.Collectible.SetTemperature(Api.World, slot.Itemstack, (float)pileTemperature, false);
                if (waterRatio.HasValue)
                    CompostItemBehavior.SetWater(slot.Itemstack, waterRatio.Value);
            }
        }

        internal void ApplyPhysics(CompostPhysics.State next, double burned, double hours)
        {
            updatingPhysics = true;
            try
            {
                pileTemperature = next.Temperature;
                oxygen = next.Oxygen;
                brownBurnRemainder += burned;
                foreach (var slot in inventory)
                {
                    if (slot.Empty || !IsDryOffering(slot.Itemstack))
                        continue;
                    const double unit = 1.0 / 64;
                    int count = Math.Min(slot.StackSize, (int)(brownBurnRemainder / unit));
                    if (count <= 0)
                        continue;
                    slot.Itemstack.StackSize -= count;
                    brownBurnRemainder -= count * unit;
                    if (slot.StackSize <= 0)
                        slot.Itemstack = null;
                    slot.MarkDirty();
                }
                SyncStackHeat(next.DryMass > 0 ? next.Water / next.DryMass : 0);
                UpdateSmoldering(hours);
                transitionHours += hours;
                brownTransitionHours = HasGreens ? brownTransitionHours + hours : 0;
                if (transitionHours >= 0.051 && !Sealed && !IsSmoldering)
                {
                    AllowBrownAdvance = true;
                    // Resolve food first so the last green becoming rot pauses
                    // browns in this same update, regardless of inventory order.
                    for (int pass = 0; pass < 2; pass++)
                    {
                        foreach (var slot in inventory)
                        {
                            if (slot.Empty)
                                continue;
                            var old = slot.Itemstack;
                            bool binTransition = CompostItemBehavior.HasBinTransition(old);
                            if (binTransition != (pass == 1))
                                continue;
                            // Native transitions ignore intervals <= 0.05 hours.
                            // Keep a shorter active brown interval for the next
                            // flush instead of discarding it with the food clock.
                            if (CompostItemBehavior.IsBrown(old) && brownTransitionHours < 0.051)
                                continue;
                            var tree = old.Attributes.GetTreeAttribute("transitionstate");
                            if (binTransition)
                                tree?.SetDouble("lastUpdatedTotalHours", Api.World.Calendar.TotalHours
                                    - (CompostItemBehavior.IsBrown(old) ? brownTransitionHours : transitionHours));
                            old.Collectible.UpdateAndGetTransitionStates(Api.World, slot);
                            if (!slot.Empty && !ReferenceEquals(old, slot.Itemstack))
                            {
                                CompostItemBehavior.SetWater(slot.Itemstack, CompostItemBehavior.WaterRatio(old));
                                slot.Itemstack.Collectible.SetTemperature(Api.World, slot.Itemstack, (float)pileTemperature, false);
                                slot.MarkDirty();
                            }
                        }
                    }
                    transitionHours = 0;
                    if (!HasGreens || brownTransitionHours >= 0.051)
                        brownTransitionHours = 0;
                }
                else if (Sealed || IsSmoldering)
                {
                    transitionHours = 0;
                    brownTransitionHours = 0;
                }
                ignitionRequested |= CompostPhysics.CanIgnite(ReadPhysicsState(), Settings);
            }
            finally { AllowBrownAdvance = false; updatingPhysics = false; }
        }

        private bool IsSmolderFuel(ItemStack stack) => stack != null
            && stack.Collectible.Code?.Path is not ("rot" or "compost")
            && (IsDryOffering(stack) || HasPerishTransition(stack) || CompostItemBehavior.IsPeat(stack));

        private void UpdateSmoldering(double hours)
        {
            var state = ReadPhysicsState();
            bool hasFuel = false;
            foreach (var slot in inventory)
                hasFuel |= IsSmolderFuel(slot.Itemstack);
            bool canContinue = !Sealed && !IsBurning && hasFuel
                && pileTemperature >= Settings.OverheatingTemperature
                && state.Moisture < Settings.IgnitionMaxMoisture && oxygen >= Settings.IgnitionMinAeration;
            IsSmoldering = !Settings.EnableSelfIgnition && canContinue
                && (IsSmoldering || CompostPhysics.CanStartSmoldering(state, Settings));
            if (!IsSmoldering)
            {
                smolderConsumptionRemainder = 0;
                return;
            }

            // Contents are lost instead of spawning a spreading fire. Rotate through
            // eligible slots so both greens and browns are consumed, preserving rot.
            smolderConsumptionRemainder += hours * Settings.SmolderConsumptionItemsPerHour;
            while (smolderConsumptionRemainder >= 1)
            {
                bool consumed = false;
                for (int i = 0; i < inventory.Count; i++)
                {
                    var slot = inventory[smolderSlotCursor];
                    smolderSlotCursor = (smolderSlotCursor + 1) % inventory.Count;
                    if (!IsSmolderFuel(slot.Itemstack))
                        continue;
                    slot.Itemstack.StackSize--;
                    if (slot.Itemstack.StackSize <= 0)
                        slot.Itemstack = null;
                    slot.MarkDirty();
                    smolderConsumptionRemainder -= 1;
                    consumed = true;
                    break;
                }
                if (!consumed)
                {
                    IsSmoldering = false;
                    smolderConsumptionRemainder = 0;
                    break;
                }
            }
            hasFuel = false;
            foreach (var slot in inventory)
                hasFuel |= IsSmolderFuel(slot.Itemstack);
            if (!hasFuel)
            {
                IsSmoldering = false;
                smolderConsumptionRemainder = 0;
            }
        }

        internal void FinishPhysicsTick()
        {
            if (Sealed && Api.World.Calendar.TotalHours - SealedSinceTotalHours >= CompostingDurationHours)
                CompleteComposting();
            else if (Settings.EnableSelfIgnition && (ignitionRequested || CompostPhysics.CanIgnite(ReadPhysicsState(), Settings)))
                Ignite();
            ignitionRequested = false;
            MarkDirty();
        }

        public void OnTurned()
        {
            if (Sealed || IsBurning)
                return;
            double ambient = Api.World.BlockAccessor.GetClimateAt(Pos, EnumGetClimateMode.NowValues)?.Temperature ?? 20;
            pileTemperature += (ambient - pileTemperature) * Settings.TurningCoolingFraction;
            oxygen = Math.Max(oxygen, Settings.TurningAeration);
            lastTurnedTotalHours = Api.World.Calendar.TotalHours;
            hasBeenTurned = true;
            SyncStackHeat(null);
            MarkDirty(true);
        }

        public void OnWatered()
        {
            if (Sealed || IsBurning)
                return;
            AddWater(Settings.WateringAmount);
        }

        private void AddWater(double amount)
        {
            var state = ReadPhysicsState();
            double ambient = Api.World.BlockAccessor.GetClimateAt(Pos, EnumGetClimateMode.NowValues)?.Temperature ?? 20;
            double addedWater = Math.Min(amount, Math.Max(0, 4 * state.DryMass - state.Water));
            pileTemperature = (state.Capacity * pileTemperature + addedWater * 4.2 * ambient) / (state.Capacity + addedWater * 4.2);
            SyncStackHeat(state.DryMass > 0 ? (state.Water + addedWater) / state.DryMass : 0);
            if (IsSmoldering)
                UpdateSmoldering(0);
            MarkDirty(true);
        }

        public string PhysicsDescription() => Lang.Get("compostbin:physics-status", Math.Round(pileTemperature),
            Math.Round(Moisture * 100), Math.Round(oxygen * 100), Math.Round(DecompositionRate, 1))
            + (ReadPhysicsState().Browns > 0 && !HasGreens ? "\n" + Lang.Get("compostbin:physics-browns-paused")
                : Settings.BrownDecompositionSpeedMultiplier != 1 ? "\n" + Lang.Get("compostbin:physics-brown-rate", Math.Round(BrownDecompositionRate, 1)) : "")
            + PeatDescription()
            + (IsSmoldering ? "\n" + Lang.Get("compostbin:physics-smoldering") : Overheating ? "\n" + Lang.Get("compostbin:physics-warning") : "");

        private string PeatDescription()
        {
            var state = ReadPhysicsState();
            if (state.Peat <= 0)
                return "";
            double dose = CompostPhysics.PeatDose(state, Settings);
            return "\n" + Lang.Get("compostbin:physics-peat", Math.Round(dose * 100),
                Math.Round((CompostPhysics.PeatDecompositionMultiplier(state, Settings) - 1) * 100))
                + (dose > 1 ? "\n" + Lang.Get("compostbin:physics-peat-excess") : "");
        }

        public override void OnBlockRemoved()
        {
            EndInventoryAccess();
            Api?.ModLoader.GetModSystem<CompostBinModSystem>()?.Unregister(this);
            CloseInventoryDialog();
            base.OnBlockRemoved();
        }
    }
}
