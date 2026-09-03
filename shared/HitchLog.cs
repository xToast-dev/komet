using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Komet.Measure;

/// <summary>
/// Records every frame that crosses the hitch threshold, with its complete bucket attribution
/// and what the camera was doing at that moment.
///
/// Averages cannot answer "warum ruckelt es beim Umschauen": a hitch is rare by definition, so
/// its cause is invisible in the smoothed figures, and the worst-frame row only keeps the
/// single worst frame of a rolling window - gone by the time anyone reads it. This log books
/// each offender individually, splits them by whether the camera was turning, moving or still,
/// and counts which bucket dominated - so "es ruckelt beim drehen" becomes a measured,
/// attributable statement instead of an impression.
///
/// Lives in shared/ so the baseline records hitches identically: hitches per minute is then a
/// number that can be compared between vanilla and komet by construction.
/// </summary>
public static class HitchLog
{
    // Bucket indices, mirroring the worst-frame tail's vocabulary. "schatten" and "post" are
    // the same stage sums FrameStats publishes; "draussen" is outside-stages minus the swap.
    public const int Before = 0, Schatten = 1, Opaque = 2, Oit = 3, Post = 4,
                     Ortho = 5, Done = 6, Tick = 7, Swap = 8, Draussen = 9;
    public const int BucketCount = 10;

    public static readonly string[] BucketNames =
        { "before", "schatten", "opaque", "oit", "post", "ortho", "done", "tick", "swap", "draussen" };

    /// <summary>Floor in milliseconds - below this a frame is never booked...</summary>
    public static double MinMs = 15.0;

    /// <summary>...and it also has to be this many times the current average frame, so a
    /// heavy scene running steadily at 20 ms does not drown the log in non-events.</summary>
    public static double Factor = 2.0;

    /// <summary>Camera turn rate from which a hitch counts as "beim drehen". A deliberate
    /// look-around is hundreds of degrees per second; idle mouse noise is near zero.</summary>
    public const double TurnThresholdDegPerSec = 45.0;

    /// <summary>Movement speed from which a hitch counts as "in bewegung" (walking is ~4.3).</summary>
    public const double MoveThresholdBlocksPerSec = 2.0;

    /// <summary>Sink for the per-hitch log line, so readings survive the session
    /// (client-main.log). Null logs nothing; the ring buffer still fills.</summary>
    public static Action<string> Log;

    /// <summary>Where the reader finds details - the HUD hint differs per mod.</summary>
    public static string CommandHint = "client-main.log";

    /// <summary>The most expensive renderer of the frame being booked, when the renderer
    /// profiler measured it (it samples every fourth frame). Null when unavailable. Queried
    /// at detection time, before the profiler folds and clears its per-frame ticks.</summary>
    public static Func<(string name, double ms)?> TopRendererProvider;

    /// <summary>The frame's most expensive game tick listener, from the tick profiler -
    /// same contract as <see cref="TopRendererProvider"/>: raw per-frame ticks, queried at
    /// detection time before the profiler folds them. Null when nothing is wrapped.</summary>
    public static Func<(string name, double ms)?> TopTickListenerProvider;

    /// <summary>
    /// The entity Before stage split of the frame just ended (vor-render ms, animation ms,
    /// entities animated, the most expensive single entity and its ms), when the optimising
    /// mod measures it. Read at hitch detection, like the tick listener.
    /// </summary>
    public static Func<(double beforeMs, double animMs, int animated, string topName, double topMs)?> EntityFrameProvider;

