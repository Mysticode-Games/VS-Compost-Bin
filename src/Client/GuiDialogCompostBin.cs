using Cairo;
using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

#nullable disable

namespace CompostBin
{
    /// <summary>
    /// Displays inventory, pile conditions, and the seal action.
    /// Status text resizes with wrapping to keep the action button accessible.
    /// </summary>
    public class GuiDialogCompostBin : GuiDialogBlockEntity
    {
        EnumPosFlag screenPos;
        BECompostBin beCompostBin;

        // The draw callback uses the most recent synchronized temperature.
        int cachedDisplayTemp;
        private bool disposed;

        protected override double FloatyDialogPosition => 0.6;
        protected override double FloatyDialogAlign => 0.8;
        public override double DrawOrder => 0.2;

        public GuiDialogCompostBin(
            string dialogTitle, InventoryBase inventory, BlockPos blockEntityPos,
            ICoreClientAPI capi, BECompostBin be)
            : base(dialogTitle, inventory, blockEntityPos, capi)
        {
            if (IsDuplicate)
                return;
            beCompostBin = be;
        }

        private void SetupDialog()
        {
            // Match the eight-slot inventory with a 4x2 grid.
            int[] slotIds = new int[] { 0, 1, 2, 3, 4, 5, 6, 7 };
            ElementBounds slotGridBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, 30, 4, 2);

            double top = slotGridBounds.fixedHeight + slotGridBounds.fixedY + 10;

            // Thermometer bar — immediately below the slots, fills left-to-right
            ElementBounds barBounds = ElementBounds.Fixed(0, top, 165, 16);

            // Temperature overlay — left-aligned on the bar
            ElementBounds tempLeftBounds = ElementBounds.Fixed(3, top, 60, 16);

            // Warning overlay — right-aligned on the bar
            ElementBounds warnRightBounds = ElementBounds.Fixed(0, top, 162, 16);

            // Reserve room for wrapped status messages below the bar.
            double statusTop = top + 20;
            ElementBounds statusTextBounds = ElementBounds.Fixed(0, statusTop, 250, 75);

            double buttonTop = statusTop + statusTextBounds.fixedHeight + 8;

            // Seal button — below the thermometer
            ElementBounds sealButtonBounds = ElementBounds.Fixed(0, buttonTop, 80, 25);

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;
            bgBounds.WithChildren(slotGridBounds, statusTextBounds, barBounds, tempLeftBounds, warnRightBounds, sealButtonBounds);

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
                    .AddDynamicText(
                        "",
                        CairoFont.WhiteDetailText(),
                        statusTextBounds,
                        "statusText")
                    .AddDynamicCustomDraw(barBounds, OnThermometerDraw, "thermometer")
                    .AddDynamicText(
                        "",
                        CairoFont.WhiteSmallText()
                            .WithStroke(ColorUtil.BlackArgbDouble, 0.75),
                        tempLeftBounds,
                        "tempText")
                    .AddDynamicText(
                        "",
                        CairoFont.WhiteSmallText()
                            .WithOrientation(EnumTextOrientation.Right)
                            .WithStroke(ColorUtil.BlackArgbDouble, 0.75),
                        warnRightBounds,
                        "warnText")
                    .AddSmallButton(
                        Lang.Get("compostbin:compostbin-seal"),
                        OnSealClick,
                        sealButtonBounds,
                        EnumButtonStyle.Normal,
                        "sealButton")
                .EndChildElements()
                .Compose();

            UpdateContents();
        }

        /// <summary>
        /// Cairo draw callback — renders a rectangular thermometer bar, filling
        /// left-to-right with an amber-to-red gradient proportional to temperature.
        /// </summary>
        private void OnThermometerDraw(Context ctx, ImageSurface surface, ElementBounds currentBounds)
        {
            double w = currentBounds.InnerWidth;
            double h = currentBounds.InnerHeight;

            // Dark thermometer background.
            GuiElement.RoundRectangle(ctx, 0, 0, w, h, 1);
            ctx.SetSourceRGBA(0.15, 0.15, 0.15, 1);
            ctx.Fill();

            // Light border
            GuiElement.RoundRectangle(ctx, 0, 0, w, h, 1);
            ctx.SetSourceRGBA(0.4, 0.4, 0.4, 1);
            ctx.LineWidth = 1;
            ctx.Stroke();

            double fillRel = Math.Clamp(cachedDisplayTemp / beCompostBin.Settings.IgnitionTemperature, 0, 1);
            if (fillRel > 0.01)
            {
                double fillW = w * fillRel;

                // Gradient fill — amber to red, left to right
                GuiElement.RoundRectangle(ctx, 0, 0, fillW, h, 1);
                LinearGradient gradient = new LinearGradient(0, 0, w, 0);
                gradient.AddColorStop(0, new Color(0.85, 0.55, 0.1, 1));
                gradient.AddColorStop(1, new Color(0.9, 0.15, 0.1, 1));
                ctx.SetSource(gradient);
                ctx.Fill();
                gradient.Dispose();
            }
        }

