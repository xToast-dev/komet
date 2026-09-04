using System;
using Komet.Measure;
using Vintagestory.Client;
using Vintagestory.Server;

namespace Komet.Runtime;

/// <summary>
/// Matches how fast the integrated server hands out chunks to how fast this client can
/// actually turn them into meshes.
///
/// Measured in a fresh world at view distance 1536: 463 chunks a second arriving, 82 a second
/// tesselated. The client is not merely behind - it is behind by 5.6x, and everything the
/// server produces past the client's rate is actively harmful:
///   * the queue grows without bound (2400 and climbing) and none of it is on screen sooner,
///   * the network thread inserts arriving chunks under the same chunksLock the tesselator
///     needs for every neighbourhood it reads,
///   * and on six cores the worldgen threads take the CPU the one tesselation thread needs.
/// The same world once loaded, with worldgen idle, tesselates at 234/s and 3.9 ms per chunk
/// against 12 ms here. Producing faster than the consumer makes the consumer slower.
///
/// So the brake watches the backlog and scales ChunksColumnsToRequestPerTick, which
/// ServerSystemSendChunks re-reads on every 20 ms tick. Below the low water mark it does
/// nothing at all; past the high water mark it drops to one column per tick. Chunks are not
/// dropped or delayed in any way that matters - the client was never going to get to them.
///
/// Singleplayer only: it works because the integrated server shares this process, so the
/// backlog counter and the server's pacing static are the same memory.
/// </summary>
public static class InflowBrake
{
    public static bool Enabled;

    /// <summary>Backlog below which the server runs at full rate.</summary>
    public static int LowWater = 400;

    /// <summary>Backlog at which the normal brake segment ends.</summary>
    public static int HighWater = 2000;

    /// <summary>Brake level reached at the high water mark.</summary>
    private const double MinFactor = 0.05;

    /// <summary>
    /// Hardest brake, reached when the backlog hits DeepWaterMultiple times the high water
    /// mark. The 0.05 floor turned out not to be one: one column per 100 ms, times four for
    /// a local connection, times the ~21 chunks of a full-height mountain column, is still
    /// 850 chunks a second - measured arriving against 276/s digested, with the HUD showing
    /// the brake "fully" engaged. Past the high water mark the factor therefore keeps
    /// falling, which pushes the tick interval into its 500 ms cap: about 170 chunks a
    /// second, below what the client digests even in rough terrain, so the queue drains.
    /// </summary>
    private const double DeepMinFactor = 0.01;
    private const int DeepWaterMultiple = 3;

    /// <summary>The unbraked values, captured before the brake ever touches them.</summary>
    public static int BaseColumns = 4;
    public static int BaseTickMs = 20;

    /// <summary>What is in force right now, for the HUD.</summary>
    public static int CurrentColumns = 4;
    public static int CurrentTickMs = 20;

    /// <summary>Current throttle as a percentage of full speed - the honest single number.</summary>
    public static int CurrentPercent = 100;

    /// <summary>Seconds spent braking, so "is this thing even doing anything" has an answer.</summary>
    public static double SecondsBraking;

    public static void Capture(int baseColumns, int baseTickMs)
    {
        BaseColumns = Math.Max(1, baseColumns);
        BaseTickMs = Math.Max(1, baseTickMs);
        CurrentColumns = BaseColumns;
        CurrentTickMs = BaseTickMs;
        CurrentPercent = 100;
    }

