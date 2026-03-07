using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

#nullable disable

namespace CompostBin
{
    /// <summary>
    /// The heart of the compost bin — a vessel that accelerates rot through heat-driven
    /// decomposition, seals when filled with sufficient rot, and transmutes it into compost.
    ///
    /// Decomposition speed scales with four factors:
    ///   effective_speed = BasePerishSpeedMul * heatFactor * seasonalModifier * turningBoost
    ///
    /// Range: 0.375x (frozen winter, cold pile, no turning) to 9.0x (peak summer, max heat, freshly turned).
    ///
    /// Extends BlockEntity directly, so it must handle transition ticking manually.
    /// The standard container hierarchy (BlockEntityContainer → InWorldContainer) is not used
    /// because we need custom slot filtering and composting logic.
    /// </summary>
    public class BECompostBin : BlockEntity
    {
        // The inventory — eight vessels for the offerings of decay
        internal InventoryGeneric inventory;

        // The current state of the seal
        public bool Sealed;
        public double SealedSinceTotalHours;

        // Thermal runaway tracking — when the nitrogen overload began
        public double OverheatingSinceTotalHours;

        // The bin's heat — derived from the green:brown ratio each tick.
        // Range: 1.0 (cold/baseline) to 2.0 (maximum thermal output).
        private float cachedHeatFactor = 1f;

        // Ambient temperature modifier — the season's breath upon the pile.
        // Range: 0.5 (frozen winter) to 1.5 (peak summer).
        private float cachedSeasonalModifier = 1f;

        // Carbon consumption: for every 4 rot produced from perishables,
        // 1 dry offering is consumed. The remainder carries between ticks.
        private int rotConsumeRemainder;

        // Round-robin index for cycling dry offering consumption across slots
        private int dryOfferingConsumeSlot;

        // Dry offering → rot yield accumulator: consumed dry offerings
        // yield 1 rot per CompostYieldDivisor consumed. The remainder carries
        // between ticks, fixing the bug where consumed/4 always yielded 0
        // because consumed was typically 1–2 per tick.
        private int dryOfferingConsumeYieldRemainder;

        // Turning / aeration: sneak + right-click with shovel grants a 2x→1x decaying boost
        public double lastTurnedTotalHours;

        // Watering: right-click with watering can halves seasonal modifier
        // and resets overheating. Cooling persists until this hour mark.
        public double wateredCoolingUntilTotalHours;

        // Dialog reference, client-side only
        GuiDialogCompostBin invDialog;

        // Shape references for sealed/unsealed rendering
        MeshData currentMesh;

        // ── The Fixed Geometry ──────────────────────────────────────────
        // Named constants that govern the composting rite.
        // Every number earns its name, or it has no right to dwell here.

        /// <summary>Number of offering vessels within the bin.</summary>
        public const int SlotCount = 8;

        /// <summary>Base Perish transition speed multiplier when unsealed.</summary>
        public const float BasePerishSpeedMul = 1.5f;

        /// <summary>Game hours the sealed rite requires to transmute rot into compost.</summary>
        public const double CompostingDurationHours = 480.0;

        /// <summary>Minimum total rot required to seal the bin.</summary>
        public const int MinRotForSeal = 64;

        /// <summary>Network packet ID for the seal command (barrel convention).</summary>
        public const int SealPacketId = 1337;

        /// <summary>Rot-to-compost conversion divisor: N rot yields N/this compost.</summary>
        public const int CompostYieldDivisor = 4;

        /// <summary>
        /// Perishable-to-dry ratio threshold that triggers thermal runaway.
        /// Above this ratio (with browns present), the bin begins overheating.
        /// </summary>
        public const int OverheatRatioThreshold = 2;

        /// <summary>
        /// Target accumulated effective hours for dry offerings to decompose into rot.
        /// This is the threshold for accumulated progress, not wall-clock time.
        /// At baseline (1.5x, heatFactor 1.0, seasonal 1.0, no turning):
        ///   144 / 1.5 = 96 wall-hours = 4 game days.
        /// At maximum speed (1.5 * 2.0 * 1.5 * 2.0 = 9.0x):
        ///   144 / 9.0 = 16 wall-hours = 0.67 game days.
        /// </summary>
        private const double DryOfferingDecomposeHours = 144.0;

        /// <summary>
        /// Game hours before an overheating bin ignites. One full game day
        /// to notice the danger and correct the green-to-brown ratio.
        /// </summary>
        public const double OverheatIgnitionHours = 24.0;

        /// <summary>
        /// Hours after turning during which aeration boost decays linearly from 2.0x to 1.0x.
        /// </summary>
        public const double TurningBoostHours = 12.0;

        /// <summary>
        /// Hours of cooling effect from watering. While active, seasonal modifier is halved.
        /// </summary>
        public const double WateringCoolHours = 6.0;

        // The dry offerings that decompose by custom timer rather than transition properties
        internal static readonly string[] DryOfferingCodes = new string[]
        {
            "drygrass",
            "cattailtops",
            "papyrustops",
            "thatch"
        };

        // ── Particle Properties ──────────────────────────────────────────
        // Static vessels for steam and smoke — conjured once, reused for all bins.

        private static SimpleParticleProperties steamParticles;
        private static SimpleParticleProperties smokeParticles;

        private static void InitParticles()
        {
            if (steamParticles != null) return;

            // Light steam — translucent white wisps rising from the pile
            steamParticles = new SimpleParticleProperties(
                1, 1,
                ColorUtil.ToRgba(50, 220, 220, 220),
                new Vec3d(), new Vec3d(),
                new Vec3f(-0.1f, 0.2f, -0.1f),
                new Vec3f(0.1f, 0.4f, 0.1f),
                1.5f, 0, 0.25f, 0.5f,
                EnumParticleModel.Quad
            );
            steamParticles.SelfPropelled = true;
            steamParticles.OpacityEvolve = new EvolvingNatFloat(EnumTransformFunction.LINEAR, -150);
            steamParticles.SizeEvolve = new EvolvingNatFloat(EnumTransformFunction.LINEAR, 1.5f);

            // Dense smoke — grey plumes from an overheating bin
            smokeParticles = new SimpleParticleProperties(
                1, 1,
                ColorUtil.ToRgba(80, 150, 150, 150),
                new Vec3d(), new Vec3d(),
                new Vec3f(-0.1f, 0.3f, -0.1f),
                new Vec3f(0.1f, 0.6f, 0.1f),
                2f, 0, 0.3f, 0.7f,
                EnumParticleModel.Quad
            );
            smokeParticles.SelfPropelled = true;
            smokeParticles.OpacityEvolve = new EvolvingNatFloat(EnumTransformFunction.LINEAR, -100);
            smokeParticles.SizeEvolve = new EvolvingNatFloat(EnumTransformFunction.LINEAR, 2f);
        }

