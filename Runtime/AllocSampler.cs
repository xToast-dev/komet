using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Komet.Runtime;

/// <summary>
/// Names every allocating thread in the process, and the types they allocate - without a
/// patch on any of them.
///
/// The bracket-based attribution (ClientAllocPatches, ServerAllocPatches, the tesselation
/// and network brackets) can only book what it has a bracket around, and the 03.09. report
/// still carried "rest 46 MB/s (rest = ungemessen)" after the client's eight worker threads
/// and the integrated server's threads were all bracketed. Whatever is left is on threads
/// nobody thought to patch: the runtime's own pool, another mod's workers, this mod's own
/// culling threads. A bracket per suspect does not scale to "everything".
///
/// The runtime already samples its own allocations: with the GC keyword enabled at verbose
/// level, the CLR raises a GCAllocationTick event roughly every 100 KB of allocation,
/// carrying the size and the type of the object that crossed the threshold and the OS thread
/// it happened on. An in-process EventListener receives those; at 200 MB/s that is two
/// thousand events a second, each a dictionary lookup - and no patch, no bracket, no
/// engine method touched. The thread id becomes a name through what the OS knows about the
/// thread (Linux: /proc/self/task/tid/comm, which .NET fills from Thread.Name, cut to 15
/// characters; Windows: GetThreadDescription), so the line reads "tess 27, netz 21,
/// chunkdbthread 26, komet-cull 9" rather than four numbers.
///
/// It is a sample, and the report says so: the per-thread figures add up to the allocation
/// rate within the sampling noise, and the type split is the same 100 KB lottery - a type
/// that allocates 40 % of the bytes wins about 40 % of the tickets. Good enough to answer
/// "whose garbage is this" to the nearest thread and array type, which is the question.
/// </summary>
public sealed class AllocSampler : EventListener
{
    private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";
    private const EventKeywords GcKeyword = (EventKeywords)0x1;

    public sealed class Bucket
    {
        public string Name;
        public long Bytes;
        public long Samples;
        internal long seenBytes;
        public double MbPerSecond;
    }

    public static bool Enabled { get; private set; }
    public static long Samples { get; private set; }
    public static long SampledBytes { get; private set; }
    /// <summary>Why the listener is not running, if it is not - for the report.</summary>
    public static string Failure { get; private set; }

    private static readonly ConcurrentDictionary<long, Bucket> ByThread = new();
    private static readonly ConcurrentDictionary<string, Bucket> ByType = new();
    private static readonly ConcurrentDictionary<long, string> ThreadNames = new();
    private static long mainTid;
    private static AllocSampler instance;
    private static long lastSampleTs;
    private const double Alpha = 0.4;
    private const int MaxTypes = 256;
    private const string OverflowType = "(andere)";

    // ---- lifecycle ----------------------------------------------------------------------

    /// <summary>Call from the main thread: the calling thread is what "main" will mean.</summary>
    public static void Start()
    {
        if (instance != null) return;
        try
        {
            mainTid = CurrentOsThreadId();
            instance = new AllocSampler();
            Enabled = true;
            Failure = null;
        }
        catch (Exception e)
        {
            Failure = e.GetType().Name + ": " + e.Message;
            Enabled = false;
        }
    }

    public static void Stop()
    {
        var l = instance;
        instance = null;
        Enabled = false;
        try { l?.Dispose(); } catch (Exception) { /* nothing to salvage */ }
    }

    private AllocSampler() { }

