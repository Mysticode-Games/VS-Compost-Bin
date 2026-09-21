using System.Collections.Generic;
using System.Text;
using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

#nullable disable

namespace CompostBin
{
    // Inventory, interactions and sealed conversion. The partial Physics file
    // owns unsealed heat, moisture and decomposition updates.
    public partial class BECompostBin : BlockEntity
    {
        internal InventoryGeneric inventory;
        public bool Sealed;
        public double SealedSinceTotalHours;
        private double pileTemperature = 20;
        private float clientDisplayElapsed;
        private double lastTurnedTotalHours;
        private bool hasBeenTurned;
        GuiDialogCompostBin invDialog;
        private float contentsMeshFullness;
        public const int SlotCount = 8;
        public const float BasePerishSpeedMul = 1.5f;
        private string settingsJson;
        private CompostBinConfig settings = new();
        public CompostBinConfig Settings
        {
            get
            {
                var worldConfig = Api?.World?.Config;
                string json = worldConfig?.GetString(CompostBinConfig.SettingsKey);
                if (json != settingsJson)
                {
                    settings = json == null ? new CompostBinConfig() : CompostBinConfig.Read(json);
                    settingsJson = json;
                }
                if (json == null)
                    settings.CompostingDurationHours = CompostBinConfig.GetDuration(worldConfig);
                return settings;
            }
        }
        public double CompostingDurationHours => Settings.CompostingDurationHours;
        public int MinRotForSeal => Math.Max(Settings.MinimumRotToSeal, Settings.RotPerCompost);
        public const int SealPacketId = 1337;
        public int CompostYieldDivisor => Settings.RotPerCompost;
        internal static readonly string[] DryOfferingCodes = new string[]
        {
            "drygrass",
            "cattailtops",
            "papyrustops",
            "thatch"
        };

        private static SimpleParticleProperties steamParticles;
        private static SimpleParticleProperties smokeParticles;

        private static void InitParticles()
        {
            if (steamParticles != null)
                return;
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

        public bool IsBurning => GetBehavior<BEBehaviorBurning>()?.IsBurning == true;

        public BECompostBin()
        {
            inventory = new InventoryGeneric(SlotCount, null, null, (id, self) =>
            {
                return new ItemSlotCompostBin(self, this);
            });
            inventory.BaseWeight = 1;

            inventory.SlotModified += OnSlotModified;
            inventory.OnAcquireTransitionSpeed += OnAcquireTransitionSpeed;
        }
        private float OnAcquireTransitionSpeed(EnumTransitionType transType, ItemStack stack, float mul)
        {
            if (Sealed || IsBurning || IsSmoldering)
                return 0;
            return transType == EnumTransitionType.Perish
                ? DecompositionRate * (CompostItemBehavior.HasPeatTransition(stack) ? (float)(144 / Settings.PeatDecompositionHours)
                    : CompostItemBehavior.IsBrown(stack) ? (float)Settings.BrownDecompositionSpeedMultiplier : 1) : 1;
        }

        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);
            ConfigureBurningBehavior();
            inventory.LateInitialize(InventoryClassName + "-" + Pos.X + "/" + Pos.Y + "/" + Pos.Z, api);
            inventory.Pos = Pos;
            inventory.ResolveBlocksOrItems();
            UpdateInventoryLockState();
            InitializePhysics();

            if (api.Side == EnumAppSide.Server)
            {
                api.ModLoader.GetModSystem<CompostBinModSystem>().Register(this);
            }

