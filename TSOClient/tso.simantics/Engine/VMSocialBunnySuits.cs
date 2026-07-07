using FSO.Content.Framework;
using FSO.SimAntics.Model;
using FSO.Vitaboy;

namespace FSO.SimAntics.Engine
{
    /// <summary>
    /// "Social Bunny" costume, assembled from stock outfit/accessory content (pink dancer
    /// body + hair, with bunny ear/tail/slipper decoration accessories). Mirrors
    /// VMSuitProvider's JobOutfits registration.
    /// </summary>
    public static class VMSocialBunnySuits
    {
        private const string BodyOutfitName = "fab981_dancer_pinkg.oft";
        private const string HeadOutfitName = "fah924_pinkg.oft";
        private const string EarsDecorationName = "uad001_ah__bunnyear.oft";
        private const string SlippersDecorationName = "uad001_as__bunnyslippers.oft";
        private const string TailDecorationName = "uad001_at__bunnytail.oft";

        public static VMOutfitReference GetBodyOutfit()
        {
            return new VMOutfitReference(BodyOutfitName);
        }

        public static VMOutfitReference GetHeadOutfit()
        {
            return new VMOutfitReference(HeadOutfitName);
        }

        /// <summary>
        /// Applies the full bunny costume. Decorations go in the marshalled Decoration slots (by
        /// content id) so they survive save/load and reach late joiners; the direct
        /// Avatar.Decoration* sets cover clients that witness the spawn live.
        /// </summary>
        public static void ApplyBunnyCostume(VMAvatar bunny)
        {
            bunny.BodyOutfit = GetBodyOutfit();
            bunny.HeadOutfit = GetHeadOutfit();

            bunny.Decoration.Head = ResolveOutfitID(EarsDecorationName);
            bunny.Decoration.Shoes = ResolveOutfitID(SlippersDecorationName);
            bunny.Decoration.Tail = ResolveOutfitID(TailDecorationName);

            if (!VMEntity.UseWorld || bunny.Avatar == null) return;

            var outfits = Content.Content.Get().AvatarOutfits;
            if (outfits == null) return;

            bunny.Avatar.DecorationHead = outfits.Get(EarsDecorationName);
            bunny.Avatar.DecorationShoes = outfits.Get(SlippersDecorationName);
            bunny.Avatar.DecorationTail = outfits.Get(TailDecorationName);
        }

        private static ulong ResolveOutfitID(string name)
        {
            var provider = Content.Content.Get().AvatarOutfits as TSOAvatarContentProvider<Outfit>;
            if (provider?.FAR?.EntriesByName != null
                && provider.FAR.EntriesByName.TryGetValue(name, out var entry))
            {
                return entry.ID;
            }
            return 0;
        }
    }
}
