using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Client;

namespace Komet.Culling;

/// <summary>
/// Checks, on sampled sweeps, that <see cref="FastCuller"/> drew exactly what vanilla's
/// FrustumCull would have drawn.
///
/// The bug that earned this: the engine assigns CullVisible and LodLevel to a
/// ModelDataPoolLocation *after* MeshDataPool.TryAdd returns, so the TryAdd postfix reads a
/// LodLevel of 0 and a throwaway Bools. NoteInserted snapshotted both, and LodLevel 0 means
/// "invisible" in the camera pass - every part squeezed into a fragmented pool disappeared
/// until the next rebuild brought it back. It shipped, it flickered in the field, and no test
/// caught it, because every test built its locations fully populated. A rule this project has
/// now learned twice (the window prebuilder's staleness guard was the first): a cache whose
/// source can change behind it needs something that keeps asking, in the running game, whether
/// it still agrees.
///
/// The comparison allows merged ranges - glMultiDrawElements renders the concatenation of its
/// ranges in order, so back-to-back ranges may be fused - and, when gap merging is on, ranges
/// bridged across parts whose box the CURRENT culler proves fully outside the frustum (the GPU
/// clips those; identical pixels). Nothing else: a range crossing a part that is merely
/// LOD-rejected, hidden, occluded or free space is still a hard mismatch.
/// </summary>
public static class CullVerifier
{
    /// <summary>Check one sweep in every N. 0 = off.</summary>
    public static int SampleEvery;

    public static long StatChecked;
    public static long StatMismatches;

    /// <summary>Set by the mod system; called at most <see cref="MaxReports"/> times.</summary>
    public static Action<string> Log;

    private const int MaxReports = 5;
    private static int reports;
    private static int countdown;

    private static readonly AccessTools.FieldRef<MeshDataPool, List<ModelDataPoolLocation>> LocationsRef =
        AccessTools.FieldRefAccess<MeshDataPool, List<ModelDataPoolLocation>>("poolLocations");

    /// <summary>Reused so a check that runs every few frames allocates nothing.</summary>
    [ThreadStatic] private static List<int> expected;

    /// <summary>Parts a bridge may legally cross - invisible AND provably outside the frustum.</summary>
    [ThreadStatic] private static List<int> allowedTls;

    public static void Reset()
    {
        StatChecked = 0;
        StatMismatches = 0;
        reports = 0;
        countdown = 0;
    }

    /// <summary>
    /// Vanilla's ModelDataPoolLocation.IsVisible, minus the one side effect it has
    /// (UpdateVisibleFlag writing FrustumVisible). A verifier that changes the state it is
    /// verifying is worse than none.
    /// </summary>
    internal static bool VanillaVisible(ModelDataPoolLocation loc, EnumFrustumCullMode mode, FrustumCulling culler)
    {
        var buf = ModelDataPoolLocation.VisibleBufIndex;
        switch (mode)
        {
            case EnumFrustumCullMode.CullInstant:
                return !loc.Hide && loc.CullVisible[buf] && culler.InFrustum(loc.FrustumCullSphere);
            case EnumFrustumCullMode.CullInstantShadowPassNear:
                // the far pictures never cast (see FastCuller); the engine's mesh of a part
                // that has one (level 5) casts like level 1; not drawn, the pictures are
                // hidden and the engine's answer stands for every level
                if (Runtime.FarMesh.Active && Runtime.FarMesh.IsPicture(loc.LodLevel)) return false;
                return !loc.Hide && loc.CullVisible[buf] && culler.InFrustumShadowPass(loc.FrustumCullSphere);
            case EnumFrustumCullMode.CullInstantShadowPassFar:
                if (Runtime.FarMesh.Active && Runtime.FarMesh.IsPicture(loc.LodLevel)) return false;
                return !loc.Hide && loc.CullVisible[buf]
                       && culler.InFrustumShadowPass(loc.FrustumCullSphere) && loc.LodLevel >= 1;
            case EnumFrustumCullMode.CullNormal:
                if (loc.Hide || !loc.CullVisible[buf]) return false;
                if (loc.LodLevel == Runtime.FarMesh.LodNear || Runtime.FarMesh.IsPicture(loc.LodLevel))
                {
                    // The far LOD's levels are not the engine's; this is the sweep's rule for
                    // them restated: level 5 is a LOD 1 part within the far distance
                    // (inclusive), 4 a picture from there to twice the distance (inclusive),
                    // 6 one beyond that, 7 one from the distance to the view distance - or,
                    // with the pictures not drawn, level 5 is plain LOD 1 and the rest never show.
                    if (!culler.InFrustumAndRange(loc.FrustumCullSphere, loc.FrustumVisible, 1)) return false;
                    if (!Runtime.FarMesh.Active) return loc.LodLevel == Runtime.FarMesh.LodNear;
                    var s = loc.FrustumCullSphere;
                    double d = FastCuller.PlayerPosOf(culler).HorDistanceSqTo(s.x, s.z);
                    var farSq = Runtime.FarMesh.EffectiveDistanceSq(culler);
                    var tier2 = Runtime.FarMesh.Tier2;
                    var far2Sq = tier2 ? Runtime.FarMesh.EffectiveDistance2Sq(culler) : double.PositiveInfinity;
                    switch (loc.LodLevel)
                    {
                        case Runtime.FarMesh.LodNear: return d <= farSq;
                        case Runtime.FarMesh.LodFar: return d > farSq && (!tier2 || d <= far2Sq);
                        case Runtime.FarMesh.LodFar2: return tier2 && d > far2Sq;
                        default: return d > farSq;
                    }
                }
                return culler.InFrustumAndRange(loc.FrustumCullSphere, loc.FrustumVisible, loc.LodLevel);
            default:
                return !loc.Hide;
        }
    }

