using System.Reflection;
using CompostBin;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

class FireTestBin : BECompostBin
{
    public override long RegisterGameTickListener(Action<float> callback, int interval, int delay = 0) => 1;
    public override void MarkDirty(bool redraw = false, IPlayer player = null) { }
}

static class NativeFireChecks
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        foreach (bool blocked in new[] { false, true })
        {
            var bin = new FireTestBin { Pos = new BlockPos(0, 0, 0) };
            bin.Block = new Block { BlockId = 8, Code = new AssetLocation("compostbin:compostbin"),
                CombustibleProps = new CombustibleProperties { BurnDuration = 30, BurnTemperature = 800 } };
            var fire = new Block { BlockId = 9, Code = new AssetLocation("game:fire") };
            Block above = new Block { BlockId = blocked ? 10 : 0, Replaceable = blocked ? 0 : 10000 };
            var topEntity = new FireTestBin { Pos = bin.Pos.UpCopy(), Block = fire };
            var ownBurning = new BEBehaviorBurning(bin);
            var topBurning = new BEBehaviorBurning(topEntity);
            bin.Behaviors.Add(ownBurning);
            topEntity.Behaviors.Add(topBurning);
            int removals = 0;
            var accessor = Stub.Make<IBlockAccessor>((m, a) => {
                if (m.Name == "GetBlock") return ((BlockPos)a[0]).Y == 0 ? bin.Block : above;
                if (m.Name == "GetBlockEntity") return ((BlockPos)a[0]).Y == 0 ? bin : topEntity;
                if (m.Name == "SetBlock") { if ((int)a[0] == 0) removals++; else above = fire; }
                return null;
            });
            var calendar = Stub.Make<IGameCalendar>((m, a) => m.Name == "get_TotalHours" ? 100d : null);
            var configTree = new TreeAttribute();
            var world = Stub.Make<IWorldAccessor>((m, a) => m.Name switch {
                "GetBlock" => fire, "get_BlockAccessor" => accessor, "get_Config" => configTree,
                "get_Calendar" => calendar, "get_AllOnlinePlayers" => Array.Empty<IPlayer>(), _ => null
            });
            var loader = Stub.Make<IModLoader>((m, a) => null);
            var api = Stub.Make<ICoreAPI>((m, a) => m.Name switch {
                "get_World" => world, "get_ModLoader" => loader, "get_Side" => EnumAppSide.Server, _ => null
            });
            bin.Api = topEntity.Api = api;
            ownBurning.Initialize(api, null);
            topBurning.Initialize(api, null);
            typeof(BECompostBin).GetMethod("ConfigureBurningBehavior", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(bin, null);
            bin.Inventory[0].Itemstack = new ItemStack(new Item { Code = new AssetLocation("game:rot") }, 32);
            new CompostBinConfig { EnableSelfIgnition = false }.Publish(configTree);
            typeof(BECompostBin).GetMethod("Ignite", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(bin, null);
            Check(!ownBurning.IsBurning && !topBurning.IsBurning && bin.Inventory[0].StackSize == 32,
                "Disabled self-ignition preserves bin and contents");
            new CompostBinConfig().Publish(configTree);
            typeof(BECompostBin).GetMethod("Ignite", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(bin, null);
            Check(ownBurning.IsBurning, "Bin must burn even under ceiling");
            Check(ownBurning.FirePos.Equals(bin.Pos) && ownBurning.FuelPos.Equals(bin.Pos), "Bin burns in place");
            Check(bin.Inventory.Empty && bin.Inventory.TakeLocked && bin.Inventory.PutLocked, "Burning consumes and locks contents");
            Check(topBurning.IsBurning == !blocked, "Upper fire only in available space");
            Check(above.BlockId == (blocked ? 10 : 9), "Solid ceiling preserved");
            var saved = new TreeAttribute();
            ownBurning.ToTreeAttributes(saved);
            var restored = new BEBehaviorBurning(new FireTestBin { Pos = bin.Pos.Copy(), Block = bin.Block });
            restored.FromTreeAttributes(saved, world);
            Check(restored.IsBurning && restored.FirePos.Equals(bin.Pos) && restored.FuelPos.Equals(bin.Pos), "Native burning save restores location and state");
            Check(restored.remainingBurnDuration == 30, "Native burn duration persists");
            foreach (int packet in new[] { 7, 8, 9, 1000 }) bin.OnReceivedClientPacket(null, packet, null);
            Check(!bin.CanSeal() && !bin.IsTurningDue(100), "Burning excludes sealing and turning");
            ownBurning.IsBurning = false;
            ownBurning.OnFireDeath(false);
            Check(removals == 0 && !bin.Inventory.PutLocked, "Extinguishing preserves and unlocks bin");
            ownBurning.OnFireDeath(true);
            Check(removals > 0, "Burnout removes bin through native callback");
        }
        Console.WriteLine($"Passed {checks} native burning checks with open and blocked tops.");
    }
}
