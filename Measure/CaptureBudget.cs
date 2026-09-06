using System.Diagnostics;
using System.Threading;

namespace Komet.Measure;

/// <summary>
/// The cost ceiling the two "who did this?" samplers share - dirty marks
/// (RetessSourcePatches) and single-block packets (PacketSourcePatches).
///
/// A capture resolves method metadata for the frames it walks, which is tens of microseconds,
/// and both events arrive in the thousands per second while chunks stream. Every Nth call is a
/// candidate, and this bucket caps what a storm can cost on top: 25/s x tens of microseconds is
/// one or two milliseconds a second, spread over the threads that raised the events. Within one
/// second heavy sources still dominate the ranking; across mixed phases the shares are
/// time-weighted, so a specific question wants a reset in the scene it is about.
/// </summary>
internal sealed class CaptureBudget(int perSecond)
{
    private long startTicks;
    private int taken;

    /// <summary>
    /// One capture token, at most <c>perSecond</c> per rolling second. Races on the second
    /// boundary cost at worst a few extra captures - this is a cost ceiling, not a contract.
    /// Takes the clock as a parameter so the rule is testable.
    /// </summary>
    internal bool Allows(long nowTicks)
    {
        var start = Interlocked.Read(ref startTicks);
        if (nowTicks - start >= Stopwatch.Frequency
            && Interlocked.CompareExchange(ref startTicks, nowTicks, start) == start)
            Interlocked.Exchange(ref taken, 0);
        return Interlocked.Increment(ref taken) <= perSecond;
    }

    internal void Reset()
    {
        Interlocked.Exchange(ref startTicks, 0);
        Interlocked.Exchange(ref taken, 0);
    }
}