    public struct Entry
    {
        public double AtSeconds;
        public double FrameMs;
        public double AvgMs;
        public double GcPauseMs;
        public double[] Buckets;
        /// <summary>Highest GC generation collected during this frame ("gen0".."gen2"),
        /// null when no collection boundary was crossed or the counts were unknown.</summary>
        public string GcTag;
        /// <summary>NaN while no camera sample arrived for this frame (menu, missing wiring).</summary>
        public double TurnDegPerSec;
        public double MoveBlocksPerSec;
        public string TopRenderer;
        public double TopRendererMs;
        /// <summary>
        /// What the mod's own visibility sweep and the chunk upload took in exactly this frame.
        /// These two exist to make a hitch line self-attributing: "schatten 44,1" with
        /// "sweep 0,9" says the time went into GL submission or driver back-pressure, not into
        /// this mod's code - the distinction every spike report so far has needed answered
        /// before anything else.
        /// </summary>
        public double SweepMs;
        public double UploadMs;

        /// <summary>The debug overlay's own text rebuild inside this frame - measured after a
        /// tester's ~40 ms Cairo rebuilds at fixed 4 Hz turned out to BE the ortho stutter.</summary>
        public double HudMs;

        /// <summary>Main-thread entity shape tesselation inside this frame. The world-join
        /// bursts book into "before" next to chunk uploads and the liquid-depth pass; this
        /// share says whether a before-hitch was the entity swarm or something else in the
        /// same stage.</summary>
        public double EntityTessMs;

        /// <summary>
        /// The part of <see cref="SweepMs"/> the render thread spent waiting for the cull
        /// worker threads. A sweep that is almost all wait is a scheduling stall - somebody
        /// else had the cores - and no amount of making the kernel faster touches it.
        /// </summary>
        public double SweepWaitMs;

        /// <summary>
        /// The part of <see cref="SweepMs"/> spent rebuilding pool caches from their location
        /// objects, and how many pools were rebuilt. A sweep that is mostly rebuild after a
        /// chunk-unload burst is index maintenance, not culling arithmetic - a different fix.
        /// </summary>
        public double SweepRebuildMs;
        public int SweepRebuilds;

        /// <summary>
        /// Main-thread task drain inside this frame (ClientMain.ExecuteMainThreadTasks - every
        /// server packet that is not chunk data runs there: entity loads, block updates,
        /// attribute syncs), and the single most expensive task code of the frame. This is
        /// the "draussen" bucket's biggest unnamed tenant: it runs after the render stages
        /// and before the next frame boundary, so until now a task burst read as driver
        /// back-pressure. Only the optimising mod fills these; the baseline books 0.
        /// </summary>
        public double MainTaskMs;
        public string MainTaskTop;
        public double MainTaskTopMs;

        /// <summary>Mesh pools created inside this frame's upload drain, and their share of it.</summary>
        public double PoolAllocMs;
        public int PoolAllocs;

        /// <summary>Dialogs open during an Ortho-dominated frame: the GUI's cost has a name.</summary>
        public string Dialogs;

        /// <summary>The frame's most expensive game tick listener (name and ms), when the
        /// tick profiler measured it. A tick hitch then names its listener instead of
        /// leaving "tick 12,7" as a bucket with a hundred possible owners.</summary>
        public string TickTop;
        public double TickTopMs;

        /// <summary>Main-thread entity loading inside this frame (the budgeted second half:
        /// Initialize, chunk registration, renderer creation). Booked separately from
        /// MainTaskMs because the budget moves it OUT of the task drain and into the frame
        /// boundary - a before-hitch has to be able to say it was this.</summary>
        public double EntityLoadMs;

        /// <summary>The entity Before stage's two halves (EntityRenderer.BeforeRender for
        /// the visible ones, AnimManager.OnClientFrame for all) plus the frame's most
        /// expensive single entity - so a "before 19 ms | renderer Before-ree" hitch can say
        /// whether it was animation, and of what.</summary>
        public double EntityBeforeMs, EntityAnimMs, EntityTopMs;
        public int EntityAnimated;
        public string EntityTopName;
    }

    private const int Capacity = 48;
    private static readonly Entry[] ring = new Entry[Capacity];
    private static int ringCount, ringNext;

    // A detected hitch is held back for one frame boundary: the camera delta covering the
    // hitch frame only becomes known when the *next* frame's boundary samples the camera.
    private static Entry pending;
    private static bool hasPending;

