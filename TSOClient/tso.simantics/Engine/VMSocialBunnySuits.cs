using FSO.Content;
using FSO.SimAntics;
using FSO.SimAntics.Model;

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
        /// accessories) to a freshly spawned avatar. Only affects rendering - safe to call
        /// on both the server (where it's a no-op, since VM.UseWorld is false) and clients.
        /// </summary>
        public static void ApplyBunnyCostume(VMAvatar bunny)
        {
            bunny.BodyOutfit = GetBodyOutfit();
            bunny.HeadOutfit = GetHeadOutfit();

            if (!VMEntity.UseWorld || bunny.Avatar == null) return;

            var outfits = Content.Content.Get().AvatarOutfits;
            if (outfits == null) return;

            bunny.Avatar.DecorationHead = outfits.Get(EarsDecorationName);
            bunny.Avatar.DecorationShoes = outfits.Get(SlippersDecorationName);
            bunny.Avatar.DecorationTail = outfits.Get(TailDecorationName);
        }
    }
}
