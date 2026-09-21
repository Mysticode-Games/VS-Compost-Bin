using System.Reflection;
using System.Runtime.Loader;
using CompostBin;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using HarmonyLib;
string game = args.FirstOrDefault() ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VintagestoryPre");
AssemblyLoadContext.Default.Resolving += (context, name) => {
 foreach (var dir in new[] { game, Path.Combine(game,"Lib"), Path.Combine(game,"Mods") }) {
  string f=Path.Combine(dir,name.Name+".dll"); if(File.Exists(f)) return context.LoadFromAssemblyPath(f);
 } return null;
};
try { Checks.Run(); NativeFireChecks.Run(); PacketChecks.Run(game); StartupChecks.Run(); NeighborHeatChecks.Run(); ConfigChecks.Run(); SettingsChecks.Run(); SmolderChecks.Run(); GuideReviewChecks.Run(); PeatChecks.Run(); PeatIntegrationChecks.Run(); } catch (Exception e) { Console.WriteLine(e); Environment.Exit(1); }

public class Stub : DispatchProxy
{
 public System.Func<MethodInfo,object[],object> Handler;
 protected override object Invoke(MethodInfo m,object[] a)=>Handler(m,a);
 public static T Make<T>(System.Func<MethodInfo,object[],object> h) where T:class {var p=Create<T,Stub>();((Stub)(object)p).Handler=h;return p;}
}
static class Checks
{
 static int checks;
 static void Check(bool b,string msg){if(!b)throw new Exception(msg);checks++;}
 static void Near(double a,double b,string msg)=>Check(Math.Abs(a-b)<1e-5,msg+": "+a+" != "+b);
 public static void Run()
 {
  var air=Enumerable.Repeat(double.NaN,6).ToArray();
  var hot=new CompostPhysics.State{Temperature=80,DryMass=8,Water=4,Oxygen=.8,Sealed=true};
  var cold=new CompostPhysics.State{Temperature=20,DryMass=2,Water=1,Oxygen=.8,Sealed=true};
  // Cancel ambient loss by giving each node its own temperature as ambient.
  var nh=(double[])air.Clone(); var nc=(double[])air.Clone();nh[4]=cold.Temperature;nc[5]=hot.Temperature;
  var h=CompostPhysics.Advance(hot,80,nh,.01,out _,out _);
  var c=CompostPhysics.Advance(cold,20,nc,.01,out _,out _);
  Near((h.Temperature-hot.Temperature)*hot.Capacity+(c.Temperature-cold.Temperature)*cold.Capacity,0,"Shared-face energy conservation");
  Check(h.Temperature<80&&c.Temperature>20,"Heat moves hot to cold");
  var small=cold;small.Temperature=60;var large=hot;large.Temperature=60;
  Check(CompostPhysics.Advance(small,20,air,.01,out _,out _).Temperature<CompostPhysics.Advance(large,20,air,.01,out _,out _).Temperature,"Larger wet mass cools more slowly");
  Near(CompostPhysics.SharedConductance(4),CompostPhysics.SharedConductance(5),"Top-bottom shared exchange is symmetric");
  var t1=hot;var t2=hot;
  for(int i=0;i<120;i++)t1=CompostPhysics.Advance(t1,20,air,1d/120,out _,out _);
  for(int i=0;i<240;i++)t2=CompostPhysics.Advance(t2,20,air,1d/240,out _,out _);
  Check(Math.Abs(t1.Temperature-t2.Temperature)<.02,"Small-step convergence");
  Check(!CompostPhysics.CanIgnite(hot),"Sealed cannot ignite");
  var noFuel=hot;noFuel.Sealed=false;noFuel.Temperature=130;noFuel.Water=0;
  Check(!CompostPhysics.CanIgnite(noFuel),"No browns cannot ignite");
  noFuel.Browns=1; Check(CompostPhysics.CanIgnite(noFuel),"Hot dry oxygenated fuel can ignite");
  noFuel.Oxygen=0;Check(!CompostPhysics.CanIgnite(noFuel),"No oxygen prevents ignition");
  double now=100;
  var calendar=Stub.Make<IGameCalendar>((m,a)=>m.Name=="get_TotalHours"?now:null);
  var world=Stub.Make<IWorldAccessor>((m,a)=>m.Name switch {"get_Calendar"=>calendar,"get_Side"=>EnumAppSide.Server,"get_Rand"=>new Random(1),_=>null});
  var api=Stub.Make<ICoreAPI>((m,a)=>m.Name switch {"get_World"=>world,"get_Side"=>EnumAppSide.Server,_=>null});
  var brown=new Item { Code=new AssetLocation("game:drygrass"),MaxStackSize=64 };
  typeof(CollectibleObject).GetField("api",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(brown,api);
  brown.CollectibleBehaviors=new CollectibleBehavior[]{new CompostItemBehavior(brown,true)};
  brown.TransitionableProps=new[]{new TransitionableProperties{Type=EnumTransitionType.Perish,FreshHours=NatFloat.createUniform(143,0),TransitionHours=NatFloat.createUniform(1,0)}};
  var chest=new InventoryGeneric(2,"test-1",null) { Api = api };
  chest[0].Itemstack=new ItemStack(brown,32);
  var patch=new Harmony("compostbin.tests");patch.PatchAll(typeof(CompostBrownRatePatch).Assembly);
  try {
   brown.UpdateAndGetTransitionStates(world,chest[0]);
   brown.SetTransitionState(chest[0].Itemstack,EnumTransitionType.Perish,50);
   now=200;
   var state=brown.UpdateAndGetTransitionStates(world,chest[0])[0];
   Near(state.TransitionedHours,50,"Brown progress pauses in chest");
   Near(brown.GetTransitionRateMul(world,chest[0],EnumTransitionType.Perish),0,"Brown rate outside bin is zero");
   brown.SetTemperature(world,chest[0].Itemstack,80,false);
   CompostItemBehavior.SetWater(chest[0].Itemstack,.4);
   var split=chest[0].TakeOut(8);
   Near(CompostItemBehavior.WaterRatio(split),.4,"Split preserves moisture");
   Near(brown.GetTemperature(world,split),80,"Split preserves heat");
   Near(((FloatArrayAttribute)split.Attributes.GetTreeAttribute("transitionstate")["transitionedHours"]).value[0],50,"Split preserves progress");
   now+=.1;
   Check(brown.GetTemperature(world,split)<80,"Removed stack cools with native API");
   chest[1].Itemstack=new ItemStack(brown,16);
   brown.UpdateAndGetTransitionStates(world,chest[1]);
   brown.SetTemperature(world,chest[0].Itemstack,80,false);
   brown.SetTemperature(world,chest[1].Itemstack,20,false);
   CompostItemBehavior.SetWater(chest[1].Itemstack,.8);
   var move=new ItemStackMoveOperation(world,EnumMouseButton.Left,0,EnumMergePriority.DirectMerge,16);
   var merge=move.ToMergeOperation(chest[1],chest[0]);
   brown.TryMergeStacks(merge);
   Check(merge.MovedQuantity==16,"Native direct merge accepts different heat and progress");
   Near(brown.GetTemperature(world,chest[1].Itemstack),50,"Native merge averages heat");
   Near(CompostItemBehavior.WaterRatio(chest[1].Itemstack),.6,"Merge averages moisture");
   Near(brown.UpdateAndGetTransitionStates(world,chest[1])[0].TransitionedHours,25,"Native merge averages decomposition");
   var bin=new FireTestBin{Api=api,Pos=new BlockPos(0,0,0),Block=new Block{Code=new AssetLocation("compostbin:compostbin")}};
   bin.Inventory.Api=api;
   bin.Inventory[0].Itemstack=split;
   brown.UpdateAndGetTransitionStates(world,bin.Inventory[0]);
   Near(((FloatArrayAttribute)split.Attributes.GetTreeAttribute("transitionstate")["transitionedHours"]).value[0],50,"Insertion has no outside-time catchup");
   // Force a nonzero bin rate through its public inventory callback for a native transition test.
   var green=new Item{Code=new AssetLocation("game:carrot"),MaxStackSize=64,TransitionableProps=brown.TransitionableProps};
   bin.Inventory[1].Itemstack=new ItemStack(green,64);
   typeof(BECompostBin).GetField("pileTemperature",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(bin,40d);
   brown.SetTemperature(world,split,40,false);
   typeof(BECompostBin).GetField("AllowBrownAdvance",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(bin,true);
   now+=1;
   Check(brown.UpdateAndGetTransitionStates(world,bin.Inventory[0])[0].TransitionedHours>50,"Brown progress advances inside bin");
   bin.Sealed=true;
   Near(brown.GetTransitionRateMul(world,bin.Inventory[0],EnumTransitionType.Perish),0,"Sealed browns pause");
   bin.Sealed=false;
   var climateAccess=Stub.Make<IBlockAccessor>((m,a)=>m.Name=="GetClimateAt"?new ClimateCondition{Temperature=20}:null);
   var physicsWorld=Stub.Make<IWorldAccessor>((m,a)=>m.Name switch {
    "get_Calendar"=>calendar,"get_Side"=>EnumAppSide.Server,"get_BlockAccessor"=>climateAccess,
    "get_Rand"=>new Random(1),_=>null});
   bin.Api=Stub.Make<ICoreAPI>((m,a)=>m.Name switch {"get_World"=>physicsWorld,"get_Side"=>EnumAppSide.Server,_=>null});
   typeof(BECompostBin).GetField("physicsLoaded",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(bin,true);
   double moisture=bin.Moisture;
   bin.OnWatered();
   Check(bin.GetTemperature()<40 && bin.GetTemperature()>20,"Water cools by energy mixing");
   Check(bin.Moisture>moisture,"Water adds persistent moisture");
   foreach(var slot in bin.Inventory)if(!slot.Empty)Near(slot.Itemstack.Collectible.GetTemperature(world,slot.Itemstack),bin.GetTemperature(),"Pile heat synchronized to native stacks");
   typeof(BECompostBin).GetField("oxygen",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(bin,.5d);
   Check(bin.IsTurningDue(now),"Low aeration prompts turning");
   bin.OnTurned();
   Check(!bin.IsTurningDue(now),"Turning clears aeration prompt");
   var oldProgress=new TreeAttribute();oldProgress.GetOrAddTreeAttribute("0").SetDouble("progress",83);
   typeof(BECompostBin).GetField("legacyDecomposition",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(bin,oldProgress);
   typeof(BECompostBin).GetMethod("InitializePhysics",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(bin,null);
   Near(brown.UpdateAndGetTransitionStates(world,bin.Inventory[0])[0].TransitionedHours,83,"Old slot progress migrates to native transition");
   var saved=new TreeAttribute();bin.ToTreeAttributes(saved);
   Check(saved.HasAttribute("physicsVersion")&&!saved.HasAttribute("overheatingProgressHours"),"New save drops old countdown");
   var emptySaved=new TreeAttribute();
   emptySaved.SetDouble("pileTemperature",61);emptySaved.SetDouble("oxygen",.42);emptySaved.SetBool("sealed",true);
   var restored=new BECompostBin{Pos=new BlockPos(0,0,0),Block=new Block{Code=new AssetLocation("compostbin:compostbin")}};
   restored.FromTreeAttributes(emptySaved,world);
   Near(restored.GetTemperature(),61,"Saved temperature restores");
   Check(restored.Inventory.TakeLocked&&restored.Inventory.PutLocked,"Saved sealed locks restore");
   var compost=new Item{Code=new AssetLocation("game:compost"),MaxStackSize=64};
   var sealConfig=new TreeAttribute();
   var sealWorld=Stub.Make<IWorldAccessor>((m,a)=>m.Name switch {
    "get_Config"=>sealConfig,"get_Calendar"=>calendar,"get_AllOnlinePlayers"=>Array.Empty<IPlayer>(),"GetItem"=>compost,_=>null});
   var sealApi=Stub.Make<ICoreAPI>((m,a)=>m.Name switch {"get_World"=>sealWorld,"get_Side"=>EnumAppSide.Server,_=>null});
   var sealBin=new FireTestBin{Api=sealApi,Pos=new BlockPos(0,0,0),Block=new Block{Code=new AssetLocation("compostbin:compostbin")}};
   sealBin.Inventory.Api=sealApi;
   sealBin.Inventory[0].Itemstack=new ItemStack(new Item{Code=new AssetLocation("game:rot"),MaxStackSize=64},64);
   Check(sealBin.CanSeal(),"64 rot still enables sealing");
   sealBin.SealBin();
   Check(sealBin.Sealed&&sealBin.Inventory.TakeLocked,"Sealing locks native inventory");
   var finish=typeof(BECompostBin).GetMethod("FinishPhysicsTick",BindingFlags.Instance|BindingFlags.NonPublic);
   now+=479;finish.Invoke(sealBin,null);Check(sealBin.Sealed,"Sealed conversion waits full duration");
   now+=1;finish.Invoke(sealBin,null);
   Check(!sealBin.Sealed&&sealBin.Inventory[0].StackSize==16&&sealBin.Inventory[0].Itemstack.Collectible==compost,"480 hours still converts 64 rot to 16 compost");
   sealConfig.SetDouble(CompostBinConfig.DurationKey,240);
   sealBin.Inventory[0].Itemstack=new ItemStack(new Item{Code=new AssetLocation("game:rot"),MaxStackSize=64},64);
   sealBin.SealBin();
   now+=239;finish.Invoke(sealBin,null);Check(sealBin.Sealed,"Configured conversion does not finish early");
   now+=1;finish.Invoke(sealBin,null);
   Check(!sealBin.Sealed&&sealBin.Inventory[0].StackSize==16&&sealBin.Inventory[0].Itemstack.Collectible==compost,"Configured 240 hours converts 64 rot to 16 compost");
  } finally {patch.UnpatchAll("compostbin.tests");}
  Console.WriteLine($"Passed {checks} physics/native transition checks.");
 }
}



