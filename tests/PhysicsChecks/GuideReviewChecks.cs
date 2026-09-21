using System.Reflection;
using CompostBin;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

static class GuideReviewChecks
{
    private static IPlayerInventoryManager testManager;
    private static bool SupplyInventoryManager(ref IPlayerInventoryManager __result)
    {
        __result = testManager;
        return false;
    }
    public static void Run()
    {
        int checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        var registrationApi = Stub.Make<ICoreAPI>((m, a) => null);
        var first = new CompostBinModSystem();
        var second = new CompostBinModSystem();
        first.Start(registrationApi); second.Start(registrationApi);
        first.Dispose();
        Check(Harmony.HasAnyPatches("compostbin.native-brown-transition"), "One disposed mod instance must not unpatch the other");
        first.Dispose();
        Check(Harmony.HasAnyPatches("compostbin.native-brown-transition"), "Repeated disposal does not release another instance's lease");
        second.Dispose();
        Check(!Harmony.HasAnyPatches("compostbin.native-brown-transition"), "Final disposal cleans up owned patches");

        bool allowed = false;
        int opened = 0, dropped = 0;
        EnumAppSide side = EnumAppSide.Server;
        Item output = null;
        var calendar = Stub.Make<IGameCalendar>((m,a) => m.Name == "get_TotalHours" ? 1000d : null);
        var claims = Stub.Make<ILandClaimAPI>((m,a) => m.Name == "TryAccess" ? allowed : null);
        var manager = Stub.Make<IPlayerInventoryManager>((m,a) => {
            if (m.Name == "OpenInventory") opened++;
            return m.ReturnType == typeof(bool) ? false : null;
        });
        // IPlayer has an internal abstract method in 1.22, so DispatchProxy cannot
        // implement it. Use the real player type with only its inventory getter replaced.
        var playerType = Assembly.Load("VintagestoryLib").GetType("Vintagestory.Server.ServerPlayer", true);
        var player = (IPlayer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(playerType);
        testManager = manager;
        var playerPatch = new Harmony("compostbin.tests.review-player");
        playerPatch.Patch(playerType.GetProperty("InventoryManager").GetMethod,
            prefix: new HarmonyMethod(typeof(GuideReviewChecks).GetMethod(nameof(SupplyInventoryManager), BindingFlags.Static | BindingFlags.NonPublic)));
        try
        {
        var loader = Stub.Make<IModLoader>((m,a) => null);
        var world = Stub.Make<IWorldAccessor>((m,a) => {
            if (m.Name == "SpawnItemEntity") { dropped++; return null; }
            return m.Name switch { "get_Side" => side, "get_Claims" => claims, "get_Calendar" => calendar,
                "get_AllOnlinePlayers" => Array.Empty<IPlayer>(), "GetItem" => output, _ => null };
        });
        var api = Stub.Make<ICoreAPI>((m,a) => m.Name switch {
            "get_Side" => side, "get_World" => world, "get_ModLoader" => loader, _ => null });
        FireTestBin MakeBin()
        {
            var bin = new FireTestBin { Api = api, Pos = new BlockPos(0,0,0), Block = new Block { Code = new AssetLocation("compostbin:compostbin") } };
            bin.Inventory.Api = api;
            bin.Inventory[0].Itemstack = new ItemStack(new Item { Code = new AssetLocation("game:rot") },64);
            return bin;
        }
        var target = MakeBin();
        target.OnReceivedClientPacket(player, (int)EnumBlockEntityPacketId.Open, null);
        target.OnReceivedClientPacket(player, BECompostBin.SealPacketId, null);
        target.OnReceivedClientPacket(player, 7, null); // Must reject before decoding inventory data.
        target.OnReceivedClientPacket(null, BECompostBin.SealPacketId, null);
        Check(opened == 0 && !target.Sealed && target.Inventory[0].StackSize == 64, "Unauthorized/null packets cannot open, seal, or change contents");
        allowed = true;
        byte[] forgedSettings = System.Text.Encoding.UTF8.GetBytes("{\"CompostingDurationHours\":1,\"RotPerCompost\":1,\"EnableSelfIgnition\":false,\"sealedSinceTotalHours\":0}");
        target.OnReceivedClientPacket(player, 2000, forgedSettings);
        Check(target.CompostingDurationHours == 480 && target.Settings.RotPerCompost == 4 && target.Settings.EnableSelfIgnition,
            "Unrecognized block packet cannot inject settings");
        target.OnReceivedClientPacket(player, (int)EnumBlockEntityPacketId.Open, null);
        target.OnReceivedClientPacket(player, BECompostBin.SealPacketId, forgedSettings);
        Check(opened == 1 && target.Sealed, "Authorized opening and sealing still work");
        Check(target.SealedSinceTotalHours == 1000 && target.CompostingDurationHours == 480,
            "Seal packet cannot supply its own timestamp or conversion duration");
        side = EnumAppSide.Client;
        var clientBin = MakeBin();
        clientBin.OnBlockBroken();
        Check(dropped == 0, "Client breaking does not spawn inventory items");
        side = EnumAppSide.Server;
        var serverBin = MakeBin(); serverBin.OnBlockBroken();
        Check(dropped == 1 && serverBin.Inventory.Empty, "Server breaking drops inventory once");

        var finish = typeof(BECompostBin).GetMethod("CompleteComposting", BindingFlags.Instance | BindingFlags.NonPublic);
        foreach (var badOutput in new Item[] { null, new Item { Code = new AssetLocation("game:compost"), MaxStackSize = 0 } })
        {
            output = badOutput;
            var bin = MakeBin(); bin.Sealed = true;
            finish.Invoke(bin, null);
            Check(!bin.Sealed && bin.Inventory[0].StackSize == 64 && bin.Inventory[0].Itemstack.Collectible.Code.Path == "rot",
                "Invalid output item preserves rot and unlocks the bin");
        }
        var corrupt = new TreeAttribute();
        foreach (string key in new[] { "pileTemperature", "oxygen", "brownBurnRemainder", "smolderConsumptionRemainder", "sealedSinceTotalHours", "lastTurnedTotalHours" })
            corrupt.SetDouble(key, double.NaN);
        var recovered = MakeBin(); recovered.FromTreeAttributes(corrupt,world);
        var saved = new TreeAttribute(); recovered.ToTreeAttributes(saved);
        foreach (string key in new[] { "pileTemperature", "oxygen", "brownBurnRemainder", "smolderConsumptionRemainder", "sealedSinceTotalHours", "lastTurnedTotalHours" })
            Check(double.IsFinite(saved.GetDouble(key)), "Recovered finite saved value: " + key);
        var stack = new ItemStack(new Item { Code = new AssetLocation("game:rot") });
        stack.Attributes.GetOrAddTreeAttribute("temperature").SetDouble("compostWater",double.NaN);
        Check(double.IsFinite(CompostItemBehavior.WaterRatio(stack)), "Corrupt item moisture does not poison heat calculations");
        Console.WriteLine($"Passed {checks} guide-review checks (patch lifetime, server authority, item preservation, saved numbers).");
        }
        finally { playerPatch.UnpatchAll("compostbin.tests.review-player"); testManager = null; }
    }
}
