using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Komet.Runtime;

/// <summary>
/// An automated in-game measurement run: flips one system at a time, samples every frame,
/// and prints a comparison table. Before this, answering "what does each patch buy on THIS
/// machine in THIS scene" meant toggling by hand, counting seconds and reading screenshots.
///
/// The design is interleaved, not block-by-block, because the first field run proved blocks
/// wrong: measured while flying over fresh terrain, the frame time drifted from 4.3 ms to
/// 9.4 ms across the run, and every phase's "delta" was mostly its POSITION in that climb -
/// the glGetError phase read +8.6 ms for two quads. So the schedule is now
/// baseline, system, baseline, system, ... repeated for several rounds, and each test
/// slice is scored against the MEAN OF ITS TWO NEIGHBOURING BASELINE SLICES. A linear
/// drift cancels exactly in that pairing; a spike lands in one slice of one round instead
/// of poisoning a whole system's only phase, and the per-round spread in the report shows
/// how much to trust each number.
///
/// Whatever happens - finished, aborted, world left - the active system's Exit runs, so
/// the session always returns to exactly the configured state. Driven from the frame
/// boundary on the main thread; the state machine takes timestamps as parameters so the
/// sequencing and the drift-cancelling arithmetic are testable without a game.
/// </summary>
public static class StressTest
{
    /// <summary>One switchable system: a label, a switch to flip, and how to flip it back.</summary>
    public sealed class Phase
    {
        public string Name;
        public Action Enter;
        public Action Exit;
    }

    internal sealed class Slice
    {
        public int System;          // -1 = baseline
        public int Frames;
        public double SumMs, WorstMs;

        /// <summary>Swap and shadow-stage time over the same frames, for the same drift-cancelled
        /// arithmetic. A system that moves the frame without moving either is CPU work somewhere
        /// else; one that moves only swap is driver or GPU back-pressure.</summary>
        public double SumSwapMs, SumShadowMs;

        public double AvgMs => Frames > 0 ? SumMs / Frames : 0;
        public double AvgSwapMs => Frames > 0 ? SumSwapMs / Frames : 0;
        public double AvgShadowMs => Frames > 0 ? SumShadowMs / Frames : 0;
    }

    /// <summary>Frames dropped at each slice start while toggled state settles.</summary>
    private const int SettleFrames = 12;

    public static bool Running { get; private set; }

    /// <summary>One line for the HUD while the test runs, null otherwise.</summary>
    public static string StatusLine { get; private set; }

    private static List<Phase> systems;
    private static int[] schedule;              // per slice: system index or -1
    private static List<Slice> slices;
    private static int sliceIndex;
    private static int settleLeft;
    private static long sliceEndsAt, lastFrameTs;
    private static double secondsPerSlice;
    private static int rounds;
    private static Action<string> report;

    public static string Start(List<Phase> plan, double secPerSlice, int roundCount, Action<string> reportSink)
    {
        if (Running) return "a stress test is already running - '.komet stress stop' aborts it.";
        if (plan == null || plan.Count == 0) return "no systems defined";

        systems = plan;
        secondsPerSlice = Math.Clamp(secPerSlice, 1, 30);
        rounds = Math.Clamp(roundCount, 1, 10);
        report = reportSink;
        schedule = BuildSchedule(plan.Count, rounds);
        slices = new List<Slice>(schedule.Length);
        sliceIndex = -1;
        lastFrameTs = 0;
        Running = true;

        var total = schedule.Length * secondsPerSlice;
        return $"stress test started: {plan.Count} systems x {rounds} rounds, interleaved with baselines "
             + $"({schedule.Length} Scheiben a {secondsPerSlice:0.#}s = ~{total:0}s). Bewegung ist ok - "
             + "drift is cancelled out by the neighbouring baselines. '.komet stress stop' aborts.";
    }

    /// <summary>B, S1, B, S2, ... per round, one closing baseline - every test slice ends up
    /// with a baseline on BOTH sides, which is what cancels drift.</summary>
    internal static int[] BuildSchedule(int systemCount, int roundCount)
    {
        var plan = new List<int>(roundCount * systemCount * 2 + 1);
        for (var r = 0; r < roundCount; r++)
        {
            for (var s = 0; s < systemCount; s++)
            {
                plan.Add(-1);
                plan.Add(s);
            }
        }
        plan.Add(-1);
        return plan.ToArray();
    }

    public static string Stop(string reason)
    {
        if (!Running) return "no stress test running";
        var current = CurrentSystem();
        if (current >= 0) SafeExit(systems[current]);
        Running = false;
        StatusLine = null;
        systems = null;
        slices = null;
        schedule = null;
        return "Stresstest abgebrochen (" + reason + ") - every system back to its configured state.";
    }

    private static int CurrentSystem()
        => sliceIndex >= 0 && schedule != null && sliceIndex < schedule.Length ? schedule[sliceIndex] : -1;

    /// <summary>Hooked to the frame boundary; the real clock and the frame's raw split go in here.</summary>
    public static void OnFrameBoundary()
        => Tick(Stopwatch.GetTimestamp(), Measure.FrameStats.LastSwapMs, Measure.FrameStats.LastShadowMs);

    /// <summary>The whole state machine, clock injected for the tests.</summary>
    internal static void Tick(long now) => Tick(now, 0, 0);

