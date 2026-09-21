using System.Reflection;
using System.Runtime.CompilerServices;
using CompostBin;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Server;

// IPlayer contains an internal member that DispatchProxy cannot implement. Keep
// real player/world-data objects; replace only the manager's engine dependency.
sealed class SecurityTestPlayer : IDisposable
{
    private static readonly Dictionary<ServerPlayer, IPlayerInventoryManager> Managers = new();
    private readonly Harmony patch = new("compostbin.tests.security-player");
    public ServerPlayer Player { get; }
    public ServerWorldPlayerData Data { get; }
    public SecurityTestPlayer(IPlayerInventoryManager manager)
    {
        Player = (ServerPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ServerPlayer));
        Data = ServerWorldPlayerData.CreateNew("Security test", "security-test-player");
        typeof(ServerWorldPlayerData).GetField("connected", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(Data, true);
        Data.PickingRange = 4.5f;
        Data.EntityPlayer.Pos.SetPos(0.5, 0, 2);
        typeof(ServerPlayer).GetField("worlddata", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(Player, Data);
        Managers.Add(Player, manager);
        patch.Patch(typeof(ServerPlayer).GetProperty(nameof(ServerPlayer.InventoryManager)).GetMethod,
            prefix: new HarmonyMethod(typeof(SecurityTestPlayer).GetMethod(nameof(InventoryManager), BindingFlags.Static | BindingFlags.NonPublic)));
    }
    private static bool InventoryManager(ServerPlayer __instance, ref IPlayerInventoryManager __result)
    {
        if (!Managers.TryGetValue(__instance, out var manager)) return true;
        __result = manager;
        return false;
    }
    public void Dispose() { Managers.Remove(Player); patch.UnpatchAll(patch.Id); }
}

static class InventorySecurityChecks
{
    private sealed class TestRot : Item
    {
        public override string GetHeldItemName(ItemStack itemStack) => "Rot";
    }
    public static void Run()
    {
        int checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        bool allowed = true;
        int opened = 0, closed = 0;
        IPlayer player = null;
        var inventories = new Dictionary<string, IInventory>();
        var manager = Stub.Make<IPlayerInventoryManager>((m, a) => {
            if (m.Name == "get_Inventories") return inventories;
            if (m.Name == "GetInventory") return inventories.GetValueOrDefault((string)a[0]);
            if (m.Name == "OpenInventory") { opened++; ((IInventory)a[0]).Open(player); }
            if (m.Name == "CloseInventory") { closed++; ((IInventory)a[0]).Close(player); }
            return m.ReturnType == typeof(bool) ? false : null;
        });
        using var fixture = new SecurityTestPlayer(manager);
        player = fixture.Player;
        var logger = Stub.Make<ILogger>((m, a) => null);
        var claims = Stub.Make<ILandClaimAPI>((m, a) => m.Name == "TryAccess" ? allowed : null);
        var calendar = Stub.Make<IGameCalendar>((m, a) => m.Name == "get_TotalHours" ? 1000d : null);
        var world = Stub.Make<IWorldAccessor>((m, a) => m.Name switch {
            "get_Side" => EnumAppSide.Server, "get_Claims" => claims, "get_Logger" => logger,
            "get_Calendar" => calendar, "get_AllOnlinePlayers" => Array.Empty<IPlayer>(),
            "get_Rand" => new Random(1), _ => null });
        var network = Stub.Make<IServerNetworkAPI>((m, a) => null);
        var loader = Stub.Make<IModLoader>((m, a) => null);
        var api = Stub.Make<ICoreServerAPI>((m, a) => m.Name switch {
            "get_Side" => EnumAppSide.Server, "get_World" => world, "get_Network" => network,
            "get_Logger" => logger, "get_ModLoader" => loader, _ => null });
        var bin = new FireTestBin { Api = api, Pos = new BlockPos(0, 0, 0),
            Block = new Block { Code = new AssetLocation("compostbin:compostbin") } };
        bin.Inventory.LateInitialize("compostbin-security", api);
        var mouse = new InventoryGeneric(1, "mouse-" + player.PlayerUID, null) { Api = api };
        mouse.InvNetworkUtil = new InventoryNetworkUtil(mouse, api);
        mouse.Open(player);
        inventories.Add(bin.Inventory.InventoryID, bin.Inventory);
        inventories.Add(mouse.InventoryID, mouse);
        var rot = new TestRot { Code = new AssetLocation("game:rot"), MaxStackSize = 64 };
        typeof(CollectibleObject).GetField("api", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(rot, api);
        void Reset() {
            bin.Sealed = false; bin.Inventory.TakeLocked = bin.Inventory.PutLocked = false;
            foreach (var slot in bin.Inventory) slot.Itemstack = null;
            bin.Inventory[0].Itemstack = new ItemStack(rot, 64); mouse[0].Itemstack = null;
            bin.Inventory.lastChangedSinceServerStart = mouse.lastChangedSinceServerStart = 0;
        }
        void Open() => bin.OnReceivedClientPacket(player, (int)EnumBlockEntityPacketId.Open, null);
        Packet_Client Activate(int slot = 0) => new() { Id = 7, ActivateInventorySlot = new() {
            TargetInventoryId = bin.Inventory.InventoryID, TargetSlot = slot,
            MouseButton = (int)EnumMouseButton.Left, Priority = (int)EnumMergePriority.DirectMerge } };
        Packet_Client Move(int quantity = 8) => new() { Id = 8, MoveItemstack = new() {
            SourceInventoryId = bin.Inventory.InventoryID, TargetInventoryId = mouse.InventoryID,
            SourceSlot = 0, TargetSlot = 0, Quantity = quantity,
            MouseButton = (int)EnumMouseButton.Left, Priority = (int)EnumMergePriority.DirectMerge } };
        Packet_Client Flip() => new() { Id = 9, Flipitemstacks = new() {
            SourceInventoryId = bin.Inventory.InventoryID, TargetInventoryId = mouse.InventoryID,
            SourceSlot = 0, TargetSlot = 0 } };
        void Send(Packet_Client packet, int? outerId = null) =>
            bin.OnReceivedClientPacket(player, outerId ?? packet.Id, Packet_ClientSerializer.SerializeToBytes(packet));
        void Unchanged(string label) => Check(bin.Inventory[0].StackSize == 64 && mouse[0].Empty && !bin.Sealed, label);

        Reset();
        Send(Activate()); bin.OnReceivedClientPacket(player, BECompostBin.SealPacketId, null);
        Unchanged("Inventory operations and seal require an open session");
        Check(!bin.Inventory.CanPlayerModify(player, player.Entity.Pos), "Destination inventory rejects absent session");
        Open();
        Check(opened == 1 && bin.Inventory.HasOpened(player), "Nearby permitted player can open a session");
        Check(bin.Inventory.CanPlayerModify(player, player.Entity.Pos), "Open session permits inventory destination");
        foreach (var bytes in new byte[][] { null, Array.Empty<byte>(), new byte[] { 0x80 }, new byte[] { 0x0a, 0xff, 0xff, 0xff, 0xff, 0x07 }, new byte[4097] })
        {
            bin.OnReceivedClientPacket(player, 7, bytes);
            Unchanged("Malformed or excessive packet is ignored without mutation");
        }
        var random = new Random(1979);
        for (int i = 0; i < 160; i++)
        {
            var garbage = new byte[random.Next(1, 160)]; random.NextBytes(garbage);
            bin.OnReceivedClientPacket(player, 7 + i % 3, garbage);
        }
        Unchanged("160 deterministic malformed payloads leave inventories untouched and do not throw");
        var validBytes = Packet_ClientSerializer.SerializeToBytes(Activate());
        bin.OnReceivedClientPacket(player, 7, new byte[] { 0x88, 0x80, 0x00 }.Concat(validBytes.Skip(1)).ToArray());
        Unchanged("Overlong key encoding rejected before native decoding");
        bin.OnReceivedClientPacket(player, 7, validBytes.Concat(new byte[] { 8, 7 }).ToArray());
        Unchanged("Nonzero data hidden after zero padding is rejected");
        bin.OnReceivedClientPacket(player, 7, validBytes[..^5]);
        Unchanged("Truncated native inventory packet is ignored");
        foreach (var suffix in new[] { new byte[] { 8, 7 }, new byte[] { 0x10, 1 }, new byte[] { 0x9a, 1, 0 } })
        {
            bin.OnReceivedClientPacket(player, 7, validBytes[..^4].Concat(suffix).ToArray());
            Unchanged("Duplicate ID/body or unknown protobuf field rejected");
        }
        foreach (int slot in new[] { -1, bin.Inventory.Count, int.MaxValue }) { Send(Activate(slot)); Unchanged("Out-of-bounds slot rejected"); }
        Send(Activate(), 8); Unchanged("Outer and embedded packet IDs must agree");
        var foreign = Activate(); foreign.ActivateInventorySlot.TargetInventoryId = "foreign-inventory";
        Send(foreign); Unchanged("Foreign activation inventory rejected");
        foreach (int quantity in new[] { -1, 0 }) { Send(Move(quantity)); Unchanged("Invalid move quantity rejected"); }
        var missing = Move(); missing.MoveItemstack.TargetInventoryId = "missing-inventory";
        Send(missing); Unchanged("Unknown destination rejected");
        var badControls = Activate(); badControls.ActivateInventorySlot.MouseButton = 999;
        Send(badControls); Unchanged("Undefined mouse control rejected");
        badControls = Activate(); badControls.ActivateInventorySlot.Modifiers = 8;
        Send(badControls); Unchanged("Unknown modifier bits rejected");
        var typedUtility = (InventoryNetworkUtil)bin.Inventory.InvNetworkUtil;
        var mixed = Activate(); mixed.MoveItemstack = Move().MoveItemstack;
        typedUtility.HandleClientPacket(player, 7, mixed); Unchanged("Direct typed packet rejects multiple operation bodies");

        player.Entity.Pos.X = 100;
        Open(); Send(Activate()); bin.OnReceivedClientPacket(player, BECompostBin.SealPacketId, null);
        Unchanged("Stale distant session cannot activate or seal");
        Check(opened == 1 && !bin.Inventory.HasOpened(player), "Distant open is denied and stale session is unusable");
        Check(!bin.Inventory.CanPlayerModify(player, new EntityPos(0, 0, 0)), "Caller-supplied position cannot bypass server distance");
        typedUtility.HandleClientPacket(player, 7, Activate());
        Unchanged("Direct typed native packet route denies distant player");
        var operation = new ItemStackMoveOperation(world, EnumMouseButton.Left, 0, EnumMergePriority.DirectMerge) { ActingPlayer = player };
        bin.Inventory.ActivateSlot(0, mouse[0], ref operation);
        Unchanged("Direct native activation path respects server access");
        mouse[0].Itemstack = new ItemStack(rot, 1);
        Check(bin.Inventory.GetBestSuitedSlot(mouse[0], operation).slot == null, "Shift-transfer destination rejects distant player");
        mouse[0].Itemstack = null;
        int beforeClose = closed;
        bin.OnReceivedClientPacket(player, (int)EnumBlockEntityPacketId.Close, null);
        player.Entity.Pos.X = 0.5;
        Check(closed == beforeClose + 1 && !bin.Inventory.HasOpened(player), "Close removes session even after leaving range");
        Open(); player.Entity.Pos.Dimension = 1;
        Send(Activate()); bin.OnReceivedClientPacket(player, BECompostBin.SealPacketId, null);
        Unchanged("Other dimension denied despite existing session");
        player.Entity.Pos.Dimension = 0; Open();
        allowed = false; Send(Activate()); bin.OnReceivedClientPacket(player, BECompostBin.SealPacketId, null);
        Unchanged("Claim revocation invalidates existing session"); allowed = true;
        Open(); player.Entity.Pos.X = double.NaN; Send(Activate()); Unchanged("Non-finite position denied"); player.Entity.Pos.X = 0.5;
        Open(); fixture.Data.PickingRange = float.NaN; Send(Activate()); Unchanged("Non-finite picking range denied"); fixture.Data.PickingRange = 4.5f;

        Open();
        Check(bin.Inventory.HasOpened(player) && mouse.HasOpened(player), "Positive packet fixtures retain both open inventories");
        var utilityType = bin.Inventory.InvNetworkUtil.GetType();
        Check((bool)utilityType.GetMethod("ValidWirePacket", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { Packet_ClientSerializer.SerializeToBytes(Activate()), 7 }), "Native serialized activation passes wire preflight");
        Check((bool)utilityType.GetMethod("ValidRequest", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(bin.Inventory.InvNetworkUtil, new object[] { player, 7, Activate() }), "Native serialized activation passes semantic validation");
        Send(Activate());
        Check(bin.Inventory[0].Empty && mouse[0].StackSize == 64, "Native serialized activation moves contents for authorized session");
        Reset(); var rightClick = Activate(); rightClick.ActivateInventorySlot.MouseButton = (int)EnumMouseButton.Right;
        Send(rightClick);
        Check(bin.Inventory[0].StackSize == 32 && mouse[0].StackSize == 32, "Native serialized right-click splits stack");
        Reset(); var wheel = Activate(); wheel.ActivateInventorySlot.MouseButton = (int)EnumMouseButton.Wheel; wheel.ActivateInventorySlot.Dir = -1;
        Send(wheel);
        Check(bin.Inventory[0].StackSize == 63 && mouse[0].StackSize == 1, "Native serialized wheel moves one item");
        Reset(); Send(Move());
        Check(bin.Inventory[0].StackSize == 56 && mouse[0].StackSize == 8, "Native serialized move transfers requested quantity");
        Reset(); Send(Move(int.MaxValue));
        Check(bin.Inventory[0].Empty && mouse[0].StackSize == 64, "Huge positive request is clamped by native transfer without duplicating items");
        Reset(); Send(Flip());
        Check(bin.Inventory[0].Empty && mouse[0].StackSize == 64, "Native serialized flip exchanges authorized slots");
        Reset();
        var creative = new InventoryPlayerCreative("creative", player.PlayerUID, null) { Api = api };
        var tab = new InventoryGeneric(2, "creative-tab", null) { Api = api };
        creative.CreativeTabs.Add(new CreativeTab("test", tab));
        inventories.Add(creative.InventoryID, creative);
        fixture.Data.GameMode = EnumGameMode.Creative;
        creative.Open(player);
        Check(bin.Inventory.CanPlayerModify(player, player.Entity.Pos), "Creative fixture retains bin session");
        Check(creative.CanPlayerModify(player, player.Entity.Pos), "Native creative inventory permits its owner");
        bool Valid(Packet_Client packet) => (bool)utilityType.GetMethod("ValidRequest", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(bin.Inventory.InvNetworkUtil, new object[] { player, packet.Id, packet });
        var creativeFlip = Flip(); creativeFlip.Flipitemstacks.TargetInventoryId = creative.InventoryID;
        Check(Valid(creativeFlip), "Creative flip permits existing target tab and slot");
        creativeFlip.Flipitemstacks.TargetSlot = 99999;
        Check(Valid(creativeFlip), "Creative disposal slot retains native support");
        creativeFlip.Flipitemstacks.TargetSlot = 2;
        Check(!Valid(creativeFlip), "Creative out-of-range slot rejected before tab selection");
        creativeFlip.Flipitemstacks.TargetSlot = 0; creativeFlip.Flipitemstacks.TargetTabIndex = 999;
        Check(!Valid(creativeFlip), "Unknown creative tab rejected");
        creativeFlip.Flipitemstacks.TargetTabIndex = 0; fixture.Data.GameMode = EnumGameMode.Survival;
        Check(!Valid(creativeFlip), "Survival player cannot route transfers through creative inventory");
        Reset(); bin.OnReceivedClientPacket(player, BECompostBin.SealPacketId, null);
        Check(bin.Sealed && bin.SealedSinceTotalHours == 1000, "Authorized seal uses server time");
        Send(Activate());
        Check(bin.Inventory[0].StackSize == 64 && mouse[0].Empty && !bin.Inventory.HasOpened(player), "Sealed inventory denies stale session packets");
        Reset(); Open();
        bin.OnBlockUnloaded();
        typedUtility.HandleClientPacket(player, 7, Activate());
        Unchanged("Detached bin denies native requests despite a previously open session");
        Console.WriteLine($"Passed {checks} inventory-security checks (session/access guards, malformed packets, native activation/move/flip).");
    }
}