    /// <summary>
    /// Compares what the pool ended up holding against the byte ranges vanilla would have
    /// emitted. Pure and free of engine types, so the merge rule can be checked directly.
    /// Returns null when they agree, otherwise a description of the first disagreement.
    /// </summary>
    internal static string Compare(int[] starts, int[] sizes, int groups, List<int> want,
                                   List<int> allowed = null)
    {
        // want and allowed are flat (startByte, lengthInIndices) lists; want is in emit order
        var wantCount = want.Count / 2;
        var w = 0;

        for (var g = 0; g < groups; g++)
        {
            var gStart = starts[g * 2];
            var gLen = sizes[g];

            if (w >= wantCount)
                return $"range {g} at byte {gStart} len {gLen} was not drawn by vanilla";
            if (want[w * 2] != gStart)
                return $"range {g} starts at byte {gStart}, vanilla starts at {want[w * 2]}";

            // one emitted range may cover several consecutive vanilla ranges - plus, between
            // them, parts a bridge may cross - but only if the bytes are genuinely contiguous
            var covered = 0;
            var cursorByte = gStart;
            while (covered < gLen)
            {
                if (w < wantCount && want[w * 2] == cursorByte)
                {
                    covered += want[w * 2 + 1];
                    cursorByte += want[w * 2 + 1] * 4;
                    w++;
                    continue;
                }

                var bridged = AllowedLenAt(allowed, cursorByte);
                if (bridged > 0)
                {
                    covered += bridged;
                    cursorByte += bridged * 4;
                    continue;
                }

                if (w >= wantCount)
                    return $"range {g} is {gLen} indices long, vanilla only had {covered}";
                return $"range {g} runs over a gap: expected a part at byte {cursorByte}, "
                     + $"vanilla's next is at {want[w * 2]}";
            }
            if (covered != gLen)
                return $"range {g} covers {covered} indices, emitted {gLen}";

            // a range must end on a drawn part, never on bridge filler - a trailing bridge
            // would mean the sweep widened a range for no visible part at all
            if (want[(w - 1) * 2] + want[(w - 1) * 2 + 1] * 4 != cursorByte)
                return $"range {g} ends at byte {cursorByte} on bridged filler, "
                     + $"vanilla's last part ends at {want[(w - 1) * 2] + want[(w - 1) * 2 + 1] * 4}";
        }

        if (w != wantCount)
            return $"vanilla drew {wantCount} parts, only {w} of them were emitted "
                 + $"(missing from byte {want[w * 2]})";
        return null;
    }

    /// <summary>
    /// Length (in indices) of the bridgeable part starting at exactly this byte, 0 if none.
    /// Linear scan: the verifier is sampled and a pool holds a few hundred parts, so an index
    /// structure would cost more to build than it saves.
    /// </summary>
    private static int AllowedLenAt(List<int> allowed, int startByte)
    {
        if (allowed == null) return 0;
        for (var a = 0; a < allowed.Count; a += 2)
            if (allowed[a] == startByte) return allowed[a + 1];
        return 0;
    }