    internal static void Tick(long now, double swapMs, double shadowMs)
    {
        if (!Running) return;

        var frameMs = lastFrameTs != 0 ? (now - lastFrameTs) * 1000.0 / Stopwatch.Frequency : 0;
        lastFrameTs = now;

        if (sliceIndex < 0 || now >= sliceEndsAt)
        {
            var leaving = CurrentSystem();
            if (leaving >= 0) SafeExit(systems[leaving]);

            sliceIndex++;
            if (sliceIndex >= schedule.Length)
            {
                Running = false;
                StatusLine = null;
                var table = BuildReport(slices, schedule, systems);
                systems = null;
                slices = null;
                schedule = null;
                report?.Invoke(table);
                return;
            }

            var entering = schedule[sliceIndex];
            if (entering >= 0)
            {
                try { systems[entering].Enter?.Invoke(); }
                catch (Exception) { Stop("System '" + systems[entering].Name + "' could not be enabled"); return; }
            }

            slices.Add(new Slice { System = entering });
            settleLeft = SettleFrames;
            sliceEndsAt = now + (long)(secondsPerSlice * Stopwatch.Frequency);
            var round = sliceIndex / (systems.Count * 2) + 1;
            StatusLine = entering < 0
                ? $"runde {Math.Min(round, rounds)}/{rounds}: baseline"
                : $"runde {round}/{rounds}: {systems[entering].Name}";
            return;
        }

        if (settleLeft > 0) { settleLeft--; return; }
        if (frameMs <= 0) return;

        var s = slices[sliceIndex];
        s.Frames++;
        s.SumMs += frameMs;
        s.SumSwapMs += swapMs;
        s.SumShadowMs += shadowMs;
        if (frameMs > s.WorstMs) s.WorstMs = frameMs;
    }

    private static void SafeExit(Phase phase)
    {
        try { phase.Exit?.Invoke(); }
        catch (Exception) { /* restoring must never cascade */ }
    }

    /// <summary>
    /// Per system: the mean over all rounds of (test slice minus the mean of its two
    /// neighbouring baselines), plus the spread between rounds - the honest error bar.
    /// </summary>
    internal static string BuildReport(List<Slice> done, int[] plan, List<Phase> sys)
    {
        var ci = CultureInfo.CurrentCulture;
        var sb = new StringBuilder(1024);

        double firstBase = -1, lastBase = 0, baseSum = 0;
        var baseCount = 0;
        foreach (var s in done)
        {
            if (s.System != -1 || s.Frames == 0) continue;
            if (firstBase < 0) firstBase = s.AvgMs;
            lastBase = s.AvgMs;
            baseSum += s.AvgMs;
            baseCount++;
        }
        var baseMean = baseCount > 0 ? baseSum / baseCount : 0;

        sb.AppendFormat(ci, "stress test finished - baseline averaging {0:F2} ms ({1:F0} fps)",
            baseMean, baseMean > 0 ? 1000 / baseMean : 0);
        if (firstBase > 0 && Math.Abs(lastBase - firstBase) > baseMean * 0.15)
            // no angle brackets in chat-bound text: the game chat parses VTML markup, and a
            // single stray 'greater than' derails its parser into repeating error spam
            sb.AppendFormat(ci, ", the scene drifted from {0:F1} to {1:F1} ms (cancelled out by the neighbouring baselines)",
                firstBase, lastBase);
        sb.Append('\n');

        for (var sysIdx = 0; sysIdx < sys.Count; sysIdx++)
        {
            double sum = 0, swapSum = 0, shadowSum = 0;
            double min = double.MaxValue, max = double.MinValue, worst = 0;
            var n = 0;
            for (var i = 0; i < done.Count && i < plan.Length; i++)
            {
                if (plan[i] != sysIdx || done[i].Frames == 0) continue;
                if (i - 1 < 0 || i + 1 >= done.Count) continue;
                if (done[i - 1].Frames == 0 || done[i + 1].Frames == 0) continue;

                var local = (done[i - 1].AvgMs + done[i + 1].AvgMs) / 2;
                var d = done[i].AvgMs - local;
                sum += d;
                swapSum += done[i].AvgSwapMs - (done[i - 1].AvgSwapMs + done[i + 1].AvgSwapMs) / 2;
                shadowSum += done[i].AvgShadowMs - (done[i - 1].AvgShadowMs + done[i + 1].AvgShadowMs) / 2;
                if (d < min) min = d;
                if (d > max) max = d;
                if (done[i].WorstMs > worst) worst = done[i].WorstMs;
                n++;
            }

            if (n == 0)
            {
                sb.Append(sys[sysIdx].Name).Append(": no usable slice\n");
                continue;
            }

            var mean = sum / n;
            var spread = (max - min) / 2;
            sb.AppendFormat(ci, "{0}: delta {1}{2:F2} ms", sys[sysIdx].Name, mean >= 0 ? "+" : "", mean);
            if (n > 1) sb.AppendFormat(ci, " (+-{0:F2} over {1} rounds)", spread, n);
            sb.AppendFormat(ci, " [swap {0}{1:F2}, shadow {2}{3:F2}]",
                swapSum / n >= 0 ? "+" : "", swapSum / n, shadowSum / n >= 0 ? "+" : "", shadowSum / n);
            sb.AppendFormat(ci, ", worst {0:F1}\n", worst);
        }

        sb.Append("reading: a positive delta on 'X off' = that is what X saves here; on 'X on' = that is what X costs.\n");
        sb.Append("deltas are computed per round against the two NEIGHBOURING baselines - drift cancels out;\n");
        sb.Append("+- is the half spread between the rounds: deltas smaller than their +- are noise.\n");
        sb.Append("swap/shadow in square brackets split the delta: swap = driver or GPU back-pressure,\n");
        sb.Append("shadow = the two shadow stages on the CPU. rest = CPU elsewhere.");
        return sb.ToString();
    }
}