    public static int TotalHitches { get; private set; }
    public static int CountTurning { get; private set; }
    public static int CountMoving { get; private set; }
    public static int CountStill { get; private set; }
    public static int CountGcPause { get; private set; }
    public static int CountGen2 { get; private set; }
    /// <summary>Frames over the threshold that were dropped because the game was paused at
    /// one of the frame's two boundaries. A tester's log carried a 5,6 s "draussen" hitch
    /// that was the pause menu standing open - which reads exactly like a stall and is not
    /// one. Counted so the report can say the log is complete.</summary>
    public static int CountPaused { get; private set; }
    private static bool prevPaused;
    private static readonly int[] dominantCounts = new int[BucketCount];

    private static readonly Stopwatch uptime = Stopwatch.StartNew();
    private static long observingSince;

    private static bool hasCamera;
    private static double prevYaw, prevPitch, prevX, prevY, prevZ;
    private static double lastTurnDeg, lastMoveBlocks;

    private static int logsInWindow, suppressedLogs;
    private static double logWindowStart = double.MinValue;

    /// <summary>The rule, pure so it can be checked directly.</summary>
    public static bool IsHitch(double frameMs, double avgFrameMs, double minMs, double factor)
        => avgFrameMs > 0 && frameMs >= minMs && frameMs >= avgFrameMs * factor;

    /// <summary>Highest GC generation with a collection in this frame - the label that
    /// decides which GC lever applies when the pauses get hunted.</summary>
    internal static string GcGenTag(int gen0Delta, int gen1Delta, int gen2Delta)
        => gen2Delta > 0 ? "gen2" : gen1Delta > 0 ? "gen1" : gen0Delta > 0 ? "gen0" : null;

    /// <summary>
    /// Called once per frame from the frame accounting, while the frame's buckets are still
    /// intact. The buckets array is reused by the caller and copied here when needed.
    /// </summary>
    public static void OnFrame(double frameMs, double avgFrameMs, double gcPauseMs, double[] buckets,
                               string gcTag = null, double sweepMs = 0, double uploadMs = 0,
                               double sweepWaitMs = 0, double hudMs = 0, double entityTessMs = 0,
                               double sweepRebuildMs = 0, int sweepRebuilds = 0,
                               double mainTaskMs = 0, string mainTaskTop = null, double mainTaskTopMs = 0,
                               double entityLoadMs = 0, double poolAllocMs = 0, int poolAllocs = 0)
    {
        // a pending hitch whose camera sample never came (main menu, no wiring) is booked
        // without one rather than lost
        if (hasPending) CommitPending();
        if (observingSince == 0) observingSince = Stopwatch.GetTimestamp();

        // Tracked for every frame, not only for hitches: the point is the worst freeze the GC
        // mode produced, and a long pause that happened to land in a frame with slack still
        // proves the mode can produce it.
        if (gcPauseMs > WorstEphemeralPauseMs && (gcTag == "gen0" || gcTag == "gen1"))
            WorstEphemeralPauseMs = gcPauseMs;

        if (!IsHitch(frameMs, avgFrameMs, MinMs, Factor)) return;

        pending = new Entry
        {
            AtSeconds = uptime.Elapsed.TotalSeconds,
            FrameMs = frameMs,
            AvgMs = avgFrameMs,
            GcPauseMs = gcPauseMs,
            GcTag = gcTag,
            Buckets = (double[])buckets.Clone(),
            TurnDegPerSec = double.NaN,
            MoveBlocksPerSec = double.NaN,
            SweepMs = sweepMs,
            UploadMs = uploadMs,
            SweepWaitMs = sweepWaitMs,
            SweepRebuildMs = sweepRebuildMs,
            SweepRebuilds = sweepRebuilds,
            HudMs = hudMs,
            EntityTessMs = entityTessMs,
            MainTaskMs = mainTaskMs,
            MainTaskTop = mainTaskTop,
            MainTaskTopMs = mainTaskTopMs,
            EntityLoadMs = entityLoadMs,
            PoolAllocMs = poolAllocMs,
            PoolAllocs = poolAllocs,
        };
        // The 0.5 ms floor matters since the Before stage is attributed on every frame: on
        // an unsampled frame the "top renderer" is merely the top BEFORE renderer, and a
        // 30 ms opaque hitch must not get a meaningless "renderer Before-camera 0,02 ms"
        // stamped on it - absence says "no measured renderer explains this" more honestly.
        var top = TopRendererProvider?.Invoke();
        if (top.HasValue && top.Value.ms >= 0.5)
        {
            pending.TopRenderer = top.Value.name;
            pending.TopRendererMs = top.Value.ms;
        }
        // Same floor for the tick listener: a 30 ms opaque hitch must not get a 0,1 ms
        // listener stamped on as if it explained anything.
        var tickTop = TopTickListenerProvider?.Invoke();
        if (tickTop.HasValue && tickTop.Value.ms >= 0.5)
        {
            pending.TickTop = tickTop.Value.name;
            pending.TickTopMs = tickTop.Value.ms;
        }
        var ent = EntityFrameProvider?.Invoke();
        if (ent.HasValue)
        {
            pending.EntityBeforeMs = ent.Value.beforeMs;
            pending.EntityAnimMs = ent.Value.animMs;
            pending.EntityAnimated = ent.Value.animated;
            pending.EntityTopName = ent.Value.topName;
            pending.EntityTopMs = ent.Value.topMs;
        }
        hasPending = true;
    }