    /// <summary>
    /// How much of full speed the current ARRIVAL rate justifies, independent of the queue.
    ///
    /// The queue-based brake alone has a blind spot that 6 worldgen threads exposed: entering
    /// a fresh region starts with a near-empty queue, so the full-rate window accepts
    /// thousands of columns before the backlog ever reaches the low water mark - and once
    /// accepted, delivery cannot be un-asked. Measured: 1648 chunks/s arriving against 275/s
    /// digested with the queue brake reading "1 %", queue at 45k. This term throttles as soon
    /// as arrivals outpace digestion by more than half, which is the project's core loading
    /// insight (inflow must roughly match digestion) applied BEFORE the damage instead of
    /// after it shows up as backlog.
    /// </summary>
    internal static double RateFactorFor(double arrivalPerSecond, double digestPerSecond)
    {
        if (arrivalPerSecond < 200) return 1.0;          // trickle - nothing worth braking
        if (digestPerSecond < 100) digestPerSecond = 100; // cold-start grace, never divide small
        return Math.Clamp(digestPerSecond * 1.5 / arrivalPerSecond, DeepMinFactor, 1.0);
    }

    /// <summary>
    /// How much of full speed the backlog justifies: 1.0 up to the low water mark, falling
    /// linearly to MinFactor at the high water mark, then on down to DeepMinFactor at
    /// DeepWaterMultiple times the high water mark - a queue that grows PAST the point of
    /// "fully braked" is proof the brake was not hard enough.
    /// </summary>
    internal static double FactorFor(int backlog, int lowWater, int highWater)
    {
        if (highWater <= lowWater) highWater = lowWater + 1;
        if (backlog <= lowWater) return 1.0;

        var deepWater = (long)highWater * DeepWaterMultiple;
        if (backlog >= deepWater) return DeepMinFactor;

        if (backlog >= highWater)
        {
            var d = (double)(backlog - highWater) / (deepWater - highWater);
            return MinFactor - d * (MinFactor - DeepMinFactor);
        }

        var t = (double)(backlog - lowWater) / (highWater - lowWater);
        return 1.0 - t * (1.0 - MinFactor);
    }

    /// <summary>
    /// Turns a factor into the two knobs that actually pace delivery.
    ///
    /// Columns per tick alone cannot brake hard enough: it bottoms out at 1, and one column
    /// per 20 ms tick - times four for a local connection, times eight chunks per column -
    /// still allows well over a thousand chunks a second. That is why a fully "braked" client
    /// was still taking 795/s. So whatever the integer column count cannot express is taken
    /// out of the tick interval instead, which ServerSystemSendChunks re-reads every tick.
    /// </summary>
    internal static void KnobsFor(double factor, int baseColumns, int baseTickMs,
                                  out int columns, out int tickMs)
    {
        if (factor > 1.0) factor = 1.0;
        if (factor < DeepMinFactor) factor = DeepMinFactor;

        columns = Math.Max(1, (int)Math.Round(baseColumns * factor));
        var achieved = (double)columns / baseColumns;   // what the column count alone gives
        tickMs = (int)Math.Round(baseTickMs * achieved / factor);
        tickMs = Math.Clamp(tickMs, baseTickMs, 500);
    }

    /// <summary>Called a couple of times a second from the client's tick listener.</summary>
    public static void Update(double dtSeconds)
    {
        if (!Enabled) return;

        var backlog = RuntimeStats.chunksAwaitingTesselation;
        var factor = Math.Min(
            FactorFor(backlog, LowWater, HighWater),
            RateFactorFor(TesselationStats.ReceivedPerSecond, TesselationStats.ChunksPerSecond));
        KnobsFor(factor, BaseColumns, BaseTickMs, out var columns, out var tickMs);

        if (factor < 1.0) SecondsBraking += dtSeconds;
        CurrentPercent = (int)Math.Round(factor * 100);

        if (columns != CurrentColumns)
        {
            CurrentColumns = columns;
            MagicNum.ChunksColumnsToRequestPerTick = columns;
        }
        if (tickMs != CurrentTickMs)
        {
            CurrentTickMs = tickMs;
            MagicNum.ChunkRequestTickTime = tickMs;
        }
    }

    public static void Release()
    {
        if (!Enabled) return;
        MagicNum.ChunksColumnsToRequestPerTick = BaseColumns;
        MagicNum.ChunkRequestTickTime = BaseTickMs;
        CurrentColumns = BaseColumns;
        CurrentTickMs = BaseTickMs;
        CurrentPercent = 100;
    }
}
