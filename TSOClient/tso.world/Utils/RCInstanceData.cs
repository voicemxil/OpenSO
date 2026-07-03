using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FSO.LotView.Utils
{
    /// <summary>
    /// Per-instance stream-1 vertex data for DGRPRenderer.DrawInstanced: a full affine World matrix, one
    /// row per TEXCOORD register (TEXCOORD2..5) so RCObject.fx's vsRCInstanced can reconstruct it without
    /// any row/column-major repacking - see the comment above vsRCInstanced in RCObject.fx.
    /// </summary>
    public struct RCInstanceData : IVertexType
    {
        public Vector4 Row0;
        public Vector4 Row1;
        public Vector4 Row2;
        public Vector4 Row3;

        public static readonly VertexDeclaration VertexDeclaration = new VertexDeclaration(
            new VertexElement(0, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 2),
            new VertexElement(16, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 3),
            new VertexElement(32, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 4),
            new VertexElement(48, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 5)
        );

        VertexDeclaration IVertexType.VertexDeclaration => VertexDeclaration;

        public static RCInstanceData FromMatrix(ref Matrix m)
        {
            RCInstanceData d;
            d.Row0 = new Vector4(m.M11, m.M12, m.M13, m.M14);
            d.Row1 = new Vector4(m.M21, m.M22, m.M23, m.M24);
            d.Row2 = new Vector4(m.M31, m.M32, m.M33, m.M34);
            d.Row3 = new Vector4(m.M41, m.M42, m.M43, m.M44);
            return d;
        }
    }

    /// <summary>
    /// Velocity-pass variant of <see cref="RCInstanceData"/>: adds the previous-frame World matrix
    /// (TEXCOORD6..9) so the instanced velocity technique can compute per-pixel motion vectors.
    /// </summary>
    public struct RCInstanceDataVelocity : IVertexType
    {
        public Vector4 Row0;
        public Vector4 Row1;
        public Vector4 Row2;
        public Vector4 Row3;
        public Vector4 PrevRow0;
        public Vector4 PrevRow1;
        public Vector4 PrevRow2;
        public Vector4 PrevRow3;

        public static readonly VertexDeclaration VertexDeclaration = new VertexDeclaration(
            new VertexElement(0, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 2),
            new VertexElement(16, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 3),
            new VertexElement(32, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 4),
            new VertexElement(48, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 5),
            new VertexElement(64, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 6),
            new VertexElement(80, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 7),
            new VertexElement(96, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 8),
            new VertexElement(112, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 9)
        );

        VertexDeclaration IVertexType.VertexDeclaration => VertexDeclaration;

        public static RCInstanceDataVelocity FromMatrices(ref Matrix world, ref Matrix prevWorld)
        {
            RCInstanceDataVelocity d;
            d.Row0 = new Vector4(world.M11, world.M12, world.M13, world.M14);
            d.Row1 = new Vector4(world.M21, world.M22, world.M23, world.M24);
            d.Row2 = new Vector4(world.M31, world.M32, world.M33, world.M34);
            d.Row3 = new Vector4(world.M41, world.M42, world.M43, world.M44);
            d.PrevRow0 = new Vector4(prevWorld.M11, prevWorld.M12, prevWorld.M13, prevWorld.M14);
            d.PrevRow1 = new Vector4(prevWorld.M21, prevWorld.M22, prevWorld.M23, prevWorld.M24);
            d.PrevRow2 = new Vector4(prevWorld.M31, prevWorld.M32, prevWorld.M33, prevWorld.M34);
            d.PrevRow3 = new Vector4(prevWorld.M41, prevWorld.M42, prevWorld.M43, prevWorld.M44);
            return d;
        }
    }
}
