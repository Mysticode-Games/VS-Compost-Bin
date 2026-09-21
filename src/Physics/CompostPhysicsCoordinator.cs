using System.Collections.Generic;
using System.Linq;
using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

#nullable disable

namespace CompostBin
{
    public class CompostPhysicsCoordinator
    {
        private ICoreServerAPI sapi;
        private CompostBinConfig config;
        private double lastHours = -1;
        private readonly HashSet<BECompostBin> bins = new();

        public void Start(ICoreServerAPI api, CompostBinConfig settings)
        {
            sapi = api;
            config = settings;
            lastHours = -1;
        }

        public void Register(BECompostBin bin) => bins.Add(bin);
        public void Unregister(BECompostBin bin) => bins.Remove(bin);
        public void Clear() => bins.Clear();

        public void OnServerTick(float dt)
        {
            var calendar = sapi.World?.Calendar;
            if (calendar == null)
                return;
            double now = calendar.TotalHours;
            if (lastHours < 0)
            {
                lastHours = now;
                return;
            }
            double elapsed = Math.Clamp(now - lastHours, 0, 2);
            lastHours = now;
            // Only loaded bins participate; unloaded/offline time is not simulated.
            var active = bins.Where(b => !b.IsBurning).ToArray();
            var indices = new Dictionary<BlockPos, int>();
            var states = new CompostPhysics.State[active.Length];
            var ambient = new double[active.Length];
            for (int i = 0; i < active.Length; i++)
            {
                indices[active[i].Pos] = i;
                states[i] = active[i].ReadPhysicsState();
                ambient[i] = sapi.World.BlockAccessor.GetClimateAt(active[i].Pos, EnumGetClimateMode.NowValues)?.Temperature ?? 20;
            }
            while (elapsed > 0.000001)
            {
                double step = Math.Min(CompostPhysics.StepHours, elapsed);
                var next = new CompostPhysics.State[active.Length];
                var burned = new double[active.Length];
                for (int i = 0; i < active.Length; i++)
                {
                    var neighbors = Enumerable.Repeat(double.NaN, 6).ToArray();
                    for (int f = 0; f < 6; f++)
                        if (indices.TryGetValue(active[i].Pos.AddCopy(BlockFacing.ALLFACES[f]), out int j))
                            neighbors[f] = states[j].Temperature;
                    next[i] = CompostPhysics.Advance(states[i], ambient[i], neighbors, step, out burned[i], out _, config);
                }
                // Apply only after every node used the same snapshot.
                for (int i = 0; i < active.Length; i++)
                {
                    active[i].ApplyPhysics(next[i], burned[i], step);
                    states[i] = active[i].ReadPhysicsState();
                }
                elapsed -= step;
            }
            foreach (var bin in active)
                bin.FinishPhysicsTick();
        }

    }
}
