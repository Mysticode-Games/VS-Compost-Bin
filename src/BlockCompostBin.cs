using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

#nullable disable

namespace CompostBin
{
    /// <summary>
    /// The compost bin block — a barrel-shaped vessel that accelerates decay.
    /// Handles player interaction and block breaking.
    /// </summary>
    public class BlockCompostBin : Block
    {
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

            if (!byPlayer.WorldData.EntityControls.ShiftKey)
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
