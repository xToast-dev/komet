using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Vintagestory.API.Client;

namespace Komet.Runtime;

/// <summary>
/// One mesh that takes part in a far-LOD build: a chunk part's LOD 1 mesh (tier 1), or a
/// tier 1 output (tier 2). <see cref="Output"/> is what the build made of it - the faces this
/// mesh owns in the coarse picture - or null when it owns none.
/// </summary>
public sealed class FarLodSource
{
    public MeshData Mesh;
    public bool TopSoil;
    /// <summary>Set by the build: the coarse mesh this source owns, or null.</summary>
    public MeshData Output;
    /// <summary>Set by the build: quads seen, or 0 when the mesh was refused (not the tesselator's quad layout).</summary>
    public int Quads;
    public bool Refused;
}

/// <summary>
/// Builds the coarse picture of a chunk: the block world downsampled to cells of two units
/// on a side, for the distance at which a block is a pixel or two and the camera pass is
/// paying the GPU front end for every one of its faces.
///
/// Why cells and not merged rectangles: the first far mesh merged coplanar unit faces, and
/// measured 1,3 faces per rectangle on natural terrain - a heightmap with block roughness
/// has no large coplanar regions, so merging cannot win there. Downsampling does not need
/// coplanarity: eight blocks become one whatever their heights, and a 2x2 run of grass tops
/// at different heights is one face where there were four, plus the sides that the height
/// steps between them no longer need.
///
/// The input is what the engine tesselated, not the block data: every appearance decision
/// (tile, colour map, light, flags, the grass overlay's second uv) has already been made
/// there, so the coarse faces copy it from a representative source face and nothing here
/// depends on block shapes or textures. The geometry is reconstructed from the faces:
///
/// <list type="bullet">
/// <item>A <b>unit face</b> is an axis-aligned quad spanning exactly one unit on both in-plane
/// axes, at integer unit positions, whose packed vertex normal points along the constant
/// axis. The block behind it is <b>solid</b>, the block in front of it is <b>air</b>.</item>
/// <item>Everything else - a plant cross, a rotated leaves cube, a stair, a fence, a slab, a
/// chiselled block - is a <b>rest face</b>, belonging to the block its centroid lies in.</item>
/// <item>Blocks that no face touches are unknown. Air is flood-filled from the known air
/// through the unknown; what the flood does not reach is buried and counts as solid. A
/// chunk tesselated edge-only leaves its centre blocks out of the flood: that region
/// belongs to parts this build does not see.</item>
/// <item>A <b>cell</b> is solid if any of its blocks is solid, else air if any of its
/// blocks is air, else buried. The picture is therefore never thinner than the world,
/// only up to one unit fatter - which is what keeps neighbouring chunks at different
/// tiers free of gaps.</item>
/// <item>A solid cell emits a face towards every air neighbour. The face copies the
/// outermost source face of that direction in the cell - its uv, its four vertex lights,
/// flags, colour map data, grass uv, index pattern and corner order, so winding, the SSBO
/// face packing and the shader's view of it are the source face's, only two units wide.
/// The texture is stretched over the cell, which at the distance the cell is drawn is the
/// mip chain's average of it anyway.</item>
/// <item>Each cell keeps at most <b>one rest block</b>: the one with the most faces, scaled
/// by two about the cell's corner in x and z and about the cell's floor in y, so a plant
/// on the upper half of a cell stands on the fattened cell top instead of inside it. One
/// plant of double size where there were four is the same green at four pixels.</item>
/// </list>
///
/// Scale-free: with <c>unit = 2</c> the same build takes tier 1's output - cell faces at
/// even positions, rest faces scaled by two - and produces cells of four blocks.
///
/// Pure and allocation-free but for the outputs: every working array is thread-static and
/// grows to the largest chunk seen. Runs on the tesselation thread after the engine has
/// assembled a chunk's parts, before the main thread adds them to the pools.
/// </summary>
public static class FarLod
{
    /// <summary>Blocks along a chunk's edge.</summary>
    public const int Chunk = 32;
    /// <summary>Units of padding on every side: a face can sit on the chunk's boundary, its
    /// front block outside; the cell beyond it must be a whole cell.</summary>
    private const int Pad = 2;
    private const float Eps = 1e-4f;
    private const int Lod0Bit = 1 << 12;
    private const int SmallMesh = 128;
    /// <summary>The chunk's centre region, the blocks an edge-only tesselation does not touch.</summary>
    private const int CenterFrom = 2, CenterTo = 30;

    private const byte Solid = 1, SeedAir = 2, FloodAir = 4, Blocked = 8, PadTop = 16;
    private const byte AnyAir = SeedAir | FloodAir;
    private const byte CellSolid = 1, CellAir = 2;
    private const byte Rest = 255;