        public string InventoryClassName => "compostbin";

        public InventoryBase Inventory => inventory;

        public BECompostBin()
        {
            // Conjure the 8-slot inventory with custom compost bin slots
            inventory = new InventoryGeneric(SlotCount, null, null, (id, self) =>
            {
                return new ItemSlotCompostBin(self);
            });
            inventory.BaseWeight = 1;

            inventory.SlotModified += OnSlotModified;

            // Perish speed scales with heat when unsealed; halts when sealed
            inventory.OnAcquireTransitionSpeed += OnAcquireTransitionSpeed;
        }

        /// <summary>
        /// The transition speed multiplier — the daemon's breath that hastens decay.
        /// When sealed, all transitions halt. When unsealed, Perish speed scales
        /// with the full composting formula: base * heat * season * turning.
        /// </summary>
        private float OnAcquireTransitionSpeed(EnumTransitionType transType, ItemStack stack, float mul)
        {
            if (Sealed) return 0f;
            if (transType == EnumTransitionType.Perish)
            {
                float turningBoost = ComputeTurningBoost(Api?.World?.Calendar?.TotalHours ?? 0);
                float seasonalMod = GetEffectiveSeasonalModifier();
                return BasePerishSpeedMul * cachedHeatFactor * seasonalMod * turningBoost;
            }
            return 1f;
        }

        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);

            // Bind the inventory to this position and API
            inventory.LateInitialize(InventoryClassName + "-" + Pos.X + "/" + Pos.Y + "/" + Pos.Z, api);
            inventory.Pos = Pos;
            inventory.ResolveBlocksOrItems();

            if (api.Side == EnumAppSide.Server)
            {
                RegisterGameTickListener(OnEvery3Second, 3000);
            }

