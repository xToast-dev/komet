using System;
using System.Runtime.CompilerServices;

namespace Komet;

/// <summary>
/// The chunk-grid ray walk behind occlusion culling, with the per-ray constants hoisted out
/// of the per-step loop.
///
/// Vanilla's ChunkCuller.getExitingFace recomputes, for every chunk the ray crosses, six
/// normal-dot-direction products, six Vec3d plane positions and up to three divisions - even
/// though all six normals are axis aligned and the ray direction never changes during a walk.
/// At viewDistance 1536 the shell is ~24 700 positions x 3 rays x ~70 chunks, so that is on
/// the order of five million steps per occlusion pass.
///
/// Because every normal has exactly one non-zero component of +/-1, the plane equation
/// collapses to t = (pos[axis] + planeCentre[axis] - origin[axis]) / dir[axis], with the two
/// signs cancelling exactly in IEEE arithmetic. All the quantities involved are small
/// multiples of 1/4, so they are exactly representable and re-associating the subtraction is
/// exact too. The walk therefore visits exactly the same chunks as vanilla.
/// </summary>
public static class RayTraversal
{
    /// <summary>Per-step callbacks. A struct implementation gets fully inlined by the JIT.</summary>
    public interface IChunkSink
    {
        /// <summary>
        /// Mark the chunk at this position visible if it exists.
        /// Returns false if the ray should stop here (chunk is not traversable this way).
        /// </summary>
        bool Visit(int cx, int cy, int cz, int fromFace, int toFace, bool checkBlocking);

        bool IsValidChunkPos(int cx, int cy, int cz);
    }

    // ALLFACES order: north, east, south, west, up, down
    private static readonly int[] StepX = [0, 1, 0, -1, 0, 0];
    private static readonly int[] StepY = [0, 0, 0, 0, 1, -1];
    private static readonly int[] StepZ = [-1, 0, 1, 0, 0, 0];
    private static readonly int[] Opposite = [2, 3, 0, 1, 5, 4];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Hits(double t, double ox, double oy, double oz, double dx, double dy, double dz,
                             double planeX, double planeY, double planeZ)
    {
        return t >= 0.0
            && Math.Abs(ox + dx * t - planeX) <= 0.5
            && Math.Abs(oy + dy * t - planeY) <= 0.5
            && Math.Abs(oz + dz * t - planeZ) <= 0.5;
    }

    /// <summary>
    /// Walks from a chunk position along a direction, marking chunks visible until the ray
    /// leaves the map, runs out of length, or hits geometry it cannot see through.
    /// </summary>
    public static void Trace<TSink>(ref TSink sink,
                                    int fromX, int fromY, int fromZ,
                                    int relX, int relY, int relZ,
                                    double xoffset, double yoffset,
                                    bool aboveHeightLimit)
        where TSink : struct, IChunkSink
    {
        double ox = fromX + xoffset, oy = fromY + yoffset, oz = fromZ + 0.5;
        double dx = relX + xoffset, dy = relY + yoffset, dz = relZ + 0.5;

        // At most three of the six faces can be exit faces for a given direction.
        bool north = -dz > 1E-05, east = dx > 1E-05, south = dz > 1E-05;
        bool west = -dx > 1E-05, up = dy > 1E-05, down = -dy > 1E-05;

        // plane-centre minus ray origin, per face, along that face's own axis
        double cNorth = 0.0 - oz, cSouth = 1.0 - oz;
        double cEast = 1.0 - ox, cWest = 0.0 - ox;
        double cUp = 1.0 - oy, cDown = 0.0 - oy;

        int px = fromX, py = fromY, pz = fromZ;
        var rayLen = Math.Abs(relX) + Math.Abs(relY) + Math.Abs(relZ);
        var fromFace = -1;

        while (true)
        {
            var dist = Math.Abs(px - fromX) + Math.Abs(py - fromY) + Math.Abs(pz - fromZ);
            if (dist > rayLen + 2) break;

            double cx = px + 0.5, cy = py + 0.5, cz = pz + 0.5;
            var face = -1;
            double t;

            // tried in ALLFACES order so that a ray leaving exactly through an edge picks the
            // same face vanilla would have picked
            if (north)
            {
                t = (pz + cNorth) / dz;
                if (Hits(t, ox, oy, oz, dx, dy, dz, cx, cy, pz)) face = 0;
            }
            if (face < 0 && east)
            {
                t = (px + cEast) / dx;
                if (Hits(t, ox, oy, oz, dx, dy, dz, px + 1.0, cy, cz)) face = 1;
            }
            if (face < 0 && south)
            {
                t = (pz + cSouth) / dz;
                if (Hits(t, ox, oy, oz, dx, dy, dz, cx, cy, pz + 1.0)) face = 2;
            }
            if (face < 0 && west)
            {
                t = (px + cWest) / dx;
                if (Hits(t, ox, oy, oz, dx, dy, dz, px, cy, cz)) face = 3;
            }
            if (face < 0 && up)
            {
                t = (py + cUp) / dy;
                if (Hits(t, ox, oy, oz, dx, dy, dz, cx, py + 1.0, cz)) face = 4;
            }
            if (face < 0 && down)
            {
                t = (py + cDown) / dy;
                if (Hits(t, ox, oy, oz, dx, dy, dz, cx, py, cz)) face = 5;
            }
            if (face < 0) break;

            if (!sink.Visit(px, py, pz, fromFace, face, dist > 1)) break;

            px += StepX[face];
            py += StepY[face];
            pz += StepZ[face];
            fromFace = Opposite[face];

            if (!sink.IsValidChunkPos(px, py, pz) && (!aboveHeightLimit || py <= 0)) break;
        }
    }
}
