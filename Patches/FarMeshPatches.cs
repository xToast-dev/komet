using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Culling;
using Komet.Runtime;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;


// Harmony binds patch parameters BY NAME (__instance, __result, __state, ___field, and the engine's
// own parameter spellings). A naming cleanup that renames them makes the patch throw at Patch()
// time and the feature silently run vanilla - so naming inspections are suppressed here.
// ReSharper disable InconsistentNaming
namespace Komet.Patches;

/// <summary>
/// Wires <see cref="FarLod"/> into the engine's chunk pipeline, at three points.
///
/// On the tesselation thread, after <c>ChunkTesselator.NowProcessChunk</c> has assembled a
/// chunk's parts: the LOD 1 meshes of its Opaque, OpaqueNoCull, BlendNoCull and TopSoil parts
/// go through one tier 1 build (cells of two blocks), and tier 1's outputs through a tier 2
/// build (cells of four). The pictures ride along with their parts in a weak table until the
/// main thread adds the parts to the pools. The engine's meshes are not touched.
///
/// On the main thread, around <c>TesselatedChunkPart.AddToPools</c>: the prefix takes the
/// part's pictures out of the table, the postfix puts them into the pool manager of their
/// pass with LOD levels the engine never assigns (4, 6, 7), re-levels the part's own LOD 1
/// location to 5 - so the sweep stops it at the far distance - and appends the pictures to
/// the chunk's own location list, so the engine removes them with the chunk as it removes
/// its own. The handover through the prefix is not decoration: <c>AddToPools</c> ends by
/// calling <c>Dispose</c> on the part, so a postfix on Dispose - which is how a part that
/// never reaches the pools is cleaned up - fires BEFORE the AddToPools postfix; nothing about
/// the design may depend on which of two patches Harmony runs first.
///
/// The tier 2 pictures of a chunk live in the location list of its first centre part: a
/// chunk's centre is only ever re-tesselated whole, its two-block shell alone whenever a
/// neighbour changes, and tier 2's cells of four straddle the two. Built from both, kept on
/// the centre, a shell-only re-tesselation leaves tier 2 as it was - at four times the far
/// distance a changed block in the shell is nothing anyone can see - and its new tier 1
/// pictures are levelled to stop where tier 2 begins. A chunk with no centre part keeps
/// tier 2 on its first shell part and rebuilds it with the shell.
///
/// Before every opaque pass the mode is brought in line with the switches: with the sweep not
/// ours or the feature off, the parts are put back at level 1 (drawn at every distance, as
/// before) and the pictures are hidden - the engine's own picture, with nothing re-tesselated.
/// </summary>
public static class FarMeshPatches
{
    public static bool Installed { get; private set; }
    public static ILogger Log;

    private const byte KindNear = 1, KindFar = 2;

    /// <summary>
    /// The pool lanes (see <see cref="SpatialPools.Lane"/>): the engine's own chunk meshes,
    /// the tier 1 pictures and the tier 2 pictures each get pools of their own, so a pool's
    /// parts are all visible in the same distance band. Mixed, every picture sat between two
    /// engine parts in the index buffer and split the runs the sweep merges - and every pool
    /// held something visible in every view, so every pool cost a draw call.
    /// </summary>
    private const int LaneTier1 = 1, LaneTier2 = 2, LaneEngine = 3;
    /// <summary>Parts handed to the pools before "and no picture was placed" counts as broken.</summary>
    private const int WatchdogParts = 24;

    /// <summary>One tier 2 picture: the pool manager it belongs to and the mesh.</summary>
    private sealed class Picture
    {
        public MeshData Mesh;
        public EnumChunkRenderPass Pass;
        public int Atlas;
    }

