using System.Reflection;
using CompostBin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

static class SettingsChecks
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        void Near(double a, double b, string message) => Check(Math.Abs(a-b)<1e-6, message + $": {a} vs {b}");
        var settings = new CompostBinConfig();
        foreach (var property in typeof(CompostSettings).GetProperties())
        {
            var range = property.GetCustomAttribute<SettingRangeAttribute>();
            if (range == null) property.SetValue(settings, false);
            else property.SetValue(settings, Convert.ChangeType(range.Max, property.PropertyType));
        }
        settings.Validate();
        var tree = new TreeAttribute();
        settings.Publish(tree);
        var received = TreeAttribute.CreateFromBytes(tree.ToBytes());
        var restored = CompostBinConfig.Read(received.GetString(CompostBinConfig.SettingsKey));
        foreach (var property in typeof(CompostSettings).GetProperties())
            Check(Equals(property.GetValue(settings),property.GetValue(restored)), "World serialization retains " + property.Name);
        // The world-edit screen parses the tree's JSON, rather than its binary representation.
        var preview = JObject.Parse(received.ToJsonToken());
        var previewSettings = CompostBinConfig.Read((string)preview[CompostBinConfig.SettingsKey]);
        var legacySettings = CompostBinConfig.Read(JsonConvert.SerializeObject(settings));
        foreach (var property in typeof(CompostSettings).GetProperties())
        {
            Check(Equals(property.GetValue(settings), property.GetValue(previewSettings)), "Save preview retains " + property.Name);
            Check(Equals(property.GetValue(settings), property.GetValue(legacySettings)), "Legacy JSON retains " + property.Name);
        }
        Check(CompostBinConfig.Read("base64:invalid!").CompostingDurationHours == 480, "Malformed encoding uses defaults");
        settings.TopHeatConductance = double.NaN;
        settings.RotPerCompost = 0;
        settings.IdealGreenBrownRatioMin = 10;
        settings.IdealGreenBrownRatioMax = 1;
        Check(settings.Validate().Count == 3, "Reject invalid independent and related settings");
        Check(settings.RotPerCompost == 4 && settings.TopHeatConductance == 1.1 && settings.IdealGreenBrownRatioMin == 1, "Invalid settings reset without division by zero");

        var air = Enumerable.Repeat(double.NaN,6).ToArray();
        var state = new CompostPhysics.State { Temperature=50,DryMass=8,Greens=5,Browns=3,Water=4,Oxygen=.8 };
        CompostPhysics.Advance(state,50,air,.005,out _,out double bio);
        var doubled = new CompostBinConfig { BiologicalHeatMultiplier=2 };
        CompostPhysics.Advance(state,50,air,.005,out _,out double bio2,doubled);
        Near(bio2,2*bio,"Biological heating multiplier controls generated heat");
        state.Temperature=70;state.Water=1;
        CompostPhysics.Advance(state,70,air,.005,out double fuel,out _);
        CompostPhysics.Advance(state,70,air,.005,out double fuel2,out _,new CompostBinConfig{ChemicalHeatMultiplier=2});
        Near(fuel2,2*fuel,"Chemical heat multiplier also scales fuel consumption");
        var noEvap = CompostPhysics.Advance(state,70,air,.005,out _,out _,new CompostBinConfig{EvaporationMultiplier=0,ChemicalHeatMultiplier=0});
        Near(noEvap.Water,state.Water,"Evaporation can be disabled");
        state.Greens=0;state.Browns=0;state.Temperature=20;state.Oxygen=.5;
        var noAir = CompostPhysics.Advance(state,20,air,.005,out _,out _,new CompostBinConfig{AerationRecoveryMultiplier=0});
        Near(noAir.Oxygen,.5,"Aeration recovery can be disabled");
        var neighbors=(double[])air.Clone();neighbors[0]=80;
        var isolated = CompostPhysics.Advance(state,20,neighbors,.005,out _,out _,new CompostBinConfig{EnableNeighborHeatExchange=false});
        Near(isolated.Temperature,20,"Disabled neighbors use ambient, not warm-neighbor insulation");
        var exchange=CompostPhysics.Advance(state,20,neighbors,.005,out _,out _,new CompostBinConfig{NeighborHeatExchangeMultiplier=2});
        Check(exchange.Temperature>20,"Configured neighboring exchange warms barrel");
        var customFaces=new CompostBinConfig{TopHeatConductance=3,BottomHeatConductance=.2};
        Near(CompostPhysics.SharedConductance(4,customFaces),CompostPhysics.SharedConductance(5,customFaces),"Custom vertical conductances remain symmetric");
        state.Temperature=130;state.Browns=1;state.Water=0;state.Oxygen=.8;
        Check(!CompostPhysics.CanIgnite(state,new CompostBinConfig{EnableSelfIgnition=false}),"Fire toggle disables ignition");
        Check(!CompostPhysics.CanIgnite(state,new CompostBinConfig{IgnitionTemperature=140}),"Ignition temperature is configurable");
        Check(!CompostPhysics.CanIgnite(state,new CompostBinConfig{IgnitionMinAeration=.9}),"Ignition oxygen is configurable");
        state.Water=1;
        Check(!CompostPhysics.CanIgnite(state,new CompostBinConfig{IgnitionMaxMoisture=.05}),"Ignition moisture is configurable");

        double now=100;
        var calendar=Stub.Make<IGameCalendar>((m,a)=>m.Name=="get_TotalHours"?now:null);
        var access=Stub.Make<IBlockAccessor>((m,a)=>m.Name=="GetClimateAt"?new ClimateCondition{Temperature=20}:null);
        var compost=new Item{Code=new AssetLocation("game:compost"),MaxStackSize=64};
        var world=Stub.Make<IWorldAccessor>((m,a)=>m.Name switch {
            "get_Config"=>tree,"get_Calendar"=>calendar,"get_BlockAccessor"=>access,
            "get_AllOnlinePlayers"=>Array.Empty<IPlayer>(),"GetItem"=>compost,_=>null });
        var api=Stub.Make<ICoreAPI>((m,a)=>m.Name switch {"get_World"=>world,"get_Side"=>EnumAppSide.Server,_=>null});
        var bin=new FireTestBin{Api=api,Pos=new BlockPos(0,0,0),Block=new Block{Code=new AssetLocation("compostbin:compostbin")}};
        bin.Inventory.Api=api;
        settings=new CompostBinConfig();settings.Publish(tree);
        var green=new Item{Code=new AssetLocation("game:carrot"),TransitionableProps=new[]{new TransitionableProperties{Type=EnumTransitionType.Perish}}};
        var brown=new Item{Code=new AssetLocation("game:drygrass")};
        brown.CollectibleBehaviors=new CollectibleBehavior[]{new CompostItemBehavior(brown,true)};
        bin.Inventory[0].Itemstack=new ItemStack(green,64);
        bin.Inventory[1].Itemstack=new ItemStack(brown,64);
        double baseline=bin.Inventory.GetTransitionSpeedMul(EnumTransitionType.Perish,bin.Inventory[0].Itemstack);
        typeof(BECompostBin).GetField("pileTemperature",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(bin,17d);
        typeof(BECompostBin).GetField("oxygen",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(bin,.1d);
        Near(bin.DecompositionRate,1,"Cold poorly aerated compost does not preserve food");
        Near(bin.Inventory.GetTransitionSpeedMul(EnumTransitionType.Perish,bin.Inventory[0].Itemstack),1,"Food inventory receives normal spoilage floor");
        typeof(BECompostBin).GetField("pileTemperature",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(bin,75d);
        Near(bin.DecompositionRate,0,"Overheated compost retains biological cutoff");
        typeof(BECompostBin).GetField("pileTemperature",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(bin,20d);
        typeof(BECompostBin).GetField("oxygen",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(bin,.8d);
        settings.DecompositionSpeedMultiplier=2;settings.BrownDecompositionSpeedMultiplier=3;settings.Publish(tree);
        Near(bin.Inventory.GetTransitionSpeedMul(EnumTransitionType.Perish,bin.Inventory[0].Itemstack),baseline*2,"Green rate uses general speed setting");
        Near(bin.Inventory.GetTransitionSpeedMul(EnumTransitionType.Perish,bin.Inventory[1].Itemstack),baseline*6,"Brown rate combines general and brown speed settings");
        typeof(BECompostBin).GetField("pileTemperature",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(bin,60d);
        typeof(BECompostBin).GetField("oxygen",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(bin,.2d);
        settings.TurningCoolingFraction=.5;settings.TurningAeration=.7;settings.TurningHintAerationThreshold=.6;settings.Publish(tree);
        Check(bin.IsTurningDue(now),"Configured hint appears before turning");
        bin.OnTurned();Near(bin.GetTemperature(),40,"Turning cooling fraction applied");
        Check(!bin.IsTurningDue(now),"Configured aeration clears turning hint");
        settings.WateringAmount=0;settings.Publish(tree);
        bin.OnWatered();Near(bin.GetTemperature(),40,"Zero watering amount has no cooling effect");
        settings.WateringAmount=4;settings.Publish(tree);
        double oldMoisture=bin.Moisture;bin.OnWatered();Check(bin.Moisture>oldMoisture&&bin.GetTemperature()<40,"Configured watering supplies water and cooling");
        settings.EnvironmentalWaterMultiplier=0;settings.Publish(tree);
        oldMoisture=bin.Moisture;bin.CoolNow(1,null);Near(bin.Moisture,oldMoisture,"Environmental water can be disabled");
        bin.Inventory[0].Itemstack=new ItemStack(new Item{Code=new AssetLocation("game:rot")},16);
        bin.Inventory[1].Itemstack=null;
        settings.MinimumRotToSeal=16;settings.RotPerCompost=2;settings.CompostingDurationHours=1;settings.Publish(tree);
        Check(bin.CanSeal(),"Configured minimum permits smaller batch");
        bin.SealBin();now+=1;
        typeof(BECompostBin).GetMethod("FinishPhysicsTick",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(bin,null);
        Check(!bin.Sealed&&bin.Inventory[0].StackSize==8&&bin.Inventory[0].Itemstack.Collectible==compost,"Configured yield produces correct compost count");
        Console.WriteLine($"Passed {checks} expanded-setting checks (world serialization, validation, physics controls, native rates, maintenance, conversion).");
    }
}
