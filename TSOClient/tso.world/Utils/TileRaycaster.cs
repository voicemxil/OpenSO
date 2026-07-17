using FSO.LotView.Model;
using Microsoft.Xna.Framework;
using System.Runtime.CompilerServices;

namespace FSO.LotView.Utils
{
    internal interface ITileRaycastTarget<TResult>
        where TResult : struct
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static abstract (float, TResult)? TestRay(Ray ray, Point tile, Point nextTile, float? edge, sbyte level, Blueprint bp);
    }

    public struct WallRayHit { public WallSegments Segment; public Point Tile; }

    internal class WallTileRaycastTarget : ITileRaycastTarget<WallRayHit>
    {
        // Wall height in world units: 2.95 floors * 3 units/floor.
        private const float WallHeight = 8.85f;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (float, WallRayHit)? TestRay(Ray ray, Point tile, Point nextTile, float? edge, sbyte level, Blueprint bp)
        {
            var segs = bp.GetWall((short)tile.X, (short)tile.Y, level).Segments;
            if (segs == 0) return null;

            float hitDist;
            WallSegments hitSegment;

            if ((segs & WallSegments.AnyDiag) != 0)
            {
                var isVertical = (segs & WallSegments.VerticalDiag) != 0;
                var mid = new Vector3((tile.X + 0.5f) * 3, 0, (tile.Y + 0.5f) * 3);
                var corner = mid + (isVertical ? new Vector3(-1.5f, 0, -1.5f) : new Vector3(-1.5f, 0, 1.5f));
                var d = ray.Intersects(new Plane(mid, mid + Vector3.Up, corner));
                if (!d.HasValue) return null;
                hitDist = d.Value;
                hitSegment = isVertical ? WallSegments.VerticalDiag : WallSegments.HorizontalDiag;
            }
            else
            {
                if ((segs & WallSegments.AnyAdj) == 0 || !edge.HasValue) return null;

                WallSegments edgeSegs = 0;
                var dx = nextTile.X - tile.X;
                var dy = nextTile.Y - tile.Y;
                if (dy > 0) edgeSegs |= WallSegments.BottomLeft;
                if (dx < 0) edgeSegs |= WallSegments.TopLeft;
                if (dy < 0) edgeSegs |= WallSegments.TopRight;
                if (dx > 0) edgeSegs |= WallSegments.BottomRight;

                hitSegment = edgeSegs & segs;
                if (hitSegment == 0) return null;
                hitDist = edge.Value;
            }

            var wallBottom = bp.GetAltitude(tile.X, tile.Y) * 3;
            var rayY = ray.Position.Y + ray.Direction.Y * hitDist;
            if (rayY < wallBottom || rayY >= wallBottom + WallHeight) return null;

            return (hitDist, new WallRayHit { Segment = hitSegment, Tile = tile });
        }
    }

    internal class CombinedTileRaycastTarget<TFirst, TFirstResult, TSecond, TSecondResult> : ITileRaycastTarget<(TFirstResult?, TSecondResult?)>
        where TFirst : ITileRaycastTarget<TFirstResult>
        where TSecond : ITileRaycastTarget<TSecondResult>
        where TFirstResult : struct
        where TSecondResult : struct
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (float, (TFirstResult?, TSecondResult?))? TestRay(Ray ray, Point tile, Point nextTile, float? edge, sbyte level, Blueprint bp)
        {
            var first = TFirst.TestRay(ray, tile, nextTile, edge, level, bp);
            var second = TSecond.TestRay(ray, tile, nextTile, edge, level, bp);

            if (first.HasValue && second.HasValue)
            {
                if (first.Value.Item1 <= second.Value.Item1)
                {
                    return (first.Value.Item1, (first.Value.Item2, default));
                }
                else
                {
                    return (second.Value.Item1, (default, second.Value.Item2));
                }
            }

            if (first.HasValue)
            {
                return (first.Value.Item1, (first.Value.Item2, default));
            }

            if (second.HasValue)
            {
                return (second.Value.Item1, (default, second.Value.Item2));
            }

            return null;
        }
    }

    internal class TileRaycaster<T, TResult>
        where T : ITileRaycastTarget<TResult>
        where TResult : struct
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float? BoxRC2(Ray ray, float tileSize)
        {
            //find next tile boundary
            var px = ray.Direction.X > 0;
            var py = ray.Direction.Z > 0;
            int x = !px ? (int)MathF.Ceiling(ray.Position.X / tileSize) : (int)(ray.Position.X / tileSize);
            int y = !py ? (int)MathF.Ceiling(ray.Position.Z / tileSize) : (int)(ray.Position.Z / tileSize);
            float nx = (px ? x + 1 : x - 1) * tileSize;
            float ny = (py ? y + 1 : y - 1) * tileSize;

            const float Epsilon = 1e-6f;
            float? min = null;
            if (MathF.Abs(ray.Direction.X) > Epsilon)
            {
                min = (nx - ray.Position.X) / ray.Direction.X;
            }
            if (MathF.Abs(ray.Direction.Z) > Epsilon)
            {
                var min2 = (ny - ray.Position.Z) / ray.Direction.Z;
                if (min == null || min.Value > min2) min = min2;
            }
            return min;
        }

        public static (float, TResult)? Raycast(Ray ray, sbyte level, Blueprint bp, float maxDist)
        {
            if (bp?.Altitude == null) return null;

            var baseBox = new BoundingBox(new Vector3(0, -5000, 0), new Vector3(bp.Width * 3, 5000, bp.Height * 3));
            if (baseBox.Contains(ray.Position) != ContainmentType.Contains)
            {
                var i = baseBox.Intersects(ray);
                if (i == null || i.Value > maxDist) return null;
                ray.Position += ray.Direction * (i.Value + 0.01f);
            }

            var mx = (int)ray.Position.X / 3;
            var my = (int)ray.Position.Z / 3;
            var px = ray.Direction.X > 0;
            var py = ray.Direction.Z > 0;
            float totalDist = 0;
            int width = bp.Width;

            for (int iteration = 0; iteration < 1000; iteration++)
            {
                if ((uint)mx >= (uint)width || (uint)my >= (uint)width) break;

                var tileDist = BoxRC2(ray, 3);
                if (tileDist == null) break;

                float addDist = tileDist.Value + 0.00001f;
                var nextPos = ray.Position + ray.Direction * addDist;
                int nextX = !px ? ((int)MathF.Ceiling(nextPos.X / 3) - 1) : (int)(nextPos.X / 3);
                int nextY = !py ? ((int)MathF.Ceiling(nextPos.Z / 3) - 1) : (int)(nextPos.Z / 3);

                var result = T.TestRay(ray, new Point(mx, my), new Point(nextX, nextY), tileDist, level, bp);
                if (result != null && result.Value.Item1 <= tileDist)
                {
                    var hitDist = totalDist + result.Value.Item1 + 0.00001f;
                    if (hitDist > maxDist) return null;
                    return (hitDist, result.Value.Item2);
                }

                totalDist += addDist;
                if (totalDist > maxDist) break;

                ray.Position = nextPos;
                mx = nextX;
                my = nextY;
            }

            return null;
        }

        public static (float, TResult)? RaycastMultifloor(Ray ray, Blueprint bp, float maxDist, int maxFloor = -1)
        {
            if (maxFloor == -1) maxFloor = bp.Stories;

            (float, TResult)? bestResult = null;
            for (int i = 1; i <= maxFloor; i++)
            {
                var result = Raycast(ray, (sbyte)i, bp, maxDist);
                if (result != null && (bestResult == null || result.Value.Item1 < bestResult.Value.Item1))
                {
                    bestResult = result;
                    maxDist = result.Value.Item1;
                }
                // Next floor
                ray.Position -= new Vector3(0, 2.95f * 3, 0);
            }

            return bestResult;
        }
    }

    internal class WallRaycaster : TileRaycaster<WallTileRaycastTarget, WallRayHit>
    {

    }

    /// <summary>
    /// Exact wall picking: tests the ray against every wall segment on a floor as a bounded quad and
    /// returns the nearest hit. Replaces the tile-DDA + infinite-plane approach for wall picks, which
    /// missed diagonals whenever the view direction was near-parallel to the diagonal's plane (the
    /// game's snap camera angles sit EXACTLY there) and was fragile at grazing tile steps.
    ///
    /// Two subtleties this handles that a naive quad test does not:
    /// - A physical wall is stored twice (a tile's TopLeft is its -X neighbour's BottomRight on the
    ///   same plane), and the two representations name the wall's two FACES. The hit must report the
    ///   face the camera can see, chosen from the ray direction — otherwise pattern reads/paints land
    ///   on the far side.
    /// - Rendered walls have thickness; the math plane does not. Viewed edge-on (the 45-degree snap
    ///   rotations are edge-on to every vertical diagonal) the plane is invisible to the ray even
    ///   though the player sees and clicks a thick band. Near-parallel rays therefore fall back to a
    ///   slab test: if the ray runs within the wall's half-thickness, it hits where it first enters
    ///   the wall's bounds.
    ///
    /// A full-grid scan is a few tens of thousands of cheap tests — nothing at click/hover rates.
    /// </summary>
    internal static class WallQuadPicker
    {
        // Wall height in world units: 2.95 floors * 3 units/floor (matches WallTileRaycastTarget).
        private const float WallHeight = 8.85f;
        // Half-thickness of the edge-on slab, world units (1 tile = 3). Forgiving on purpose: at
        // edge-on angles the rendered band is only a few pixels wide.
        private const float HalfThick = 0.5f;
        // Below this |cos| between the view direction and the wall normal (ground-projected), the
        // plane test is numerically/visually edge-on and the slab fallback takes over.
        private const float EdgeOnCos = 0.05f;

        public static (float Dist, WallRayHit Hit)? Raycast(Ray ray, sbyte level, Blueprint bp, float maxDist)
        {
            if (bp?.Altitude == null || level < 1 || level > bp.Stories) return null;
            var walls = bp.Walls[level - 1];
            int width = bp.Width, height = bp.Height;

            (float, WallRayHit)? best = null;
            float bestDist = maxDist;
            for (short y = 0; y < height; y++)
            {
                int rowBase = y * width;
                for (short x = 0; x < width; x++)
                {
                    var segs = walls[rowBase + x].Segments;
                    if (segs == 0) continue;
                    float bottom = bp.GetAltitude(x, y) * 3;
                    float x0 = x * 3, x1 = x0 + 3, z0 = y * 3, z1 = z0 + 3;

                    if ((segs & WallSegments.VerticalDiag) != 0)
                        TestQuad(ray, new Vector2(x0, z0), new Vector2(x1, z1), bottom,
                            new Point(x, y), WallSegments.VerticalDiag, new Point(x, y), WallSegments.VerticalDiag, ref best, ref bestDist);
                    else if ((segs & WallSegments.HorizontalDiag) != 0)
                        TestQuad(ray, new Vector2(x0, z1), new Vector2(x1, z0), bottom,
                            new Point(x, y), WallSegments.HorizontalDiag, new Point(x, y), WallSegments.HorizontalDiag, ref best, ref bestDist);
                    else
                    {
                        // Test each physical wall ONCE via its Top representation; report the face the
                        // camera sees. (faceA = the face whose ground normal is n = (-edge.Z, edge.X),
                        // visible when dot(dir, n) < 0; faceB = the opposite face.)
                        if ((segs & WallSegments.TopLeft) != 0)
                            TestQuad(ray, new Vector2(x0, z0), new Vector2(x0, z1), bottom,
                                new Point(x - 1, y), WallSegments.BottomRight,   // faceA: -X-facing, into tile x-1
                                new Point(x, y), WallSegments.TopLeft,           // faceB: +X-facing, into tile x
                                ref best, ref bestDist);
                        if ((segs & WallSegments.TopRight) != 0)
                            TestQuad(ray, new Vector2(x0, z0), new Vector2(x1, z0), bottom,
                                new Point(x, y), WallSegments.TopRight,          // faceA: +Z-facing, into tile y
                                new Point(x, y - 1), WallSegments.BottomLeft,    // faceB: -Z-facing, into tile y-1
                                ref best, ref bestDist);
                        // Bottom segments normally mirror a neighbour's Top and would double-test the
                        // same plane; only test them when the mirroring Top is absent (lot borders).
                        if ((segs & WallSegments.BottomRight) != 0 && (x + 1 >= width || (walls[rowBase + x + 1].Segments & WallSegments.TopLeft) == 0))
                            TestQuad(ray, new Vector2(x1, z0), new Vector2(x1, z1), bottom,
                                new Point(x, y), WallSegments.BottomRight, new Point(x, y), WallSegments.BottomRight, ref best, ref bestDist);
                        if ((segs & WallSegments.BottomLeft) != 0 && (y + 1 >= height || (walls[rowBase + width + x].Segments & WallSegments.TopRight) == 0))
                            TestQuad(ray, new Vector2(x0, z1), new Vector2(x1, z1), bottom,
                                new Point(x, y), WallSegments.BottomLeft, new Point(x, y), WallSegments.BottomLeft, ref best, ref bestDist);
                    }
                }
            }
            return best;
        }

        private static void TestQuad(Ray ray, Vector2 g0, Vector2 g1, float bottom,
            Point tileA, WallSegments segA, Point tileB, WallSegments segB,
            ref (float, WallRayHit)? best, ref float bestDist)
        {
            var edge = g1 - g0;                       // ground direction of the wall
            var n = new Vector2(-edge.Y, edge.X);     // ground normal (faceA's side)
            var posG = new Vector2(ray.Position.X, ray.Position.Z);
            var dirG = new Vector2(ray.Direction.X, ray.Direction.Z);
            float denom = Vector2.Dot(dirG, n);
            float edgeLen = edge.Length();

            float t;
            if (Math.Abs(denom) >= EdgeOnCos * n.Length() * Math.Max(dirG.Length(), 1e-3f))
            {
                // Normal case: plane intersection.
                t = Vector2.Dot(g0 - posG, n) / denom;
                if (t < 0 || t >= bestDist) return;

                var hitG = posG + dirG * t;
                float u = Vector2.Dot(hitG - g0, edge) / (edgeLen * edgeLen);
                if (u < 0 || u > 1) return;
            }
            else
            {
                // Edge-on: the ray runs (near-)parallel to the wall plane. The rendered wall is a
                // thick band, so treat it as a slab: while inside the wall's along-edge span, the ray
                // must pass within half-thickness of the plane (checked AT the wall, not at the
                // camera); it hits where it first enters that span.
                var e = edge / edgeLen;
                float s0 = Vector2.Dot(posG - g0, e);
                float sd = Vector2.Dot(dirG, e);
                if (Math.Abs(sd) < 1e-6f) return;     // no ground motion along the wall (straight-down ray)

                float tA = (0 - s0) / sd, tB = (edgeLen - s0) / sd;
                float tEnter = Math.Max(Math.Min(tA, tB), 0);
                float tExit = Math.Max(tA, tB);

                // Constrain to where the ray is also inside the height window, so the reported hit
                // is a point actually ON the wall (not the span entry at some other altitude).
                if (Math.Abs(ray.Direction.Y) > 1e-6f)
                {
                    float th1 = (bottom + 1e-3f - ray.Position.Y) / ray.Direction.Y;
                    float th2 = (bottom + WallHeight - 1e-3f - ray.Position.Y) / ray.Direction.Y;
                    tEnter = Math.Max(tEnter, Math.Min(th1, th2));
                    tExit = Math.Min(tExit, Math.Max(th1, th2));
                }
                else if (ray.Position.Y < bottom || ray.Position.Y >= bottom + WallHeight) return;

                if (tEnter >= tExit || tEnter >= bestDist) return;   // span behind the ray or too far

                var nn = n / n.Length();
                float pEnter = Vector2.Dot(posG + dirG * tEnter - g0, nn);
                float pExit = Vector2.Dot(posG + dirG * tExit - g0, nn);
                bool within = Math.Abs(pEnter) <= HalfThick || Math.Abs(pExit) <= HalfThick
                    || (pEnter > 0) != (pExit > 0);   // crosses the plane inside the span
                if (!within) return;
                t = tEnter;
            }

            var hitY = ray.Position.Y + ray.Direction.Y * t;
            if (hitY < bottom || hitY >= bottom + WallHeight) return;

            // Report the FACE the camera sees: faceA's normal is n, visible when the ray runs against
            // it. Edge-on ties (denom ~ 0) resolve arbitrarily to faceA — both faces are slivers then.
            var (tile, seg) = denom < 0 ? (tileA, segA) : (tileB, segB);
            if (tile.X < 0 || tile.Y < 0) (tile, seg) = denom < 0 ? (tileB, segB) : (tileA, segA);

            bestDist = t;
            best = (t, new WallRayHit { Segment = seg, Tile = tile });
        }
    }
}