    // ---- statistics: build thread writes, report reads ----
    public static long StatBuilds;            // Build calls that produced anything
    public static long StatSources;           // meshes offered
    public static long StatRefused;           // meshes not in the quad layout
    public static long StatQuadsIn;           // faces seen
    public static long StatUnitQuads;         // of which unit faces
    public static long StatCellFaces;         // cell faces emitted
    public static long StatRestBlocksIn;      // blocks with rest faces
    public static long StatRestBlocksOut;     // of which kept as a cell's representative
    public static long StatRestFaces;         // rest faces emitted
    public static long StatNoSource;          // cell faces wanted where no source face of that direction existed
    public static long StatTicks;
    /// <summary>
    /// The output meshes' custom arrays, held for reuse. The basic arrays (xyz, uv, rgba,
    /// flags, indices) come from the engine's own recycler, but <c>MeshData.Dispose</c> nulls
    /// CustomInts and CustomShorts before handing a mesh back, so a recycled mesh always
    /// arrives without them - and a fresh int[] per output was this mod's largest remaining
    /// allocation source once the far LOD was on (31 MB/s of Int32[] on the tesselation
    /// thread in the alloc sample). Same size-class pool the tight clone uses for the
    /// engine's extras, one instance each so the budgets do not fight.
    /// </summary>
    internal static readonly ArrayPoolByClass<int> Ints = new(sizeof(int)) { BudgetMb = 48 };
    internal static readonly ArrayPoolByClass<short> Shorts = new(sizeof(short)) { BudgetMb = 16 };

    /// <summary>Whether the two pools above are used; follows the same config knob and toggle
    /// as the tight clone's extras pool.</summary>
    public static bool PoolArrays = true;

    public static long PooledBytes => Ints.HeldBytes + Shorts.HeldBytes;
    public static long StatPoolHits => Ints.StatHits + Shorts.StatHits;
    public static long StatPoolMisses => Ints.StatMisses + Shorts.StatMisses;

    /// <summary>Frees every held array - world leave, or the toggle going off.</summary>
    public static void ClearPools()
    {
        Ints.Clear();
        Shorts.Clear();
    }

    /// <summary>Where the build's time goes: classify, flood, cells, sort, choose, allocate, emit.</summary>
    public static readonly long[] PhaseTicks = new long[7];