            if (api.Side == EnumAppSide.Client)
            {
                contentsMeshFullness = (float)GetFullness();
                RegisterGameTickListener(OnClientParticleTick, 200);
            }
        }
        private void OnClientParticleTick(float dt)
        {
            clientDisplayElapsed += dt;
            if (clientDisplayElapsed >= 1)
            {
                clientDisplayElapsed = 0;
                invDialog?.UpdateContents();
            }
            if (Sealed || IsBurning)
                return;

            GetCriticalMassCounts(out int perishableCount, out int dryOfferingCount);
            if (perishableCount == 0 && dryOfferingCount == 0)
                return;
            int displayTemp = GetDisplayTemperature();

            if (displayTemp < 40)
                return;

            InitParticles();

            Vec3d spawnPos = Pos.ToVec3d().Add(0.5, 0.9375, 0.5);
            double hoursSinceTurned = Api.World.Calendar.TotalHours - lastTurnedTotalHours;
            bool recentlyTurned = lastTurnedTotalHours > 0 && hoursSinceTurned < 1.0;

            SimpleParticleProperties particles;
            float spawnChance;

            if (IsSmoldering || displayTemp > Settings.OverheatingTemperature)
            {
                particles = smokeParticles;
                spawnChance = IsSmoldering ? 0.8f : 0.4f;
            }
            else if (displayTemp > 55)
            {
                particles = steamParticles;
                spawnChance = 0.25f;
            }
            else
            {
                particles = steamParticles;
                spawnChance = 0.1f;
            }

            if (recentlyTurned)
                spawnChance = Math.Max(spawnChance, 0.5f);

            if (Api.World.Rand.NextDouble() < spawnChance)
            {
                particles.MinPos = spawnPos;
                Api.World.SpawnParticles(particles);
            }
        }

        public double GetFullness()
        {
            double occupiedSlots = 0;
            foreach (ItemSlot slot in inventory)
            {
                if (slot.Empty)
                    continue;
                int capacity = Math.Min(slot.MaxSlotStackSize, slot.Itemstack.Collectible.MaxStackSize);
                if (capacity > 0)
                    occupiedSlots += Math.Min(1.0, (double)slot.Itemstack.StackSize / capacity);
            }
            return occupiedSlots / inventory.Count;
        }
        private void ConfigureBurningBehavior()
        {
            var burning = GetBehavior<BEBehaviorBurning>();
            if (burning == null)
                return;
            var defaultFireDeath = burning.OnFireDeath;
            burning.OnFireDeath = consumeFuel =>
            {
                if (consumeFuel)
                    defaultFireDeath(true);
                else
                {
                    UpdateInventoryLockState();
                    MarkDirty(true);
                }
            };
        }

        private void Ignite()
        {
            if (IsBurning || !Settings.EnableSelfIgnition)
                return;
            var burning = GetBehavior<BEBehaviorBurning>();
            Block fireBlock = Api.World.GetBlock(new AssetLocation("fire"));
            if (burning == null || fireBlock == null)
                return;
            burning.OnFirePlaced(Pos, Pos, null);
            if (!burning.IsBurning)
                return;

            UpdateInventoryLockState();
            foreach (IPlayer player in Api.World.AllOnlinePlayers)
                if (inventory.HasOpened(player))
                    CloseInventoryForPlayer(player);
            for (int i = 0; i < inventory.Count; i++)
            {
                inventory[i].Itemstack = null;
                inventory[i].MarkDirty();
            }
            MarkDirty(true);

            TryPlaceFireAbove(fireBlock);
        }

        private void TryPlaceFireAbove(Block fireBlock)
        {
            BlockPos firePos = Pos.UpCopy();
            var above = Api.World.BlockAccessor.GetBlock(firePos);
            if (above.BlockId == fireBlock.BlockId || above.Replaceable < 6000)
                return;
            Api.World.BlockAccessor.SetBlock(fireBlock.BlockId, firePos);
            if (Api.World.BlockAccessor.GetBlock(firePos).BlockId != fireBlock.BlockId)
                return;
            var topBurning = Api.World.BlockAccessor.GetBlockEntity(firePos)?.GetBehavior<BEBehaviorBurning>();
            topBurning?.OnFirePlaced(firePos, Pos, null);
        }
        private bool IsDryOffering(ItemStack stack)
        {
            if (stack?.Collectible == null)
                return false;
            string code = stack.Collectible.Code?.Path;
            if (code == null)
                return false;

            for (int i = 0; i < DryOfferingCodes.Length; i++)
            {
                if (code == DryOfferingCodes[i])
                    return true;
            }

            return false;
        }
        private bool HasPerishTransition(ItemStack stack)
        {
            if (stack?.Collectible == null || Api?.World == null)
                return false;
            var transProps = stack.Collectible.GetTransitionableProperties(Api.World, stack, null);
            if (transProps == null)
                return false;

            for (int i = 0; i < transProps.Length; i++)
            {
                if (transProps[i].Type == EnumTransitionType.Perish)
                    return true;
            }

            return false;
        }
        public void GetCriticalMassCounts(out int perishableCount, out int dryOfferingCount)
        {
            perishableCount = 0;
            dryOfferingCount = 0;

            for (int i = 0; i < inventory.Count; i++)
            {
                ItemSlot slot = inventory[i];
                if (slot.Itemstack == null)
                    continue;

                if (IsDryOffering(slot.Itemstack))
                {
                    dryOfferingCount += slot.Itemstack.StackSize;
                }
                else if (!CompostItemBehavior.IsPeat(slot.Itemstack) && HasPerishTransition(slot.Itemstack))
                {
                    perishableCount += slot.Itemstack.StackSize;
                }
            }
        }
        public bool IsTurningDue(double currentTotalHours)
        {
            GetCriticalMassCounts(out int greens, out _);
            return !Sealed && !IsBurning && greens > 0 && oxygen < Settings.TurningHintAerationThreshold;
        }