    protected override void OnEventSourceCreated(EventSource source)
    {
        // runs from the base constructor for every source that already exists, and later
        // for new ones - static state only in here
        if (source?.Name == RuntimeProvider)
        {
            try { EnableEvents(source, EventLevel.Verbose, GcKeyword); }
            catch (Exception e) { Failure = e.GetType().Name + ": " + e.Message; }
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        var name = e.EventName;
        if (name == null || !name.StartsWith("GCAllocationTick", StringComparison.Ordinal)) return;
        var names = e.PayloadNames;
        var payload = e.Payload;
        if (names == null || payload == null) return;

        long amount = 0;
        string type = null;
        for (var i = 0; i < names.Count && i < payload.Count; i++)
        {
            switch (names[i])
            {
                case "AllocationAmount64": amount = ToLong(payload[i]); break;
                case "AllocationAmount": if (amount == 0) amount = ToLong(payload[i]); break;
                case "TypeName": type = payload[i] as string; break;
            }
        }
        if (amount <= 0) return;
        Book(e.OSThreadId, type, amount);
    }

    private static long ToLong(object o) => o switch
    {
        long l => l,
        ulong u => (long)Math.Min(u, long.MaxValue),
        int i => i,
        uint ui => ui,
        _ => 0,
    };

    /// <summary>One sample: bytes to the thread's bucket and the type's bucket.</summary>
    internal static void Book(long osThreadId, string typeName, long bytes)
    {
        Samples++;
        SampledBytes += bytes;
        var t = ByThread.GetOrAdd(osThreadId, static id => new Bucket { Name = null });
        Interlocked.Add(ref t.Bytes, bytes);
        Interlocked.Increment(ref t.Samples);
        if (t.Name == null) t.Name = ThreadLabel(osThreadId);

        var key = ShortType(typeName);
        if (!ByType.TryGetValue(key, out var ty))
        {
            if (ByType.Count >= MaxTypes) key = OverflowType;
            ty = ByType.GetOrAdd(key, static k => new Bucket { Name = k });
        }
        Interlocked.Add(ref ty.Bytes, bytes);
        Interlocked.Increment(ref ty.Samples);
    }

    // ---- rates ----------------------------------------------------------------------------

    /// <summary>Hooked on FrameStats.PeriodicSample; the interval is measured, not assumed.</summary>
    public static void Sample()
    {
        var now = Stopwatch.GetTimestamp();
        if (lastSampleTs == 0) { lastSampleTs = now; return; }
        var dt = (now - lastSampleTs) / (double)Stopwatch.Frequency;
        if (dt < 0.2) return;
        lastSampleTs = now;
        Sample(dt);
    }

    internal static void Sample(double dtSeconds)
    {
        foreach (var b in ByThread.Values) Fold(b, dtSeconds);
        foreach (var b in ByType.Values) Fold(b, dtSeconds);
    }

    private static void Fold(Bucket b, double dt)
    {
        var bytes = Interlocked.Read(ref b.Bytes);
        var rate = (bytes - b.seenBytes) / dt / 1048576.0;
        b.seenBytes = bytes;
        b.MbPerSecond += (rate - b.MbPerSecond) * Alpha;
    }

    public static double ThreadMbPerSecond
    {
        get { double s = 0; foreach (var b in ByThread.Values) s += b.MbPerSecond; return s; }
    }

    /// <summary>Bytes booked so far under a thread label (the harness's question).</summary>
    public static long BytesForThread(string label)
    {
        long s = 0;
        foreach (var b in ByThread.Values) if (b.Name == label) s += Interlocked.Read(ref b.Bytes);
        return s;
    }

    /// <summary>Every thread seen, for the harness's failure message.</summary>
    internal static List<(long tid, string name, long bytes)> Threads()
    {
        var list = new List<(long, string, long)>();
        foreach (var kv in ByThread) list.Add((kv.Key, kv.Value.Name, Interlocked.Read(ref kv.Value.Bytes)));
        list.Sort((x, y) => y.Item3.CompareTo(x.Item3));
        return list;
    }

    public static long BytesForType(string shortType)
        => ByType.TryGetValue(shortType, out var b) ? Interlocked.Read(ref b.Bytes) : 0;

    // ---- report ---------------------------------------------------------------------------

    /// <summary>The two ranked lists, one line. Threads and types under 0,5 MB/s are noise.</summary>
    public static void Write(StringBuilder sb, CultureInfo ci)
    {
        sb.AppendFormat(ci, "  alloc-stichprobe ({0:N0} proben a ~100 KB): threads ", Samples);
        AppendRanked(sb, ci, ByThread.Values, 10, "MB/s");
        sb.Append(" | typen ");
        AppendRanked(sb, ci, ByType.Values, 8, "MB/s");
        if (!Enabled) sb.Append(" (OFF)");
        sb.Append('\n');
    }

    private static void AppendRanked(StringBuilder sb, CultureInfo ci, IEnumerable<Bucket> buckets, int max, string unit)
    {
        var list = new List<Bucket>();
        foreach (var b in buckets) if (b.MbPerSecond >= 0.5) list.Add(b);
        list.Sort((x, y) => y.MbPerSecond.CompareTo(x.MbPerSecond));
        if (list.Count == 0) { sb.Append("unter 0,5 MB/s"); return; }
        var shown = Math.Min(list.Count, max);
        for (var i = 0; i < shown; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.AppendFormat(ci, "{0} {1:F0}", list[i].Name ?? "?", list[i].MbPerSecond);
        }
        if (list.Count > shown) sb.Append(", ...");
        sb.Append(' ').Append(unit);
    }

    public static void ResetStats()
    {
        Samples = 0;
        SampledBytes = 0;
        foreach (var b in ByThread.Values) { Interlocked.Exchange(ref b.Bytes, 0); Interlocked.Exchange(ref b.Samples, 0); b.seenBytes = 0; }
        foreach (var b in ByType.Values) { Interlocked.Exchange(ref b.Bytes, 0); Interlocked.Exchange(ref b.Samples, 0); b.seenBytes = 0; }
    }

    /// <summary>World left: thread ids get reused, names would go stale.</summary>
    public static void Clear()
    {
        ByThread.Clear();
        ByType.Clear();
        ThreadNames.Clear();
        Samples = 0;
        SampledBytes = 0;
        lastSampleTs = 0;
    }

    // ---- naming ---------------------------------------------------------------------------

    private static readonly char[] GenericOrArray = ['`', '['];

    /// <summary>"System.Int32[]" -> "Int32[]", "List`1[...]" -> "List`1[...]".</summary>
    internal static string ShortType(string typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return "?";
        var end = typeName.IndexOfAny(GenericOrArray);
        var head = end < 0 ? typeName : typeName.Substring(0, end);
        var dot = head.LastIndexOf('.');
        return dot < 0 ? typeName : typeName.Substring(dot + 1);
    }

    private static string ThreadLabel(long tid)
    {
        if (ThreadNames.TryGetValue(tid, out var known)) return known;
        var label = tid == mainTid && mainTid != 0 ? "main" : Label(OsThreadName(tid), tid);
        ThreadNames[tid] = label;
        return label;
    }

    /// <summary>
    /// The engine's thread names, as the OS reports them (Linux truncates to 15 characters),
    /// mapped to the labels the other attribution lines use, so "tess" means the same thread
    /// in every row. Unknown names pass through; a numbered worker loses its number.
    /// </summary>
    internal static string Label(string osName, long tid)
    {
        if (string.IsNullOrEmpty(osName)) return "#" + tid.ToString(CultureInfo.InvariantCulture);
        var n = osName;
        if (Starts(n, "tesselateterrain")) return "tess";
        if (Starts(n, "networkproc")) return "netz";
        if (Starts(n, "compresschunks")) return "compress";
        if (Starts(n, "chunkculling")) return "chunkcull";
        if (Starts(n, "chunkvis")) return "chunkvis";
        if (Starts(n, "blockticking")) return "blockticks";
        if (Starts(n, "asyncparticles")) return "particles";
        if (Starts(n, "relight")) return "relight";
        if (Starts(n, ".NET TP Worker") || Starts(n, ".NET ThreadPool")) return "tp-worker";
        if (Starts(n, ".NET Tiered")) return "jit";
        if (Starts(n, ".NET BGC") || Starts(n, ".NET Background")) return "gc";
        if (Starts(n, ".NET EventPipe")) return "eventpipe";
        if (Starts(n, ".NET Finalizer")) return "finalizer";
        // this mod's own workers: "komet-cull-3" -> "komet-cull"
        var dash = n.LastIndexOf('-');
        if (dash > 0 && dash < n.Length - 1 && IsDigits(n, dash + 1)) n = n.Substring(0, dash);
        return n;
    }

    private static bool Starts(string s, string prefix)
    {
        // the OS name may be the truncated form of the prefix, or the prefix may be longer
        // than what the OS kept - either direction counts
        var len = Math.Min(s.Length, Math.Min(prefix.Length, 15));
        return len > 0 && string.CompareOrdinal(s, 0, prefix, 0, len) == 0;
    }

    private static bool IsDigits(string s, int from)
    {
        for (var i = from; i < s.Length; i++) if (!char.IsDigit(s[i])) return false;
        return true;
    }

    private static string OsThreadName(long tid)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var p = "/proc/self/task/" + tid.ToString(CultureInfo.InvariantCulture) + "/comm";
                return File.Exists(p) ? File.ReadAllText(p).Trim() : null;
            }
            if (OperatingSystem.IsWindows()) return WindowsThreadName((uint)tid);
        }
        catch (Exception) { /* name stays the id */ }
        return null;
    }

    // ---- os thread ids ---------------------------------------------------------------------

    [DllImport("libc", EntryPoint = "gettid")]
    private static extern int LinuxGetTid();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint desiredAccess, bool inheritHandle, uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int GetThreadDescription(IntPtr thread, out IntPtr description);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    internal static long CurrentOsThreadId()
    {
        try
        {
            if (OperatingSystem.IsLinux()) return LinuxGetTid();
            if (OperatingSystem.IsWindows()) return GetCurrentThreadId();
        }
        catch (Exception) { /* older libc without gettid: no main label, everything else works */ }
        return 0;
    }

    private static string WindowsThreadName(uint tid)
    {
        const uint QueryLimited = 0x0040;
        var h = OpenThread(QueryLimited, false, tid);
        if (h == IntPtr.Zero) return null;
        try
        {
            if (GetThreadDescription(h, out var desc) < 0 || desc == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(desc); }
            finally { LocalFree(desc); }
        }
        finally { CloseHandle(h); }
    }
}
