using FSO.Content.Framework;
using FSO.SimAntics.Model;
using FSO.Vitaboy;

namespace FSO.SimAntics.Engine
{
    /// <summary>
    /// Costume registration for the "Social Bunny" NPC, mirroring how VMSuitProvider's
    /// JobOutfits arrays register job costumes for NPC-like avatars.
    ///
    /// The costume is assembled entirely from existing stock outfit/accessory content
    /// (no new mesh assets): a pink dancer body + matching hair as the base look, with
    /// bunny ears/tail/slippers layered on as decoration accessories.
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
        /// Applies the full Social Bunny costume (body, head, and bunny ear/tail/slipper
        /// accessories) to an avatar. The decorations are stored in the avatar's marshalled
        /// Decoration slots (by content id) so they survive lot save/load and reach
        /// late-joining clients via the entity snapshot - VMAvatar's marshal-load path
        /// re-applies Decoration ids to the renderer. The direct Avatar.Decoration*
        /// assignments below cover the client(s) that witness the spawn live.
        /// </summary>
        public static void ApplyBunnyCostume(VMAvatar bunny)
        {
            bunny.BodyOutfit = GetBodyOutfit();
            bunny.HeadOutfit = GetHeadOutfit();

            // Synced/marshalled state: identical on server + all clients (ids resolve from
            // the same content archives everywhere).
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
