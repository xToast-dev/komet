using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Komet.AllocTool;

/// <summary>
/// Reads the allocation ticks of a .nettrace and ranks where the bytes came from.
///
/// The runtime raises GCAllocationTick roughly every 100 KB of allocation, with the type of
/// the object that crossed the line, the thread, and - in an EventPipe file - the stack. So
/// the bytes are a sample: a site that allocates 40 % of the bytes wins about 40 % of the
/// ticks. The report groups the sampled bytes by thread, by type, by the innermost frame
/// that is not the runtime's own (Array.Resize, List.Add and friends are how a site
/// allocates, not where), and prints the most frequent stacks under the top types.
/// </summary>
public static class Program
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "selftest") return SelfTest() ? 0 : 1;
        if (args.Length >= 2 && args[0] == "report")
        {
            var top = 25;
            for (var i = 2; i + 1 < args.Length; i++) if (args[i] == "-top") top = int.Parse(args[i + 1], Ci);
            Console.Write(Report(args[1], top));
            return 0;
        }
        Console.Error.WriteLine("usage: report <file.nettrace> [-top N] | selftest");
        return 2;
    }

    private sealed class Site
    {
        public long Bytes;
        public long Ticks;
        public readonly Dictionary<string, long> Types = new();
    }

    public static string Report(string path, int top)
    {
        var threadNames = ThreadNames(path + ".threads.txt");
        var byThread = new Dictionary<int, long>();
        var byType = new Dictionary<string, long>();
        var bySite = new Dictionary<string, Site>();
        var stacksByType = new Dictionary<string, Dictionary<string, long>>();
        long ticks = 0, bytes = 0, noStack = 0;
        double first = double.MaxValue, last = double.MinValue;

        // a .nettrace is converted with its rundown into an .etlx next to it (that is where
        // the stacks get their method names); OpenOrConvert would take it for an ETW file
        var etlx = path.EndsWith(".etlx", StringComparison.OrdinalIgnoreCase) ? path : TraceLog.CreateFromEventPipeDataFile(path);
        using (var log = new TraceLog(etlx))
        {
            var source = log.Events.GetSource();
            source.Clr.GCAllocationTick += (GCAllocationTickTraceData d) =>
            {
                var amount = d.AllocationAmount64 > 0 ? d.AllocationAmount64 : d.AllocationAmount;
                ticks++;
                bytes += amount;
                first = Math.Min(first, d.TimeStampRelativeMSec);
                last = Math.Max(last, d.TimeStampRelativeMSec);
                byThread[d.ThreadID] = byThread.GetValueOrDefault(d.ThreadID) + amount;
                var type = string.IsNullOrEmpty(d.TypeName) ? "(unknown)" : d.TypeName;
                byType[type] = byType.GetValueOrDefault(type) + amount;

                var frames = Frames(d.CallStack());
                if (frames.Count == 0) { noStack += amount; return; }
                var site = InnermostOwn(frames);
                if (!bySite.TryGetValue(site, out var s)) bySite[site] = s = new Site();
                s.Bytes += amount;
                s.Ticks++;
                s.Types[type] = s.Types.GetValueOrDefault(type) + amount;

                if (!stacksByType.TryGetValue(type, out var stacks)) stacksByType[type] = stacks = new Dictionary<string, long>();
                var key = string.Join(" <- ", frames.Take(7));
                stacks[key] = stacks.GetValueOrDefault(key) + amount;
            };
            source.Process();
        }

        var sb = new StringBuilder();
        var seconds = ticks > 0 ? Math.Max(0.001, (last - first) / 1000.0) : 0;
        sb.AppendFormat(Ci, "allocation trace: {0}\n", path);
        if (ticks == 0)
        {
            sb.Append("  no allocation ticks in the file - was the GC keyword at verbose level?\n");
            return sb.ToString();
        }
        sb.AppendFormat(Ci, "  {0:F1} s | {1:N0} ticks | {2:F0} MB sampled | {3:F1} MB/s | {4:P0} without a stack\n\n",
            seconds, ticks, bytes / 1048576.0, bytes / 1048576.0 / seconds, bytes > 0 ? noStack / (double)bytes : 0);

        sb.Append("by thread:\n");
        foreach (var (tid, b) in byThread.OrderByDescending(kv => kv.Value).Take(top))
            sb.AppendFormat(Ci, "  {0,5:P0}  {1,7:F1} MB/s  {2} ({3})\n", b / (double)bytes, b / 1048576.0 / seconds,
                threadNames.GetValueOrDefault(tid, "?"), tid);

        sb.Append("\nby type:\n");
        foreach (var (type, b) in byType.OrderByDescending(kv => kv.Value).Take(top))
            sb.AppendFormat(Ci, "  {0,5:P0}  {1,7:F1} MB/s  {2}\n", b / (double)bytes, b / 1048576.0 / seconds, type);

        sb.Append("\nby site (innermost frame outside the runtime):\n");
        foreach (var (site, s) in bySite.OrderByDescending(kv => kv.Value.Bytes).Take(top))
        {
            var types = string.Join(", ", s.Types.OrderByDescending(kv => kv.Value).Take(3)
                .Select(kv => string.Format(Ci, "{0} {1:P0}", kv.Key, kv.Value / (double)s.Bytes)));
            sb.AppendFormat(Ci, "  {0,5:P0}  {1,7:F1} MB/s  {2}  [{3}]\n", s.Bytes / (double)bytes, s.Bytes / 1048576.0 / seconds, site, types);
        }

        sb.Append("\nstacks under the top types (innermost first):\n");
        foreach (var (type, b) in byType.OrderByDescending(kv => kv.Value).Take(Math.Min(top, 12)))
        {
            sb.AppendFormat(Ci, "  {0} ({1:P0})\n", type, b / (double)bytes);
            if (!stacksByType.TryGetValue(type, out var stacks)) continue;
            foreach (var (stack, sb2) in stacks.OrderByDescending(kv => kv.Value).Take(4))
                sb.AppendFormat(Ci, "    {0,5:P0}  {1}\n", sb2 / (double)bytes, stack);
        }
        return sb.ToString();
    }

    /// <summary>Method names from the allocation outwards, arguments stripped, unresolved frames dropped.</summary>
    private static List<string> Frames(TraceCallStack stack)
    {
        var frames = new List<string>();
        var guard = 0;
        while (stack != null && guard++ < 64)
        {
            var name = stack.CodeAddress?.FullMethodName;
            if (!string.IsNullOrEmpty(name))
            {
                var paren = name.IndexOf('(');
                frames.Add(paren > 0 ? name.Substring(0, paren) : name);
            }
            stack = stack.Caller;
        }
        return frames;
    }

    private static bool IsRuntime(string frame)
        => frame.StartsWith("System.", StringComparison.Ordinal)
           || frame.StartsWith("Microsoft.", StringComparison.Ordinal)
           || frame.StartsWith("Internal.", StringComparison.Ordinal)
           || frame.StartsWith("ILStubClass", StringComparison.Ordinal)
           || frame.StartsWith("DynamicClass", StringComparison.Ordinal);

    private static string InnermostOwn(List<string> frames)
    {
        foreach (var f in frames) if (!IsRuntime(f)) return f;
        return frames[0];
    }

    private static Dictionary<int, string> ThreadNames(string sidecar)
    {
        var names = new Dictionary<int, string>();
        try
        {
            if (!File.Exists(sidecar)) return names;
            foreach (var line in File.ReadAllLines(sidecar))
            {
                var sp = line.IndexOf(' ');
                if (sp <= 0) continue;
                if (int.TryParse(line.AsSpan(0, sp), NumberStyles.Integer, Ci, out var tid)) names[tid] = line.Substring(sp + 1);
            }
        }
        catch (Exception) { /* names are a convenience */ }
        return names;
    }

    // ---- self test: the whole pipeline on this process, a site of known name ----

    private static int[][] keep = new int[64][];

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void KometAllocChurn(int seconds)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        var i = 0;
        while (DateTime.UtcNow < end)
        {
            keep[i++ & 63] = new int[2048];
            if ((i & 1023) == 0) Thread.Sleep(1);
        }
    }

    private static bool SelfTest()
    {
        var path = Path.Combine(Path.GetTempPath(), "komet-alloctool-selftest-" + Environment.ProcessId + ".nettrace");
        try
        {
            var client = new DiagnosticsClient(Environment.ProcessId);
            var providers = new List<EventPipeProvider> { new("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose, 0x1) };
            using (var session = client.StartEventPipeSession(providers, requestRundown: true, circularBufferMB: 64))
            using (var file = File.Create(path))
            {
                var copy = Task.Run(() => session.EventStream.CopyTo(file));
                KometAllocChurn(3);
                session.Stop();
                copy.Wait(TimeSpan.FromSeconds(30));
            }
            var report = Report(path, 10);
            Console.Write(report);
            var ok = report.Contains("KometAllocChurn") && report.Contains("Int32[]");
            Console.WriteLine(ok ? "\nselftest ok: the churn site is named under Int32[]" : "\nselftest FAILED: the churn site is not in the report");
            return ok;
        }
        catch (Exception e)
        {
            Console.WriteLine("selftest FAILED: " + e);
            return false;
        }
        finally
        {
            try { File.Delete(path); File.Delete(Path.ChangeExtension(path, ".etlx")); } catch (Exception) { }
        }
    }
}
