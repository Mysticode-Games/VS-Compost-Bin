using System.Reflection;
using CompostBin;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

static class PeatIntegrationChecks
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        void Near(double actual, double expected, string message) =>
            Check(Math.Abs(actual - expected) < 1e-5, $"{message}: {actual} != {expected}");
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        double now = 0;
        var config = new TreeAttribute();
        var random = new Random(17);
        var peat = new Item { Code = new AssetLocation("game:peatbrick"), MaxStackSize = 32, ItemId = 1 };
        var rot = new Item { Code = new AssetLocation("game:rot"), MaxStackSize = 64, ItemId = 2 };
        var grass = new Item { Code = new AssetLocation("game:drygrass"), MaxStackSize = 64, ItemId = 3 };
        var foreign = new Item { Code = new AssetLocation("othermod:peatbrick"), MaxStackSize = 32, ItemId = 4 };
        var items = new[] { peat, rot, grass, foreign };
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
        CompostMaterialRegistry.Initialize(api);
        CompostMaterialRegistry.Initialize(api);
        Check(peat.TransitionableProps.Length == 1 && peat.CollectibleBehaviors.Length == 1,
            "Peat registry is idempotent");
        Check(CompostItemBehavior.HasPeatTransition(new ItemStack(peat)) &&
            !CompostItemBehavior.IsBrown(new ItemStack(peat)), "Peat has its own transition category");
        Check(foreign.TransitionableProps == null && !CompostItemBehavior.IsPeat(new ItemStack(foreign)),
            "Other-domain peat names do not acquire peat behavior");
        FireTestBin NewBin()
        {
            var result = new FireTestBin { Api = api, Pos = new BlockPos(0, 0, 0),
                Block = new Block { Code = new AssetLocation("compostbin:compostbin") } };
            result.Inventory.Api = api;
            return result;
        }
        CompostPhysics.State State(BECompostBin bin) => (CompostPhysics.State)
            typeof(BECompostBin).GetMethod("ReadPhysicsState", hidden).Invoke(bin, null);
        var bin = NewBin();
        var chest = new InventoryGeneric(2, "peat-check", null) { Api = api };
        chest[0].Itemstack = new ItemStack(peat, 16);
        chest[1].Itemstack = new ItemStack(foreign, 16);
        var patch = new Harmony("compostbin.peat-integration-checks");
        patch.PatchAll(typeof(CompostBrownRatePatch).Assembly);
        try
        {
            Check(bin.Inventory[0].CanHold(chest[0]), "Native peat can enter the bin");
            Check(!bin.Inventory[0].CanHold(chest[1]), "Unrelated peat-name item is rejected");
            peat.UpdateAndGetTransitionStates(world, chest[0]);
            now = 100;
            Near(peat.UpdateAndGetTransitionStates(world, chest[0])[0].TransitionedHours, 0,
                "Peat pauses outside the bin");
            Near(peat.GetTransitionRateMul(world, chest[0], EnumTransitionType.Perish), 0,
                "Outside peat rate is zero");
            bin.Inventory[0].Itemstack = chest[0].Itemstack;
            for (int i = 1; i < 8; i++) bin.Inventory[i].Itemstack = new ItemStack(grass, 64);
            Near(State(bin).OrganicStacks, 7, "Organic dosage counts full native stacks");
            Near(CompostPhysics.PeatDose(State(bin)), 1, "Sixteen peat beside seven stacks is the target dose");
            Near(bin.DecompositionRate, 1.25, "Target dose boosts baseline decomposition");
            bin.Inventory[0].Itemstack.StackSize = 32;
            Near(CompostPhysics.PeatDose(State(bin)), 2, "Full peat stack doubles the dose");
            Near(bin.DecompositionRate, 1.25, "Excess peat does not double useful speed bonus");
            bin.Inventory[0].Itemstack = null;
            Near(bin.DecompositionRate, 1, "Decomposition bonus stops when peat is gone");

            // Run the actual game transition pipeline, including output replacement.
            foreach (var scenario in new[] { (Hours: 6d, Yield: 0.0625, Output: 2),
                (Hours: 12d, Yield: 0.125, Output: 4), (Hours: 6d, Yield: 0d, Output: 0) })
            {
                new CompostBinConfig { PeatDecompositionHours = scenario.Hours,
                    PeatRotYield = scenario.Yield }.Publish(config);
                var active = NewBin();
                active.Inventory[0].Itemstack = new ItemStack(peat, 32);
                typeof(BECompostBin).GetField("AllowBrownAdvance", hidden).SetValue(active, true);
                peat.UpdateAndGetTransitionStates(world, active.Inventory[0]);
                Near(peat.GetTransitionRateMul(world, active.Inventory[0], EnumTransitionType.Perish),
                    144 / scenario.Hours, "Configured peat duration sets native transition rate");
                active.Sealed = true;
                Near(peat.GetTransitionRateMul(world, active.Inventory[0], EnumTransitionType.Perish), 0,
                    "Sealed peat stops decomposing");
                active.Sealed = false;
                for (int tick = 0; tick < (int)(scenario.Hours * 10) - 1; tick++)
                {
                    now += .1;
                    peat.UpdateAndGetTransitionStates(world, active.Inventory[0]);
                }
                Check(active.Inventory[0].Itemstack?.Collectible == peat, "Peat does not finish before configured duration");
                now += .2;
                peat.UpdateAndGetTransitionStates(world, active.Inventory[0]);
                Check(scenario.Output == 0 ? active.Inventory[0].Empty :
                    active.Inventory[0].Itemstack?.Collectible == rot && active.Inventory[0].StackSize == scenario.Output,
                    "Native peat completion honors low configurable rot yield");
            }

            new CompostBinConfig().Publish(config);
            now = 0;
            var turned = NewBin();
            turned.OnTurned();
            var saved = new TreeAttribute();
            turned.ToTreeAttributes(saved);
            Check(saved.GetBool("hasBeenTurned") && saved.GetDouble("lastTurnedTotalHours") == 0,
                "Turning at hour zero is saved explicitly");
            var restored = NewBin();
            restored.FromTreeAttributes(saved, world);
            Check(State(restored).HasBeenTurned, "Hour-zero turning survives reload");
            now = 3;
            Near(State(restored).TurnAgeHours, 3, "Turn benefit age survives reload");
            var legacy = new TreeAttribute();
            legacy.SetDouble("lastTurnedTotalHours", 2);
            restored.FromTreeAttributes(legacy, world);
            Check(State(restored).HasBeenTurned, "Existing positive turn timestamps migrate");
            restored.FromTreeAttributes(new TreeAttribute(), world);
            Check(!State(restored).HasBeenTurned, "Untouched legacy bin does not gain turning benefit");
        }
        finally { patch.UnpatchAll("compostbin.peat-integration-checks"); }
        Console.WriteLine($"Passed {checks} peat inventory, transition, dosage, and persistence checks.");
    }
}
