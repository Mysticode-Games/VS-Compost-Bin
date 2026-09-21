using Newtonsoft.Json.Linq;
using System.Linq;
using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

#nullable disable

namespace CompostBin
{
    public static class CompostMaterialRegistry
    {
        public static void Initialize(ICoreAPI api)
        {
            if (api.World.Collectibles == null)
                return;
            foreach (var item in api.World.Collectibles)
            {
                bool brown = item.Code?.Domain == "game" && Array.IndexOf(BECompostBin.DryOfferingCodes, item.Code.Path) >= 0;
                bool peat = item.Code?.Domain == "game" && item.Code.Path == "peatbrick";
                bool perishable = item.TransitionableProps?.Any(p => p.Type == EnumTransitionType.Perish) == true;
                // Respect other mods that already define a native perish process.
                bool addedBrown = brown && !perishable;
                bool addedPeat = peat && !perishable;
                if (addedBrown || addedPeat)
                {
                    var output = new JsonItemStack { Type = EnumItemClass.Item, Code = new AssetLocation("game:rot"), StackSize = 1 };
                    if (!output.Resolve(api.World, "compost bin brown decomposition"))
                        continue;
                    var prop = new TransitionableProperties
                    {
                        Type = EnumTransitionType.Perish,
                        FreshHours = NatFloat.createUniform(143, 0),
                        TransitionHours = NatFloat.createUniform(1, 0),
                        TransitionedStack = output,
                        TransitionRatio = 1
                    };
                    item.TransitionableProps = (item.TransitionableProps ?? Array.Empty<TransitionableProperties>()).Append(prop).ToArray();
                    item.Attributes ??= new JsonObject(new JObject());
                    item.Attributes.Token[addedPeat ? "compostBinPeatTransition" : "compostBinBrownTransition"] = true;
                }
                bool ownsBrownTransition = addedBrown || item.Attributes?["compostBinBrownTransition"].AsBool() == true;
                bool ownsPeatTransition = addedPeat || item.Attributes?["compostBinPeatTransition"].AsBool() == true;
                if ((brown || peat || perishable || item.Code?.Path is "rot" or "compost") && item.GetBehavior<CompostItemBehavior>() == null)
                    item.CollectibleBehaviors = (item.CollectibleBehaviors ?? Array.Empty<CollectibleBehavior>()).Append(new CompostItemBehavior(item, ownsBrownTransition, ownsPeatTransition)).ToArray();
            }
        }

    }
}
