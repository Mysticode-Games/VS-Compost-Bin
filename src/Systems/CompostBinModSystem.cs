using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

#nullable disable

namespace CompostBin
{
    public class CompostBinModSystem : ModSystem
    {
        private const string PatchId = "compostbin.native-brown-transition";
        private static readonly object PatchLock = new();
        private static int patchUsers;
        private bool ownsPatchLease;
        private Harmony harmony;
        private ICoreServerAPI sapi;
        private ICoreClientAPI capi;
        private long listener;
        private CompostBinConfig config;
        private readonly CompostPhysicsCoordinator physics = new();
        public void Register(BECompostBin bin) => physics.Register(bin);
        public void Unregister(BECompostBin bin) => physics.Unregister(bin);
        public override void Start(ICoreAPI api)
        {
            base.Start(api);

            api.RegisterBlockClass("BlockCompostBin", typeof(BlockCompostBin));
            api.RegisterBlockEntityClass("BECompostBin", typeof(BECompostBin));
            api.RegisterCollectibleBehaviorClass("CompostBinMaterial", typeof(CompostItemBehavior));
            harmony ??= new Harmony(PatchId);
            lock (PatchLock)
            {
                if (!ownsPatchLease)
                {
                    if (patchUsers == 0 && !Harmony.HasAnyPatches(PatchId))
                        harmony.PatchCategory(typeof(CompostBrownRatePatch).Assembly, "compostbin");
                    patchUsers++;
                    ownsPatchLease = true;
                }
            }
        }

        public override void AssetsFinalize(ICoreAPI api) => CompostMaterialRegistry.Initialize(api);

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            // In multiplayer, collectible definitions arrive from the server after StartClientSide.
            capi.Event.BlockTexturesLoaded += OnClientAssetsReady;
        }
        private void OnClientAssetsReady() => AssetsFinalize(capi);

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            config = CompostBinConfig.Load(api);
            sapi.Event.SaveGameLoaded += OnSaveGameLoaded;
            physics.Start(api, config);
            listener = api.Event.RegisterGameTickListener(physics.OnServerTick, 2000);
        }

        private void OnSaveGameLoaded()
        {
            // World settings are sent by the game to joining clients. Read them
            // on both sides so the GUI and server conversion use the same time.
            config.Publish(sapi.World.Config);
        }

        public override void Dispose()
        {
            if (sapi != null)
            {
                sapi.Event.UnregisterGameTickListener(listener);
                sapi.Event.SaveGameLoaded -= OnSaveGameLoaded;
            }
            if (capi != null)
                capi.Event.BlockTexturesLoaded -= OnClientAssetsReady;
            physics.Clear();
            lock (PatchLock)
            {
                if (ownsPatchLease)
                {
                    ownsPatchLease = false;
                    if (--patchUsers == 0)
                        harmony?.UnpatchAll(PatchId);
                }
            }
            base.Dispose();
        }

    }
}
