using System.Reflection;
using CompostBin;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

static class BrownDependencyChecks
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        void Near(double actual, double expected, string message) =>
            Check(Math.Abs(actual - expected) < 1e-4, $"{message}: {actual} != {expected}");
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        double now = 100;
        var config = new TreeAttribute();
        var random = new Random(31);
        var grass = new Item { Code = new AssetLocation("game:drygrass"), MaxStackSize = 64, ItemId = 1 };
        var carrot = new Item { Code = new AssetLocation("game:carrot"), MaxStackSize = 64, ItemId = 2 };
        var rot = new Item { Code = new AssetLocation("game:rot"), MaxStackSize = 64, ItemId = 3 };
        var compost = new Item { Code = new AssetLocation("game:compost"), MaxStackSize = 64, ItemId = 4 };
        var peat = new Item { Code = new AssetLocation("game:peatbrick"), MaxStackSize = 32, ItemId = 5 };
        var items = new[] { grass, carrot, rot, compost, peat };
        var calendar = Stub.Make<IGameCalendar>((m, a) => m.Name == "get_TotalHours" ? now : null);
        var accessor = Stub.Make<IBlockAccessor>((m, a) => m.Name == "GetClimateAt"
            ? new ClimateCondition { Temperature = 20 } : null);
        var world = Stub.Make<IWorldAccessor>((m, a) => m.Name switch {
            "get_Calendar" => calendar, "get_Side" => EnumAppSide.Server,
            "get_Config" => config, "get_Rand" => random, "get_BlockAccessor" => accessor,
            "get_Collectibles" => items.Cast<CollectibleObject>().ToList(),
            "GetItem" => a[0] is int id ? items.FirstOrDefault(i => i.ItemId == id)
                : items.FirstOrDefault(i => i.Code.Equals(a[0])),
            _ => null
        });
        var api = Stub.Make<ICoreAPI>((m, a) => m.Name switch {
            "get_World" => world, "get_Side" => EnumAppSide.Server, _ => null
        });
        foreach (var item in items)
            typeof(CollectibleObject).GetField("api", hidden).SetValue(item, api);
        var output = new JsonItemStack { Type = EnumItemClass.Item, Code = rot.Code, StackSize = 1 };
        Check(output.Resolve(world, "brown dependency test"), "Native green output resolves");
        carrot.TransitionableProps = new[] { new TransitionableProperties {
            Type = EnumTransitionType.Perish, FreshHours = NatFloat.createUniform(143, 0),
            TransitionHours = NatFloat.createUniform(1, 0), TransitionedStack = output, TransitionRatio = 1
        } };
        CompostMaterialRegistry.Initialize(api);
        FireTestBin NewBin()
        {
            var result = new FireTestBin { Api = api, Pos = new BlockPos(0, 0, 0),
                Block = new Block { Code = new AssetLocation("compostbin:compostbin") } };
            result.Inventory.Api = api;
            typeof(BECompostBin).GetField("pileTemperature", hidden).SetValue(result, 20d);
            typeof(BECompostBin).GetField("physicsLoaded", hidden).SetValue(result, true);
            return result;
        }
        CompostPhysics.State State(BECompostBin bin) => (CompostPhysics.State)
            typeof(BECompostBin).GetMethod("ReadPhysicsState", hidden).Invoke(bin, null);
        void Tick(BECompostBin bin, double hours)
        {
            now += hours;
            typeof(BECompostBin).GetMethod("ApplyPhysics", hidden).Invoke(bin,
                new object[] { State(bin), 0d, hours });
        }
        void Seed(ItemSlot slot, Item item, float progress = 50)
        {
            slot.Itemstack = new ItemStack(item, 32);
            item.UpdateAndGetTransitionStates(world, slot);
            item.SetTransitionState(slot.Itemstack, EnumTransitionType.Perish, progress);
        }
        double Progress(ItemStack stack) =>
            ((FloatArrayAttribute)stack.Attributes.GetTreeAttribute("transitionstate")["transitionedHours"]).value[0];
        double BrownRate(ItemSlot slot) => grass.GetTransitionRateMul(world, slot, EnumTransitionType.Perish);
        var patch = new Harmony("compostbin.brown-dependency-checks");
        patch.PatchAll(typeof(CompostBrownRatePatch).Assembly);
        try
        {
            var bin = NewBin();
            Seed(bin.Inventory[0], grass);
            Near(BrownRate(bin.Inventory[0]), 0, "Brown-only bin has zero native perish rate");
            Tick(bin, 100);
            Near(Progress(bin.Inventory[0].Itemstack), 50, "Browns preserve progress through long inactive intervals");
            now += 100;
            grass.UpdateAndGetTransitionStates(world, bin.Inventory[0]);
            Near(Progress(bin.Inventory[0].Itemstack), 50, "Native inventory updates also pause without greens");

            foreach (var inert in new[] { rot, compost, peat })
            {
                bin.Inventory[1].Itemstack = new ItemStack(inert, 16);
                Near(State(bin).Greens, 0, $"{inert.Code.Path} does not count as greens");
                Near(BrownRate(bin.Inventory[0]), 0, $"{inert.Code.Path} cannot activate brown decomposition");
                Tick(bin, .1);
                Near(Progress(bin.Inventory[0].Itemstack), 50, $"Browns pause beside {inert.Code.Path}");
            }
            double peatBefore = Progress(bin.Inventory[1].Itemstack);
            Tick(bin, .1);
            Check(Progress(bin.Inventory[1].Itemstack) > peatBefore, "Peat still decomposes independently without greens");

            Seed(bin.Inventory[1], carrot, 0);
            double rate = BrownRate(bin.Inventory[0]);
            Check(rate > 0, "Adding greens activates brown decomposition");
            Tick(bin, .1);
            Near(Progress(bin.Inventory[0].Itemstack), 50 + .1 * rate,
                "Adding greens resumes only the active interval without inactive catchup");
            Check(Progress(bin.Inventory[1].Itemstack) > 0, "Native greens still spoil normally");
            double activeProgress = Progress(bin.Inventory[0].Itemstack);
            bin.Inventory[1].Itemstack = null;
            Tick(bin, 100);
            Near(Progress(bin.Inventory[0].Itemstack), activeProgress, "Removing greens pauses at the existing progress");
            Seed(bin.Inventory[1], carrot, 0);
            rate = BrownRate(bin.Inventory[0]);
            Tick(bin, .1);
            Near(Progress(bin.Inventory[0].Itemstack), activeProgress + .1 * rate,
                "Readding greens resumes without catching up the second pause");

            bin.Inventory[1].Itemstack = null;
            var split = bin.Inventory[0].TakeOut(8);
            double savedProgress = Progress(split);
            Near(Progress(bin.Inventory[0].Itemstack), savedProgress, "Splitting preserves brown progress on both stacks");
            var splitBin = NewBin();
            splitBin.Inventory[0].Itemstack = split;
            Tick(splitBin, 5);
            Near(Progress(split), savedProgress, "A split brown stack remains paused without greens");
            var saved = new TreeAttribute();
            splitBin.ToTreeAttributes(saved);
            var restored = NewBin();
            restored.FromTreeAttributes(saved, world);
            Check(restored.Inventory[0].Itemstack?.Collectible == grass, "Saved brown inventory resolves on reload");
            Near(Progress(restored.Inventory[0].Itemstack), savedProgress, "Reload preserves existing decomposition progress");
            Tick(restored, 5);
            Near(Progress(restored.Inventory[0].Itemstack), savedProgress, "Reloaded browns remain paused without greens");
            Seed(restored.Inventory[1], carrot, 0);
            rate = BrownRate(restored.Inventory[0]);
            Tick(restored, .1);
            Near(Progress(restored.Inventory[0].Itemstack), savedProgress + .1 * rate,
                "Reloaded browns resume from saved progress when greens return");

            var partial = NewBin();
            Seed(partial.Inventory[0], grass);
            Tick(partial, .04);
            Seed(partial.Inventory[1], carrot, 0);
            rate = BrownRate(partial.Inventory[0]);
            Tick(partial, .1);
            Near(Progress(partial.Inventory[0].Itemstack), 50 + .1 * rate,
                "Pending inactive time below the update threshold is excluded when greens arrive");
            double partialProgress = Progress(partial.Inventory[0].Itemstack);
            Tick(partial, .04);
            partial.Inventory[1].Itemstack = null;
            Tick(partial, .04);
            Near(Progress(partial.Inventory[0].Itemstack), partialProgress,
                "Removing greens before a pending update cannot advance browns");
            Seed(partial.Inventory[1], carrot, 0);
            Tick(partial, .1);
            Near(Progress(partial.Inventory[0].Itemstack), partialProgress + .1 * BrownRate(partial.Inventory[0]),
                "Returning greens does not revive pending progress from before removal");

            var shortActive = NewBin();
            Seed(shortActive.Inventory[0], grass);
            Tick(shortActive, .04);
            Seed(shortActive.Inventory[1], carrot, 0);
            rate = BrownRate(shortActive.Inventory[0]);
            Tick(shortActive, .02);
            Near(Progress(shortActive.Inventory[0].Itemstack), 50,
                "An active fraction below the native threshold waits for enough active time");
            Tick(shortActive, .04);
            Tick(shortActive, .02);
            Near(Progress(shortActive.Inventory[0].Itemstack), 50 + .08 * rate,
                "Short active fractions survive shared updates and accumulate without inactive time");

            foreach (int brownIndex in new[] { 0, 1 })
            {
                var finishing = NewBin();
                Seed(finishing.Inventory[brownIndex], grass);
                Seed(finishing.Inventory[1 - brownIndex], carrot, 143.99f);
                Tick(finishing, .1);
                Check(finishing.Inventory[1 - brownIndex].Itemstack?.Collectible == rot,
                    $"Last green becomes rot with brown in slot {brownIndex}");
                Near(Progress(finishing.Inventory[brownIndex].Itemstack), 50,
                    $"Brown pauses in the same update that consumes the last green, slot {brownIndex}");
                Tick(finishing, 1);
                Near(Progress(finishing.Inventory[brownIndex].Itemstack), 50,
                    $"Last-green completion keeps browns paused in subsequent updates, slot {brownIndex}");
            }
        }
        finally { patch.UnpatchAll("compostbin.brown-dependency-checks"); }
        Console.WriteLine($"Passed {checks} brown dependency, native transition, and persistence checks.");
    }
}
