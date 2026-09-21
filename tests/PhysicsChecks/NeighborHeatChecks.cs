using System.Reflection;
using CompostBin;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;

static class NeighborHeatChecks
{
    public static void Run()
    {
        // Exercise actual neighbor discovery and the registered server callback,
        // using empty barrels to isolate conduction from decomposition and evaporation.
        const double hours = 0.005;
        int checks = 0;
        void Near(double actual, double expected, string label)
        {
            if (Math.Abs(actual - expected) > 1e-7)
                throw new Exception(label + $": {actual} != {expected}");
            checks++;
        }
        for (int face = 0; face < 6; face++)
        {
            var neighbor = new BlockPos(0, 0, 0).AddCopy(BlockFacing.ALLFACES[face]);
            var result = Simulate(neighbor, hours);
            double transfer = CompostPhysics.SharedConductance(face) * 40 * hours;
            Near(result.cold, 20 + transfer / 2, "Cold barrel receives heat on face " + face);
            double ambientLoss = (CompostPhysics.FaceConductance.Sum() - CompostPhysics.FaceConductance[face]) * 40 * hours;
            Near(2 * (result.hot - 60) + 2 * (result.cold - 20), -ambientLoss,
                "Shared-face transfer conserves energy on face " + face);
            var reversed = Simulate(neighbor, hours, reverse: true);
            Near(reversed.hot, result.hot, "Hot barrel independent of registration order");
            Near(reversed.cold, result.cold, "Cold barrel independent of registration order");
        }
        Near(Simulate(new BlockPos(1, 0, 1), hours).cold, 20, "Diagonal barrels do not exchange heat");
        Near(Simulate(new BlockPos(2, 0, 0), hours).cold, 20, "Heat does not jump a gap");
        Near(Simulate(new BlockPos(1, 0, 0), hours, unloadHot: true).cold, 20,
            "Unloaded barrel stops supplying heat");
        Near(Simulate(new BlockPos(1, 0, 0), hours, settings: new CompostBinConfig { EnableNeighborHeatExchange = false }).cold, 20,
            "Server tick honors disabled neighbor exchange");
        var sustained = Simulate(new BlockPos(1, 0, 0), 0.5);
        if (sustained.cold <= 20 || sustained.hot >= 60 || sustained.hot <= sustained.cold)
            throw new Exception("Repeated server substeps failed to move heat hot to cold");
        checks++;
        Console.WriteLine($"Passed {checks} server neighbor heat checks (all six faces, energy, ordering, gaps, unload, repeated steps).");
        Console.WriteLine($"Empty-barrel isolation test after half a game hour: 60 C / 20 C -> {sustained.hot:F2} C / {sustained.cold:F2} C.");
    }

    static (double hot, double cold) Simulate(BlockPos neighbor, double hours, bool reverse = false, bool unloadHot = false, CompostBinConfig settings = null)
    {
        settings ??= new CompostBinConfig();
        var configTree = new Vintagestory.API.Datastructures.TreeAttribute();
        settings.Publish(configTree);
        double now = 100;
        Action<float> tick = null;
        var calendar = Stub.Make<IGameCalendar>((m, a) => m.Name == "get_TotalHours" ? now : null);
        var accessor = Stub.Make<IBlockAccessor>((m, a) => m.Name == "GetClimateAt" ? new ClimateCondition { Temperature = 20 } : null);
        var world = Stub.Make<IServerWorldAccessor>((m, a) => m.Name switch {
            "get_Calendar" => calendar, "get_BlockAccessor" => accessor, "get_Config" => configTree, _ => null
        });
        var events = Stub.Make<IServerEventAPI>((m, a) => {
            if (m.Name == "RegisterGameTickListener") { tick = (Action<float>)a[0]; return 1L; }
            return null;
        });
        var api = Stub.Make<ICoreServerAPI>((m, a) => m.Name switch {
            "get_World" => world, "get_Event" => events, "get_Side" => EnumAppSide.Server, "LoadModConfig" => settings, _ => null
        });
        var hot = new FireTestBin { Api = api, Pos = new BlockPos(0, 0, 0) };
        var cold = new FireTestBin { Api = api, Pos = neighbor };
        typeof(BECompostBin).GetField("pileTemperature", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(hot, 60d);
        var mod = new CompostBinModSystem();
        try
        {
            mod.StartServerSide(api);
            mod.Register(reverse ? cold : hot);
            mod.Register(reverse ? hot : cold);
            if (unloadHot) mod.Unregister(hot);
            tick(2);
            now += hours;
            tick(2);
            return (hot.GetTemperature(), cold.GetTemperature());
        }
        finally { mod.Dispose(); }
    }
}
