using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Komet.Runtime;

/// <summary>
/// How many physical cores this machine has - asked, not guessed.
///
/// Every thread budget in this mod derives from the core count: the cull helpers, the
/// occlusion helpers, the integrated server's worldgen threads. The first version derived it
/// from Environment.ProcessorCount with a rule of thumb ("above four hardware threads, assume
/// two-way SMT"), which is right for a 6c/12t desktop part and exactly wrong for the laptops
/// that need the budget most: a tester's i3-7100U (2 cores, 4 threads) was treated as four
/// cores and got three cull helpers, two occlusion helpers and five extra worldgen threads on
/// top of the render and tesselation threads - twelve busy threads on four hardware threads,
/// with the render thread and the collector queueing behind all of them.
///
/// The OS knows the answer: Linux exposes the package/core id of every CPU in sysfs, Windows
/// has GetLogicalProcessorInformation, macOS sysctl hw.physicalcpu. The rule of thumb stays as
/// the fallback when none of them answers, and the report names which source was used.
/// </summary>
public static class CpuTopology
{
    private static int physical;
    private static string source;

    /// <summary>Hardware threads, as the runtime sees them (respects affinity and cgroup limits).</summary>
    public static int LogicalCores => Environment.ProcessorCount;

    /// <summary>Physical cores, probed once. Never less than 1, never more than LogicalCores.</summary>
    public static int PhysicalCores
    {
        get { Probe(); return physical; }
    }

    /// <summary>"sysfs", "win32", "sysctl" or "heuristik" - what <see cref="PhysicalCores"/> came from.</summary>
    public static string Source
    {
        get { Probe(); return source; }
    }

    private static void Probe()
    {
        if (physical > 0) return;
        var logical = LogicalCores;
        int found = 0;
        string from = null;
        try
        {
            if (OperatingSystem.IsLinux()) { found = FromSysfs(logical); from = "sysfs"; }
            else if (OperatingSystem.IsWindows()) { found = FromWindows(); from = "win32"; }
            else if (OperatingSystem.IsMacOS()) { found = FromSysctl(); from = "sysctl"; }
        }
        catch (Exception)
        {
            found = 0; // an OS query that throws is the same as one that does not exist
        }

        if (found <= 0) { found = Heuristic(logical); from = "heuristik"; }
        physical = Math.Clamp(found, 1, Math.Max(1, logical));
        source = from;
    }

    /// <summary>The fallback: two-way SMT assumed above four hardware threads, none below.</summary>
    internal static int Heuristic(int logical) => logical > 4 ? logical / 2 : logical;

    // ---- Linux -----------------------------------------------------------------------

    private static int FromSysfs(int logical)
    {
        const string root = "/sys/devices/system/cpu/";
        if (!Directory.Exists(root)) return 0;
        return CountDistinctCores(logical, i =>
        {
            var dir = root + "cpu" + i + "/topology/";
            var pkg = Path.Combine(dir, "physical_package_id");
            var core = Path.Combine(dir, "core_id");
            if (!File.Exists(pkg) || !File.Exists(core)) return null;
            return (File.ReadAllText(pkg), File.ReadAllText(core));
        });
    }

    /// <summary>
    /// Distinct (package, core) pairs over the first <paramref name="logical"/> CPUs. A CPU
    /// whose topology files are missing (offline, or a container that hides sysfs) ends the
    /// count as unknown rather than guessing - the caller falls back to the heuristic then.
    /// Separated from the file reads so the harness can feed it a synthetic topology.
    /// </summary>
    internal static int CountDistinctCores(int logical, Func<int, (string package, string core)?> read)
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < logical; i++)
        {
            var t = read(i);
            if (t == null) return 0;
            seen.Add(t.Value.package.Trim() + ":" + t.Value.core.Trim());
        }
        return seen.Count;
    }

    // ---- Windows ---------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemLogicalProcessorInformation
    {
        public UIntPtr ProcessorMask;
        public int Relationship;   // 0 = RelationProcessorCore
        public long Reserved0;     // the 16-byte union, laid out as two longs
        public long Reserved1;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformation(IntPtr buffer, ref uint returnLength);

    private static int FromWindows()
    {
        uint length = 0;
        GetLogicalProcessorInformation(IntPtr.Zero, ref length);
        if (length == 0) return 0;
        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformation(buffer, ref length)) return 0;
            var size = Marshal.SizeOf<SystemLogicalProcessorInformation>();
            var cores = 0;
            for (var offset = 0; offset + size <= length; offset += size)
            {
                var entry = Marshal.PtrToStructure<SystemLogicalProcessorInformation>(buffer + offset);
                if (entry.Relationship == 0) cores++;
            }
            return cores;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ---- macOS -----------------------------------------------------------------------

    [DllImport("libc", EntryPoint = "sysctlbyname")]
    private static extern int SysctlByName(string name, out int value, ref IntPtr size, IntPtr newp, IntPtr newlen);

    private static int FromSysctl()
    {
        var size = (IntPtr)sizeof(int);
        return SysctlByName("hw.physicalcpu", out var value, ref size, IntPtr.Zero, IntPtr.Zero) == 0 ? value : 0;
    }
}
