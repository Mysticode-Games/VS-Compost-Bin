using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

#nullable disable

namespace CompostBin
{
    /// <summary>
    /// The compost bin block — a barrel-shaped vessel that accelerates decay.
    /// Handles player interaction: GUI access, shovel turning, watering can cooling,
    /// and block breaking.
    /// </summary>
    public class BlockCompostBin : Block
    {
        private WorldInteraction[] turningInteractions = System.Array.Empty<WorldInteraction>();

        public override void OnLoaded(ICoreAPI api)
        {
            base.OnLoaded(api);

            // Grant the bin interaction priority even when sneaking,
            // so the shovel turning mechanic (sneak + right-click) fires
            // before the shovel's own OnHeldInteractStart.
            PlacedPriorityInteract = true;

            if (api.Side == EnumAppSide.Client)
            {
                var shovels = new List<ItemStack>();
                foreach (CollectibleObject collectible in api.World.Collectibles)
                {
                    if (collectible.Code != null && collectible.Tool == EnumTool.Shovel)
                        shovels.Add(new ItemStack(collectible));
                }

                turningInteractions = new[]
                {
                    new WorldInteraction
                    {
                        ActionLangCode = "compostbin:blockhelp-turn",
                        MouseButton = EnumMouseButton.Right,
                        HotKeyCode = "shift",
                        Itemstacks = shovels.ToArray(),
                        GetMatchingStacks = (interaction, selection, entitySelection) =>
                        {
                            var bin = selection == null ? null :
                                api.World.BlockAccessor.GetBlockEntity(selection.Position) as BECompostBin;
                            return bin != null && bin.IsTurningDue(api.World.Calendar.TotalHours)
                                ? interaction.Itemstacks : null;
                        }
                    }
                };
            }
        }

        public override WorldInteraction[] GetPlacedBlockInteractionHelp(
            IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
        {
            return turningInteractions.Append(base.GetPlacedBlockInteractionHelp(world, selection, forPlayer));
        }

        public override bool OnBlockInteractStart(
            IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (blockSel == null)
                return base.OnBlockInteractStart(world, byPlayer, blockSel);

            if (!world.Claims.TryAccess(byPlayer, blockSel.Position, EnumBlockAccessFlags.Use))
            {
                return false;
            }

            BECompostBin be = world.BlockAccessor.GetBlockEntity(blockSel.Position) as BECompostBin;
            if (be == null)
                return base.OnBlockInteractStart(world, byPlayer, blockSel);

            // Sealed conversion and native burning lock interaction.
            if (be.Sealed || be.IsBurning)
                return true;

            ItemSlot activeSlot = byPlayer.InventoryManager.ActiveHotbarSlot;
            bool isSneaking = byPlayer.WorldData.EntityControls.ShiftKey;

            // ── Turning: sneak + right-click with shovel aerates the pile ──
            if (isSneaking && byPlayer.InventoryManager.ActiveTool == EnumTool.Shovel)
            {
                if (world.Side == EnumAppSide.Server)
                {
                    be.OnTurned();

                    // Apply the server's configured shovel durability cost.
                    if (activeSlot.Itemstack != null && be.Settings.TurningDurabilityCost > 0)
                    {
                        activeSlot.Itemstack.Collectible.DamageItem(
                            world, byPlayer.Entity, activeSlot, be.Settings.TurningDurabilityCost, destroyOnZeroDurability: true);
                        activeSlot.MarkDirty();
                    }
                }

                // The sound of earth being turned
                world.PlaySoundAt(
                    new AssetLocation("game:sounds/block/dirt1"),
                    blockSel.Position.X + 0.5, blockSel.Position.Y + 0.5, blockSel.Position.Z + 0.5,
                    byPlayer);

                return true;
            }

            // ── Watering: non-sneak right-click with watering can cools the pile ──
            if (!isSneaking && activeSlot.Itemstack?.Collectible is BlockWateringCan wateringCan)
            {
                float remainingWater = wateringCan.GetRemainingWateringSeconds(activeSlot.Itemstack);
                float waterCost = (float)be.Settings.WateringCanSeconds;
                if (remainingWater >= waterCost)
                {
                    if (world.Side == EnumAppSide.Server)
                    {
                        be.OnWatered();

                        // Consume the configured watering-can charge.
                        wateringCan.SetRemainingWateringSeconds(
                            activeSlot.Itemstack, remainingWater - waterCost);
                        activeSlot.MarkDirty();
                    }

                    // The hiss of water meeting heated organic matter
                    world.PlaySoundAt(
                        new AssetLocation("game:sounds/effect/extinguish"),
                        blockSel.Position.X + 0.5, blockSel.Position.Y + 0.5, blockSel.Position.Z + 0.5,
                        byPlayer);

                    return true;
                }
            }

            // ── Default: open the inventory GUI ──
            if (!isSneaking)
            {
                be.OnPlayerRightClick(byPlayer);
                return true;
            }

            return base.OnBlockInteractStart(world, byPlayer, blockSel);
        }

        /// <summary>
        /// Override drops to return the bin itself as an empty block.
        /// The entity's OnBlockBroken handles spilling contents separately.
        /// </summary>
        public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1)
        {
            return new ItemStack[] { new ItemStack(this) };
        }
    }
}
