using Vintagestory.API.Common;

#nullable disable

namespace CompostBin
{
    public class CompostBinModSystem : ModSystem
    {
        public override void Start(ICoreAPI api)
        {
            base.Start(api);

            api.RegisterBlockClass("BlockCompostBin", typeof(BlockCompostBin));
            api.RegisterBlockEntityClass("BECompostBin", typeof(BECompostBin));
        }
    }
}