    public static void ResetStats()
    {
        StatBuilds = StatSources = StatRefused = StatQuadsIn = StatUnitQuads = StatCellFaces = 0;
        StatRestBlocksIn = StatRestBlocksOut = StatRestFaces = StatNoSource = StatTicks = 0;
        Ints.ResetStats();
        Shorts.ResetStats();
        Array.Clear(PhaseTicks, 0, PhaseTicks.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Phase(int i, ref long t)
    {
        var now = Stopwatch.GetTimestamp();
        Interlocked.Add(ref PhaseTicks[i], now - t);
        t = now;
    }

    /// <summary>Per-thread working set; grows to the largest chunk seen and is never freed.</summary>
    private sealed class Scratch
    {
        // per quad, over all sources of one build
        public int[] QSrc = Array.Empty<int>();
        public int[] QIdx = Array.Empty<int>();
        public byte[] QDir = Array.Empty<byte>();     // axis * 2 + (positive ? 1 : 0), or Rest
        public int[] QBlock = Array.Empty<int>();     // grid index of the owner (unit) or the centroid's block (rest)
        public int[] QPlane = Array.Empty<int>();     // unit faces: the plane in units
        public byte[] QCorner = Array.Empty<byte>();  // unit faces: 2 bits per vertex, db | dc << 1
        public int[] QRel = Array.Empty<int>();       // 6 x 2 bits: the face's index pattern
        // unit quads by cell (counting sort); rest quads sorted by block, each block's run stamped
        public int[] CellStart = Array.Empty<int>();
        public int[] CellQuad = Array.Empty<int>();
        public int[] RestQuad = Array.Empty<int>();
        public int[] BlockGen = Array.Empty<int>();
        public int[] BlockFirst = Array.Empty<int>();
        public int[] BlockCount = Array.Empty<int>();
        public int Gen;
        // the grids; Template[unit-1][edgeOnly] is the block grid's starting state
        public byte[] Block = Array.Empty<byte>();
        public byte[] Cell = Array.Empty<byte>();
        public int[] Queue = Array.Empty<int>();
        public readonly byte[][][] Template = { new byte[2][], new byte[2][] };
        public int[] Touched = Array.Empty<int>();
        // the choices per cell: a quad per direction (or -1), a rest block (or -1)
        public int[] CellRep = Array.Empty<int>();
        public int[] CellRest = Array.Empty<int>();
        // faces each source will own
        public int[] OutQuads = Array.Empty<int>();

        public void EnsureQuads(int n)
        {
            if (QSrc.Length >= n) return;
            var cap = Math.Max(1024, n + (n >> 1));
            QSrc = new int[cap];
            QIdx = new int[cap];
            QDir = new byte[cap];
            QBlock = new int[cap];
            QPlane = new int[cap];
            QCorner = new byte[cap];
            QRel = new int[cap];
            CellQuad = new int[cap];
            RestQuad = new int[cap];
        }

        public void EnsureGrid(int blocks, int cells)
        {
            if (Block.Length < blocks)
            {
                Block = new byte[blocks];
                Queue = new int[blocks];
                BlockGen = new int[blocks];
                BlockFirst = new int[blocks];
                BlockCount = new int[blocks];
                Touched = new int[blocks];
                Gen = 0;
            }
            if (Cell.Length < cells)
            {
                Cell = new byte[cells];
                CellStart = new int[cells + 1];
                CellRep = new int[cells * 6];
                CellRest = new int[cells];
            }
        }

        public void EnsureSources(int n)
        {
            if (OutQuads.Length < n) OutQuads = new int[Math.Max(16, n)];
        }
    }

    [ThreadStatic] private static Scratch tls;

    /// <summary>
    /// Builds the coarse picture of one chunk from <paramref name="count"/> sources at the
    /// given unit (1 for a chunk's own parts, 2 for tier 1's outputs). Fills each source's
    /// <see cref="FarLodSource.Output"/>. Returns false when no source could be used.
    /// </summary>
    public static bool Build(FarLodSource[] sources, int count, int unit, bool edgeOnly)
    {
        if (sources == null || count <= 0 || (unit != 1 && unit != 2)) return false;
        var t0 = Stopwatch.GetTimestamp();
        var tp = t0;
        var s = tls ??= new Scratch();

        // the grid, in units
        var n = Chunk / unit;                 // units along the chunk
        var g = n + 2 * Pad;                  // grid edge, in units
        var c = g / 2;                        // grid edge, in cells (g is even)
        var blocks = g * g * g;
        var cells = c * c * c;
        s.EnsureGrid(blocks, cells);
        s.EnsureSources(count);
        var block = s.Block;
        var cell = s.Cell;
        Array.Copy(Template(s, unit, edgeOnly, n, g), block, blocks);
        Array.Clear(cell, 0, cells);

        // ---- pass 1: classify every quad of every source ----
        var total = 0;
        for (var i = 0; i < count; i++)
        {
            var src = sources[i];
            src.Output = null;
            src.Quads = 0;
            src.Refused = false;
            Interlocked.Increment(ref StatSources);
            var m = src.Mesh;
            if (!QuadLayout(m, src.TopSoil))
            {
                src.Refused = true;
                Interlocked.Increment(ref StatRefused);
                continue;
            }
            src.Quads = m.VerticesCount / 4;
            total += src.Quads;
        }
        if (total == 0)
        {
            Interlocked.Add(ref StatTicks, Stopwatch.GetTimestamp() - t0);
            return false;
        }
        s.EnsureQuads(total);

        var q = 0;
        var unitQuads = 0;
        var restQuads = 0;
        var invUnit = 1f / unit;
        for (var i = 0; i < count; i++)
        {
            var src = sources[i];
            if (src.Quads == 0) continue;
            var m = src.Mesh;
            for (var k = 0; k < src.Quads; k++, q++)
            {
                s.QSrc[q] = i;
                s.QIdx[q] = k;
                if (Classify(m, k, unit, invUnit, n, g, s, q, out var owner, out var front))
                {
                    unitQuads++;
                    block[owner] |= Solid;
                    block[front] |= SeedAir;
                }
                else
                {
                    restQuads++;
                    s.QDir[q] = Rest;
                    s.QBlock[q] = RestBlock(m, k, invUnit, n, g);
                }
            }
        }

        Phase(0, ref tp);

        // ---- pass 2: air floods from the seeds through the unknown ----
        // Within the chunk only. The padding stands for the neighbouring chunks, about which
        // the faces say exactly one thing: the block in front of a face is air. Letting the
        // flood run through the padding would call the neighbour's terrain air wherever the
        // sky touches the chunk's boundary, and every boundary cell would grow a side face
        // into the neighbour's solid - hidden, and a third more triangles. The one direction
        // the flood does take is up, one block into the padding above the chunk: a cell at
        // the chunk's top is up to a block fatter than its blocks, and its top face needs the
        // cell above to be air - which it is, the block above the surface being air.
        // The template has every padding block Blocked but the row above the chunk, which
        // is PadTop: a flooded block's upward neighbour there turns to air without being
        // queued, so the flood ends on that row. Nothing else in the padding is ever
        // entered, which is what makes the six neighbour tests below need no bounds.
        var queue = s.Queue;
        var head = 0;
        var tail = 0;
        var gg = g * g;
        for (var y = 0; y < n; y++)
            for (var z = 0; z < n; z++)
            {
                var row = ((y + Pad) * g + (z + Pad)) * g + Pad;
                for (var x = 0; x < n; x++)
                {
                    var b = row + x;
                    if ((block[b] & (SeedAir | Solid)) == SeedAir) queue[tail++] = b;
                }
            }
        while (head < tail)
        {
            var b = queue[head++];
            if (block[b - 1] == 0) { block[b - 1] = FloodAir; queue[tail++] = b - 1; }
            if (block[b + 1] == 0) { block[b + 1] = FloodAir; queue[tail++] = b + 1; }
            if (block[b - g] == 0) { block[b - g] = FloodAir; queue[tail++] = b - g; }
            if (block[b + g] == 0) { block[b + g] = FloodAir; queue[tail++] = b + g; }
            if (block[b - gg] == 0) { block[b - gg] = FloodAir; queue[tail++] = b - gg; }
            var up = block[b + gg];
            if (up == 0) { block[b + gg] = FloodAir; queue[tail++] = b + gg; }
            else if (up == PadTop) block[b + gg] = PadTop | FloodAir;
        }

        Phase(1, ref tp);

        // ---- pass 3: cells ----
        for (var cy = 0; cy < c; cy++)
            for (var cz = 0; cz < c; cz++)
                for (var cx = 0; cx < c; cx++)
                {
                    var st = 0;
                    for (var dy = 0; dy < 2; dy++)
                        for (var dz = 0; dz < 2; dz++)
                        {
                            var row = ((cy * 2 + dy) * g + cz * 2 + dz) * g + cx * 2;
                            st |= block[row] | block[row + 1];
                        }
                    cell[(cy * c + cz) * c + cx] = (st & Solid) != 0 ? CellSolid
                        : (st & AnyAir) != 0 ? CellAir : (byte)0;
                }

        Phase(2, ref tp);

        // ---- pass 4: unit quads by cell (counting sort), rest quads by block (sort + stamps) ----
        var cellStart = s.CellStart;
        Array.Clear(cellStart, 0, cells + 1);
        // The rest quads are few and scattered over a large grid: a counting sort over the
        // blocks that actually have one - stamped with this build's generation and listed
        // as they are met - instead of a table the size of the grid cleared per build.
        var gen = ++s.Gen;
        var blockGen = s.BlockGen;
        var blockFirst = s.BlockFirst;
        var blockCount = s.BlockCount;
        var touched = s.Touched;
        var touchedCount = 0;
        for (var k = 0; k < total; k++)
        {
            var b = s.QBlock[k];
            if (s.QDir[k] == Rest)
            {
                if (blockGen[b] != gen)
                {
                    blockGen[b] = gen;
                    blockCount[b] = 0;
                    touched[touchedCount++] = b;
                }
                blockCount[b]++;
            }
            else cellStart[CellOfBlock(b, g, c) + 1]++;
        }
        for (var k = 0; k < cells; k++) cellStart[k + 1] += cellStart[k];
        var restAt = 0;
        for (var k = 0; k < touchedCount; k++)
        {
            var b = touched[k];
            blockFirst[b] = restAt;
            restAt += blockCount[b];
        }
        for (var k = 0; k < total; k++)
        {
            var b = s.QBlock[k];
            if (s.QDir[k] == Rest) s.RestQuad[blockFirst[b]++] = k;
            else s.CellQuad[cellStart[CellOfBlock(b, g, c)]++] = k;
        }
        for (var k = cells; k > 0; k--) cellStart[k] = cellStart[k - 1];
        cellStart[0] = 0;
        for (var k = 0; k < touchedCount; k++) blockFirst[touched[k]] -= blockCount[touched[k]];

        Phase(3, ref tp);

        // ---- pass 5a: choose, per cell, the face of each direction and the rest block; count per source ----
        long cellFaces = 0, restFaces = 0, restBlocksIn = 0, restBlocksOut = 0, noSource = 0;
        var chunkCells = n / 2;
        var cellRep = s.CellRep;
        var cellRest = s.CellRest;
        var outQuads = s.OutQuads;
        Array.Clear(outQuads, 0, count);
        for (var cy = 0; cy < chunkCells; cy++)
            for (var cz = 0; cz < chunkCells; cz++)
                for (var cx = 0; cx < chunkCells; cx++)
                {
                    // cell coordinates in the grid (the pad cell is index 0)
                    int gx = cx + 1, gy = cy + 1, gz = cz + 1;
                    var ci = (gy * c + gz) * c + gx;
                    var reps = ci * 6;
                    for (var dir = 0; dir < 6; dir++) cellRep[reps + dir] = -1;
                    cellRest[ci] = -1;

                    if (cell[ci] == CellSolid)
                    {
                        var qs = cellStart[ci];
                        var qe = cellStart[ci + 1];
                        for (var dir = 0; dir < 6; dir++)
                        {
                            var axis = dir >> 1;
                            var positive = (dir & 1) != 0;
                            var ni = axis == 0 ? ci + (positive ? 1 : -1)
                                   : axis == 2 ? ci + (positive ? c : -c)
                                   : ci + (positive ? c * c : -c * c);
                            if (cell[ni] != CellAir) continue;

                            // the outermost source face of this direction in the cell
                            var best = -1;
                            var bestPlane = 0;
                            for (var k = qs; k < qe; k++)
                            {
                                var qq = s.CellQuad[k];
                                if (s.QDir[qq] != dir) continue;
                                var plane = s.QPlane[qq];
                                if (best < 0 || (positive ? plane > bestPlane : plane < bestPlane))
                                {
                                    best = qq;
                                    bestPlane = plane;
                                }
                            }
                            if (best < 0) { noSource++; continue; }
                            cellRep[reps + dir] = best;
                            outQuads[s.QSrc[best]]++;
                            cellFaces++;
                        }
                    }

                    // the cell's rest blocks: keep the one with the most faces
                    var bestBlock = -1;
                    var bestCount = 0;
                    for (var dy = 0; dy < 2; dy++)
                        for (var dz = 0; dz < 2; dz++)
                            for (var dx = 0; dx < 2; dx++)
                            {
                                var b = ((gy * 2 + dy) * g + gz * 2 + dz) * g + gx * 2 + dx;
                                if (blockGen[b] != gen) continue;
                                restBlocksIn++;
                                var cnt = blockCount[b];
                                if (cnt > bestCount) { bestCount = cnt; bestBlock = b; }
                            }
                    if (bestBlock < 0) continue;
                    restBlocksOut++;
                    cellRest[ci] = bestBlock;
                    for (int k = blockFirst[bestBlock], e = k + blockCount[bestBlock]; k < e; k++)
                    {
                        outQuads[s.QSrc[s.RestQuad[k]]]++;
                        restFaces++;
                    }
                }

        Phase(4, ref tp);

        // ---- pass 5b: the outputs, exactly sized, and the faces ----
        for (var i = 0; i < count; i++)
            if (outQuads[i] > 0) sources[i].Output = NewMesh(outQuads[i] * 4, sources[i].TopSoil);
        Phase(5, ref tp);
        for (var cy = 0; cy < chunkCells; cy++)
            for (var cz = 0; cz < chunkCells; cz++)
                for (var cx = 0; cx < chunkCells; cx++)
                {
                    var ci = ((cy + 1) * c + cz + 1) * c + cx + 1;
                    var reps = ci * 6;
                    for (var dir = 0; dir < 6; dir++)
                    {
                        var best = cellRep[reps + dir];
                        if (best < 0) continue;
                        var src = sources[s.QSrc[best]];
                        EmitCellFace(src.Output, src.Mesh, src.TopSoil, s, best, cx * 2, cy * 2, cz * 2, dir >> 1, (dir & 1) != 0, unit);
                    }
                    var bestBlock = cellRest[ci];
                    if (bestBlock < 0) continue;
                    // the block's origin in units, relative to the chunk
                    var bx = bestBlock % g - Pad;
                    var bz = (bestBlock / g) % g - Pad;
                    var by = bestBlock / gg - Pad;
                    for (int k = blockFirst[bestBlock], e = k + blockCount[bestBlock]; k < e; k++)
                    {
                        var qq = s.RestQuad[k];
                        var src = sources[s.QSrc[qq]];
                        EmitRestFace(src.Output, src.Mesh, src.TopSoil, s.QIdx[qq], s.QRel[qq], bx, by, bz, cx * 2, cy * 2, cz * 2, unit);
                    }
                }

        Phase(6, ref tp);
        Interlocked.Increment(ref StatBuilds);
        Interlocked.Add(ref StatQuadsIn, total);
        Interlocked.Add(ref StatUnitQuads, unitQuads);
        Interlocked.Add(ref StatCellFaces, cellFaces);
        Interlocked.Add(ref StatRestBlocksIn, restBlocksIn);
        Interlocked.Add(ref StatRestBlocksOut, restBlocksOut);
        Interlocked.Add(ref StatRestFaces, restFaces);
        Interlocked.Add(ref StatNoSource, noSource);
        Interlocked.Add(ref StatTicks, Stopwatch.GetTimestamp() - t0);
        return true;
    }

    /// <summary>
    /// The block grid's starting state for a unit and mode, built once per thread: padding
    /// Blocked, the row above the chunk PadTop, and in a shell-only build the centre Blocked
    /// too (its parts are not in the build, so nothing may be assumed about it).
    /// </summary>
    private static byte[] Template(Scratch s, int unit, bool edgeOnly, int n, int g)
    {
        ref var slot = ref s.Template[unit - 1][edgeOnly ? 1 : 0];
        if (slot != null) return slot;
        var t = new byte[g * g * g];
        var gg = g * g;
        for (var y = 0; y < g; y++)
            for (var z = 0; z < g; z++)
                for (var x = 0; x < g; x++)
                {
                    var inside = x >= Pad && x < Pad + n && z >= Pad && z < Pad + n && y >= Pad && y < Pad + n;
                    if (inside) continue;
                    var b = (y * g + z) * g + x;
                    t[b] = y == Pad + n && x >= Pad && x < Pad + n && z >= Pad && z < Pad + n ? PadTop : Blocked;
                }
        if (edgeOnly)
        {
            var lo = CenterFrom / unit;
            var hi = CenterTo / unit;
            for (var y = lo; y < hi; y++)
                for (var z = lo; z < hi; z++)
                {
                    var row = ((y + Pad) * g + (z + Pad)) * g + Pad;
                    for (var x = lo; x < hi; x++) t[row + x] = Blocked;
                }
        }
        _ = gg;
        slot = t;
        return t;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CellOfBlock(int b, int g, int c)
    {
        var x = b % g;
        var z = (b / g) % g;
        var y = b / (g * g);
        return ((y >> 1) * c + (z >> 1)) * c + (x >> 1);
    }

    /// <summary>The tesselator's quad layout: four vertices and six indices per face, one colour map int per vertex.</summary>
    private static bool QuadLayout(MeshData m, bool topsoil)
    {
        if (m == null || m.xyz == null || m.Uv == null || m.Rgba == null || m.Flags == null || m.Indices == null
            || m.CustomInts == null || m.CustomInts.Values == null
            || (topsoil && (m.CustomShorts == null || m.CustomShorts.Values == null)))
            return false;
        var quads = m.VerticesCount / 4;
        if (quads < 1 || m.VerticesCount != quads * 4 || m.IndicesCount != quads * 6
            || m.VerticesPerFace != 4 || m.IndicesPerFace != 6)
            return false;
        if (m.CustomInts.Count < m.VerticesCount) return false;
        if (topsoil && m.CustomShorts.Count < m.VerticesCount * 2) return false;
        return true;
    }

    /// <summary>The grid block a rest quad belongs to: the one its centroid lies in, clamped into the chunk.</summary>
    private static int RestBlock(MeshData m, int k, float invUnit, int n, int g)
    {
        var xyz = m.xyz;
        var v = k * 12;
        var cx = (xyz[v] + xyz[v + 3] + xyz[v + 6] + xyz[v + 9]) * 0.25f * invUnit;
        var cy = (xyz[v + 1] + xyz[v + 4] + xyz[v + 7] + xyz[v + 10]) * 0.25f * invUnit;
        var cz = (xyz[v + 2] + xyz[v + 5] + xyz[v + 8] + xyz[v + 11]) * 0.25f * invUnit;
        var bx = Clamp((int)MathF.Floor(cx), 0, n - 1);
        var by = Clamp((int)MathF.Floor(cy), 0, n - 1);
        var bz = Clamp((int)MathF.Floor(cz), 0, n - 1);
        return ((by + Pad) * g + (bz + Pad)) * g + (bx + Pad);
    }

    /// <summary>
    /// Decides whether quad <paramref name="k"/> of <paramref name="m"/> is a unit face at
    /// this scale and records what the emitter needs. The index pattern is recorded for both
    /// kinds; a quad whose indices leave its own four vertices is treated as rest.
    /// </summary>
    private static bool Classify(MeshData m, int k, int unit, float invUnit, int n, int g, Scratch s, int q, out int owner, out int front)
    {
        owner = front = 0;
        var xyz = m.xyz;
        var idx = m.Indices;
        var flags = m.Flags;
        var v = k * 12;
        var fi = k * 4;
        var ii = k * 6;

        // the index pattern, relative to the face's first vertex
        var pattern = 0;
        for (var j = 0; j < 6; j++)
        {
            var r = idx[ii + j] - fi;
            if ((uint)r > 3u) { s.QRel[q] = 0b100100_000000; return false; }   // 0,1,2,0,2,3 when unusable
            pattern |= r << (2 * j);
        }
        s.QRel[q] = pattern;

        // flags: all four equal, no LOD 0 fade
        var f = flags[fi];
        if (flags[fi + 1] != f || flags[fi + 2] != f || flags[fi + 3] != f || (f & Lod0Bit) != 0) return false;

        // the constant axis
        float x0 = xyz[v], y0 = xyz[v + 1], z0 = xyz[v + 2];
        var cx = MathF.Abs(xyz[v + 3] - x0) <= Eps && MathF.Abs(xyz[v + 6] - x0) <= Eps && MathF.Abs(xyz[v + 9] - x0) <= Eps;
        var cy = MathF.Abs(xyz[v + 4] - y0) <= Eps && MathF.Abs(xyz[v + 7] - y0) <= Eps && MathF.Abs(xyz[v + 10] - y0) <= Eps;
        var cz = MathF.Abs(xyz[v + 5] - z0) <= Eps && MathF.Abs(xyz[v + 8] - z0) <= Eps && MathF.Abs(xyz[v + 11] - z0) <= Eps;
        int axis;
        if (cx) { if (cy || cz) return false; axis = 0; }
        else if (cy) { if (cz) return false; axis = 1; }
        else if (cz) axis = 2;
        else return false;
        var ab = axis == 2 ? 0 : axis + 1;
        var ac = axis == 0 ? 2 : axis - 1;

        // the packed normal must point along that axis
        var nx = ((f >> 14) & 7) * (((f >> 12) & 2) != 0 ? -1 : 1);
        var ny = ((f >> 18) & 7) * (((f >> 16) & 2) != 0 ? -1 : 1);
        var nz = ((f >> 22) & 7) * (((f >> 20) & 2) != 0 ? -1 : 1);
        int na, nb, nc;
        if (axis == 0) { na = nx; nb = ny; nc = nz; }
        else if (axis == 1) { na = ny; nb = nz; nc = nx; }
        else { na = nz; nb = nx; nc = ny; }
        if (na == 0 || nb != 0 || nc != 0) return false;
        var positive = na > 0;

        // the plane and the in-plane extent: whole units at unit positions
        var plane = xyz[v + axis] * invUnit;
        var planeR = MathF.Round(plane);
        if (MathF.Abs(plane - planeR) > Eps) return false;
        var b0 = xyz[v + ab]; var b1 = xyz[v + 3 + ab]; var b2 = xyz[v + 6 + ab]; var b3 = xyz[v + 9 + ab];
        var c0 = xyz[v + ac]; var c1 = xyz[v + 3 + ac]; var c2 = xyz[v + 6 + ac]; var c3 = xyz[v + 9 + ac];
        var minB = MathF.Min(MathF.Min(b0, b1), MathF.Min(b2, b3)) * invUnit;
        var minC = MathF.Min(MathF.Min(c0, c1), MathF.Min(c2, c3)) * invUnit;
        var rb = MathF.Round(minB);
        var rc = MathF.Round(minC);
        if (MathF.Abs(minB - rb) > Eps || MathF.Abs(minC - rc) > Eps) return false;

        var used = 0;
        var code = 0;
        for (var j = 0; j < 4; j++)
        {
            var b = xyz[v + 3 * j + ab] * invUnit - rb;
            var cc = xyz[v + 3 * j + ac] * invUnit - rc;
            int db, dc;
            if (b <= Eps && b >= -Eps) db = 0; else if (MathF.Abs(b - 1f) <= Eps) db = 1; else return false;
            if (cc <= Eps && cc >= -Eps) dc = 0; else if (MathF.Abs(cc - 1f) <= Eps) dc = 1; else return false;
            var bit = 1 << (db + 2 * dc);
            if ((used & bit) != 0) return false;
            used |= bit;
            code |= (db | (dc << 1)) << (2 * j);
        }

        // owner and front, in grid units: the block behind the face and the one before it
        var pi = (int)planeR;
        var bi = (int)rb;
        var ci = (int)rc;
        var ownerA = positive ? pi - 1 : pi;
        var frontA = positive ? pi : pi - 1;
        if (bi < -Pad || bi >= n + Pad || ci < -Pad || ci >= n + Pad) return false;
        if (ownerA < -Pad || ownerA >= n + Pad || frontA < -Pad || frontA >= n + Pad) return false;
        // grid index = ((y + Pad) * g + (z + Pad)) * g + (x + Pad); the axis picks the stride
        var strideA = axis == 0 ? 1 : axis == 1 ? g * g : g;
        var strideB = ab == 0 ? 1 : ab == 1 ? g * g : g;
        var strideC = ac == 0 ? 1 : ac == 1 ? g * g : g;
        var basePad = Pad * (1 + g + g * g);
        var inPlane = basePad + bi * strideB + ci * strideC;
        owner = inPlane + ownerA * strideA;
        front = inPlane + frontA * strideA;
        s.QBlock[q] = owner;
        s.QDir[q] = (byte)(axis * 2 + (positive ? 1 : 0));
        s.QPlane[q] = pi;
        s.QCorner[q] = (byte)code;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    /// <summary>
    /// An empty output mesh of the given capacity. The basic arrays come from the engine's
    /// recycler; the custom parts cannot - <c>MeshData.Dispose</c> nulls CustomInts and
    /// CustomShorts before handing a mesh back, so every recycled mesh arrives without them
    /// and a fresh int[] per output was the mod's largest remaining allocation source (the
    /// report's alloc sample put 31 MB/s of Int32[] on the tesselation thread). They come
    /// from the same size-class pool the tight clone already uses for the engine's own
    /// extras, and <see cref="Release"/> gives them back.
    /// </summary>
    private static MeshData NewMesh(int vertices, bool topsoil)
    {
        MeshData m;
        var recycler = MeshData.Recycler;
        if (recycler != null && vertices >= SmallMesh)
        {
            m = recycler.GetOrCreateMesh(vertices);
            if (m.Indices == null || m.Indices.Length < m.VerticesMax * 6 / 4)
            {
                m.Indices = new int[m.VerticesMax * 6 / 4];
                m.IndicesMax = m.Indices.Length;
            }
        }
        else
        {
            m = new MeshData(Math.Max(4, vertices));
        }
        m.VerticesCount = 0;
        m.IndicesCount = 0;
        m.VerticesPerFace = 4;
        m.IndicesPerFace = 6;
        var pooled = PoolArrays;
        m.CustomInts = new CustomMeshDataPartInt { InterleaveStride = 4, Count = 0 };
        m.CustomInts.Values = pooled
            ? Ints.RentBlank(m.VerticesMax)
            : new int[m.VerticesMax];
        m.CustomShorts = null;
        if (topsoil)
        {
            m.CustomShorts = new CustomMeshDataPartShort { InterleaveStride = 4, Count = 0 };
            m.CustomShorts.Values = pooled
                ? Shorts.RentBlank(m.VerticesMax * 2)
                : new short[m.VerticesMax * 2];
        }
        m.CustomFloats = null;
        m.CustomBytes = null;
        return m;
    }

    /// <summary>
    /// Gives an output mesh back: the custom parts' arrays to the size-class pool, the mesh
    /// itself to the engine's recycler. Must be called instead of <c>Dispose</c>, and only
    /// once the mesh has been uploaded - the arrays are handed to the next renter at once.
    /// </summary>
    public static void Release(MeshData m)
    {
        if (m == null) return;
        if (PoolArrays)
        {
            Ints.Return(m.CustomInts?.Values);
            Shorts.Return(m.CustomShorts?.Values);
        }
        // Nulled before Dispose whatever happened: Dispose nulls them anyway, and a second
        // Release must not hand the same arrays to a second renter.
        m.CustomInts = null;
        m.CustomShorts = null;
        m.Dispose();
    }

    /// <summary>
    /// Copies the four vertices' appearance (uv, light, flags, colour map, grass uv) of face
    /// k in src to slot d in dst, vertex for vertex. Plain element copies: at sixteen to
    /// thirty-two bytes per attribute a memmove call costs more than the bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyAppearance(MeshData dst, int d, MeshData src, int k, bool topsoil)
    {
        var sv = k * 4;
        var su = src.Uv; var du = dst.Uv;
        for (var j = 0; j < 8; j++) du[d * 2 + j] = su[sv * 2 + j];
        ref var sr = ref src.Rgba[sv * 4];
        ref var dr = ref dst.Rgba[d * 4];
        Unsafe.WriteUnaligned(ref dr, Unsafe.ReadUnaligned<ulong>(ref sr));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dr, 8), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref sr, 8)));
        var sf = src.Flags; var df = dst.Flags;
        var sc = src.CustomInts.Values; var dc = dst.CustomInts.Values;
        for (var j = 0; j < 4; j++)
        {
            df[d + j] = sf[sv + j];
            dc[d + j] = sc[sv + j];
        }
        if (topsoil)
        {
            ref var ss = ref src.CustomShorts.Values[sv * 2];
            ref var ds = ref dst.CustomShorts.Values[d * 2];
            Unsafe.WriteUnaligned(ref Unsafe.As<short, byte>(ref ds), Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<short, byte>(ref ss)));
            Unsafe.WriteUnaligned(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref ds, 4)), Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref ss, 4))));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CloseFace(MeshData dst, int d, int rel, bool topsoil)
    {
        dst.CustomInts.Count = d + 4;
        if (topsoil) dst.CustomShorts.Count = (d + 4) * 2;
        var di = dst.IndicesCount;
        for (var k = 0; k < 6; k++) dst.Indices[di + k] = d + ((rel >> (2 * k)) & 3);
        dst.VerticesCount = d + 4;
        dst.IndicesCount = di + 6;
    }

    /// <summary>
    /// One cell face: the cell's boundary square in direction (axis, positive), two units on
    /// a side, in the corner order and with the appearance of source face q.
    /// </summary>
    private static void EmitCellFace(MeshData dst, MeshData src, bool topsoil, Scratch s, int q,
                                     int cellX, int cellY, int cellZ, int axis, bool positive, int unit)
    {
        var k = s.QIdx[q];
        var code = s.QCorner[q];
        var ab = (axis + 1) % 3;
        var ac = (axis + 2) % 3;
        var cellMin = axis == 0 ? cellX : axis == 1 ? cellY : cellZ;
        var plane = (positive ? cellMin + 2 : cellMin) * unit;
        var b0 = (ab == 0 ? cellX : ab == 1 ? cellY : cellZ) * unit;
        var c0 = (ac == 0 ? cellX : ac == 1 ? cellY : cellZ) * unit;
        var side = 2 * unit;

        var d = dst.VerticesCount;
        for (var j = 0; j < 4; j++)
        {
            var db = (code >> (2 * j)) & 1;
            var dc = (code >> (2 * j + 1)) & 1;
            var o = (d + j) * 3;
            dst.xyz[o + axis] = plane;
            dst.xyz[o + ab] = b0 + db * side;
            dst.xyz[o + ac] = c0 + dc * side;
        }
        CopyAppearance(dst, d, src, k, topsoil);
        CloseFace(dst, d, s.QRel[q], topsoil);
    }

    /// <summary>
    /// One rest face of the cell's representative block, scaled by two: about the cell's
    /// corner in x and z (the block's own corner lands on the cell's), about the cell's floor
    /// in y (the block's height within the cell doubles, so the upper half stands on top).
    /// </summary>
    private static void EmitRestFace(MeshData dst, MeshData src, bool topsoil, int k, int rel,
                                     int bx, int by, int bz, int cellX, int cellY, int cellZ, int unit)
    {
        var d = dst.VerticesCount;
        float ox = bx * unit, oz = bz * unit;
        float cx = cellX * unit, cy = cellY * unit, cz = cellZ * unit;
        for (var j = 0; j < 4; j++)
        {
            var sv = (k * 4 + j) * 3;
            var o = (d + j) * 3;
            dst.xyz[o] = cx + 2f * (src.xyz[sv] - ox);
            dst.xyz[o + 1] = 2f * src.xyz[sv + 1] - cy;
            dst.xyz[o + 2] = cz + 2f * (src.xyz[sv + 2] - oz);
        }
        CopyAppearance(dst, d, src, k, topsoil);
        CloseFace(dst, d, rel, topsoil);
    }
}
