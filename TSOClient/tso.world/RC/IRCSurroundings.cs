using FSO.Common.Rendering.Framework.Camera;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FSO.LotView.RC
{
    public interface IRCSurroundings
    {
        // taaJitter = the lot's current TAA sub-pixel projection jitter (NDC). The surroundings backdrop must
        // apply the SAME jitter so distant terrain accumulates temporal samples like the lot geometry does.
        void DrawSurrounding(GraphicsDevice gfx, ICamera cam, Vector4 fogColor, int surroundNumber, Vector2 taaJitter);
    };
}
