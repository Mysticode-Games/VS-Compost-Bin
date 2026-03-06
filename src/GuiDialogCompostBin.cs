using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

#nullable disable

namespace CompostBin
{
    /// <summary>
    /// The scrying glass through which the summoner peers into the compost bin —
    /// 8 slots arranged in a 4x2 grid, with a seal button that manifests
    /// only when the rite's prerequisites are met.
    /// </summary>
    public class GuiDialogCompostBin : GuiDialogBlockEntity
    {
        EnumPosFlag screenPos;
        BECompostBin beCompostBin;

        protected override double FloatyDialogPosition => 0.6;
        protected override double FloatyDialogAlign => 0.8;
        public override double DrawOrder => 0.2;

        public GuiDialogCompostBin(
            string dialogTitle, InventoryBase inventory, BlockPos blockEntityPos,
            ICoreClientAPI capi, BECompostBin be)
            : base(dialogTitle, inventory, blockEntityPos, capi)
        {
            if (IsDuplicate) return;
            beCompostBin = be;
        }

        private void SetupDialog()
        {
            // 4x2 slot grid for the 8 vessels of decay
            int[] slotIds = new int[] { 0, 1, 2, 3, 4, 5, 6, 7 };
            ElementBounds slotGridBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, 30, 4, 2);

            double top = slotGridBounds.fixedHeight + slotGridBounds.fixedY + 10;

            // Seal button — appears below the grid
            ElementBounds sealButtonBounds = ElementBounds.Fixed(0, top, 80, 25);

            // Status text — to the right of the button
            ElementBounds statusTextBounds = ElementBounds.Fixed(90, top, 200, 60);

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;
            bgBounds.WithChildren(slotGridBounds, sealButtonBounds, statusTextBounds);

            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithFixedAlignmentOffset(
                    IsRight(screenPos) ? -GuiStyle.DialogToScreenPadding : GuiStyle.DialogToScreenPadding, 0)
                .WithAlignment(
                    IsRight(screenPos) ? EnumDialogArea.RightMiddle : EnumDialogArea.LeftMiddle);

            SingleComposer = capi.Gui
                .CreateCompo("blockentitycompostbin" + BlockEntityPosition, dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar(DialogTitle, OnTitleBarClose)
                .BeginChildElements(bgBounds)
                    .AddItemSlotGrid(Inventory, SendInvPacket, 4, slotIds, slotGridBounds, "slotGrid")
                    .AddSmallButton(
                        Lang.Get("compostbin:compostbin-seal"),
                        OnSealClick,
                        sealButtonBounds,
                        EnumButtonStyle.Normal,
                        "sealButton")
                    .AddDynamicText(
                        GetStatusText(),
                        CairoFont.WhiteDetailText(),
                        statusTextBounds,
                        "statusText")
                .EndChildElements()
                .Compose();

            UpdateSealButtonVisibility();
        }

        /// <summary>
        /// Refresh the displayed contents and seal button state.
        /// </summary>
        public void UpdateContents()
        {
            if (SingleComposer == null) return;

            SingleComposer.GetDynamicText("statusText")?.SetNewText(GetStatusText());
            UpdateSealButtonVisibility();
        }

        private string GetStatusText()
        {
            if (beCompostBin == null)
            {
                beCompostBin = capi.World.BlockAccessor.GetBlockEntity(BlockEntityPosition) as BECompostBin;
            }
            if (beCompostBin == null) return "";

            if (beCompostBin.Sealed)
            {
                double hoursPassed = capi.World.Calendar.TotalHours - beCompostBin.SealedSinceTotalHours;
                string passedText = hoursPassed > 24
                    ? Lang.Get("{0} days", Math.Floor(hoursPassed / capi.World.Calendar.HoursPerDay * 10) / 10)
                    : Lang.Get("{0} hours", Math.Floor(hoursPassed));
                string totalText = Lang.Get("{0} days", Math.Round(480.0 / capi.World.Calendar.HoursPerDay, 1));
                return Lang.Get("compostbin:compostbin-composting", passedText, totalText);
            }

            // Count the rot in all slots
            int totalRot = 0;
            bool allRot = true;
            bool hasItems = false;

            for (int i = 0; i < Inventory.Count; i++)
            {
                ItemSlot slot = Inventory[i];
                if (slot.Empty) continue;
                hasItems = true;

                if (slot.Itemstack.Collectible.Code.Path == "rot")
                {
                    totalRot += slot.Itemstack.StackSize;
                }
                else
                {
                    allRot = false;
                }
            }

            if (!hasItems)
            {
                return Lang.Get("compostbin:compostbin-empty");
            }

            if (allRot && totalRot >= 64)
            {
                int compostYield = totalRot / 4;
                return Lang.Get("compostbin:compostbin-willproduce", compostYield,
                    Lang.Get("item-compost"),
                    Lang.Get("{0} days", Math.Round(480.0 / capi.World.Calendar.HoursPerDay, 1)));
            }

            return Lang.Get("compostbin:compostbin-contents", totalRot);
        }

        private void UpdateSealButtonVisibility()
        {
            if (SingleComposer == null) return;

            if (beCompostBin == null)
            {
                beCompostBin = capi.World.BlockAccessor.GetBlockEntity(BlockEntityPosition) as BECompostBin;
            }

            bool canSeal = beCompostBin != null && !beCompostBin.Sealed && beCompostBin.CanSeal();
            var sealButton = SingleComposer.GetButton("sealButton");
            if (sealButton != null) sealButton.Visible = canSeal;
        }

        private bool OnSealClick()
        {
            if (beCompostBin == null)
            {
                beCompostBin = capi.World.BlockAccessor.GetBlockEntity(BlockEntityPosition) as BECompostBin;
            }
            if (beCompostBin == null || beCompostBin.Sealed) return true;
            if (!beCompostBin.CanSeal()) return true;

            // Do not invoke SealBin() client-side — the server is the sole authority.
            // Packet 1337 tells the server to seal; it will propagate the state back.
            capi.Network.SendBlockEntityPacket(BlockEntityPosition, 1337);
            capi.World.PlaySoundAt(new AssetLocation("game:sounds/player/seal"), BlockEntityPosition, 0.4, null);

            TryClose();
            return true;
        }

        private void SendInvPacket(object packet)
        {
            capi.Network.SendBlockEntityPacket(
                BlockEntityPosition.X, BlockEntityPosition.Y, BlockEntityPosition.Z, packet);
        }

        private void OnTitleBarClose()
        {
            TryClose();
        }

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            screenPos = GetFreePos("smallblockgui");
            OccupyPos("smallblockgui", screenPos);
            SetupDialog();
        }

        public override void OnGuiClosed()
        {
            SingleComposer.GetSlotGrid("slotGrid")?.OnGuiClosed(capi);
            base.OnGuiClosed();
            FreePos("smallblockgui", screenPos);
        }
    }
}