    /// <summary>
    /// The pause state at this frame boundary, sampled BEFORE the camera. A pending hitch whose
    /// frame started or ended paused is dropped: the singleplayer pause menu stops the game
    /// clock and the frame that spans it (or the one that leaves it) is menu time, not a
    /// stutter. The previous boundary counts too, because the frame that closes the menu
    /// begins paused and ends running.
    /// </summary>
    /// <summary>
    /// True while a pending hitch spent a quarter or more of its frame in the Ortho stage -
    /// the GUI - so the caller knows whether naming the open dialogs is worth a string.
    /// </summary>
    public static bool PendingWantsDialogs
        => hasPending && pending.Buckets[Ortho] >= pending.FrameMs * 0.25;

    /// <summary>
    /// Names the open dialogs on a pending Ortho hitch. The 03.09. log had a 2,7 s frame with
    /// "ortho 2645" and an 880 ms gen1 pause, standing still, nothing in the log beside it -
    /// a first-opened world map or handbook, presumably, but the line could not say which.
    /// Called before the camera sample commits the hitch.
    /// </summary>
    public static void NoteDialogs(string names)
    {
        if (!hasPending || string.IsNullOrEmpty(names)) return;
        if (pending.Buckets[Ortho] >= pending.FrameMs * 0.25) pending.Dialogs = names;
    }

    public static void NotePaused(bool paused)
    {
        if (hasPending && (paused || prevPaused))
        {
            hasPending = false;
            CountPaused++;
        }
        prevPaused = paused;
    }

