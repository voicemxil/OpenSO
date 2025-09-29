using Microsoft.Xna.Framework;

namespace FSO.Common.Model
{
    public enum LotTransitionType
    {
        None,
        DirectControl,
        Routing
    }

    public class LotTransitionInfo
    {
        public uint BeforeLocation;
        public int RelativeChangeX;
        public int RelativeChangeY;

        public int AvatarLotTilePosX;
        public int AvatarLotTilePosY;
        public float AvatarDirection;

        public LotTransitionType Type;
        public uint RoutingTargetLocation;
        public int RoutingLotTilePosX;
        public int RoutingLotTilePosY;

        /// <summary>
        /// By default, relative change x/y are in lot space, for calculation of the new lot tile pos on the target lot.
        /// This function converts them to offsets usable for city map coordinates.
        /// </summary>
        /// <param name="relativeChange"></param>
        /// <returns></returns>
        public static Point RelativeChangeLotToCity(Point relativeChange)
        {
            return new Point(-relativeChange.Y, relativeChange.X);
        }

        /// <summary>
        /// Gets a mask for the surrounding lots that need to be reloaded for this transition.
        /// In order (-1, -1), (0, -1), (1, -1), (-1, 0)...
        /// Bits that aren't set should be able to copy surrounding lots from the previous lot, rather than re-initialzing them.
        /// </summary>
        /// <returns></returns>
        public uint GetSurroundingLotMask()
        {
            uint updateMask = 0;
            var cityOffset = RelativeChangeLotToCity(new Point(RelativeChangeX, RelativeChangeY));

            if (cityOffset.Y > 0)
            {
                updateMask |= 0b010000111;
            }

            if (cityOffset.Y < 0)
            {
                updateMask |= 0b111000010;
            }

            if (cityOffset.X > 0)
            {
                updateMask |= 0b001101001;
            }

            if (cityOffset.X < 0)
            {
                updateMask |= 0b100101100;
            }

            return updateMask;
        }
    }
}