    /// <summary>What one part carries to the pools - read on the tesselation thread, because
    /// the part is disposed before the postfix sees it.</summary>
    private sealed class Extra
    {
        /// <summary>Tier 1, or null when the part owns nothing beyond the distance (it is still re-levelled).</summary>
        public MeshData Tier1;
        public int Tier1Level;
        public EnumChunkRenderPass Pass;
        public int Atlas;
        /// <summary>Tier 2 pictures of the whole chunk, on the host part only.</summary>
        public List<Picture> Tier2;
    }
    private sealed class Mark { public byte Kind; }

    private static ConditionalWeakTable<TesselatedChunkPart, Extra> extras = new();
    private static ConditionalWeakTable<ModelDataPoolLocation, Mark> tracked = new();

    /// <summary>Chunks whose tier 2 lives on a centre part, by chunk position: a shell-only
    /// re-tesselation must not rebuild it. Tesselation thread, under its own lock because
    /// Reset clears it from the main thread.</summary>
    private static readonly HashSet<long> tier2OnCenter = new();
    private static readonly object registryLock = new();

    /// <summary>Locations of each kind currently in the pools (appended minus removed).</summary>
    public static int TrackedNear, TrackedFar;
    /// <summary>Chunks that went through a build (tesselation thread).</summary>
    public static long StatChunks;
    /// <summary>Parts that reached AddToPools carrying a picture or a re-levelling (main thread) ...</summary>
    public static long StatAttempts;
    /// <summary>... and of those, the ones whose pictures were placed - the pair the watchdog reads.</summary>
    public static long StatPlaced;
    public static long StatErrors;
    /// <summary>Set when the watchdog stopped the feature; the report says so.</summary>
    public static string Broken { get; private set; }
    private static bool modeKnown;

    // ---- engine internals ----
    private static readonly AccessTools.FieldRef<TesselatedChunkPart, MeshData> Lod1Ref =
        AccessTools.FieldRefAccess<TesselatedChunkPart, MeshData>("modelDataLod1");
    private static readonly AccessTools.FieldRef<TesselatedChunkPart, EnumChunkRenderPass> PassRef =
        AccessTools.FieldRefAccess<TesselatedChunkPart, EnumChunkRenderPass>("pass");
    private static readonly AccessTools.FieldRef<TesselatedChunkPart, int> AtlasRef =
        AccessTools.FieldRefAccess<TesselatedChunkPart, int>("atlasNumber");
    private static readonly AccessTools.FieldRef<TesselatedChunk, TesselatedChunkPart[]> CenterPartsRef =
        AccessTools.FieldRefAccess<TesselatedChunk, TesselatedChunkPart[]>("centerParts");
    private static readonly AccessTools.FieldRef<TesselatedChunk, TesselatedChunkPart[]> EdgePartsRef =
        AccessTools.FieldRefAccess<TesselatedChunk, TesselatedChunkPart[]>("edgeParts");
    private static readonly AccessTools.FieldRef<TesselatedChunk, int> PosXRef =
        AccessTools.FieldRefAccess<TesselatedChunk, int>("positionX");
    private static readonly AccessTools.FieldRef<TesselatedChunk, int> PosYDimRef =
        AccessTools.FieldRefAccess<TesselatedChunk, int>("positionYAndDimension");
    private static readonly AccessTools.FieldRef<TesselatedChunk, int> PosZRef =
        AccessTools.FieldRefAccess<TesselatedChunk, int>("positionZ");
    private static Action<ChunkRenderer, MeshData, EnumChunkRenderPass> setStrides;