            if (api.Side == EnumAppSide.Client)
            {
                RegisterGameTickListener(OnClientParticleTick, 200);
            }
        }

        /// <summary>
        /// The pulse of the composting rite — every 3 seconds, the daemon:
        /// 1. Caches the seasonal modifier from ambient temperature
        /// 2. Ticks transition states on perishable items (so food actually rots)
        /// 3. Decomposes dry offerings (drygrass, papyrustops, thatch) into rot
        /// 4. Checks whether the sealed bin's composting time has elapsed
        /// </summary>
        private void OnEvery3Second(float dt)
        {
            if (Api.Side != EnumAppSide.Server) return;

            // Cache the seasonal modifier from ambient temperature
            ClimateCondition climate = Api.World.BlockAccessor.GetClimateAt(
                Pos, EnumGetClimateMode.NowValues);
            cachedSeasonalModifier = ComputeSeasonalModifier(climate?.Temperature ?? 20f);

            if (!Sealed)
            {
                // Single inventory census for the entire tick cycle —
                // three methods consumed these counts independently before this refactor
                GetCriticalMassCounts(out int perishableCount, out int dryOfferingCount);
                cachedHeatFactor = ComputeHeatFactor(perishableCount, dryOfferingCount);

                // Tick transitions on all perishable items — without this, nothing rots.
                // Count rot before and after to detect newly produced rot.
                int rotBefore = CountRot();
                TickPerishTransitions();
                int newRot = CountRot() - rotBefore;

                // Carbon consumption: the microbial colony devours browns as greens decay
                if (newRot > 0) ConsumeDryOfferings(newRot);

                // Decompose dry offerings that have no innate perish properties
                bool hasCriticalMass = dryOfferingCount == 0 || perishableCount >= dryOfferingCount;
                TickDryOfferings(hasCriticalMass);

                // Monitor the green-to-brown ratio — nitrogen overload begets fire
                TickOverheat(perishableCount, dryOfferingCount);
            }
            else
            {
                // Check if the composting rite is complete
                double hoursPassed = Api.World.Calendar.TotalHours - SealedSinceTotalHours;
                if (hoursPassed >= CompostingDurationHours)
                {
                    CompleteComposting();
                }
            }
        }

        /// <summary>
        /// Client-side particle tick — visual feedback from the bin's thermal state.
        /// Steam rises from hot bins; smoke pours from overheating ones.
        /// A recently turned bin vents trapped heat in a brief burst.
        /// </summary>
        private void OnClientParticleTick(float dt)
        {
            if (Sealed) return;

            GetCriticalMassCounts(out int perishableCount, out int dryOfferingCount);
            if (perishableCount == 0 && dryOfferingCount == 0) return;

            // Compute display temperature for particle thresholds
            ClimateCondition climate = Api.World.BlockAccessor.GetClimateAt(
                Pos, EnumGetClimateMode.NowValues);
            float seasonalMod = ComputeSeasonalModifier(climate?.Temperature ?? 20f);
            if (wateredCoolingUntilTotalHours > Api.World.Calendar.TotalHours)
            {
                seasonalMod *= 0.5f;
            }

            int displayTemp = ComputeDisplayTemperature(
                perishableCount, dryOfferingCount,
                OverheatingSinceTotalHours, Api.World.Calendar.TotalHours,
                seasonalMod);

            if (displayTemp < 40) return;

            InitParticles();

            Vec3d spawnPos = Pos.ToVec3d().Add(0.5, 0.9375, 0.5);

            // Recently turned: a burst of trapped heat escaping the aerated pile
            double hoursSinceTurned = Api.World.Calendar.TotalHours - lastTurnedTotalHours;
            bool recentlyTurned = lastTurnedTotalHours > 0 && hoursSinceTurned < 1.0;

            SimpleParticleProperties particles;
            float spawnChance;

            if (displayTemp > 70)
            {
                // Overheating — dense smoke signals danger
                particles = smokeParticles;
                spawnChance = 0.4f;
            }
            else if (displayTemp > 55)
            {
                // Thermophilic — moderate steam from vigorous decomposition
                particles = steamParticles;
                spawnChance = 0.25f;
            }
            else
            {
                // Mesophilic — light wisps of warm moisture
                particles = steamParticles;
                spawnChance = 0.1f;
            }

            if (recentlyTurned) spawnChance = Math.Max(spawnChance, 0.5f);

            if (Api.World.Rand.NextDouble() < spawnChance)
            {
                particles.MinPos = spawnPos;
                Api.World.SpawnParticles(particles);
            }
        }

        /// <summary>
        /// Invoke the game's transition system on every occupied slot.
        /// This is what InWorldContainer.OnTick does for standard containers.
        /// Without this call, items with Perish transitions sit inert.
        /// </summary>
        private void TickPerishTransitions()
        {
            bool anyChanged = false;

            for (int i = 0; i < inventory.Count; i++)
            {
                ItemSlot slot = inventory[i];
                if (slot.Itemstack == null) continue;

                // Skip dry offerings — they're handled by TickDryOfferings
                if (IsDryOffering(slot.Itemstack)) continue;

                AssetLocation codeBefore = slot.Itemstack.Collectible.Code;
                slot.Itemstack.Collectible.UpdateAndGetTransitionStates(Api.World, slot);

                if (slot.Itemstack?.Collectible?.Code != codeBefore)
                {
                    anyChanged = true;
                }
            }

            if (anyChanged)
            {
                MarkDirty(true);
            }
        }

        /// <summary>
        /// Counts total rot across all slots — used to detect newly produced rot
        /// by comparing before and after perish transitions.
        /// </summary>
        private int CountRot()
        {
            int total = 0;
            for (int i = 0; i < inventory.Count; i++)
            {
                ItemSlot slot = inventory[i];
                if (slot.Itemstack != null && slot.Itemstack.Collectible.Code.Path == "rot")
                {
                    total += slot.Itemstack.StackSize;
                }
            }
            return total;
        }

        /// <summary>
        /// Carbon consumption: as perishable items rot, the microbial colony consumes
        /// dry offerings in proportion. For every 4 rot produced from perishables,
        /// 1 dry offering item is consumed. Consumption cycles across slots in
        /// round-robin order so that multiple dry offering types deplete evenly.
        ///
        /// The consumed dry offerings yield rot in turn: every CompostYieldDivisor
        /// consumed produces 1 rot. The remainder accumulates in
        /// dryOfferingConsumeYieldRemainder across ticks to ensure no yield is lost.
        /// </summary>
        private void ConsumeDryOfferings(int newRotProduced)
        {
            rotConsumeRemainder += newRotProduced;
            int toConsume = rotConsumeRemainder / CompostYieldDivisor;
            rotConsumeRemainder %= CompostYieldDivisor;

            if (toConsume <= 0) return;

            int consumed = 0;
            while (consumed < toConsume)
            {
                // Seek the next dry offering slot starting from the current position
                bool found = false;
                for (int j = 0; j < inventory.Count; j++)
                {
                    int idx = (dryOfferingConsumeSlot + j) % inventory.Count;
                    ItemSlot slot = inventory[idx];

                    if (slot.Itemstack != null && IsDryOffering(slot.Itemstack))
                    {
                        slot.Itemstack.StackSize--;
                        if (slot.Itemstack.StackSize <= 0) slot.Itemstack = null;
                        slot.MarkDirty();
                        consumed++;
                        dryOfferingConsumeSlot = (idx + 1) % inventory.Count;
                        found = true;
                        break;
                    }
                }

                if (!found) break; // No dry offerings remain — the carbon is exhausted
            }

            // The consumed carbon yields rot: CompostYieldDivisor dry offerings → 1 rot.
            // Accumulate the consumed count across ticks so that small per-tick
            // quantities (typically 1–2) are never silently rounded to zero.
            if (consumed > 0)
            {
                dryOfferingConsumeYieldRemainder += consumed;
                int rotYield = dryOfferingConsumeYieldRemainder / CompostYieldDivisor;
                dryOfferingConsumeYieldRemainder %= CompostYieldDivisor;

                if (rotYield > 0)
                {
                    Item rotItem = Api.World.GetItem(new AssetLocation("game:rot"));
                    if (rotItem != null) TryAddToInventory(new ItemStack(rotItem, rotYield));
                }
                MarkDirty(true);
            }
        }

        /// <summary>
        /// Attempts to merge a stack into existing matching slots or an empty slot.
        /// Returns the number of items that could not be placed.
        /// </summary>
        private int TryAddToInventory(ItemStack stack)
        {
            // Merge into existing stacks of the same item first
            for (int i = 0; i < inventory.Count && stack.StackSize > 0; i++)
            {
                ItemSlot slot = inventory[i];
                if (slot.Itemstack != null
                    && slot.Itemstack.Collectible.Code.Equals(stack.Collectible.Code))
                {
                    int canAdd = Math.Min(stack.StackSize,
                        slot.Itemstack.Collectible.MaxStackSize - slot.Itemstack.StackSize);
                    if (canAdd > 0)
                    {
                        slot.Itemstack.StackSize += canAdd;
                        stack.StackSize -= canAdd;
                        slot.MarkDirty();
                    }
                }
            }

            // Place remainder in an empty slot
            for (int i = 0; i < inventory.Count && stack.StackSize > 0; i++)
            {
                if (inventory[i].Itemstack == null)
                {
                    inventory[i].Itemstack = stack.Clone();
                    inventory[i].MarkDirty();
                    stack.StackSize = 0;
                }
            }

            return stack.StackSize;
        }

        /// <summary>
        /// Dry offerings (drygrass, cattailtops, papyrustops, thatch) have no Perish transition,
        /// so the standard system cannot rot them. Instead, we accumulate decomposition progress
        /// each tick, scaled by the bin's full speed formula.
        ///
        /// Progress per tick: deltaGameHours * BasePerishSpeedMul * heatFactor * seasonalModifier * turningBoost
        ///   At baseline (all factors 1.0 except base): effective speed is 1.5x
        ///   At maximum speed (1.5 * 2.0 * 1.5 * 2.0 = 9.0x): 144 / 9.0 = 16 wall-hours
        ///   Below critical mass: progress is frozen (natural pause).
        ///
        /// The accumulated progress model is necessary because the speed varies
        /// with the ratio, season, and turning state. An absolute timestamp cannot
        /// account for a rate that shifts beneath it.
        ///
        /// Attributes per dry offering stack:
        ///   "compostBinDecomposeProgress" — accumulated effective hours toward the 144h target
        ///   "compostBinLastTickHours" — game hours at last tick (for computing delta)
        /// </summary>
        private void TickDryOfferings(bool hasCriticalMass)
        {
            double nowHours = Api.World.Calendar.TotalHours;
            float turningBoost = ComputeTurningBoost(nowHours);
            float seasonalMod = GetEffectiveSeasonalModifier();

            for (int i = 0; i < inventory.Count; i++)
            {
                ItemSlot slot = inventory[i];
                if (slot.Itemstack == null) continue;
                if (!IsDryOffering(slot.Itemstack)) continue;

                // Initialize or migrate from older attribute formats
                if (!slot.Itemstack.Attributes.HasAttribute("compostBinDecomposeProgress"))
                {
                    // Migration from v1.0.0: convert old timestamp to accumulated progress.
                    // Assumes heatFactor was 1.0 during the legacy period (the only value it could have been).
                    if (slot.Itemstack.Attributes.HasAttribute("compostBinInsertedHours"))
                    {
                        double insertedHours = slot.Itemstack.Attributes.GetDouble("compostBinInsertedHours");
                        double pauseStart = slot.Itemstack.Attributes.GetDouble("compostBinPauseStartHours", 0);
                        double pausedHours = pauseStart > 0 ? nowHours - pauseStart : 0;
                        double activeHours = Math.Max(0, (nowHours - insertedHours) - pausedHours);
                        double legacyProgress = activeHours * 1.5;

                        slot.Itemstack.Attributes.SetDouble("compostBinDecomposeProgress", legacyProgress);
                        slot.Itemstack.Attributes.SetDouble("compostBinLastTickHours", nowHours);
                        slot.Itemstack.Attributes.RemoveAttribute("compostBinInsertedHours");
                        if (pauseStart > 0) slot.Itemstack.Attributes.RemoveAttribute("compostBinPauseStartHours");
                        slot.MarkDirty();
                        continue;
                    }

                    // Brand new dry offering — no progress yet
                    slot.Itemstack.Attributes.SetDouble("compostBinDecomposeProgress", 0.0);
                    slot.Itemstack.Attributes.SetDouble("compostBinLastTickHours", nowHours);
                    slot.MarkDirty();
                    continue;
                }

                double lastTickHours = slot.Itemstack.Attributes.GetDouble("compostBinLastTickHours");
                double deltaHours = nowHours - lastTickHours;

                // Always advance the tick clock so delta doesn't accumulate during pause
                slot.Itemstack.Attributes.SetDouble("compostBinLastTickHours", nowHours);

                if (!hasCriticalMass || deltaHours <= 0)
                {
                    // Below critical mass or no time elapsed — the pile is cold, progress frozen
                    continue;
                }

                // Advance decomposition: full speed formula
                double progress = slot.Itemstack.Attributes.GetDouble("compostBinDecomposeProgress");
                progress += deltaHours * BasePerishSpeedMul * cachedHeatFactor * seasonalMod * turningBoost;
                slot.Itemstack.Attributes.SetDouble("compostBinDecomposeProgress", progress);

                if (progress >= DryOfferingDecomposeHours)
                {
                    // The dry offering has decomposed — transmute to rot
                    int stackSize = slot.Itemstack.StackSize;
                    Item rotItem = Api.World.GetItem(new AssetLocation("game:rot"));

                    if (rotItem != null)
                    {
                        slot.Itemstack = new ItemStack(rotItem, stackSize);
                        slot.MarkDirty();
                        MarkDirty(true);
                    }
                }
            }
        }

        /// <summary>
        /// Monitors the nitrogen-to-carbon ratio for thermal runaway.
        /// When perishable item count exceeds 2x the dry offering count (and dry offerings
        /// are present), the bin begins overheating. After OverheatIgnitionHours, it ignites.
        ///
        /// The safe band is [1:1, 2:1] perishable-to-dry. Below = stalled. Above = fire.
        /// Pure greens with no browns: slimy but safe — no fire risk.
        ///
        /// The timer resets if the ratio drops back into safe range (the heat dissipates).
        /// </summary>
        private void TickOverheat(int perishableCount, int dryOfferingCount)
        {
            bool isOverheating = dryOfferingCount > 0 && perishableCount > OverheatRatioThreshold * dryOfferingCount;

            if (!isOverheating)
            {
                // The ratio is safe — clear any overheating state
                if (OverheatingSinceTotalHours > 0)
                {
                    OverheatingSinceTotalHours = 0;
                    MarkDirty(true);
                }
                return;
            }

            double nowHours = Api.World.Calendar.TotalHours;

            if (OverheatingSinceTotalHours <= 0)
            {
                // The nitrogen overload begins — heat is building
                OverheatingSinceTotalHours = nowHours;
                MarkDirty(true);
                return;
            }

            double hoursOverheating = nowHours - OverheatingSinceTotalHours;
            if (hoursOverheating >= OverheatIgnitionHours)
            {
                Ignite();
            }
        }

        /// <summary>
        /// The thermal runaway has reached its terminus. The nitrogen overload,
        /// unchecked by sufficient carbon structure, has driven microbial activity
        /// past the point of no return. The bin ignites.
        ///
        /// All contents are consumed. The vessel is destroyed. Fire is loosed
        /// upon the material plane to spread as it will.
        /// </summary>
        private void Ignite()
        {
            // Consume all contents — nothing survives the blaze
            for (int i = 0; i < inventory.Count; i++)
            {
                inventory[i].Itemstack = null;
            }
            MarkDirty(true);

            // Place fire ABOVE the bin — the bin itself is the fuel
            BlockPos firePos = Pos.UpCopy();
            Block fireBlock = Api.World.GetBlock(new AssetLocation("fire"));

            if (fireBlock != null && Api.World.BlockAccessor.GetBlock(firePos).Replaceable >= 6000)
            {
                Api.World.BlockAccessor.SetBlock(fireBlock.BlockId, firePos);

                // The fire feeds on the bin below
                BlockEntity befire = Api.World.BlockAccessor.GetBlockEntity(firePos);
                befire?.GetBehavior<BEBehaviorBurning>()?.OnFirePlaced(firePos, Pos, null);
            }
        }

        /// <summary>
        /// Determines whether a stack is one of the dry offerings that requires
        /// custom decomposition handling.
        /// </summary>
        private bool IsDryOffering(ItemStack stack)
        {
            if (stack?.Collectible == null) return false;
            string code = stack.Collectible.Code?.Path;
            if (code == null) return false;

            for (int i = 0; i < DryOfferingCodes.Length; i++)
            {
                if (code == DryOfferingCodes[i]) return true;
            }

            return false;
        }

        /// <summary>
        /// Determines whether a stack bears the Perish transition —
        /// the mark of organic matter that will decompose over time.
        /// </summary>
        private bool HasPerishTransition(ItemStack stack)
        {
            if (stack?.Collectible == null || Api?.World == null) return false;
            var transProps = stack.Collectible.GetTransitionableProperties(Api.World, stack, null);
            if (transProps == null) return false;

            for (int i = 0; i < transProps.Length; i++)
            {
                if (transProps[i].Type == EnumTransitionType.Perish) return true;
            }

            return false;
        }

        /// <summary>
        /// Exposes the raw counts of perishable and dry offering items
        /// for UI display — the summoner deserves to see the numbers.
        /// </summary>
        public void GetCriticalMassCounts(out int perishableCount, out int dryOfferingCount)
        {
            perishableCount = 0;
            dryOfferingCount = 0;

            for (int i = 0; i < inventory.Count; i++)
            {
                ItemSlot slot = inventory[i];
                if (slot.Itemstack == null) continue;

                if (IsDryOffering(slot.Itemstack))
                {
                    dryOfferingCount += slot.Itemstack.StackSize;
                }
                else if (HasPerishTransition(slot.Itemstack))
                {
                    perishableCount += slot.Itemstack.StackSize;
                }
            }
        }

        // ── Static Computations ──────────────────────────────────────────
        // Pure functions that derive state from inputs. No side effects.

        /// <summary>
        /// Computes the heat factor from the green:brown ratio.
        /// The ratio drives microbial activity, which generates heat:
        ///   No browns present:     1.0 (neutral — no carbon structure to interact with)
        ///   ratio &lt; 1.0:        1.0 (cold pile, insufficient activation energy)
        ///   ratio 1.0–2.0:         linearly scales from 1.0 to 2.0
        ///   ratio &gt; 2.0:        capped at 2.0 (maximum thermal output)
        ///
        /// The effective decomposition speed is always BasePerishSpeedMul * heatFactor * ...:
        ///   At 1:1 ratio:  heatFactor = 1.0
        ///   At 1.5:1:      heatFactor = 1.5
        ///   At 2:1+:       heatFactor = 2.0
        /// </summary>
        public static float ComputeHeatFactor(int perishableCount, int dryOfferingCount)
        {
            if (dryOfferingCount == 0) return 1f;
            float ratio = (float)perishableCount / dryOfferingCount;
            if (ratio < 1f) return 1f;
            if (ratio <= 2f) return 1f + (ratio - 1f);
            return 2f;
        }

        /// <summary>
        /// Computes the seasonal temperature modifier from ambient temperature.
        /// The season's breath upon the pile — winter slows, summer hastens.
        ///   Below 0°C:   0.5x (near-frozen, microbial activity barely persists)
        ///   0–20°C:      linear 0.5x–1.0x
        ///   20–40°C:     linear 1.0x–1.5x (summer bonus)
        ///   Above 40°C:  capped at 1.5x
        /// </summary>
        public static float ComputeSeasonalModifier(float ambientTemp)
        {
            if (ambientTemp < 0f) return 0.5f;
            if (ambientTemp <= 20f) return 0.5f + (ambientTemp / 20f) * 0.5f;
            if (ambientTemp <= 40f) return 1f + ((ambientTemp - 20f) / 20f) * 0.5f;
            return 1.5f;
        }

        /// <summary>
        /// Computes the turning/aeration boost from hours since last turned.
        /// Decays linearly from 2.0x (just turned) to 1.0x (expired).
        /// </summary>
        public float ComputeTurningBoost(double currentTotalHours)
        {
            if (lastTurnedTotalHours <= 0) return 1f;
            double hoursSince = currentTotalHours - lastTurnedTotalHours;
            if (hoursSince >= TurningBoostHours) return 1f;
            return 1f + (float)(1.0 - hoursSince / TurningBoostHours);
        }

        /// <summary>
        /// Returns the effective seasonal modifier, accounting for watering cooling.
        /// While cooling is active, the seasonal modifier is halved — water
        /// counteracts the ambient heat, buying time against thermal runaway.
        /// </summary>
        public float GetEffectiveSeasonalModifier()
        {
            float mod = cachedSeasonalModifier;
            if (Api?.World != null && wateredCoolingUntilTotalHours > Api.World.Calendar.TotalHours)
            {
                mod *= 0.5f;
            }
            return mod;
        }

        /// <summary>
        /// Called when the summoner turns the pile with a shovel.
        /// Grants a 2.0x decaying aeration boost over TurningBoostHours.
        /// </summary>
        public void OnTurned()
        {
            lastTurnedTotalHours = Api.World.Calendar.TotalHours;
            MarkDirty(true);
        }

        /// <summary>
        /// Called when the summoner waters the pile.
        /// Resets the overheating timer and grants temporary cooling
        /// that halves the seasonal modifier for WateringCoolHours.
        /// </summary>
        public void OnWatered()
        {
            wateredCoolingUntilTotalHours = Api.World.Calendar.TotalHours + WateringCoolHours;
            OverheatingSinceTotalHours = 0;
            MarkDirty(true);
        }

        /// <summary>
        /// Computes a display temperature in °C from the bin's current state,
        /// grounded in real-world composting temperature ranges and shifted
        /// by the seasonal modifier to reflect ambient conditions:
        ///   Ambient/inactive:           ~20°C (shifts with season)
        ///   Stalled (insufficient greens): ~25°C (shifts with season)
        ///   Active mesophilic (1:1):     ~40°C
        ///   Thermophilic (1.5:1):        ~55°C
        ///   Peak thermophilic (2:1):     ~70°C
        ///   Overheating → ignition:      70–93°C
        /// </summary>
        public static int ComputeDisplayTemperature(
            int perishableCount, int dryOfferingCount,
            double overheatingSinceTotalHours, double currentTotalHours,
            float seasonalModifier = 1f)
        {
            // Nothing biological is happening — the pile is ambient
            if (perishableCount == 0 && dryOfferingCount == 0)
                return Math.Max(0, (int)(20 * seasonalModifier));

            // Stalled: browns present but insufficient greens to activate the colony
            if (dryOfferingCount > 0 && perishableCount < dryOfferingCount)
                return Math.Max(0, (int)(25 * seasonalModifier));

            // Active composting: map heatFactor 1.0–2.0 onto base 40–70°C,
            // then shift by seasonal modifier
            float heatFactor = ComputeHeatFactor(perishableCount, dryOfferingCount);
            int baseTemp = 40 + (int)((heatFactor - 1f) * 30f);
            int temp = (int)(baseTemp * seasonalModifier);

            // Thermal runaway: push from peak toward 93°C over the ignition window
            if (overheatingSinceTotalHours > 0 && currentTotalHours > overheatingSinceTotalHours)
            {
                double progress = (currentTotalHours - overheatingSinceTotalHours) / OverheatIgnitionHours;
                temp = Math.Max(temp, 70) + (int)(Math.Min(progress, 1.0) * 23);
            }

            return temp;
        }

        /// <summary>
        /// The culmination of the rite: ALL rot is consumed and transmuted to compost
        /// at the standard barrel ratio of 4:1 (every 4 rot yields 1 compost).
        /// </summary>
        private void CompleteComposting()
        {
            // Count all rot across every slot
            int totalRot = 0;
            for (int i = 0; i < inventory.Count; i++)
            {
                ItemSlot slot = inventory[i];
                if (!slot.Empty && slot.Itemstack.Collectible.Code.Path == "rot")
                {
                    totalRot += slot.Itemstack.StackSize;
                }
            }

            if (totalRot <= 0)
            {
                Sealed = false;
                MarkDirty(true);
                return;
            }

            int compostToCreate = totalRot / CompostYieldDivisor;
            if (compostToCreate <= 0) compostToCreate = 1; // At least 1 if any rot was present

            // Consume all rot
            for (int i = 0; i < inventory.Count; i++)
            {
                ItemSlot slot = inventory[i];
                if (!slot.Empty && slot.Itemstack.Collectible.Code.Path == "rot")
                {
                    slot.Itemstack = null;
                    slot.MarkDirty();
                }
            }

            // Distribute compost across available slots
            Item compostItem = Api.World.GetItem(new AssetLocation("game:compost"));
            if (compostItem == null)
            {
                Sealed = false;
                MarkDirty(true);
                return;
            }

            int remaining = compostToCreate;
            int maxStack = compostItem.MaxStackSize;

            // Fill empty slots first
            for (int i = 0; i < inventory.Count && remaining > 0; i++)
            {
                ItemSlot slot = inventory[i];
                if (slot.Empty)
                {
                    int amount = Math.Min(remaining, maxStack);
                    slot.Itemstack = new ItemStack(compostItem, amount);
                    slot.MarkDirty();
                    remaining -= amount;
                }
            }

            // Merge into existing compost stacks
            for (int i = 0; i < inventory.Count && remaining > 0; i++)
            {
                ItemSlot slot = inventory[i];
                if (!slot.Empty && slot.Itemstack.Collectible.Code.Path == "compost")
                {
                    int canAdd = Math.Min(remaining, maxStack - slot.Itemstack.StackSize);
                    if (canAdd > 0)
                    {
                        slot.Itemstack.StackSize += canAdd;
                        slot.MarkDirty();
                        remaining -= canAdd;
                    }
                }
            }

            // If still no room, spill the remainder unto the earth
            while (remaining > 0)
            {
                int amount = Math.Min(remaining, maxStack);
                Api.World.SpawnItemEntity(
                    new ItemStack(compostItem, amount),
                    Pos.ToVec3d().Add(0.5, 0.5, 0.5)
                );
                remaining -= amount;
            }

            Sealed = false;
            MarkDirty(true);
        }

        /// <summary>
        /// Determines whether the bin can be sealed:
        /// all non-empty slots must contain rot, and total rot >= 64.
        /// </summary>
        public bool CanSeal()
        {
            int totalRot = 0;
            for (int i = 0; i < inventory.Count; i++)
            {
                ItemSlot slot = inventory[i];
                if (slot.Empty) continue;

                if (slot.Itemstack.Collectible.Code.Path != "rot")
                {
                    return false;
                }

                totalRot += slot.Itemstack.StackSize;
            }

            return totalRot >= MinRotForSeal;
        }

        /// <summary>
        /// Seal the bin — the composting rite begins.
        /// </summary>
        public void SealBin()
        {
            if (Sealed) return;

            Sealed = true;
            SealedSinceTotalHours = Api.World.Calendar.TotalHours;

            // Quench any thermal runaway and reset accumulators —
            // the seal halts all activity
            OverheatingSinceTotalHours = 0;
            rotConsumeRemainder = 0;
            dryOfferingConsumeYieldRemainder = 0;
            wateredCoolingUntilTotalHours = 0;

            MarkDirty(true);
        }

        // --- Slot modification callback ---

        private void OnSlotModified(int slotId)
        {
            if (Api?.Side == EnumAppSide.Client)
            {
                currentMesh = null;
            }

            invDialog?.UpdateContents();
            MarkDirty(true);
        }

        // --- Player interaction ---

        public void OnPlayerRightClick(IPlayer byPlayer)
        {
            if (Sealed) return;

            if (Api.Side == EnumAppSide.Client)
            {
                ToggleInventoryDialog(byPlayer);
            }
        }

        private void ToggleInventoryDialog(IPlayer byPlayer)
        {
            if (invDialog == null)
            {
                ICoreClientAPI capi = Api as ICoreClientAPI;
                invDialog = new GuiDialogCompostBin(
                    Lang.Get("compostbin:compostbin-title"),
                    inventory, Pos, capi, this
                );
                invDialog.OnClosed += () =>
                {
                    invDialog = null;
                    capi.Network.SendBlockEntityPacket(Pos, (int)EnumBlockEntityPacketId.Close, null);
                    capi.Network.SendPacketClient(inventory.Close(byPlayer));
                };

                invDialog.TryOpen();
                capi.Network.SendPacketClient(inventory.Open(byPlayer));
                capi.Network.SendBlockEntityPacket(Pos, (int)EnumBlockEntityPacketId.Open, null);
            }
            else
            {
                invDialog.TryClose();
            }
        }

        // --- Network packet handling ---

        public override void OnReceivedClientPacket(IPlayer player, int packetid, byte[] data)
        {
            base.OnReceivedClientPacket(player, packetid, data);

            if (packetid < 1000)
            {
                inventory.InvNetworkUtil.HandleClientPacket(player, packetid, data);
                Api.World.BlockAccessor.GetChunkAtBlockPos(Pos).MarkModified();
                return;
            }

            if (packetid == (int)EnumBlockEntityPacketId.Close)
            {
                player.InventoryManager?.CloseInventory(inventory);
            }

            if (packetid == (int)EnumBlockEntityPacketId.Open)
            {
                player.InventoryManager?.OpenInventory(inventory);
            }

            if (packetid == SealPacketId)
            {
                if (CanSeal())
                {
                    SealBin();
                }
            }
        }

        public override void OnReceivedServerPacket(int packetid, byte[] data)
        {
            base.OnReceivedServerPacket(packetid, data);

            if (packetid == (int)EnumBlockEntityPacketId.Close)
            {
                (Api.World as IClientWorldAccessor).Player.InventoryManager.CloseInventory(inventory);
                invDialog?.TryClose();
                invDialog?.Dispose();
                invDialog = null;
            }
        }

        // --- Block broken ---

        public override void OnBlockBroken(IPlayer byPlayer = null)
        {
            // Spill the contents unto the earth — sealed or not.
            // A broken vessel holds nothing.
            for (int i = 0; i < inventory.Count; i++)
            {
                ItemSlot slot = inventory[i];
                if (!slot.Empty)
                {
                    // Strip the bin's tracking attributes before the offering returns to the world
                    slot.Itemstack.Attributes?.RemoveAttribute("compostBinDecomposeProgress");
                    slot.Itemstack.Attributes?.RemoveAttribute("compostBinLastTickHours");
                    Api.World.SpawnItemEntity(slot.Itemstack, Pos.ToVec3d().Add(0.5, 0.5, 0.5));
                    slot.Itemstack = null;
                    slot.MarkDirty();
                }
            }

            invDialog?.TryClose();
            invDialog = null;

            base.OnBlockBroken(byPlayer);
        }

        /// <summary>
        /// The inscription that appears when the summoner gazes upon the bin —
        /// hover text revealing the vessel's state and contents.
        /// </summary>
        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            base.GetBlockInfo(forPlayer, dsc);

            if (Sealed)
            {
                double hoursPassed = Api.World.Calendar.TotalHours - SealedSinceTotalHours;
                string passedText = hoursPassed > 24
                    ? Lang.Get("{0} days", Math.Floor(hoursPassed / Api.World.Calendar.HoursPerDay * 10) / 10)
                    : Lang.Get("{0} hours", Math.Floor(hoursPassed));
                string totalText = Lang.Get("{0} days", Math.Round(CompostingDurationHours / Api.World.Calendar.HoursPerDay, 1));
                dsc.AppendLine(Lang.Get("compostbin:compostbin-composting", passedText, totalText));
                return;
            }

            int totalRot = 0;
            bool hasItems = false;

            for (int i = 0; i < inventory.Count; i++)
            {
                ItemSlot slot = inventory[i];
                if (slot.Empty) continue;
                hasItems = true;

                if (slot.Itemstack.Collectible.Code.Path == "rot")
                {
                    totalRot += slot.Itemstack.StackSize;
                }
            }

            if (!hasItems)
            {
                dsc.AppendLine(Lang.Get("compostbin:compostbin-empty"));
            }
            else if (totalRot > 0)
            {
                dsc.AppendLine(Lang.Get("compostbin:compostbin-contents", totalRot));
            }

            // Temperature-driven status — single count pass for all indicators
            if (hasItems)
            {
                GetCriticalMassCounts(out int perishableCount, out int dryOfferingCount);
                float heatFactor = ComputeHeatFactor(perishableCount, dryOfferingCount);

                // Compute seasonal modifier for display
                ClimateCondition climate = Api.World.BlockAccessor.GetClimateAt(
                    Pos, EnumGetClimateMode.NowValues);
                float seasonalMod = ComputeSeasonalModifier(climate?.Temperature ?? 20f);
                if (wateredCoolingUntilTotalHours > Api.World.Calendar.TotalHours)
                {
                    seasonalMod *= 0.5f;
                }

                // Temperature display — following VS convention (crucible, oven, forge)
                int displayTemp = ComputeDisplayTemperature(
                    perishableCount, dryOfferingCount,
                    OverheatingSinceTotalHours, Api.World.Calendar.TotalHours,
                    seasonalMod);

                if (displayTemp <= 25)
                {
                    dsc.AppendLine(Lang.Get("Temperature: {0}", Lang.Get("Cold")));
                }
                else
                {
                    dsc.AppendLine(Lang.Get("Temperature: {0}°C", displayTemp));
                }

                // Show effective decomposition speed when the bin is running
                float turningBoost = ComputeTurningBoost(Api.World.Calendar.TotalHours);
                float effectiveSpeed = BasePerishSpeedMul * heatFactor * seasonalMod * turningBoost;
                if (effectiveSpeed > 1f)
                {
                    dsc.AppendLine(Lang.Get("compostbin:compostbin-rate", Math.Round(effectiveSpeed, 1)));
                }

                // Turning status — the aeration boost
                if (turningBoost > 1f)
                {
                    dsc.AppendLine(Lang.Get("compostbin:compostbin-turned",
                        Math.Round(turningBoost, 1)));
                }

                // Critical mass stalled warning
                if (dryOfferingCount > 0 && perishableCount < dryOfferingCount)
                {
                    dsc.AppendLine(Lang.Get("compostbin:compostbin-stalled", perishableCount, dryOfferingCount));
                }

                // Thermal runaway warning — the most urgent inscription
                if (OverheatingSinceTotalHours > 0)
                {
                    double hoursLeft = OverheatIgnitionHours - (Api.World.Calendar.TotalHours - OverheatingSinceTotalHours);
                    if (hoursLeft > 0)
                    {
                        string timeText = Lang.Get("{0} hours", (int)Math.Ceiling(hoursLeft));
                        dsc.AppendLine(Lang.Get("compostbin:compostbin-overheating", timeText));
                    }
                }
            }
        }

        // --- Collectible mappings for world export/import ---

        public override void OnStoreCollectibleMappings(
            Dictionary<int, AssetLocation> blockIdMapping,
            Dictionary<int, AssetLocation> itemIdMapping)
        {
            foreach (ItemSlot slot in inventory)
            {
                if (slot.Itemstack == null) continue;

                if (slot.Itemstack.Class == EnumItemClass.Block)
                {
                    blockIdMapping[slot.Itemstack.Id] = slot.Itemstack.Collectible.Code;
                }
                else
                {
                    itemIdMapping[slot.Itemstack.Id] = slot.Itemstack.Collectible.Code;
                }
            }
        }

        public override void OnLoadCollectibleMappings(
            IWorldAccessor worldForResolve,
            Dictionary<int, AssetLocation> oldBlockIdMapping,
            Dictionary<int, AssetLocation> oldItemIdMapping,
            int schematicSeed, bool resolveImports)
        {
            foreach (ItemSlot slot in inventory)
            {
                if (slot.Itemstack == null) continue;
                if (!slot.Itemstack.FixMapping(oldBlockIdMapping, oldItemIdMapping, worldForResolve))
                {
                    slot.Itemstack = null;
                }
            }
        }

        public override void OnBlockUnloaded()
        {
            base.OnBlockUnloaded();
            invDialog?.Dispose();
        }

        // --- Persistence: the seal's memory ---

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);

            tree.SetBool("sealed", Sealed);
            tree.SetDouble("sealedSinceTotalHours", SealedSinceTotalHours);
            tree.SetDouble("overheatingSinceTotalHours", OverheatingSinceTotalHours);
            tree.SetInt("rotConsumeRemainder", rotConsumeRemainder);
            tree.SetInt("dryOfferingConsumeSlot", dryOfferingConsumeSlot);
            tree.SetInt("dryOfferingConsumeYieldRemainder", dryOfferingConsumeYieldRemainder);
            tree.SetDouble("lastTurnedTotalHours", lastTurnedTotalHours);
            tree.SetDouble("wateredCoolingUntilTotalHours", wateredCoolingUntilTotalHours);

            // Persist inventory
            inventory.ToTreeAttributes(tree.GetOrAddTreeAttribute("inventory"));
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
        {
            // Restore inventory before base call
            if (inventory == null)
            {
                // This can happen during world load before Initialize
                inventory = new InventoryGeneric(SlotCount, null, null, (id, self) =>
                {
                    return new ItemSlotCompostBin(self);
                });
                inventory.BaseWeight = 1;
                inventory.SlotModified += OnSlotModified;
                inventory.OnAcquireTransitionSpeed += OnAcquireTransitionSpeed;
            }

            ITreeAttribute invTree = tree.GetTreeAttribute("inventory");
            if (invTree != null)
            {
                inventory.FromTreeAttributes(invTree);
            }

            base.FromTreeAttributes(tree, worldForResolving);

            Sealed = tree.GetBool("sealed");
            SealedSinceTotalHours = tree.GetDouble("sealedSinceTotalHours");
            OverheatingSinceTotalHours = tree.GetDouble("overheatingSinceTotalHours");
            rotConsumeRemainder = tree.GetInt("rotConsumeRemainder");
            dryOfferingConsumeSlot = tree.GetInt("dryOfferingConsumeSlot");
            dryOfferingConsumeYieldRemainder = tree.GetInt("dryOfferingConsumeYieldRemainder");
            lastTurnedTotalHours = tree.GetDouble("lastTurnedTotalHours");
            wateredCoolingUntilTotalHours = tree.GetDouble("wateredCoolingUntilTotalHours");

            if (Api?.Side == EnumAppSide.Client)
            {
                currentMesh = null;
                MarkDirty(true);
                invDialog?.UpdateContents();
            }
        }

        // --- Tesselation: the bin's visual form ---

        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tesselator)
        {
            if (currentMesh == null)
            {
                currentMesh = GenMesh();
            }

            if (currentMesh != null)
            {
                mesher.AddMeshData(currentMesh);
                return true;
            }

            return false;
        }

        private MeshData GenMesh()
        {
            if (Block == null) return null;
            ICoreClientAPI capi = Api as ICoreClientAPI;
            if (capi == null) return null;

            AssetLocation shapeLocation;
            if (Sealed)
            {
                shapeLocation = AssetLocation.Create("game:block/wood/barrel/closed");
            }
            else
            {
                shapeLocation = AssetLocation.Create("game:block/wood/barrel/empty");
            }

            shapeLocation = shapeLocation.WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
            Shape shape = Vintagestory.API.Common.Shape.TryGet(capi, shapeLocation);
            if (shape == null) return null;

            capi.Tesselator.TesselateShape(Block, shape, out MeshData mesh);
            return mesh;
        }
    }
}
