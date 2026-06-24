using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;

namespace FSO.Files.RC.Utils
{
    /// <summary>
    /// Extracts the zero isosurface of a signed distance field using marching tetrahedra. Each grid cube
    /// is split into six tetrahedra, which avoids the large (and error-prone) marching-cubes case tables
    /// and the ambiguous-face problems, at the cost of more triangles (decimated later).
    ///
    /// Convention: the field is positive outside the surface and negative inside, so the gradient points
    /// outward and is used directly as the (smooth) surface normal. Vertices are welded by grid edge.
    /// </summary>
    public class MarchingTets
    {
        public List<Vector3> Positions = new List<Vector3>();
        public List<Vector3> Normals = new List<Vector3>();
        public List<int> Indices = new List<int>();

        private readonly float[] _f;
        private readonly Vector3 _origin;
        private readonly float _voxel;
        private readonly int _nx, _ny, _nz;
        private readonly float _iso;
        private readonly Dictionary<long, int> _edgeVerts = new Dictionary<long, int>();

        // Cube corner offsets.
        private static readonly int[,] CornerOff = {
            {0,0,0},{1,0,0},{1,1,0},{0,1,0},{0,0,1},{1,0,1},{1,1,1},{0,1,1}
        };
        // Six tetrahedra sharing the 0-6 diagonal.
        private static readonly int[,] Tets = {
            {0,5,1,6},{0,1,2,6},{0,2,3,6},{0,3,7,6},{0,7,4,6},{0,4,5,6}
        };

        /// <param name="iso">Isosurface level. A small positive value dilates the solid outward, which
        /// closes thin-surface holes (e.g. the grazing-angle top of objects) at the cost of slightly
        /// fatter geometry.</param>
        public MarchingTets(float[] field, Vector3 origin, float voxel, int nx, int ny, int nz, float iso = 0f)
        {
            _f = field; _origin = origin; _voxel = voxel; _nx = nx; _ny = ny; _nz = nz; _iso = iso;
        }

        private int Idx(int x, int y, int z) => (z * _ny + y) * _nx + x;

        public void Polygonize()
        {
            var corner = new int[8];
            for (int z = 0; z < _nz - 1; z++)
            {
                for (int y = 0; y < _ny - 1; y++)
                {
                    for (int x = 0; x < _nx - 1; x++)
                    {
                        for (int c = 0; c < 8; c++)
                            corner[c] = Idx(x + CornerOff[c, 0], y + CornerOff[c, 1], z + CornerOff[c, 2]);

                        for (int t = 0; t < 6; t++)
                            PolygonizeTet(
                                corner[Tets[t, 0]], corner[Tets[t, 1]],
                                corner[Tets[t, 2]], corner[Tets[t, 3]]);
                    }
                }
            }
        }

        private void PolygonizeTet(int g0, int g1, int g2, int g3)
        {
            Span<int> g = stackalloc int[4] { g0, g1, g2, g3 };
            Span<float> v = stackalloc float[4] { _f[g0], _f[g1], _f[g2], _f[g3] };

            Span<int> inside = stackalloc int[4];
            Span<int> outside = stackalloc int[4];
            int ni = 0, no = 0;
            for (int i = 0; i < 4; i++)
            {
                if (v[i] < _iso) inside[ni++] = i;
                else outside[no++] = i;
            }

            if (ni == 0 || ni == 4) return;

            if (ni == 1)
            {
                int a = inside[0];
                EmitTri(
                    Edge(g[a], g[outside[0]]),
                    Edge(g[a], g[outside[1]]),
                    Edge(g[a], g[outside[2]]));
            }
            else if (ni == 3)
            {
                int a = outside[0];
                EmitTri(
                    Edge(g[a], g[inside[0]]),
                    Edge(g[a], g[inside[1]]),
                    Edge(g[a], g[inside[2]]));
            }
            else // ni == 2
            {
                int i0 = inside[0], i1 = inside[1], o0 = outside[0], o1 = outside[1];
                int v00 = Edge(g[i0], g[o0]);
                int v01 = Edge(g[i0], g[o1]);
                int v11 = Edge(g[i1], g[o1]);
                int v10 = Edge(g[i1], g[o0]);
                EmitTri(v00, v01, v11);
                EmitTri(v00, v11, v10);
            }
        }

        private int Edge(int ga, int gb)
        {
            long key = ga < gb ? ((long)ga << 32) | (uint)gb : ((long)gb << 32) | (uint)ga;
            if (_edgeVerts.TryGetValue(key, out var existing)) return existing;

            float fa = _f[ga], fb = _f[gb];
            float denom = fb - fa;
            float t = Math.Abs(denom) < 1e-9f ? 0.5f : (_iso - fa) / denom;
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;

            var pa = CornerPos(ga);
            var pb = CornerPos(gb);
            var pos = Vector3.Lerp(pa, pb, t);

            var na = Gradient(ga);
            var nb = Gradient(gb);
            var normal = Vector3.Lerp(na, nb, t);
            if (normal.LengthSquared() < 1e-9f) normal = Vector3.UnitY;
            else normal.Normalize();

            int id = Positions.Count;
            Positions.Add(pos);
            Normals.Add(normal);
            _edgeVerts[key] = id;
            return id;
        }

        private void EmitTri(int a, int b, int c)
        {
            if (a == b || b == c || a == c) return; // degenerate

            // Orient so the geometric normal agrees with the (outward) gradient normal.
            var geo = Vector3.Cross(Positions[b] - Positions[a], Positions[c] - Positions[a]);
            var avg = Normals[a] + Normals[b] + Normals[c];
            if (Vector3.Dot(geo, avg) < 0f)
            {
                Indices.Add(a); Indices.Add(c); Indices.Add(b);
            }
            else
            {
                Indices.Add(a); Indices.Add(b); Indices.Add(c);
            }
        }

        private Vector3 CornerPos(int gi)
        {
            int x = gi % _nx;
            int y = (gi / _nx) % _ny;
            int z = gi / (_nx * _ny);
            return _origin + new Vector3(x, y, z) * _voxel;
        }

        private Vector3 Gradient(int gi)
        {
            int x = gi % _nx;
            int y = (gi / _nx) % _ny;
            int z = gi / (_nx * _ny);
            float gx = Sample(x + 1, y, z) - Sample(x - 1, y, z);
            float gy = Sample(x, y + 1, z) - Sample(x, y - 1, z);
            float gz = Sample(x, y, z + 1) - Sample(x, y, z - 1);
            return new Vector3(gx, gy, gz);
        }

        private float Sample(int x, int y, int z)
        {
            if (x < 0) x = 0; else if (x >= _nx) x = _nx - 1;
            if (y < 0) y = 0; else if (y >= _ny) y = _ny - 1;
            if (z < 0) z = 0; else if (z >= _nz) z = _nz - 1;
            return _f[(z * _ny + y) * _nx + x];
        }
    }
}