        /// <summary>
        /// Refresh all displayed state: status text, thermometer arrow, °C readout, seal button.
        /// The unified update — all state rendered in one pass.
        /// </summary>
        public void UpdateContents()
        {
            if (SingleComposer == null)
                return;

            if (beCompostBin == null)
            {
                beCompostBin = capi.World.BlockAccessor.GetBlockEntity(BlockEntityPosition) as BECompostBin;
            }
            if (beCompostBin == null)
                return;

            var tempText = SingleComposer.GetDynamicText("tempText");
            var warnText = SingleComposer.GetDynamicText("warnText");
            int displayTemp = beCompostBin.GetDisplayTemperature();
            if (cachedDisplayTemp != displayTemp)
            {
                cachedDisplayTemp = displayTemp;
                SingleComposer.GetCustomDraw("thermometer")?.Redraw();
            }
            tempText?.SetNewText(displayTemp <= 25 ? Lang.Get("Cold") : displayTemp + "\u00B0C");
            warnText?.SetNewText(beCompostBin.Overheating ? Lang.Get("compostbin:physics-overheating") : "");

            // Sealed conversion progress.
            if (beCompostBin.Sealed)
            {
                double hoursPassed = capi.World.Calendar.TotalHours - beCompostBin.SealedSinceTotalHours;
                string passedText = hoursPassed > 24
                    ? Lang.Get("{0} days", Math.Floor(hoursPassed / capi.World.Calendar.HoursPerDay * 10) / 10)
                    : Lang.Get("{0} hours", Math.Floor(hoursPassed));
                string totalText = Lang.Get("{0} days", Math.Round(beCompostBin.CompostingDurationHours / capi.World.Calendar.HoursPerDay, 1));

                SetStatusText(Lang.Get("compostbin:compostbin-composting", passedText, totalText));
                UpdateSealButtonVisibility();
                return;
            }

            // --- Census the inventory ---
            int totalRot = 0;
            bool allRot = true;
            bool hasItems = false;

            for (int i = 0; i < Inventory.Count; i++)
            {
                ItemSlot slot = Inventory[i];
                if (slot.Empty)
                    continue;
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

            // Empty inventory.
            if (!hasItems)
            {
                SetStatusText(Lang.Get("compostbin:compostbin-empty"));
                UpdateSealButtonVisibility();
                return;
            }

            // --- All rot, sufficient for sealing ---
            if (allRot && totalRot >= beCompostBin.MinRotForSeal)
            {
                int compostYield = totalRot / beCompostBin.CompostYieldDivisor;
                SetStatusText(Lang.Get("compostbin:compostbin-willproduce", compostYield,
                    Lang.Get("item-compost"),
                    Lang.Get("{0} days", Math.Round(beCompostBin.CompostingDurationHours / capi.World.Calendar.HoursPerDay, 1))));
                UpdateSealButtonVisibility();
                return;
            }

            // --- Active decomposition: compute temperature and speed ---
            SetStatusText(beCompostBin.PhysicsDescription());

            UpdateSealButtonVisibility();
        }

        private void SetStatusText(string text)
        {
            var statusText = SingleComposer.GetDynamicText("statusText");
            if (statusText == null)
                return;
            text = Lang.Get("compostbin:compostbin-fullness",
                Math.Round(beCompostBin.GetFullness() * 100, 1)) + "\n" + text;

            // Measure at the current GUI scale so longer translations also fit.
            double textHeight = new TextDrawUtil().GetMultilineTextHeight(
                statusText.Font, text, statusText.Bounds.InnerWidth);
            double height = Math.Max(75, Math.Ceiling(textHeight / RuntimeEnv.GUIScale) + 4);
            if (statusText.Bounds.fixedHeight != height)
            {
                statusText.Bounds.fixedHeight = height;
                SingleComposer.GetButton("sealButton").Bounds.fixedY =
                    statusText.Bounds.fixedY + height + 8;
                SingleComposer.ReCompose();
            }

            statusText.SetNewText(text);
        }

        private void UpdateSealButtonVisibility()
        {
            if (SingleComposer == null)
                return;

            if (beCompostBin == null)
            {
                beCompostBin = capi.World.BlockAccessor.GetBlockEntity(BlockEntityPosition) as BECompostBin;
            }

            bool canSeal = beCompostBin != null && !beCompostBin.Sealed && beCompostBin.CanSeal();
            var sealButton = SingleComposer.GetButton("sealButton");
            if (sealButton != null)
                sealButton.Visible = canSeal;
        }

        private bool OnSealClick()
        {
            if (beCompostBin == null)
            {
                beCompostBin = capi.World.BlockAccessor.GetBlockEntity(BlockEntityPosition) as BECompostBin;
            }
            if (beCompostBin == null || beCompostBin.Sealed)
                return true;
            if (!beCompostBin.CanSeal())
                return true;

            // The server validates contents and claim access before sealing.
            capi.Network.SendBlockEntityPacket(BlockEntityPosition, BECompostBin.SealPacketId);
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
            SingleComposer?.GetSlotGrid("slotGrid")?.OnGuiClosed(capi);
            base.OnGuiClosed();
            FreePos("smallblockgui", screenPos);
        }

        public override void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            base.Dispose();
        }
    }
}