        public int GetDisplayTemperature() => (int)Math.Round(GetTemperature());
        private void CompleteComposting()
        {
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
                UpdateInventoryLockState();
                MarkDirty(true);
                return;
            }

            int compostToCreate = totalRot / CompostYieldDivisor;
            if (compostToCreate <= 0)
                compostToCreate = 1; // At least 1 if any rot was present
            Item compostItem = Api.World.GetItem(new AssetLocation("game:compost"));
            if (compostItem == null || compostItem.MaxStackSize <= 0)
            {
                Api.Logger?.Warning("[compostbin] Compost item is missing or has an invalid stack size; preserving rot at {0}.", Pos);
                Sealed = false;
                UpdateInventoryLockState();
                MarkDirty(true);
                return;
            }
            for (int i = 0; i < inventory.Count; i++)
            {
                ItemSlot slot = inventory[i];
                if (!slot.Empty && slot.Itemstack.Collectible.Code.Path == "rot")
                {
                    slot.Itemstack = null;
                    slot.MarkDirty();
                }
            }
            int remaining = compostToCreate;
            int maxStack = compostItem.MaxStackSize;
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
            UpdateInventoryLockState();
            MarkDirty(true);
        }
        public bool CanSeal()
        {
            if (IsBurning)
                return false;
            int totalRot = 0;
            for (int i = 0; i < inventory.Count; i++)
            {
                ItemSlot slot = inventory[i];
                if (slot.Empty)
                    continue;

                if (slot.Itemstack.Collectible.Code.Path != "rot")
                {
                    return false;
                }

                totalRot += slot.Itemstack.StackSize;
            }

            return totalRot >= MinRotForSeal;
        }
        public void SealBin()
        {
            if (Sealed || IsBurning)
                return;

            Sealed = true;
            UpdateInventoryLockState();
            SealedSinceTotalHours = Api.World.Calendar.TotalHours;

            foreach (IPlayer player in Api.World.AllOnlinePlayers)
                if (inventory.HasOpened(player))
                    CloseInventoryForPlayer(player);

            MarkDirty(true);
        }

        private void UpdateInventoryLockState()
        {
            inventory.TakeLocked = Sealed || IsBurning;
            inventory.PutLocked = Sealed || IsBurning;
        }

        private void CloseInventoryForPlayer(IPlayer player)
        {
            player?.InventoryManager?.CloseInventory(inventory);
            if (player is IServerPlayer serverPlayer && Api is ICoreServerAPI serverApi)
                serverApi.Network.SendBlockEntityPacket(serverPlayer, Pos, (int)EnumBlockEntityPacketId.Close);
        }

        private void OnSlotModified(int slotId)
        {
            if (!updatingPhysics && Api?.Side == EnumAppSide.Server)
                MixContents();
            if (Api?.Side == EnumAppSide.Client)
            {
                contentsMeshFullness = (float)GetFullness();
            }

            invDialog?.UpdateContents();
            MarkDirty(true);
        }

        public void OnPlayerRightClick(IPlayer byPlayer)
        {
            if (Sealed || IsBurning)
                return;

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
                var dialog = new GuiDialogCompostBin(
                    Lang.Get("compostbin:compostbin-title"),
                    inventory, Pos, capi, this
                );
                invDialog = dialog;
                dialog.OnClosed += () =>
                {
                    if (ReferenceEquals(invDialog, dialog))
                        invDialog = null;
                    capi.Event.EnqueueMainThreadTask(dialog.Dispose, "dispose-compostbin-dialog");
                };

                if (!dialog.TryOpen())
                {
                    invDialog = null;
                    dialog.Dispose();
                    return;
                }
                capi.Network.SendPacketClient(inventory.Open(byPlayer));
                capi.Network.SendBlockEntityPacket(Pos, (int)EnumBlockEntityPacketId.Open, null);
            }
            else
            {
                CloseInventoryDialog();
            }
        }

        private void CloseInventoryDialog()
        {
            var dialog = invDialog;
            invDialog = null;
            if (dialog == null)
                return;
            if (dialog.IsOpened())
                dialog.TryClose();
            else
                dialog.Dispose();
        }

        public override void OnReceivedClientPacket(IPlayer player, int packetid, byte[] data)
        {
            if (Api?.Side != EnumAppSide.Server || player?.InventoryManager == null)
                return;
            // Closing is always permitted; all actions must recheck permissions
            // on the server, including packets sent without the normal UI.
            if (packetid == (int)EnumBlockEntityPacketId.Close)
            {
                player.InventoryManager.CloseInventory(inventory);
                return;
            }
            if (Api.World.Claims?.TryAccess(player, Pos, EnumBlockAccessFlags.Use) != true)
            {
                CloseInventoryForPlayer(player);
                return;
            }
            base.OnReceivedClientPacket(player, packetid, data);

            if (packetid < 1000)
            {
                if (Sealed || IsBurning)
                {
                    CloseInventoryForPlayer(player);
                    return;
                }
                inventory.InvNetworkUtil.HandleClientPacket(player, packetid, data);
                Api.World.BlockAccessor.GetChunkAtBlockPos(Pos)?.MarkModified();
                return;
            }

            if (packetid == (int)EnumBlockEntityPacketId.Open)
            {
                if (Sealed || IsBurning)
                {
                    CloseInventoryForPlayer(player);
                    return;
                }
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
                CloseInventoryDialog();
            }
        }

        public override void OnBlockBroken(IPlayer byPlayer = null)
        {
            for (int i = 0; Api?.Side == EnumAppSide.Server && i < inventory.Count; i++)
            {
                ItemSlot slot = inventory[i];
                if (!slot.Empty)
                {
                    ItemSlotCompostBin.StripBinAttributes(slot.Itemstack);
                    Api.World.SpawnItemEntity(slot.Itemstack, Pos.ToVec3d().Add(0.5, 0.5, 0.5));
                    slot.Itemstack = null;
                    slot.MarkDirty();
                }
            }

            CloseInventoryDialog();

            Api?.ModLoader.GetModSystem<CompostBinModSystem>()?.Unregister(this);
            base.OnBlockBroken(byPlayer);
        }
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

            dsc.AppendLine(Lang.Get("compostbin:compostbin-fullness", Math.Round(GetFullness() * 100, 1)));

            for (int i = 0; i < inventory.Count; i++)
            {
                ItemSlot slot = inventory[i];
                if (slot.Empty)
                    continue;
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

            if (hasItems)
                dsc.AppendLine(PhysicsDescription());
        }

        public override void OnStoreCollectibleMappings(
            Dictionary<int, AssetLocation> blockIdMapping,
            Dictionary<int, AssetLocation> itemIdMapping)
        {
            foreach (ItemSlot slot in inventory)
            {
                if (slot.Itemstack == null)
                    continue;

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
                if (slot.Itemstack == null)
                    continue;
                if (!slot.Itemstack.FixMapping(oldBlockIdMapping, oldItemIdMapping, worldForResolve))
                {
                    slot.Itemstack = null;
                }
            }
        }

        public override void OnBlockUnloaded()
        {
            Api?.ModLoader.GetModSystem<CompostBinModSystem>()?.Unregister(this);
            base.OnBlockUnloaded();
            CloseInventoryDialog();
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);

            tree.SetBool("sealed", Sealed);
            tree.SetDouble("sealedSinceTotalHours", SealedSinceTotalHours);
            tree.SetDouble("pileTemperature", pileTemperature);
            tree.SetDouble("lastTurnedTotalHours", lastTurnedTotalHours);
            tree.SetBool("hasBeenTurned", hasBeenTurned);

            tree.SetInt("physicsVersion", 1);
            tree.SetDouble("oxygen", oxygen);
            tree.SetDouble("brownBurnRemainder", brownBurnRemainder);
            tree.SetBool("smoldering", IsSmoldering);
            tree.SetDouble("smolderConsumptionRemainder", smolderConsumptionRemainder);
            tree.SetInt("smolderSlotCursor", smolderSlotCursor);
            inventory.ToTreeAttributes(tree.GetOrAddTreeAttribute("inventory"));
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
        {
            bool previouslySealed = Sealed;
            float previousFullness = contentsMeshFullness;
            if (inventory == null)
            {
                inventory = new InventoryGeneric(SlotCount, null, null, (id, self) =>
                {
                    return new ItemSlotCompostBin(self, this);
                });
                inventory.BaseWeight = 1;
                inventory.SlotModified += OnSlotModified;
                inventory.OnAcquireTransitionSpeed += OnAcquireTransitionSpeed;
            }

            ITreeAttribute invTree = tree.GetTreeAttribute("inventory");
            if (invTree != null)
            {
                inventory.FromTreeAttributes(invTree);
                legacyDecomposition = tree.GetTreeAttribute("dryDecomposition");
            }

            base.FromTreeAttributes(tree, worldForResolving);

            Sealed = tree.GetBool("sealed");
            UpdateInventoryLockState();
            SealedSinceTotalHours = ReadSavedNumber(tree, "sealedSinceTotalHours", 0, 0, double.MaxValue);
            pileTemperature = ReadSavedNumber(tree, "pileTemperature", 20, -80, 500);
            physicsLoaded = tree.HasAttribute("pileTemperature");
            oxygen = ReadSavedNumber(tree, "oxygen", 0.8, 0, 1);
            brownBurnRemainder = ReadSavedNumber(tree, "brownBurnRemainder", 0, 0, 1);
            IsSmoldering = tree.GetBool("smoldering");
            smolderConsumptionRemainder = ReadSavedNumber(tree, "smolderConsumptionRemainder", 0, 0, 1);
            smolderSlotCursor = Math.Clamp(tree.GetInt("smolderSlotCursor"), 0, SlotCount - 1);
            lastTurnedTotalHours = ReadSavedNumber(tree, "lastTurnedTotalHours", 0, 0, double.MaxValue);
            hasBeenTurned = tree.GetBool("hasBeenTurned", lastTurnedTotalHours > 0);

            if (Api?.Side == EnumAppSide.Client)
            {
                contentsMeshFullness = (float)GetFullness();
                if (previouslySealed != Sealed || previousFullness != contentsMeshFullness)
                    MarkDirty(true);
                if (Sealed || IsBurning)
                    CloseInventoryDialog();
                else
                    invDialog?.UpdateContents();
            }
        }

        private double ReadSavedNumber(ITreeAttribute tree, string key, double fallback, double min, double max)
        {
            double value = tree.GetDouble(key, fallback);
            if (double.IsFinite(value))
                return Math.Clamp(value, min, max);
            Api?.Logger?.Warning("[compostbin] Invalid saved {0} at {1}; using {2}.", key, Pos, fallback);
            return fallback;
        }

        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tesselator)
        {
            MeshData mesh = GenMesh(tesselator, Sealed, contentsMeshFullness);
            if (mesh != null)
            {
                mesher.AddMeshData(mesh);
                return true;
            }

            return false;
        }

        private MeshData GenMesh(ITesselatorAPI tesselator, bool sealedBin, float fullness)
        {
            if (Block == null)
                return null;
            ICoreClientAPI capi = Api as ICoreClientAPI;
            if (capi == null)
                return null;

            AssetLocation shapeLocation;
            if (sealedBin)
            {
                shapeLocation = AssetLocation.Create("game:block/wood/barrel/closed");
            }
            else
            {
                shapeLocation = AssetLocation.Create("game:block/wood/barrel/empty");
            }

            shapeLocation = shapeLocation.WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
            Shape shape = Vintagestory.API.Common.Shape.TryGet(capi, shapeLocation);
            if (shape == null)
                return null;

            tesselator.TesselateShape(Block, shape, out MeshData mesh);

            if (!sealedBin && fullness > 0)
            {
                Shape contentsShape = Vintagestory.API.Common.Shape.TryGet(capi,
                    new AssetLocation("game:shapes/block/wood/barrel/contents.json"));
                if (contentsShape != null)
                {
                    tesselator.TesselateShape(Block, contentsShape, out MeshData contentsMesh);
                    contentsMesh.Translate(0, fullness * 10f / 16f, 0);
                    mesh.AddMeshData(contentsMesh);
                }
            }
            return mesh;
        }
    }
}