    /// <summary>
    /// Called after a sweep has written the pool. Cheap until the sample counter fires; the
    /// full check costs one vanilla sweep over that one pool.
    /// </summary>
    [ThreadStatic] private static int[] sortedStartsTls, sortedSizesTls, sortKeysTls;

    public static void Maybe(MeshDataPool pool, FrustumCulling culler, EnumFrustumCullMode mode)
    {
        var every = SampleEvery;
        if (every <= 0 || reports >= MaxReports) return;
        if (--countdown > 0) return;
        countdown = every;
        // a foliage pool under the foliage range legitimately differs from vanilla
        if (FastCuller.IsFoliageCapped(pool, mode)) return;

        try
        {
            var live = LocationsRef(pool);
            if (live == null) return;

            var want = expected ??= new List<int>(1024);
            want.Clear();

            // The safety criterion is computed with vanilla's own InFrustum, not with the
            // sweep's plane math: the sweep bridges on a 5- or 6-plane FastPlane test, and
            // failing any of those planes implies failing vanilla's full test on the same
            // equations - so everything legally bridged lands in this list, while a bridge
            // over an in-frustum part stays a reported mismatch.
            List<int> allowed = null;
            if (FastCuller.GapMergeDrawRanges && FastCuller.MergeDrawRanges
                && mode != EnumFrustumCullMode.NoCull)
            {
                allowed = allowedTls ??= new List<int>(1024);
                allowed.Clear();
            }

            for (var i = 0; i < live.Count; i++)
            {
                var loc = live[i];
                if (VanillaVisible(loc, mode, culler))
                {
                    want.Add(loc.IndicesStart * 4);
                    want.Add(loc.IndicesEnd - loc.IndicesStart);
                }
                else if (allowed != null && !culler.InFrustum(loc.FrustumCullSphere))
                {
                    allowed.Add(loc.IndicesStart * 4);
                    allowed.Add(loc.IndicesEnd - loc.IndicesStart);
                }
            }

            StatChecked++;
            // A sorted sweep emits the same ranges in another order. Compare walks vanilla's
            // list in emit order, so the emitted ranges are put back into byte order first -
            // what is drawn is the set, and the set is what the check is for.
            var starts = pool.indicesStartsByte;
            var sizes = pool.indicesSizes;
            var groups = pool.indicesGroupsCount;
            if (FastCuller.FrontToBack && mode == EnumFrustumCullMode.CullNormal && groups > 1)
            {
                var sortedStarts = sortedStartsTls ??= new int[groups * 2 + 64];
                var sortedSizes = sortedSizesTls ??= new int[groups + 64];
                if (sortedStarts.Length < groups * 2) sortedStartsTls = sortedStarts = new int[groups * 2 + 64];
                if (sortedSizes.Length < groups) sortedSizesTls = sortedSizes = new int[groups + 64];
                var keys = sortKeysTls ??= new int[groups + 64];
                if (keys.Length < groups) sortKeysTls = keys = new int[groups + 64];
                for (var g = 0; g < groups; g++) { keys[g] = starts[g * 2]; sortedSizes[g] = sizes[g]; }
                Array.Sort(keys, sortedSizes, 0, groups);
                for (var g = 0; g < groups; g++) sortedStarts[g * 2] = keys[g];
                starts = sortedStarts;
                sizes = sortedSizes;
            }
            var problem = Compare(starts, sizes, groups, want, allowed);
            if (problem == null) return;

            StatMismatches++;
            if (++reports > MaxReports) return;

            var sb = new StringBuilder(256);
            sb.Append("CULL MISMATCH (").Append(mode).Append("): ").Append(problem);
            sb.Append(" | pool holds ").Append(live.Count).Append(" parts, vanilla would draw ")
              .Append(want.Count / 2).Append(", emitted ").Append(pool.indicesGroupsCount).Append(" ranges");
            if (reports == MaxReports)
                sb.Append(" | further mismatches will not be reported");
            Log?.Invoke(sb.ToString());
        }
        catch (Exception)
        {
            // A checker must never be the reason a frame dies. Give up on it instead.
            SampleEvery = 0;
        }
    }
}
