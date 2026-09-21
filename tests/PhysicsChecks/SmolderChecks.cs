using System.Reflection;
using CompostBin;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

static class SmolderChecks
{
    public static void Run()
    {
        int checks=0;
        void Check(bool ok,string message) { if(!ok)throw new Exception(message);checks++; }
        var config=new CompostBinConfig{EnableSelfIgnition=false,WateringAmount=32};
        var tree=new TreeAttribute();config.Publish(tree);
        var green=new Item{ItemId=1,Code=new AssetLocation("game:carrot"),TransitionableProps=new[]{new TransitionableProperties{Type=EnumTransitionType.Perish}}};
        var brown=new Item{ItemId=2,Code=new AssetLocation("game:drygrass")};
        brown.CollectibleBehaviors=new CollectibleBehavior[]{new CompostItemBehavior(brown,true)};
        var rot=new Item{ItemId=3,Code=new AssetLocation("game:rot")};
        var compost=new Item{ItemId=4,Code=new AssetLocation("game:compost")};
        Item[] items={null,green,brown,rot,compost};
        var calendar=Stub.Make<IGameCalendar>((m,a)=>m.Name=="get_TotalHours"?100d:null);
        var access=Stub.Make<IBlockAccessor>((m,a)=>m.Name=="GetClimateAt"?new ClimateCondition{Temperature=20}:null);
        object spawnedParticles = null;
        var world=Stub.Make<IWorldAccessor>((m,a)=> {
            if(m.Name=="GetBlock")throw new Exception("Smoldering must not request a native fire block");
            if(m.Name=="SpawnParticles") { spawnedParticles=a[0]; return null; }
            return m.Name switch {
                "get_Config"=>tree,"get_Calendar"=>calendar,"get_BlockAccessor"=>access,"get_Side"=>EnumAppSide.Server,"get_Rand"=>new Random(1),
                "GetItem"=> a[0] is int id ? items[id] : items.FirstOrDefault(i=>i?.Code.Equals(a[0])==true),_=>null};
        });
        var api=Stub.Make<ICoreAPI>((m,a)=>m.Name switch {"get_World"=>world,"get_Side"=>EnumAppSide.Server,_=>null});
        FireTestBin MakeBin()
        {
            var result=new FireTestBin{Api=api,Pos=new BlockPos(0,0,0),Block=new Block{Code=new AssetLocation("compostbin:compostbin")}};
            result.Inventory.Api=api;
            return result;
        }
        var bin=MakeBin();
        bin.Inventory[0].Itemstack=new ItemStack(green,64);
        bin.Inventory[1].Itemstack=new ItemStack(brown,64);
        bin.Inventory[2].Itemstack=new ItemStack(rot,16);
        bin.Inventory[3].Itemstack=new ItemStack(compost,16);
        foreach(var slot in bin.Inventory)if(!slot.Empty)CompostItemBehavior.SetWater(slot.Itemstack,.1);
        var apply=typeof(BECompostBin).GetMethod("ApplyPhysics",BindingFlags.Instance|BindingFlags.NonPublic);
        var finish=typeof(BECompostBin).GetMethod("FinishPhysicsTick",BindingFlags.Instance|BindingFlags.NonPublic);
        var read=typeof(BECompostBin).GetMethod("ReadPhysicsState",BindingFlags.Instance|BindingFlags.NonPublic);
        void Advance(FireTestBin target,double temperature,double hours)
        {
            var state=(CompostPhysics.State)read.Invoke(target,null);state.Temperature=temperature;
            apply.Invoke(target,new object[]{state,0d,hours});finish.Invoke(target,null);
        }
        Advance(bin,110,.01);
        Check(!bin.IsSmoldering&&bin.Inventory[0].StackSize==64,"Below ignition threshold does not destroy contents");
        Advance(bin,130,1);
        Check(bin.IsSmoldering&&!bin.IsBurning,"Disabled ignition enters smoldering without native fire");
        Check(bin.Inventory[0].StackSize==32&&bin.Inventory[1].StackSize==32,"Smoldering consumes both greens and browns at configured total rate");
        Check(bin.Inventory[2].StackSize==16&&bin.Inventory[3].StackSize==16,"Existing rot and compost survive");
        Check(bin.DecompositionRate==0,"Smoldering destroys material instead of producing rot");
        typeof(BECompostBin).GetMethod("OnClientParticleTick",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(bin,new object[]{.2f});
        Check(ReferenceEquals(spawnedParticles,typeof(BECompostBin).GetField("smokeParticles",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null))&&spawnedParticles!=null,
            "Smoldering emits smoke through the existing particle system");
        var saved=new TreeAttribute();bin.ToTreeAttributes(saved);
        var restored=MakeBin();restored.FromTreeAttributes(saved,world);
        Check(restored.IsSmoldering&&restored.Inventory[0].StackSize==32,"Smoldering and remaining contents survive save/load");
        restored.OnWatered();
        Check(!restored.IsSmoldering,"Watering quenches smoldering");
        Advance(bin,130,1);
        Check(!bin.IsSmoldering&&bin.Inventory[0].Empty&&bin.Inventory[1].Empty,"Smoldering stops when fuel is exhausted");
        Check(bin.Inventory[2].StackSize==16&&bin.Inventory[3].StackSize==16,"Exhaustion leaves finished material intact");
        var wet=MakeBin();wet.Inventory[0].Itemstack=new ItemStack(brown,64);
        CompostItemBehavior.SetWater(wet.Inventory[0].Itemstack,2);
        Advance(wet,130,.01);
        Check(!wet.IsSmoldering&&wet.Inventory[0].StackSize==64,"Wet fuel cannot begin smoldering");
        wet.Sealed=true;CompostItemBehavior.SetWater(wet.Inventory[0].Itemstack,.1);
        Advance(wet,130,.01);
        Check(!wet.IsSmoldering,"Sealed bin cannot smolder");
        Console.WriteLine($"Passed {checks} smoldering checks (threshold, destruction, no native fire, persistence, quenching, exhaustion).");
    }
}