    /// <summary>
    /// One camera sample per frame boundary. The delta to the previous sample spans exactly
    /// the frame that just ended, so rates divide by that frame's duration - no wall clock
    /// involved, which also keeps this testable with synthetic frames.
    /// </summary>
    public static void NoteCamera(double yawRad, double pitchRad, double x, double y, double z)
    {
        if (hasCamera)
        {
            var dYaw = WrappedDeltaRad(prevYaw, yawRad);
            var dPitch = WrappedDeltaRad(prevPitch, pitchRad);
            lastTurnDeg = Math.Sqrt(dYaw * dYaw + dPitch * dPitch) * (180.0 / Math.PI);
            double dx = x - prevX, dy = y - prevY, dz = z - prevZ;
            lastMoveBlocks = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
        prevYaw = yawRad; prevPitch = pitchRad;
        prevX = x; prevY = y; prevZ = z;
        hasCamera = true;

        if (hasPending)
        {
            var seconds = pending.FrameMs / 1000.0;
            if (seconds > 0)
            {
                pending.TurnDegPerSec = lastTurnDeg / seconds;
                pending.MoveBlocksPerSec = lastMoveBlocks / seconds;
            }
            CommitPending();
        }
    }

    /// <summary>
    /// Shortest angular difference: yaw lives on a circle, and a small turn across the 0/2pi
    /// seam must read as a few degrees, not as a full revolution.
    /// </summary>
    internal static double WrappedDeltaRad(double from, double to)
    {
        var d = (to - from) % (2 * Math.PI);
        if (d > Math.PI) d -= 2 * Math.PI;
        if (d < -Math.PI) d += 2 * Math.PI;
        return d;
    }

    internal static int DominantBucket(double[] buckets)
    {
        var best = 0;
        for (var i = 1; i < BucketCount; i++)
            if (buckets[i] > buckets[best]) best = i;
        return best;
    }

    private static void CommitPending()
    {
        hasPending = false;
        var slot = ringNext;
        ring[slot] = pending;
        ringNext = (ringNext + 1) % Capacity;
        if (ringCount < Capacity) ringCount++;

        TotalHitches++;
        dominantCounts[DominantBucket(pending.Buckets)]++;
        var turning = pending.TurnDegPerSec >= TurnThresholdDegPerSec;
        var moving = pending.MoveBlocksPerSec >= MoveThresholdBlocksPerSec;
        if (turning) CountTurning++;
        if (moving) CountMoving++;
        if (!turning && !moving && !double.IsNaN(pending.TurnDegPerSec)) CountStill++;
        if (pending.GcPauseMs >= 2.0) CountGcPause++;
        if (pending.GcTag == "gen2") CountGen2++;

        if (Log != null)
        {
            if (RateLimitAllows(uptime.Elapsed.TotalSeconds)) Log("ruckler: " + FormatEntry(in ring[slot]));
            else suppressedLogs++;
        }
    }

    /// <summary>A hitch storm must not turn the log into its own hitch: a handful of full
    /// lines per window, the rest only counted (the report names the suppressed number).</summary>
    internal static bool RateLimitAllows(double nowSeconds)
    {
        if (nowSeconds - logWindowStart > 30)
        {
            logWindowStart = nowSeconds;
            logsInWindow = 0;
        }
        return ++logsInWindow <= 6;
    }

    /// <summary>One full line per hitch. Chat-safe: the game chat parses VTML, so never any
    /// angle brackets in strings that may end up there.</summary>
    public static string FormatEntry(in Entry e)
    {
        var ci = CultureInfo.CurrentCulture;
        var sb = new StringBuilder(160);
        sb.AppendFormat(ci, "{0:F1} ms (avg {1:F1}) bei {2:F0}s: ", e.FrameMs, e.AvgMs, e.AtSeconds);

        var b = (double[])e.Buckets.Clone();
        var any = false;
        for (var rank = 0; rank < 3; rank++)
        {
            var best = 0;
            for (var i = 1; i < BucketCount; i++)
                if (b[i] > b[best]) best = i;
            if (b[best] < 0.5) break;
            if (any) sb.Append(" + ");
            sb.Append(BucketNames[best]).Append(' ').Append(b[best].ToString("F1", ci));
            b[best] = 0;
            any = true;
        }
        sb.Append(any ? " ms" : "unzugeordnet");

        if (e.GcPauseMs > 0.5 || e.GcTag != null)
        {
            sb.AppendFormat(ci, " | gc {0:F1}", e.GcPauseMs);
            if (e.GcTag != null) sb.Append(" (").Append(e.GcTag).Append(')');
        }
        // Only when they explain a meaningful share - a 20 ms hitch with 0,1 ms of sweep says
        // "not the sweep" loudly enough by not appearing.
        if (e.SweepMs >= 1.0 || e.UploadMs >= 1.0 || e.HudMs >= 1.0 || e.EntityTessMs >= 1.0
            || e.MainTaskMs >= 1.0 || e.EntityLoadMs >= 1.0)
        {
            sb.AppendFormat(ci, " | davon sweep {0:F1}", e.SweepMs);
            // The wait is a share OF THE SWEEP, so it prints inside the sweep figure. It used
            // to be appended after the whole list, where "upload 0,2 (davon 2,6 warten auf
            // threads)" read as an upload that waited longer than it ran. Only worth the
            // width when it is actually a large share of the sweep - which is exactly the
            // case that needs naming.
            if (e.SweepWaitMs >= 1.0 && e.SweepWaitMs >= e.SweepMs * 0.25)
                sb.AppendFormat(ci, " (davon {0:F1} warten auf threads)", e.SweepWaitMs);
            // Same rule for the rebuild share: only when it explains a real part of the sweep.
            if (e.SweepRebuildMs >= 1.0 && e.SweepRebuildMs >= e.SweepMs * 0.25)
                sb.AppendFormat(ci, " (davon {0:F1} rebuild, {1} pools)", e.SweepRebuildMs, e.SweepRebuilds);
            sb.AppendFormat(ci, ", upload {0:F1}", e.UploadMs);
            // A new GL pool inside the drain is the one upload cost a vertex budget cannot
            // bound; named when it is a real share of the upload, like the sweep's wait.
            if (e.PoolAllocs > 0 && e.PoolAllocMs >= 1.0 && e.PoolAllocMs >= e.UploadMs * 0.25)
                sb.AppendFormat(ci, " (davon {0} neue pools {1:F1})", e.PoolAllocs, e.PoolAllocMs);
            if (e.EntityTessMs >= 1.0)
                sb.AppendFormat(ci, ", enttess {0:F1}", e.EntityTessMs);
            if (e.HudMs >= 1.0)
                sb.AppendFormat(ci, ", hud {0:F1}", e.HudMs);
            if (e.EntityLoadMs >= 1.0)
                sb.AppendFormat(ci, ", entload {0:F1}", e.EntityLoadMs);
            // The task drain names its heaviest task code the way the sweep names its
            // wait: only when that one task is a real share of the drain.
            if (e.MainTaskMs >= 1.0)
            {
                sb.AppendFormat(ci, ", tasks {0:F1}", e.MainTaskMs);
                if (e.MainTaskTop != null && e.MainTaskTopMs >= 1.0 && e.MainTaskTopMs >= e.MainTaskMs * 0.25)
                    sb.AppendFormat(ci, " ({0} {1:F1})", e.MainTaskTop, e.MainTaskTopMs);
            }
        }
        if (e.TickTop != null && e.TickTopMs >= 1.0)
            sb.AppendFormat(ci, " | tick-listener {0} {1:F1} ms", e.TickTop, e.TickTopMs);
        if (e.Dialogs != null)
            sb.Append(" | dialoge ").Append(e.Dialogs);
        // The entity split only when it explains something; the top entity only when it is
        // a real share of that - same rules as the sweep's wait and the drain's top task.
        if (e.EntityBeforeMs + e.EntityAnimMs >= 1.0)
        {
            sb.AppendFormat(ci, " | entities vor-render {0:F1} ms, anim {1:F1} ms/{2}",
                e.EntityBeforeMs, e.EntityAnimMs, e.EntityAnimated);
            if (e.EntityTopName != null && e.EntityTopMs >= 1.0 && e.EntityTopMs >= (e.EntityBeforeMs + e.EntityAnimMs) * 0.25)
                sb.AppendFormat(ci, " (top {0} {1:F1})", e.EntityTopName, e.EntityTopMs);
        }
        if (!double.IsNaN(e.TurnDegPerSec))
            sb.AppendFormat(ci, " | {0:F0} grad/s, {1:F1} m/s", e.TurnDegPerSec, e.MoveBlocksPerSec);
        if (e.TopRenderer != null)
            sb.AppendFormat(ci, " | renderer {0} {1:F1} ms", e.TopRenderer, e.TopRendererMs);
        return sb.ToString();
    }

    /// <summary>Hitches per minute of observed play, so a count means the same thing after
    /// two minutes and after two hours.</summary>
    public static double PerMinute
    {
        get
        {
            if (observingSince == 0) return 0;
            var minutes = (Stopwatch.GetTimestamp() - observingSince)
                          / (double)Stopwatch.Frequency / 60.0;
            return minutes > 0.05 ? TotalHitches / minutes : 0;
        }
    }

    /// <summary>Compact tail for the HUD's "zuletzt" row; null while nothing is recorded.</summary>
    public static string LastTail()
    {
        if (ringCount == 0) return null;
        ref var e = ref ring[(ringNext - 1 + Capacity) % Capacity];
        var ci = CultureInfo.CurrentCulture;
        var dom = DominantBucket(e.Buckets);
        var s = e.FrameMs.ToString("F1", ci) + " ms, " + BucketNames[dom] + " "
                + e.Buckets[dom].ToString("F1", ci);
        if (e.GcPauseMs > 0.5)
            s += ", gc " + e.GcPauseMs.ToString("F1", ci) + (e.GcTag != null ? " " + e.GcTag : "");
        if (!double.IsNaN(e.TurnDegPerSec)) s += ", " + e.TurnDegPerSec.ToString("F0", ci) + " grad/s";
        return s;
    }

    /// <summary>One line for the .komet stats text.</summary>
    public static string SummaryLine()
    {
        var ci = CultureInfo.CurrentCulture;
        var paused = CountPaused > 0 ? string.Format(ci, " | {0} im pausenmenue verworfen", CountPaused) : "";
        if (TotalHitches == 0)
            return string.Format(ci, "keine (schwelle mind. {0:F0} ms und {1:F1}x avg){2}", MinMs, Factor, paused);
        return string.Format(ci, "{0} ({1:F1}/min): {2} beim drehen, {3} in bewegung, {4} im stand, {5} mit gc-pause ({6} gen2){7}",
            TotalHitches, PerMinute, CountTurning, CountMoving, CountStill, CountGcPause, CountGen2, paused);
    }

    /// <summary>The .komet hitch report: aggregates first, then the most recent entries.</summary>
    public static string BuildReport()
    {
        var ci = CultureInfo.CurrentCulture;
        var sb = new StringBuilder(1024);
        sb.AppendFormat(ci, "ruckler seit reset: {0} ({1:F1}/min) | schwelle: mind. {2:F0} ms und {3:F1}x avg-frame\n",
            TotalHitches, PerMinute, MinMs, Factor);

        if (TotalHitches == 0)
        {
            sb.Append("nichts aufgezeichnet - normal spielen (umschauen, laufen) und erneut abrufen");
            return sb.ToString();
        }

        sb.AppendFormat(ci, "kamera: {0} beim drehen (ab {1:F0} grad/s), {2} in bewegung, {3} im stand | {4} mit gc-pause, davon {5} gen2\n",
            CountTurning, TurnThresholdDegPerSec, CountMoving, CountStill, CountGcPause, CountGen2);
        if (CountPaused > 0)
            sb.AppendFormat(ci, "pausenmenue: {0} lange frames verworfen (spiel stand, kein ruckler)\n", CountPaused);

        // ordered by count, descending - small fixed array, done the plain way
        sb.Append("dominanter bucket: ");
        var first = true;
        var order = new int[BucketCount];
        for (var i = 0; i < BucketCount; i++) order[i] = i;
        Array.Sort(order, (a, bIdx) => dominantCounts[bIdx].CompareTo(dominantCounts[a]));
        foreach (var i in order)
        {
            if (dominantCounts[i] == 0) break;
            if (!first) sb.Append(", ");
            sb.Append(BucketNames[i]).Append(' ').Append(dominantCounts[i]).Append('x');
            first = false;
        }
        sb.Append('\n');

        if (suppressedLogs > 0)
            sb.AppendFormat(ci, "({0} weitere nicht einzeln geloggt)\n", suppressedLogs);

        var show = Math.Min(ringCount, 8);
        sb.Append("letzte ").Append(show).Append(":\n");
        for (var k = 0; k < show; k++)
        {
            var idx = (ringNext - show + k + Capacity) % Capacity;
            sb.Append("  ").Append(FormatEntry(in ring[idx])).Append('\n');
        }

        // Stated, not judged. The old text told every workstation-GC session to go and enable
        // server GC; since the 65 ms gen0 pause that advice is withdrawn, and this line was the
        // last place still giving it. The verdict below is the only thing that recommends.
        sb.Append("gc-modus: ").Append(System.Runtime.GCSettings.IsServerGC ? "server" : "workstation");
        if (WorstEphemeralPauseMs >= 1.0)
            sb.AppendFormat(CultureInfo.CurrentCulture,
                ", laengste gen0/gen1-pause {0:F0} ms", WorstEphemeralPauseMs);

        var verdict = GcModeVerdict(System.Runtime.GCSettings.IsServerGC, WorstEphemeralPauseMs);
        if (verdict != null) sb.Append('\n').Append(verdict);
        return sb.ToString();
    }

    /// <summary>
    /// The longest gen0/gen1 pause seen. Ephemeral collections are stop-the-world in every GC
    /// mode - background collection only ever applies to gen2 - so this figure is pure "all
    /// threads were frozen", with nothing to hide behind.
    /// </summary>
    public static double WorstEphemeralPauseMs { get; private set; }

    /// <summary>
    /// Whether the GC mode should be changed, given the worst ephemeral pause observed.
    ///
    /// Server GC gives every core its own heap and its own GC thread, and sizes the gen0 budget
    /// for throughput on a machine that belongs to one process. On a six core desktop running a
    /// game that is the wrong trade twice: the budget grows until an ephemeral collection has
    /// tens of megabytes per heap to walk, and the dozen GC threads all want a core at the same
    /// moment the render, tesselation and cull threads do. A stop-the-world gen0 pause of tens
    /// of milliseconds - which cannot come from the collection being *concurrent*, because
    /// ephemeral collections never are - is that trade being paid in dropped frames.
    ///
    /// The threshold is deliberately high: a handful of milliseconds is normal in either mode
    /// and says nothing. Pure, so the rule is checkable without provoking a real 60 ms pause.
    /// </summary>
    internal static string GcModeVerdict(bool serverGc, double worstEphemeralMs)
    {
        if (!serverGc || worstEphemeralMs < 25.0) return null;
        return string.Format(CultureInfo.CurrentCulture,
            "  -> server-gc hat hier {0:F0} ms am stueck alle threads angehalten, in einer gen0/gen1-"
            + "sammlung.\n     die ist in JEDEM modus stop-the-world; server-gc macht sie nur seltener "
            + "und dafuer viel laenger.\n     fuer ein spiel ist workstation+concurrent die passendere "
            + "wahl: DOTNET_gcServer=0 im startskript.",
            worstEphemeralMs);
    }

    internal static int DominantCount(int bucket) => dominantCounts[bucket];

    internal static bool TryGetLast(out Entry e)
    {
        if (ringCount == 0) { e = default; return false; }
        e = ring[(ringNext - 1 + Capacity) % Capacity];
        return true;
    }

    public static void Reset()
    {
        ringCount = ringNext = 0;
        hasPending = false;
        TotalHitches = CountTurning = CountMoving = CountStill = CountGcPause = CountGen2 = CountPaused = 0;
        prevPaused = false;
        Array.Clear(dominantCounts, 0, BucketCount);
        observingSince = 0;
        WorstEphemeralPauseMs = 0;
        hasCamera = false;
        lastTurnDeg = lastMoveBlocks = 0;
        suppressedLogs = 0;
        logsInWindow = 0;
        logWindowStart = double.MinValue;
    }
}
