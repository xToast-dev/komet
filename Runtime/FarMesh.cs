using System;
using Vintagestory.API.Client;

namespace Komet.Runtime;

/// <summary>
/// The far LOD's switches, distances and LOD levels - the state the sweep, the pool hooks,
/// the report and the command share. The build itself is <see cref="FarLod"/>.
///
/// Beyond <see cref="EffectiveDistanceSq"/> a chunk part is drawn as its tier 1 picture
/// (cells of two blocks, <see cref="LodFar"/>), beyond twice that as its tier 2 picture
/// (cells of four, <see cref="LodFar2"/>); within the distance the engine's own mesh is
/// drawn, re-levelled to <see cref="LodNear"/> so the sweep can stop it at the distance. A
/// tier 1 picture without a tier 2 sibling is <see cref="LodFarSolo"/> and carries on to the
/// view distance. The engine's own range test returns false for every one of these levels,
/// which is what makes the fallback safe: with the sweep not ours, or the feature off,
/// <c>SyncMode</c> puts the engine's meshes back at level 1 and hides the pictures - the
/// frame is then exactly the engine's, with nothing re-tesselated.
/// </summary>
public static class FarMesh
{
    /// <summary>Tier 1 (cells of two blocks), drawn from the distance to twice the distance.</summary>
    public const int LodFar = 4;
    /// <summary>The engine's own LOD 1 mesh of a part that has a far picture: drawn within the distance.</summary>
    public const int LodNear = 5;
    /// <summary>Tier 2 (cells of four blocks), drawn beyond twice the distance.</summary>
    public const int LodFar2 = 6;
    /// <summary>Tier 1 of a part with no tier 2 sibling: drawn from the distance to the view distance.</summary>
    public const int LodFarSolo = 7;

    /// <summary>Tier 2's distance as a multiple of tier 1's.</summary>
    public const double Tier2Factor = 2.0;

    /// <summary>Whether new chunks get their far pictures built as they are tesselated.</summary>
    public static bool Enabled;
    public static bool ConfiguredEnabled;

    /// <summary>Whether tier 2 pictures are built and drawn. Off, tier 1 carries on to the view distance.</summary>
    public static bool Tier2 = true;
    public static bool ConfiguredTier2 = true;

    /// <summary>
    /// Whether the far pictures are DRAWN: the sweep is ours and the feature is enabled. Set
    /// once per frame by the render hook; read by the LOD tables and the pool insertion.
    /// </summary>
    public static bool Active;

    /// <summary>Squared distance beyond which tier 1 replaces the engine's mesh; 0 means the
    /// rule in <see cref="EffectiveDistanceSq"/>.</summary>
    public static double DistanceSq;
    public static double ConfiguredDistanceSq;

    /// <summary>
    /// The default rule's fraction of the view distance, and its floor in blocks. At 1440p
    /// and 70 degrees a block at 400 blocks covers about 4,6 pixels, at 540 about 3,4: a cell
    /// of two is then seven pixels, and the one-block step where the picture changes is a
    /// few. Nearer than that the change of representation would be visible on a still frame;
    /// farther, the area (and the triangles) outside the distance shrink with the square. At
    /// view distance 1536 the rule gives 538 blocks, which puts 88 % of the visible area
    /// beyond it.
    /// </summary>
    public const double DefaultFraction = 0.35;
    public const double DefaultFloor = 400;

    /// <summary>The distance the last sweep used, squared - for the report. 0 until a sweep has run.</summary>
    public static double LastEffectiveSq;

    /// <summary>
    /// The answer, with the two inputs it was computed from. Published as one reference so a
    /// reader can never see half of an update: the sweep asks for this on every pool of every
    /// stage, from five threads, and a memo made of separate fields would both tear and, far
    /// worse, write a shared cache line thousands of times a frame. The first version of this
    /// method assigned <see cref="LastEffectiveSq"/> on every call and cost 0,3 ms a frame in
    /// the benchmark's small-load line for exactly that reason.
    /// </summary>
    private sealed class Band
    {
        public int ViewDistanceSq = -1;
        public double Asked, Result;
    }
    private static Band band = new();

    /// <summary>
    /// The distance in force for this culler, squared: what was asked for, or the default
    /// rule - a fraction of the view distance with a floor. Never capped at the view
    /// distance: a distance at or beyond it simply leaves no band, and <see cref="HasBand"/>
    /// says so.
    /// </summary>
    public static double EffectiveDistanceSq(FrustumCulling culler)
    {
        var view = culler.ViewDistanceSq;
        var asked = DistanceSq;
        var b = band;
        if (b.ViewDistanceSq == view && b.Asked.Equals(asked)) return b.Result;

        double result;
        if (asked > 0) result = asked;
        else
        {
            // ViewDistanceSq carries the engine's +400 margin; the rule wants the plain distance
            var plain = Math.Sqrt(Math.Max(0, view - 400));
            var d = Math.Max(DefaultFloor, plain * DefaultFraction);
            result = d * d;
        }
        band = new Band { ViewDistanceSq = view, Asked = asked, Result = result };
        LastEffectiveSq = result;
        return result;
    }

    /// <summary>Tier 2's distance, squared, for this culler.</summary>
    public static double EffectiveDistance2Sq(FrustumCulling culler)
        => EffectiveDistanceSq(culler) * (Tier2Factor * Tier2Factor);

    /// <summary>Whether tier 1 is ever drawn at this view distance: the distance lies inside it.</summary>
    public static bool HasBand(FrustumCulling culler)
        => EffectiveDistanceSq(culler) < culler.ViewDistanceSq;

    /// <summary>Whether a level is one of the far pictures (drawn in the camera pass only, never as a shadow caster).</summary>
    public static bool IsPicture(int lodLevel) => lodLevel == LodFar || lodLevel == LodFar2 || lodLevel == LodFarSolo;
}