    public static void Apply(Harmony harmony, bool enabled, double distanceBlocks, bool tier2, ILogger log)
    {
        Log = log;
        if (Lod1Ref == null || PassRef == null || AtlasRef == null || CenterPartsRef == null
            || EdgePartsRef == null || PosXRef == null || PosYDimRef == null || PosZRef == null)
            throw new InvalidOperationException("TesselatedChunk internals not found");

        var strides = AccessTools.Method(typeof(ChunkRenderer), "SetInterleaveStrides",
                          [typeof(MeshData), typeof(EnumChunkRenderPass)])
                      ?? throw new InvalidOperationException("ChunkRenderer.SetInterleaveStrides not found");
        setStrides = AccessTools.MethodDelegate<Action<ChunkRenderer, MeshData, EnumChunkRenderPass>>(strides);

        var process = AccessTools.Method(typeof(ChunkTesselator), nameof(ChunkTesselator.NowProcessChunk),
                          [typeof(int), typeof(int), typeof(int), typeof(TesselatedChunk), typeof(bool)])
                      ?? throw new InvalidOperationException("ChunkTesselator.NowProcessChunk not found");
        harmony.Patch(process, postfix: new HarmonyMethod(AccessTools.Method(typeof(FarMeshPatches), nameof(AfterProcess))));

        var addToPools = AccessTools.Method(typeof(TesselatedChunkPart), "AddToPools")
                         ?? throw new InvalidOperationException("TesselatedChunkPart.AddToPools not found");
        harmony.Patch(addToPools,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(FarMeshPatches), nameof(BeforeAddToPools))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(FarMeshPatches), nameof(AfterAddToPools))));

        var dispose = AccessTools.Method(typeof(TesselatedChunkPart), "Dispose")
                      ?? throw new InvalidOperationException("TesselatedChunkPart.Dispose not found");
        harmony.Patch(dispose, postfix: new HarmonyMethod(AccessTools.Method(typeof(FarMeshPatches), nameof(AfterDispose))));

        var renderOpaque = AccessTools.Method(typeof(ChunkRenderer), nameof(ChunkRenderer.RenderOpaque), [typeof(float)])
                           ?? throw new InvalidOperationException("ChunkRenderer.RenderOpaque not found");
        harmony.Patch(renderOpaque, prefix: new HarmonyMethod(AccessTools.Method(typeof(FarMeshPatches), nameof(BeforeOpaque))));

        var remove = AccessTools.Method(typeof(MeshDataPool), nameof(MeshDataPool.RemoveLocation))
                     ?? throw new InvalidOperationException("MeshDataPool.RemoveLocation not found");
        harmony.Patch(remove, prefix: new HarmonyMethod(AccessTools.Method(typeof(FarMeshPatches), nameof(NoteRemoved))));

        FarMesh.ConfiguredEnabled = enabled;
        FarMesh.Enabled = enabled;
        FarMesh.ConfiguredTier2 = tier2;
        FarMesh.Tier2 = tier2;
        FarMesh.ConfiguredDistanceSq = distanceBlocks > 0 ? distanceBlocks * distanceBlocks : 0;
        FarMesh.DistanceSq = FarMesh.ConfiguredDistanceSq;
        Installed = true;
    }

    /// <summary>The passes that get a picture. Liquid needs the liquid pass's custom floats,
    /// Transparent and Meta are rare and see-through, Decor sits on other blocks' faces.</summary>
    internal static bool Pictured(EnumChunkRenderPass pass)
        => pass == EnumChunkRenderPass.Opaque || pass == EnumChunkRenderPass.OpaqueNoCull
           || pass == EnumChunkRenderPass.BlendNoCull || pass == EnumChunkRenderPass.TopSoil;

    /// <summary>
    /// Faces a part must have before it is worth a picture. A picture is two more pool parts
    /// (one per tier), two more entries in every sweep and, where it is drawn, two more draw
    /// ranges - so a part of a dozen faces (a handful of flowers in BlendNoCull, three
    /// mushrooms in OpaqueNoCull) costs more than the triangles it could ever save. Such a
    /// part is left out of the build entirely and keeps drawing its own mesh at every
    /// distance, exactly as before: its blocks are then simply not part of the coarse
    /// picture, which can only add faces to the neighbouring cells, never remove one.
    /// </summary>
    internal const int MinFacesForPicture = 96;

    /// <summary>Parts left out by the rule above.</summary>
    public static long StatTooSmall;

    // ---- tesselation thread ----

    [ThreadStatic] private static FarLodSource[] tlsSources;
    [ThreadStatic] private static TesselatedChunkPart[] tlsParts;
    [ThreadStatic] private static bool[] tlsCenter;
    [ThreadStatic] private static FarLodSource[] tlsSources2;
    [ThreadStatic] private static int[] tlsMap;

    public static void AfterProcess(TesselatedChunk tessChunk, bool skipChunkCenter)
    {
        if (!FarMesh.Enabled || tessChunk == null) return;
        try
        {
            // mini dimensions (previews, movables) have their own pool rules; left alone
            if (PosYDimRef(tessChunk) / 32768 != 0) return;
            BuildChunk(tessChunk, skipChunkCenter);
        }
        catch (Exception e)
        {
            if (++StatErrors == 1) Log?.Error("far lod: build failed, further failures are counted only: {0}", e);
        }
    }

    private static long ChunkKey(TesselatedChunk c)
        => ((long)PosXRef(c) << 40) ^ ((long)(PosYDimRef(c) % 32768) << 20) ^ (uint)PosZRef(c);

    /// <summary>
    /// Both tiers for one chunk. Public for the checks, which drive it with synthetic parts.
    /// </summary>
    public static void BuildChunk(TesselatedChunk tessChunk, bool skipChunkCenter)
    {
        var center = skipChunkCenter ? null : CenterPartsRef(tessChunk);
        var edge = EdgePartsRef(tessChunk);
        var count = (center?.Length ?? 0) + (edge?.Length ?? 0);
        if (count == 0) return;

        var sources = tlsSources;
        var parts = tlsParts;
        var isCenter = tlsCenter;
        if (sources == null || sources.Length < count)
        {
            sources = tlsSources = new FarLodSource[Math.Max(16, count)];
            parts = tlsParts = new TesselatedChunkPart[sources.Length];
            isCenter = tlsCenter = new bool[sources.Length];
            for (var i = 0; i < sources.Length; i++) sources[i] = new FarLodSource();
        }
        var n = 0;
        Gather(center, sources, parts, isCenter, ref n, true);
        Gather(edge, sources, parts, isCenter, ref n, false);
        if (n == 0) return;

        try
        {
            if (!FarLod.Build(sources, n, 1, skipChunkCenter)) return;
            StatChunks++;

            var any = false;
            for (var i = 0; i < n; i++)
                if (sources[i].Output != null) { any = true; break; }

            // the host of the chunk's tier 2: its first usable centre part, else its first usable part
            var host = -1;
            for (var i = 0; i < n; i++)
                if (!sources[i].Refused && isCenter[i]) { host = i; break; }
            if (host < 0)
                for (var i = 0; i < n; i++)
                    if (!sources[i].Refused) { host = i; break; }
            var hostIsCenter = host >= 0 && isCenter[host];

            // tier 2 from tier 1's outputs. On the centre when the chunk has one; a shell-only
            // pass leaves a centre-hosted tier 2 alone and rebuilds a shell-hosted one.
            var key = ChunkKey(tessChunk);
            bool centerHosted;
            lock (registryLock) centerHosted = tier2OnCenter.Contains(key);
            List<Picture> tier2 = null;
            if (any && host >= 0 && FarMesh.Tier2 && !(skipChunkCenter && centerHosted))
                tier2 = BuildTier2(sources, parts, n, skipChunkCenter);
            if (!skipChunkCenter)
            {
                lock (registryLock)
                {
                    if (tier2 != null && hostIsCenter) tier2OnCenter.Add(key); else tier2OnCenter.Remove(key);
                }
            }
            // tier 1 stops where tier 2 begins when the chunk has a tier 2 - this one, or the
            // centre's; without one it carries on to the view distance
            var hasTier2 = tier2 != null || (skipChunkCenter && centerHosted && FarMesh.Tier2);
            var tier1Level = hasTier2 ? FarMesh.LodFar : FarMesh.LodFarSolo;

            for (var i = 0; i < n; i++)
            {
                var src = sources[i];
                if (src.Refused) continue;
                extras.AddOrUpdate(parts[i], new Extra
                {
                    Tier1 = src.Output,
                    Tier1Level = tier1Level,
                    Pass = PassRef(parts[i]),
                    Atlas = AtlasRef(parts[i]),
                    Tier2 = i == host ? tier2 : null,
                });
                src.Output = null;
            }
        }
        finally
        {
            for (var i = 0; i < n; i++)
            {
                FarLod.Release(sources[i].Output);   // normally already handed over and null
                sources[i].Output = null;
                sources[i].Mesh = null;
                parts[i] = null;
            }
        }
    }

    /// <summary>Tier 2 pictures from the tier 1 outputs of the chunk's parts, or null when there are none.</summary>
    private static List<Picture> BuildTier2(FarLodSource[] sources, TesselatedChunkPart[] parts, int n, bool edgeOnly)
    {
        var s2 = tlsSources2;
        var map = tlsMap;
        if (s2 == null || s2.Length < n)
        {
            s2 = tlsSources2 = new FarLodSource[Math.Max(16, n)];
            map = tlsMap = new int[s2.Length];
            for (var i = 0; i < s2.Length; i++) s2[i] = new FarLodSource();
        }
        var n2 = 0;
        for (var i = 0; i < n; i++)
        {
            if (sources[i].Output == null) continue;
            s2[n2].Mesh = sources[i].Output;
            s2[n2].TopSoil = sources[i].TopSoil;
            s2[n2].Output = null;
            map[n2] = i;
            n2++;
        }
        List<Picture> tier2 = null;
        try
        {
            if (n2 == 0 || !FarLod.Build(s2, n2, 2, edgeOnly)) return null;
            for (var i = 0; i < n2; i++)
            {
                if (s2[i].Output == null) continue;
                var part = parts[map[i]];
                tier2 ??= new List<Picture>();
                tier2.Add(new Picture { Mesh = s2[i].Output, Pass = PassRef(part), Atlas = AtlasRef(part) });
                s2[i].Output = null;
            }
            return tier2;
        }
        finally
        {
            for (var i = 0; i < n2; i++)
            {
                FarLod.Release(s2[i].Output);
                s2[i].Output = null;
                s2[i].Mesh = null;
            }
        }
    }

    private static void Gather(TesselatedChunkPart[] arr, FarLodSource[] sources, TesselatedChunkPart[] parts,
                               bool[] isCenter, ref int n, bool center)
    {
        if (arr == null) return;
        foreach (var part in arr)
        {
            if (part == null) continue;
            var pass = PassRef(part);
            if (!Pictured(pass)) continue;
            var lod1 = Lod1Ref(part);
            if (lod1 == null) continue;
            if (lod1.VerticesCount < MinFacesForPicture * 4) { StatTooSmall++; continue; }
            sources[n].Mesh = lod1;
            sources[n].TopSoil = pass == EnumChunkRenderPass.TopSoil;
            sources[n].Output = null;
            parts[n] = part;
            isCenter[n] = center;
            n++;
        }
    }

    // ---- main thread ----

    private sealed class Handover
    {
        public Extra Extra;
        public int LocationsBefore;
    }

    /// <summary>
    /// Takes this part's pictures out of the table before the engine's body runs - which ends
    /// by disposing the part, and with it fires the Dispose postfix. Whatever the two patches'
    /// order, the meshes are in this call's own state by then.
    /// </summary>
    public static void BeforeAddToPools(TesselatedChunkPart __instance, List<ModelDataPoolLocation> locations, out object __state)
    {
        __state = null;
        // The engine's own meshes go into their own lane while the far LOD is on, so the
        // pictures placed in the postfix cannot end up interleaved with them. Cleared in the
        // postfix, and once per frame in BeforeOpaque should a body ever throw past it.
        if (FarMesh.Enabled) SpatialPools.Lane = LaneEngine;
        if (!extras.TryGetValue(__instance, out var ex)) return;
        extras.Remove(__instance);
        __state = new Handover { Extra = ex, LocationsBefore = locations?.Count ?? 0 };
    }

    public static void AfterAddToPools(object __state, ChunkRenderer cr, List<ModelDataPoolLocation> locations,
                                       Vec3i chunkOrigin, int dimension, Sphere boundingSphere, Bools cullVisible)
    {
        SpatialPools.Lane = 0;
        if (__state is not Handover h) return;
        var ex = h.Extra;
        StatAttempts++;
        try
        {
            // only dimension 0 is ever built (AfterProcess); anything else is not ours to place
            if (dimension != 0 || locations == null) return;
            var active = FarMesh.Active;

            // the part's own LOD 1 location, added by the body just now
            ModelDataPoolLocation nearLoc = null;
            for (var i = h.LocationsBefore; i < locations.Count; i++)
                if (locations[i].LodLevel == 1) { nearLoc = locations[i]; break; }

            var placed = false;
            var tier1Ok = true;
            if (ex.Tier1 != null && ex.Tier1.VerticesCount > 0)
            {
                var loc = Place(cr, ex.Tier1, ex.Pass, ex.Atlas, chunkOrigin, dimension, boundingSphere, cullVisible, 1, ex.Tier1Level, active, LaneTier1);
                if (loc != null) { locations.Add(loc); placed = true; }
                else tier1Ok = false;
            }
            if (ex.Tier2 != null)
            {
                foreach (var p in ex.Tier2)
                {
                    if (p.Mesh == null || p.Mesh.VerticesCount == 0) continue;
                    var loc = Place(cr, p.Mesh, p.Pass, p.Atlas, chunkOrigin, dimension, boundingSphere, cullVisible, 3, FarMesh.LodFar2, active, LaneTier2);
                    if (loc != null) { locations.Add(loc); placed = true; }
                }
            }
            // A part whose picture could not be placed stays at level 1: drawn at every
            // distance, as before. One that owns nothing beyond the distance, or whose
            // picture is in, stops at the distance.
            if (nearLoc != null && tier1Ok)
            {
                nearLoc.LodLevel = active ? FarMesh.LodNear : 1;
                tracked.AddOrUpdate(nearLoc, new Mark { Kind = KindNear });
                TrackedNear++;
                placed = true;
            }
            if (placed) StatPlaced++;
        }
        catch (Exception e)
        {
            if (++StatErrors == 1) Log?.Error("far lod: adding to the pools failed, further failures are counted only: {0}", e);
        }
        finally
        {
            DisposeExtra(ex);
        }
    }

    private static ModelDataPoolLocation Place(ChunkRenderer cr, MeshData mesh, EnumChunkRenderPass pass, int atlas,
                                               Vec3i chunkOrigin, int dimension, Sphere boundingSphere, Bools cullVisible,
                                               int grow, int lodLevel, bool active, int lane)
    {
        var managers = cr.poolsByRenderPass[(int)pass];
        if (atlas < 0 || atlas >= managers.Length || managers[atlas] == null) return null;
        setStrides(cr, mesh, pass);
        // the picture is up to `grow` blocks fatter than the chunk's own bounds on every side
        var sphere = boundingSphere;
        var pad = Sphere.sqrt3half * 2f * grow;
        sphere.radius += pad;
        sphere.radiusY += pad;
        sphere.radiusZ += pad;
        ModelDataPoolLocation loc;
        SpatialPools.Lane = lane;
        try { loc = managers[atlas].AddModel(mesh, chunkOrigin, dimension, sphere); }
        finally { SpatialPools.Lane = 0; }
        if (loc == null) return null;
        loc.CullVisible = cullVisible;
        loc.LodLevel = lodLevel;
        loc.Hide = !active;
        tracked.AddOrUpdate(loc, new Mark { Kind = KindFar });
        TrackedFar++;
        return loc;
    }

    /// <summary>
    /// Gives a part's pictures back - the custom arrays to the size-class pool, the meshes to
    /// the engine's recycler. Called after the pools have taken what they wanted (the upload
    /// is synchronous, so the arrays are free the moment AddModel returns) and for a part that
    /// never reached the pools at all.
    /// </summary>
    private static void DisposeExtra(Extra ex)
    {
        if (ex == null) return;
        FarLod.Release(ex.Tier1);
        ex.Tier1 = null;
        if (ex.Tier2 != null)
        {
            foreach (var p in ex.Tier2) FarLod.Release(p.Mesh);
            ex.Tier2 = null;
        }
    }

    /// <summary>For the checks: what a part carries, without taking it out of the table.</summary>
    internal static bool Peek(TesselatedChunkPart part, out MeshData tier1, out int tier1Level, out int tier2Pictures)
    {
        tier1 = null;
        tier1Level = 0;
        tier2Pictures = 0;
        if (!extras.TryGetValue(part, out var ex)) return false;
        tier1 = ex.Tier1;
        tier1Level = ex.Tier1Level;
        tier2Pictures = ex.Tier2?.Count ?? 0;
        return true;
    }

    /// <summary>A part disposed without ever reaching the pools (a superseded tesselation).</summary>
    public static void AfterDispose(TesselatedChunkPart __instance)
    {
        if (!extras.TryGetValue(__instance, out var ex)) return;
        extras.Remove(__instance);
        DisposeExtra(ex);
    }

    public static void NoteRemoved(ModelDataPoolLocation location)
    {
        if (location == null || !tracked.TryGetValue(location, out var m)) return;
        tracked.Remove(location);
        if (m.Kind == KindNear) TrackedNear--; else TrackedFar--;
    }

    /// <summary>Before the opaque pass: mode in line with the switches.</summary>
    public static void BeforeOpaque()
    {
        SpatialPools.Lane = 0;   // a body that threw past the AddToPools postfix must not leak the lane
        try { SyncMode(); }
        catch (Exception e)
        {
            if (++StatErrors == 1) Log?.Error("far lod: mode sync failed: {0}", e);
        }
    }

    /// <summary>
    /// Brings every tracked location in line with whether the pictures are drawn. Cheap when
    /// nothing changed (one comparison); a walk over all tracked locations otherwise, followed
    /// by a cache invalidation so the sweep re-reads the LOD levels.
    /// </summary>
    public static void SyncMode()
    {
        // Building without placing means the engine's meshes stop at the distance and nothing
        // stands beyond it: the world would end at the far distance. So it is a counter,
        // checked here, not a hope.
        if (Broken == null && StatAttempts >= WatchdogParts && StatPlaced == 0)
        {
            Broken = "handed " + StatAttempts + " parts to the pools and placed no picture - the world would end at the far distance";
            FarMesh.Enabled = false;
            Log?.Error("far lod: {0}. Switched off; the chunks already built draw as the engine's until re-tesselated.", Broken);
        }

        var want = FarMesh.Enabled && FastCuller.Enabled;
        if (modeKnown && want == FarMesh.Active) return;

        foreach (var kv in tracked)
        {
            var loc = kv.Key;
            if (kv.Value.Kind == KindNear) loc.LodLevel = want ? FarMesh.LodNear : 1;
            else loc.Hide = !want;
        }
        FarMesh.Active = want;
        modeKnown = true;
        FastCuller.InvalidateAll();
    }

    /// <summary>Leaving the world: nothing tracked, mode unknown.</summary>
    public static void Reset()
    {
        extras = new ConditionalWeakTable<TesselatedChunkPart, Extra>();
        tracked = new ConditionalWeakTable<ModelDataPoolLocation, Mark>();
        lock (registryLock) tier2OnCenter.Clear();
        TrackedNear = TrackedFar = 0;
        StatPlaced = 0;
        StatAttempts = 0;
        StatChunks = 0;
        StatTooSmall = 0;
        Broken = null;
        FarMesh.Active = false;
        modeKnown = false;
    }
}
