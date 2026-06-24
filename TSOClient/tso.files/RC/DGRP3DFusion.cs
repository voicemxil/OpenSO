using FSO.Common.Rendering;
using FSO.Files.Formats.IFF.Chunks;
using FSO.Files.RC.Utils;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FSO.Files.RC
{
    /// <summary>
    /// Volumetric multi-view fusion (Phase 2). Instead of baking four independent 2.5D relief shells
    /// (one per rotation) that overlap in space — which produces seams, off-axis see-through, and
    /// differently-shaded triangles where one rotation's relief meets another's — this fuses all four
    /// rotation depth maps into a single watertight mesh.
    ///
    /// Pipeline:
    ///  1. Gather each (rotation, sprite) as a depth "patch" with an affine image->world frame. Because
    ///     the existing per-rotation transforms already place all rotations into one common space, the
    ///     patches are mutually aligned with no extra registration.
    ///  2. Build a truncated signed distance field (TSDF) over a voxel grid: each patch contributes a
    ///     signed distance along its view axis where it sees a surface, and carves empty space where it
    ///     sees background (the silhouette / visual-hull constraint bounds the object laterally).
    ///  3. Extract the zero isosurface with marching tetrahedra (robust, no large case tables).
    ///  4. Per-vertex normals come from the TSDF gradient -> smooth and consistent across the whole
    ///     surface (the fix for the per-rotation shading seams), independent of triangulation.
    ///  5. Texture each triangle from its best-facing source view, projecting into that sprite's image
    ///     for UVs, and group by texture so the existing renderer/storage is unchanged.
    ///
    /// Only valid for static objects (no dynamic sprites); the caller falls back to the per-view path
    /// otherwise.
    /// </summary>
    public static class DGRP3DFusion
    {
        private class Patch
        {
            public int W, H;
            public float[] Depth;   // conditioned, 0..1 (1 == far/background)
            public bool[] Valid;
            public Matrix FrameInv; // world -> (px, py, u=1-depth)
            public Matrix SprInv;   // world -> sprite image space (for UVs), = Invert(sprMat)
            public Vector2 Pos;     // sprite image origin used by the UV formula
            public Vector3 CamDir;  // unit direction toward the camera for this view
            public float DLen;      // |dFactor|, converts u-space to world distance
            public bool Flip;
            public int Rotation;
            public int SprIndex;
            public Texture2D Texture;
        }

        // Grid resolution along the longest bounding-box axis.
        private const int GridMax = 96;
        // Truncation distance for the TSDF, in voxels.
        private const float TruncVoxels = 3f;

        public static bool TryBuild(DGRP dgrp, OBJD obj, GraphicsDevice gd, DGRPRCParams config,
            bool[] rotations, float zOff, float factor,
            out List<DGRP3DGeometry> geoms, out BoundingBox bounds)
        {
            geoms = null;
            bounds = new BoundingBox();

            var patches = GatherPatches(dgrp, obj, gd, config, rotations, zOff, factor);
            if (patches.Count == 0) return false;

            // --- Bounding box from the back-projected valid surface points (tight to the object,
            //     not the full sprite quad) so the grid resolution is spent where the object is. ---
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            int surfacePts = 0;
            foreach (var p in patches)
            {
                var frame = Matrix.Invert(p.FrameInv);
                for (int py = 0; py < p.H; py++)
                {
                    for (int px = 0; px < p.W; px++)
                    {
                        int idx = py * p.W + px;
                        if (!p.Valid[idx]) continue;
                        float us = 1f - p.Depth[idx];
                        var w = Vector3.Transform(new Vector3(px, py, us), frame);
                        min = Vector3.Min(min, w);
                        max = Vector3.Max(max, w);
                        surfacePts++;
                    }
                }
            }
            if (surfacePts == 0) return false;

            var size = max - min;
            float maxDim = Math.Max(size.X, Math.Max(size.Y, size.Z));
            if (maxDim <= 0) return false;
            float voxel = maxDim / GridMax;
            // pad by truncation so the surface isn't clipped at the grid edge
            var pad = new Vector3(voxel * (TruncVoxels + 2));
            min -= pad; max += pad;
            size = max - min;

            int nx = Math.Max(2, (int)Math.Ceiling(size.X / voxel)) + 1;
            int ny = Math.Max(2, (int)Math.Ceiling(size.Y / voxel)) + 1;
            int nz = Math.Max(2, (int)Math.Ceiling(size.Z / voxel)) + 1;
            // keep memory sane
            if ((long)nx * ny * nz > 6_000_000) return false;

            float trunc = voxel * TruncVoxels;

            var grid = BuildTSDF(patches, min, voxel, nx, ny, nz, trunc);
            // Gentle smoothing connects fragments and rounds the carved silhouette so the isosurface
            // doesn't break into shards at edges/grazing angles.
            SmoothField(grid, nx, ny, nz);

            // Gentle dilation as a safety net; the 2-view depth rule already closes most pinholes.
            var mc = new MarchingTets(grid, min, voxel, nx, ny, nz, iso: 0.25f * voxel);
            mc.Polygonize();
            if (mc.Indices.Count == 0) return false;

            // Remove small disconnected islands (stray shards) from the fused surface.
            CullComponents(mc);
            if (mc.Indices.Count == 0) return false;

            // --- Texture + group by source view ---
            geoms = AssignTextures(mc, patches);
            bounds = BoundingBox.CreateFromPoints(mc.Positions);
            return geoms.Count > 0;
        }

        private static List<Patch> GatherPatches(DGRP dgrp, OBJD obj, GraphicsDevice gd,
            DGRPRCParams config, bool[] rotations, float zOff, float factor)
        {
            var patches = new List<Patch>();
            for (uint rotation = 0; rotation < 4; rotation++)
            {
                if (!rotations[rotation]) continue;
                var img = dgrp.GetImage(1, 3, rotation);
                if (img == null) continue;

                var mat = Matrix.CreateTranslation(new Vector3(-72, -344, zOff));
                mat *= Matrix.CreateScale((1f / 128) * 1.43f);
                mat *= Matrix.CreateScale(1, -1, 1);
                mat *= Matrix.CreateRotationX((float)Math.PI / -6);
                mat *= Matrix.CreateRotationY(((float)Math.PI / 4) * (1 + rotation * 2));

                int sprIndex = 0;
                foreach (var sprite in img.Sprites)
                {
                    int curIndex = sprIndex++;
                    // Skip dynamic sprites; fusion only handles the static base.
                    var isDynamic = sprite.SpriteID >= obj.DynamicSpriteBaseId
                        && sprite.SpriteID < (obj.DynamicSpriteBaseId + obj.NumDynamicSprites);
                    if (isDynamic) continue;

                    var tex = sprite.GetTexture(gd);
                    if (tex == null) continue;
                    var depthB = sprite.GetDepth();
                    if (depthB == null) continue;

                    var sprMat = mat * Matrix.CreateTranslation(
                        new Vector3(sprite.ObjectOffset.X, sprite.ObjectOffset.Z, sprite.ObjectOffset.Y)
                        * new Vector3(1f / 16f, 1f / 5f, 1f / 16f));

                    var w = ((TextureInfo)tex.Tag).Size.X;
                    var h = ((TextureInfo)tex.Tag).Size.Y;
                    if (w <= 0 || h <= 0) continue;

                    var cond = DepthConditioner.Process(depthB, w, h, config);

                    var pos = sprite.SpriteOffset + new Vector2(72, 348 - h);
                    var tl = Vector3.Transform(new Vector3(pos, 0), sprMat);
                    var tr = Vector3.Transform(new Vector3(pos + new Vector2(w, 0), 0), sprMat);
                    var bl = Vector3.Transform(new Vector3(pos + new Vector2(0, h), 0), sprMat);
                    var tlFront = Vector3.Transform(new Vector3(pos, 110.851251f), sprMat);

                    var xInc = (tr - tl) / w;
                    var yInc = (bl - tl) / h;
                    var dFactor = (tlFront - tl) / factor;

                    // Affine frame: Transform((px,py,u), frame) = world
                    var frame = new Matrix(
                        xInc.X, xInc.Y, xInc.Z, 0,
                        yInc.X, yInc.Y, yInc.Z, 0,
                        dFactor.X, dFactor.Y, dFactor.Z, 0,
                        tl.X, tl.Y, tl.Z, 1);

                    patches.Add(new Patch
                    {
                        W = w,
                        H = h,
                        Depth = cond.Depth,
                        Valid = cond.Valid,
                        FrameInv = Matrix.Invert(frame),
                        SprInv = Matrix.Invert(sprMat),
                        Pos = pos,
                        CamDir = Vector3.Normalize(dFactor),
                        DLen = dFactor.Length(),
                        Flip = sprite.Flip,
                        Rotation = (int)rotation,
                        SprIndex = curIndex,
                        Texture = tex
                    });
                }
            }
            return patches;
        }

        private static float[] BuildTSDF(List<Patch> patches, Vector3 origin, float voxel,
            int nx, int ny, int nz, float trunc)
        {
            int n = nx * ny * nz;
            var field = new float[n];

            // Group sprites by rotation. Each rotation is a complete view of the whole object, possibly
            // split across several sprites (tiles). Coverage must be judged per rotation: a voxel is
            // empty for a rotation only if it falls outside ALL of that rotation's sprites.
            var byRotation = new Dictionary<int, List<Patch>>();
            foreach (var p in patches)
            {
                if (!byRotation.TryGetValue(p.Rotation, out var list))
                {
                    list = new List<Patch>();
                    byRotation[p.Rotation] = list;
                }
                list.Add(p);
            }
            var rotations = byRotation.Values.ToList();

            for (int zi = 0; zi < nz; zi++)
            {
                for (int yi = 0; yi < ny; yi++)
                {
                    for (int xi = 0; xi < nx; xi++)
                    {
                        var wp = origin + new Vector3(xi, yi, zi) * voxel;

                        // Visual hull (silhouette) intersection + depth UNION:
                        //  - Silhouette (background or out-of-coverage in ANY rotation) carves strictly,
                        //    keeping sharp corners and a tight outline.
                        //  - Depth only FILLS: a voxel is solid if ANY view sees it behind its surface
                        //    (take the most-inside value). Using depth to carve (intersection) made the
                        //    grazing top-views' disagreement about surface height punch scattered gaps;
                        //    union can't create disagreement holes.
                        bool silhouetteEmpty = false;
                        float minAll = trunc; // most-inside across all views

                        foreach (var group in rotations)
                        {
                            bool anyInBounds = false, anyValid = false;
                            float minValid = trunc;
                            foreach (var p in group)
                            {
                                var local = Vector3.Transform(wp, p.FrameInv);
                                float fx = local.X, fy = local.Y, u = local.Z;
                                int ix = (int)(fx + 0.5f), iy = (int)(fy + 0.5f);
                                if (ix < 0 || ix >= p.W || iy < 0 || iy >= p.H) continue;
                                anyInBounds = true;

                                int idx = iy * p.W + ix;
                                if (!p.Valid[idx]) continue; // background pixel: no surface here
                                anyValid = true;
                                float us = 1f - p.Depth[idx];
                                float s = (u - us) * p.DLen;
                                if (s > trunc) s = trunc; else if (s < -trunc) s = -trunc;
                                if (s < minValid) minValid = s;
                            }

                            if (!anyInBounds || !anyValid)
                            {
                                silhouetteEmpty = true; // outside coverage or on background -> empty
                                break;
                            }

                            if (minValid < minAll) minAll = minValid; // union across rotations
                        }

                        field[(zi * ny + yi) * nx + xi] = silhouetteEmpty ? trunc : minAll;
                    }
                }
            }

            return field;
        }

        /// <summary>Separable 3x3x3 box blur of the distance field (one pass).</summary>
        private static void SmoothField(float[] f, int nx, int ny, int nz)
        {
            int Idx(int x, int y, int z) => (z * ny + y) * nx + x;
            var tmp = new float[f.Length];

            // X
            for (int z = 0; z < nz; z++)
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx; x++)
                    {
                        float a = f[Idx(x > 0 ? x - 1 : 0, y, z)];
                        float b = f[Idx(x, y, z)];
                        float c = f[Idx(x < nx - 1 ? x + 1 : nx - 1, y, z)];
                        tmp[Idx(x, y, z)] = (a + b + c) / 3f;
                    }
            // Y
            for (int z = 0; z < nz; z++)
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx; x++)
                    {
                        float a = tmp[Idx(x, y > 0 ? y - 1 : 0, z)];
                        float b = tmp[Idx(x, y, z)];
                        float c = tmp[Idx(x, y < ny - 1 ? y + 1 : ny - 1, z)];
                        f[Idx(x, y, z)] = (a + b + c) / 3f;
                    }
            // Z
            for (int z = 0; z < nz; z++)
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx; x++)
                    {
                        float a = f[Idx(x, y, z > 0 ? z - 1 : 0)];
                        float b = f[Idx(x, y, z)];
                        float c = f[Idx(x, y, z < nz - 1 ? z + 1 : nz - 1)];
                        tmp[Idx(x, y, z)] = (a + b + c) / 3f;
                    }
            Array.Copy(tmp, f, f.Length);
        }

        /// <summary>Removes small disconnected triangle islands from the marching-tets output.</summary>
        private static void CullComponents(MarchingTets mc)
        {
            int vn = mc.Positions.Count;
            var ind = mc.Indices;
            if (vn == 0 || ind.Count == 0) return;

            var parent = new int[vn];
            for (int i = 0; i < vn; i++) parent[i] = i;
            int Find(int a) { while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; }
            void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }

            for (int t = 0; t < ind.Count; t += 3)
            {
                Union(ind[t], ind[t + 1]);
                Union(ind[t + 1], ind[t + 2]);
            }

            var count = new Dictionary<int, int>();
            int largest = 0;
            for (int t = 0; t < ind.Count; t += 3)
            {
                int r = Find(ind[t]);
                int c = count.TryGetValue(r, out var cur) ? cur + 1 : 1;
                count[r] = c;
                if (c > largest) largest = c;
            }
            if (largest < 12) return;
            int minTris = Math.Max(8, (int)(largest * 0.03f));

            var newInd = new List<int>(ind.Count);
            for (int t = 0; t < ind.Count; t += 3)
                if (count[Find(ind[t])] >= minTris)
                {
                    newInd.Add(ind[t]); newInd.Add(ind[t + 1]); newInd.Add(ind[t + 2]);
                }
            if (newInd.Count == ind.Count) return;

            var remap = new int[vn];
            for (int i = 0; i < vn; i++) remap[i] = -1;
            var np = new List<Vector3>();
            var nn = new List<Vector3>();
            for (int k = 0; k < newInd.Count; k++)
            {
                int old = newInd[k];
                if (remap[old] == -1)
                {
                    remap[old] = np.Count;
                    np.Add(mc.Positions[old]);
                    nn.Add(mc.Normals[old]);
                }
                newInd[k] = remap[old];
            }
            mc.Positions.Clear(); mc.Positions.AddRange(np);
            mc.Normals.Clear(); mc.Normals.AddRange(nn);
            mc.Indices.Clear(); mc.Indices.AddRange(newInd);
        }

        private static List<DGRP3DGeometry> AssignTextures(MarchingTets mc, List<Patch> patches)
        {
            var groups = new Dictionary<Texture2D, GroupBuild>();

            var pos = mc.Positions;
            var nrm = mc.Normals;
            var ind = mc.Indices;

            for (int t = 0; t < ind.Count; t += 3)
            {
                int a = ind[t], b = ind[t + 1], c = ind[t + 2];
                var faceN = nrm[a] + nrm[b] + nrm[c];
                if (faceN.LengthSquared() < 1e-9f) faceN = Vector3.UnitY;
                else faceN.Normalize();
                var centroid = (pos[a] + pos[b] + pos[c]) / 3f;

                // Choose the patch that best faces this triangle and contains its projection.
                Patch best = null;
                float bestScore = -2f;
                foreach (var p in patches)
                {
                    float score = Vector3.Dot(faceN, p.CamDir);
                    if (score <= bestScore) continue;
                    var iv = Vector3.Transform(centroid, p.SprInv);
                    if (iv.X < p.Pos.X - 1 || iv.X > p.Pos.X + p.W + 1 ||
                        iv.Y < p.Pos.Y - 1 || iv.Y > p.Pos.Y + p.H + 1) continue;
                    bestScore = score;
                    best = p;
                }
                if (best == null)
                {
                    // Fall back to most front-facing regardless of bounds.
                    foreach (var p in patches)
                    {
                        float score = Vector3.Dot(faceN, p.CamDir);
                        if (score > bestScore) { bestScore = score; best = p; }
                    }
                    if (best == null) continue;
                }

                if (!groups.TryGetValue(best.Texture, out var g))
                {
                    g = new GroupBuild { Patch = best };
                    groups[best.Texture] = g;
                }

                foreach (var vid in new[] { a, b, c })
                {
                    int local;
                    if (!g.Remap.TryGetValue(vid, out local))
                    {
                        local = g.Verts.Count;
                        g.Remap[vid] = local;
                        g.Verts.Add(new DGRP3DVert(pos[vid], nrm[vid], UV(best, pos[vid])));
                    }
                    g.Indices.Add(local);
                }
            }

            var result = new List<DGRP3DGeometry>();
            foreach (var g in groups.Values)
            {
                var geom = new DGRP3DGeometry
                {
                    Pixel = g.Patch.Texture,
                    PixelDir = (ushort)g.Patch.Rotation,
                    PixelSPR = (ushort)g.Patch.SprIndex,
                    SVerts = g.Verts,
                    SIndices = g.Indices
                };
                result.Add(geom);
            }
            return result;
        }

        private static Vector2 UV(Patch p, Vector3 worldPos)
        {
            var iv = Vector3.Transform(worldPos, p.SprInv);
            float u = (iv.X - p.Pos.X + 0.5f) / p.W;
            float v = (iv.Y - p.Pos.Y + 0.5f) / p.H;
            if (p.Flip) u = 1f - u;
            return new Vector2(u, v);
        }

        private class GroupBuild
        {
            public Patch Patch;
            public List<DGRP3DVert> Verts = new List<DGRP3DVert>();
            public List<int> Indices = new List<int>();
            public Dictionary<int, int> Remap = new Dictionary<int, int>();
        }
    }
}
