using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

#nullable disable

namespace CompostBin
{
    /// <summary>
    /// The heart of the compost bin — an 8-slot vessel that accelerates rot at 150% speed,
    /// seals when filled with 64+ rot, and transmutes rot into compost after 480 hours.
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

        // Dialog reference, client-side only
        GuiDialogCompostBin invDialog;

        // Shape references for sealed/unsealed rendering
        MeshData currentMesh;

        // The dry offerings that decompose by custom timer rather than transition properties
        private static readonly string[] DryOfferingCodes = new string[]
        {
            "drygrass",
            "cattailtops",
            "papyrustops",
            "thatch"
        };

        // Hours for dry offerings to decompose into rot inside the bin.
        // This is the base duration BEFORE the 1.5x speed multiplier is applied,
        // so effective time is 48 / 1.5 = 32 hours.
        private const double DryOfferingDecomposeHours = 48.0;

        public string InventoryClassName => "compostbin";

        public InventoryBase Inventory => inventory;

        public BECompostBin()
        {
            // Conjure the 8-slot inventory with custom compost bin slots
            inventory = new InventoryGeneric(8, null, null, (id, self) =>
            {
                return new ItemSlotCompostBin(self);
            });
            inventory.BaseWeight = 1;

            inventory.SlotModified += OnSlotModified;

            // The acceleration of decay: 1.5x for Perish transitions when unsealed,
            // 0x when sealed (the composting rite tolerates no further rot)
            inventory.OnAcquireTransitionSpeed += OnAcquireTransitionSpeed;
        }

        /// <summary>
        /// The transition speed multiplier — the daemon's breath that hastens decay.
        /// When sealed, all transitions halt. When unsealed, Perish runs at 150%.
        /// </summary>
        private float OnAcquireTransitionSpeed(EnumTransitionType transType, ItemStack stack, float mul)
        {
            if (Sealed) return 0f;
            if (transType == EnumTransitionType.Perish) return 1.5f;
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
        }

        /// <summary>
        /// The pulse of the composting rite — every 3 seconds, the daemon:
        /// 1. Ticks transition states on perishable items (so food actually rots)
        /// 2. Decomposes dry offerings (drygrass, papyrustops, thatch) into rot
        /// 3. Checks whether the sealed bin's composting time has elapsed
        /// </summary>
        private void OnEvery3Second(float dt)
        {
            if (Api.Side != EnumAppSide.Server) return;

            if (!Sealed)
            {
                // Tick transitions on all perishable items — without this, nothing rots
                TickPerishTransitions();

                // Decompose dry offerings that have no innate perish properties
                TickDryOfferings();
            }
            else
            {
                // Check if the composting rite is complete
                double hoursPassed = Api.World.Calendar.TotalHours - SealedSinceTotalHours;
                if (hoursPassed >= 480)
                {
                    CompleteComposting();
                }
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

                if (slot.Itemstack?.Collectible.Code != codeBefore)
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
        /// Dry offerings (drygrass, papyrustops, thatch) have no Perish transition,
        /// so the standard system cannot rot them. Instead, we track when each stack
        /// entered the bin and convert it to rot after DryOfferingDecomposeHours.
        ///
        /// The timestamp is stored on the item stack's attributes as "compostBinInsertedHours".
        /// The 1.5x speed multiplier is applied to the elapsed time.
        /// </summary>
        private void TickDryOfferings()
        {
            double nowHours = Api.World.Calendar.TotalHours;

            for (int i = 0; i < inventory.Count; i++)
            {
                ItemSlot slot = inventory[i];
                if (slot.Itemstack == null) continue;
                if (!IsDryOffering(slot.Itemstack)) continue;

                // Stamp the time of insertion if not already marked
                if (!slot.Itemstack.Attributes.HasAttribute("compostBinInsertedHours"))
                {
                    slot.Itemstack.Attributes.SetDouble("compostBinInsertedHours", nowHours);
                    slot.MarkDirty();
                    continue;
                }

                double insertedHours = slot.Itemstack.Attributes.GetDouble("compostBinInsertedHours");
                double elapsed = (nowHours - insertedHours) * 1.5; // Apply the 1.5x acceleration

                if (elapsed >= DryOfferingDecomposeHours)
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

            // Compute compost yield at 4:1 ratio (64 rot → 16 compost)
            int compostToCreate = totalRot / 4;
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

            return totalRot >= 64;
        }

        /// <summary>
        /// Seal the bin — the composting rite begins.
        /// </summary>
        public void SealBin()
        {
            if (Sealed) return;

            Sealed = true;
            SealedSinceTotalHours = Api.World.Calendar.TotalHours;
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

            // Packet 1337: the seal command
            if (packetid == 1337)
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
                string totalText = Lang.Get("{0} days", Math.Round(480.0 / Api.World.Calendar.HoursPerDay, 1));
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

            // Persist inventory
            inventory.ToTreeAttributes(tree.GetOrAddTreeAttribute("inventory"));
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
        {
            // Restore inventory before base call
            if (inventory == null)
            {
                // This can happen during world load before Initialize
                inventory = new InventoryGeneric(8, null, null, (id, self) =>
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
