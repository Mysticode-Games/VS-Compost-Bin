using System.Reflection;
using CompostBin;
using Vintagestory.API.Common;

static class PacketChecks
{
    public static void Run(string game)
    {
        var lib = Assembly.LoadFrom(Path.Combine(game, "VintagestoryLib.dll"));
        var registryType = lib.GetType("Vintagestory.Common.ClassRegistry", true);
        var registry = Activator.CreateInstance(registryType);
        var api = Stub.Make<ICoreAPI>((m, a) => {
            if (m.Name == "RegisterCollectibleBehaviorClass")
                registryType.GetMethod(m.Name).Invoke(registry, a);
            return null;
        });
        var mod = new CompostBinModSystem();
        mod.Start(api); // Exercise actual mod registration, not a test-only registration.
        try
        {
            var registryApi = Stub.Make<IClassRegistryAPI>((m, a) => {
                if (m.Name == "get_ItemClassToTypeMapping")
                    return registryType.GetField("ItemClassToTypeMapping").GetValue(registry);
                return registryType.GetMethod(m.Name)?.Invoke(registry, a);
            });
            var world = Stub.Make<IWorldAccessor>((m, a) => null);
            var net = lib.GetType("Vintagestory.Common.ItemTypeNet", true);
            var write = net.GetMethod("GetItemTypePacket", new[] { typeof(Item), typeof(IClassRegistryAPI) });
            var read = net.GetMethod("ReadItemTypePacket");
            foreach (bool brown in new[] { false, true })
            {
                var item = new Item { Code = new AssetLocation(brown ? "game:drygrass" : "game:rot"), LightHsv = new byte[3] };
                item.CollectibleBehaviors = new CollectibleBehavior[] { new CompostItemBehavior(item, brown) };
                var packet = write.Invoke(null, new object[] { item, registryApi });
                var received = (Item)read.Invoke(null, new[] { packet, world, registry });
                var behavior = received.GetBehavior<CompostItemBehavior>();
                if (behavior == null || behavior.Brown != brown)
                    throw new Exception("Item packet lost compost behavior or brown flag");
            }
            Console.WriteLine("Passed 2 real engine item-packet round trips (green/rot and brown).");
        }
        finally { mod.Dispose(); }
    }
}
