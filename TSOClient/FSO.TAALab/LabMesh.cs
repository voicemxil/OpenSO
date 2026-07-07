using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace FSO.TAALab
{
    /// <summary>
    /// Minimal standalone loader for the game's .fsom 3D object meshes (the reconstructed/remesh
    /// meshes 3D lot mode renders — format single source of truth: tso.files/RC/DGRP3DMesh.cs +
    /// DGRP3DGeometry.cs). Deliberately re-implemented here WITHOUT referencing tso.* (the lab's
    /// stated design is dependency-freedom): the format is a gzip stream of
    ///   "FSOm" | version i32 | reconstructVersion i32 | name pascal-str | geomCount i32 |
    ///   per group { subCount i32, per sub { PixelSPR u16, PixelDir u16, vertCount i32,
    ///     verts (v1: pos3f+uv2f = 20B; v2+: pos3f+uv2f+normal3f = 32B, raw DGRP3DVert),
    ///     indexCount i32, indices i32[] } } |
    ///   (v3+: maskType i32, optional depth-mask geometry) | bounds 6f
    ///
    /// Texture resolution (mirrors DGRP3DGeometry): PixelDir == 65535 means a CUSTOM texture,
    /// "&lt;iffprefix&gt;_TEX_&lt;PixelSPR&gt;.png" next to the .fsom (these ship committed in
    /// Content/MeshReplace). Any other PixelDir references an in-game DGRP sprite texture, which
    /// would need full game-content init — those parts get the flat fallback texture instead.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 32)]
    public struct LabMeshVert : IVertexType
    {
        public Vector3 Position;
        public Vector2 TexCoord;
        public Vector3 Normal;

        // Byte-identical to DGRP3DVert's declaration (normal rides in TEXCOORD1).
        public static readonly VertexDeclaration Declaration = new VertexDeclaration(
            new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
            new VertexElement(12, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
            new VertexElement(20, VertexElementFormat.Vector3, VertexElementUsage.TextureCoordinate, 1));

        VertexDeclaration IVertexType.VertexDeclaration => Declaration;
    }

    public class LabMeshPart
    {
        public VertexBuffer Verts;
        public IndexBuffer Indices;
        public int PrimCount;
        public Texture2D Texture; // never null (flat fallback when the part references a DGRP sprite)
    }

    public class LabMesh
    {
        public string Name;
        public List<LabMeshPart> Parts = new List<LabMeshPart>();
        public BoundingBox Bounds;
    }

    public static class LabFsomLoader
    {
        /// <summary>
        /// Load the requested dynamic-sprite groups of an .fsom as GPU-ready mesh parts.
        /// Group 0 is the object's base geometry; higher groups are dynamic-sprite states
        /// (e.g. bannerlamp's banner). Textures are cached across meshes by filename.
        /// </summary>
        public static LabMesh Load(string fsomPath, int[] groups, GraphicsDevice gd,
            Texture2D fallback, Dictionary<string, Texture2D> texCache)
        {
            var dir = Path.GetDirectoryName(fsomPath);
            var baseName = Path.GetFileNameWithoutExtension(fsomPath);       // e.g. christmastree_iff_100
            var iffPrefix = baseName.Substring(0, baseName.LastIndexOf('_')); // e.g. christmastree_iff

            using var file = File.OpenRead(fsomPath);
            using var gz = new GZipStream(file, CompressionMode.Decompress);
            using var ms = new MemoryStream();
            gz.CopyTo(ms);
            ms.Position = 0;
            using var io = new BinaryReader(ms);

            var magic = Encoding.ASCII.GetString(io.ReadBytes(4));
            if (magic != "FSOm") throw new InvalidDataException($"bad magic '{magic}' in {baseName}");
            var version = io.ReadInt32();
            io.ReadInt32(); // reconstructVersion (0 = hand-authored replacement mesh)
            var nameLen = io.ReadByte();
            var name = Encoding.ASCII.GetString(io.ReadBytes(nameLen));

            var mesh = new LabMesh { Name = name };
            var geomCount = io.ReadInt32();
            for (int g = 0; g < geomCount; g++)
            {
                var wanted = Array.IndexOf(groups, g) >= 0;
                var subCount = io.ReadInt32();
                for (int s = 0; s < subCount; s++)
                {
                    var part = ReadGeometry(io, version, wanted, gd);
                    if (part == null) continue;
                    var (verts, indices, spr, dirV) = part.Value;

                    var lp = new LabMeshPart { PrimCount = indices.Length / 3 };
                    if (lp.PrimCount == 0) continue;
                    lp.Verts = new VertexBuffer(gd, LabMeshVert.Declaration, verts.Length, BufferUsage.None);
                    lp.Verts.SetData(verts);
                    lp.Indices = new IndexBuffer(gd, IndexElementSize.ThirtyTwoBits, indices.Length, BufferUsage.None);
                    lp.Indices.SetData(indices);

                    if (dirV == 65535)
                    {
                        var texName = iffPrefix + "_TEX_" + spr + ".png";
                        if (!texCache.TryGetValue(texName, out var tex))
                        {
                            var texPath = Path.Combine(dir, texName);
                            if (File.Exists(texPath))
                            {
                                using var ts = File.OpenRead(texPath);
                                tex = Texture2D.FromStream(gd, ts);
                            }
                            else
                            {
                                Console.WriteLine($"[TAALab] WARNING: {baseName} wants missing texture {texName} — using flat fallback.");
                                tex = fallback;
                            }
                            texCache[texName] = tex;
                        }
                        lp.Texture = tex;
                    }
                    else
                    {
                        // DGRP sprite texture — needs full game-content init; flat fallback keeps the silhouette.
                        lp.Texture = fallback;
                    }
                    mesh.Parts.Add(lp);
                }
            }

            if (version > 2)
            {
                var maskType = io.ReadInt32();
                if (maskType > 0) ReadGeometry(io, version, false, gd); // skip the depth mask
            }

            var min = new Vector3(io.ReadSingle(), io.ReadSingle(), io.ReadSingle());
            var max = new Vector3(io.ReadSingle(), io.ReadSingle(), io.ReadSingle());
            mesh.Bounds = new BoundingBox(min, max);
            return mesh;
        }

        /// <summary>Read one geometry record; returns null when skipping (still advances the stream).</summary>
        private static (LabMeshVert[] verts, int[] indices, ushort spr, ushort dir)? ReadGeometry(
            BinaryReader io, int version, bool wanted, GraphicsDevice gd)
        {
            var spr = io.ReadUInt16();
            var dirV = io.ReadUInt16();
            var vertCount = io.ReadInt32();

            LabMeshVert[] verts = null;
            if (version > 1)
            {
                var bytes = io.ReadBytes(vertCount * 32);
                if (wanted)
                {
                    verts = new LabMeshVert[vertCount];
                    var handle = GCHandle.Alloc(verts, GCHandleType.Pinned);
                    Marshal.Copy(bytes, 0, handle.AddrOfPinnedObject(), bytes.Length);
                    handle.Free();
                }
            }
            else
            {
                // v1: pos + uv only, no normals (unlit lab draw doesn't need them).
                if (wanted)
                {
                    verts = new LabMeshVert[vertCount];
                    for (int i = 0; i < vertCount; i++)
                    {
                        verts[i].Position = new Vector3(io.ReadSingle(), io.ReadSingle(), io.ReadSingle());
                        verts[i].TexCoord = new Vector2(io.ReadSingle(), io.ReadSingle());
                    }
                }
                else
                {
                    io.ReadBytes(vertCount * 20);
                }
            }

            var indexCount = io.ReadInt32();
            var indexBytes = io.ReadBytes(indexCount * 4);
            if (!wanted) return null;

            var indices = new int[indexCount];
            Buffer.BlockCopy(indexBytes, 0, indices, 0, indexBytes.Length);
            return (verts, indices, spr, dirV);
        }
    }
}
