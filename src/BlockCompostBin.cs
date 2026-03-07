using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
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
        public override void OnLoaded(ICoreAPI api)
        {
            base.OnLoaded(api);

            // Grant the bin interaction priority even when sneaking,
            // so the shovel turning mechanic (sneak + right-click) fires
            // before the shovel's own OnHeldInteractStart.
            PlacedPriorityInteract = true;
        }

        public override bool OnBlockInteractStart(
            IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (blockSel == null) return base.OnBlockInteractStart(world, byPlayer, blockSel);

            if (!world.Claims.TryAccess(byPlayer, blockSel.Position, EnumBlockAccessFlags.Use))
            {
                return false;
            }

            BECompostBin be = world.BlockAccessor.GetBlockEntity(blockSel.Position) as BECompostBin;
            if (be == null) return base.OnBlockInteractStart(world, byPlayer, blockSel);

            // A sealed bin accepts no interaction — the composting rite is underway
            if (be.Sealed) return true;

            ItemSlot activeSlot = byPlayer.InventoryManager.ActiveHotbarSlot;
            bool isSneaking = byPlayer.WorldData.EntityControls.ShiftKey;

            // ── Turning: sneak + right-click with shovel aerates the pile ──
            if (isSneaking && byPlayer.InventoryManager.ActiveTool == EnumTool.Shovel)
            {
                if (world.Side == EnumAppSide.Server)
                {
                    be.OnTurned();

                    // The shovel pays its toll — 1 durability consumed
                    if (activeSlot.Itemstack != null)
                    {
                        activeSlot.Itemstack.Collectible.DamageItem(
                            world, byPlayer.Entity, activeSlot, 1);
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
                if (remainingWater >= 2f)
                {
                    if (world.Side == EnumAppSide.Server)
                    {
                        be.OnWatered();

                        // Consume 2 seconds of water from the can
                        wateringCan.SetRemainingWateringSeconds(
                            activeSlot.Itemstack, remainingWater - 2f);
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
