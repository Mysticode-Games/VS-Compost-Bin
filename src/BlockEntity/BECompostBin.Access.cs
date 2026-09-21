using System;
using Vintagestory.API.Common;

#nullable disable

namespace CompostBin
{
    public partial class BECompostBin
    {
        private bool inventoryAccessible = true;

        private void EndInventoryAccess()
        {
            inventoryAccessible = false;
            if (Api?.Side != EnumAppSide.Server || Api.World.AllOnlinePlayers == null)
                return;
            foreach (IPlayer player in Api.World.AllOnlinePlayers)
                if (((InventoryCompostBin)inventory).HasOpenSession(player))
                    CloseInventoryForPlayer(player);
        }

        // Always use the server's player position and reach, never packet coordinates.
        internal bool CanPlayerAccess(IPlayer player, bool requireSession)
        {
            if (Api?.Side != EnumAppSide.Server || !inventoryAccessible || Pos == null || Sealed || IsBurning
                || player?.InventoryManager == null || player.Entity?.Pos == null
                || player.WorldData == null)
                return false;

            var position = player.Entity.Pos;
            var eye = player.Entity.LocalEyePos;
            double reach = player.WorldData.PickingRange;
            if (position.Dimension != Pos.dimension || eye == null || !double.IsFinite(reach) || reach < 0)
                return false;

            double x = position.X + eye.X;
            double y = position.Y + eye.Y;
            double z = position.Z + eye.Z;
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
                return false;

            // Measure to the barrel's block bounds, with the GUI's half-block slack.
            double dx = Math.Max(0, Math.Max(Pos.X - x, x - (Pos.X + 1d)));
            double dy = Math.Max(0, Math.Max(Pos.Y - y, y - (Pos.Y + 1d)));
            double dz = Math.Max(0, Math.Max(Pos.Z - z, z - (Pos.Z + 1d)));
            if (dx * dx + dy * dy + dz * dz > (reach + 0.5) * (reach + 0.5))
                return false;

            return Api.World.Claims?.TryAccess(player, Pos, EnumBlockAccessFlags.Use) == true
                && (!requireSession || inventory.HasOpened(player));
        }
    }
}
