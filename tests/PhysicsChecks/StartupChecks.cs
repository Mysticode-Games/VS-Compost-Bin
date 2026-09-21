using System.Reflection;
using CompostBin;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;

static class StartupChecks
{
    public static void Run()
    {
        double now = 10000;
        bool calendarReady = false, unregistered = false;
        Action<float> tick = null;
        var calendar = Stub.Make<IGameCalendar>((m, a) => m.Name == "get_TotalHours" ? now : null);
        var accessor = Stub.Make<IBlockAccessor>((m, a) =>
            m.Name == "GetClimateAt" ? new ClimateCondition { Temperature = 20 } : null);
        var world = Stub.Make<IServerWorldAccessor>((m, a) => m.Name switch {
            "get_Calendar" => calendarReady ? calendar : null,
            "get_BlockAccessor" => accessor,
            _ => null
        });
        var events = Stub.Make<IServerEventAPI>((m, a) => {
            if (m.Name == "RegisterGameTickListener") { tick = (Action<float>)a[0]; return 42L; }
            if (m.Name == "UnregisterGameTickListener") unregistered = (long)a[0] == 42;
            return null;
        });
        var api = Stub.Make<ICoreServerAPI>((m, a) => m.Name switch {
            "get_World" => world, "get_Event" => events, "get_Side" => EnumAppSide.Server, _ => null
        });
        var mod = new CompostBinModSystem();
        mod.StartServerSide(api); // Calendar is deliberately absent, as during real startup.
        if (tick == null) throw new Exception("Physics listener was not registered at startup");
        var bin = new FireTestBin { Api = api, Pos = new BlockPos(0, 0, 0) };
        typeof(BECompostBin).GetField("pileTemperature", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(bin, 40d);
        mod.Register(bin);
        tick(2); // An early callback must also tolerate the absent calendar.
        calendarReady = true;
        tick(2);
        if (bin.GetTemperature() != 40) throw new Exception("Startup must not simulate historical world time");
        now += 0.1;
        tick(2);
        if (bin.GetTemperature() >= 40) throw new Exception("Registered barrel physics did not advance after startup");
        mod.Unregister(bin);
        double previous = bin.GetTemperature();
        now += 0.1;
        tick(2);
        if (bin.GetTemperature() != previous) throw new Exception("Unloaded barrel continued simulating");
        mod.Dispose();
        if (!unregistered) throw new Exception("Physics listener was not removed on disposal");
        Console.WriteLine("Passed startup lifecycle check: missing calendar, first tick, barrel update, unload, disposal.");
    }
}
