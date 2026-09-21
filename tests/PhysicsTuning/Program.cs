using CompostBin;
foreach (double ambient in new[] { 20d, 35d, 45d })
foreach (int shared in new[] { 0, 1, 2, 3, 4 })
{
 var s = new CompostPhysics.State { Temperature = ambient, DryMass = 8, Greens = 5.3, Browns = 2.7, Water = 4.56, Oxygen = 0.8 };
 double peak = ambient, ignited = -1;
 for (double hour = 0; hour < 168; hour += CompostPhysics.StepHours)
 {
  var n = Enumerable.Repeat(double.NaN, 6).ToArray();
  for (int i = 0; i < shared; i++) n[i] = s.Temperature;
  s = CompostPhysics.Advance(s, ambient, n, CompostPhysics.StepHours, out _, out _);
  peak = Math.Max(peak, s.Temperature);
  if (CompostPhysics.CanIgnite(s)) { ignited = hour; break; }
 }
 Console.WriteLine($"ambient={ambient} sharedSides={shared}: peak={peak:F1} ignition={ignited:F1}h moisture={s.Moisture:F2} oxygen={s.Oxygen:F2}");
}

foreach (var scenario in new[] { (2,2,20d), (2,2,35d), (2,2,45d), (2,8,20d), (2,3,20d), (3,3,20d) })
{
 var (width,length,ambient)=scenario;
 var states=Enumerable.Range(0,width*length).Select(_=>new CompostPhysics.State{
  Temperature=ambient,DryMass=8,Greens=5.3,Browns=2.7,Water=4.56,Oxygen=.8 }).ToArray();
 double ignition=-1,peak=ambient;
 int[] dx={0,1,0,-1},dz={-1,0,1,0};
 for(double hour=0;hour<168;hour+=CompostPhysics.StepHours)
 {
  var next=new CompostPhysics.State[states.Length];
  for(int x=0;x<width;x++)for(int z=0;z<length;z++){
   var neighbors=Enumerable.Repeat(double.NaN,6).ToArray();
   for(int f=0;f<4;f++){int nx=x+dx[f],nz=z+dz[f];if(nx>=0&&nx<width&&nz>=0&&nz<length)neighbors[f]=states[nx*length+nz].Temperature;}
   next[x*length+z]=CompostPhysics.Advance(states[x*length+z],ambient,neighbors,CompostPhysics.StepHours,out _,out _);
  }
  states=next;peak=Math.Max(peak,states.Max(s=>s.Temperature));
  if(states.Any(s=>CompostPhysics.CanIgnite(s))){ignition=hour;break;}
 }
 Console.WriteLine($"Actual {width}x{length} cluster ambient={ambient}: peak={peak:F1}, ignition={ignition:F1}h");
 bool expectedFire=ambient==45 || (width==2&&length==8) || width==3;
 if ((ignition>=0)!=expectedFire) throw new Exception("Cluster calibration regression: "+scenario);
}
