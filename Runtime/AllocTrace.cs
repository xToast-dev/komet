using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.NETCore.Client;

namespace Komet.Runtime;

/// <summary>
/// Records where the process allocates, with call stacks, into a .nettrace file - the
/// runtime's own EventPipe, attached to this very process.
///
/// The in-process sampler (<see cref="AllocSampler"/>) answers "which thread, which type";
/// the report has been saying "Int32[] on the tesselation thread, Vec3d on the server thread"
/// for days, and that is where an in-process listener ends: the runtime delivers its
/// allocation ticks on a dispatcher thread, without the stack that allocated. EventPipe
/// captures the stack with every event. The diagnostics port a .NET process opens for
/// dotnet-trace accepts a session from the process itself, so the game records itself for a
/// stated number of seconds and writes the file next to its logs; the repository's
/// <c>alloctool</c> turns it into allocation sites - method names, from the rundown the
/// session requests when it stops - ranked by bytes.
///
/// Costs while recording: the GC keyword at verbose level, which the sampler already keeps
/// on, plus a stack walk per tick (one per ~100 KB allocated) and the file write on a
/// background thread. A few percent for the duration, nothing after. The OS thread ids in
/// the file are named through a sidecar the recording writes from /proc, the same way the
/// sampler names its threads.
/// </summary>
public static class AllocTrace
{
    private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";
    private const long GcKeyword = 0x1;

    public static bool Running { get; private set; }
    public static string LastFile { get; private set; }
    public static string LastError { get; private set; }
    public static long LastBytes { get; private set; }

    /// <summary>
    /// Starts a recording of <paramref name="seconds"/> into <paramref name="directory"/>.
    /// Returns the file it will write, or null with <see cref="LastError"/> set. Calls
    /// <paramref name="done"/> from the recording thread with a one-line result.
    /// </summary>
    public static string Start(int seconds, string directory, Action<string> done)
    {
        LastError = null;
        if (Running)
        {
            LastError = "a recording is already running";
            return null;
        }
        seconds = Math.Clamp(seconds, 3, 300);
        string path;
        try
        {
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, "komet-alloc-" + DateTime.Now.ToString("yyMMdd-HHmmss") + ".nettrace");
        }
        catch (Exception e)
        {
            LastError = "cannot write to " + directory + ": " + e.Message;
            return null;
        }

        Running = true;
        LastFile = path;
        var thread = new Thread(() => Record(seconds, path, done)) { IsBackground = true, Name = "komet-alloctrace" };
        thread.Start();
        return path;
    }

    private static void Record(int seconds, string path, Action<string> done)
    {
        var result = "";
        try
        {
            WriteThreadNames(path + ".threads.txt");
            var client = new DiagnosticsClient(Environment.ProcessId);
            var providers = new List<EventPipeProvider>
            {
                new(RuntimeProvider, EventLevel.Verbose, GcKeyword),
            };
            using var session = client.StartEventPipeSession(providers, requestRundown: true, circularBufferMB: 256);
            using var file = File.Create(path);
            var copy = Task.Run(() => session.EventStream.CopyTo(file));
            Thread.Sleep(seconds * 1000);
            session.Stop();
            copy.Wait(TimeSpan.FromSeconds(60));
            file.Flush();
            // the rundown at the end can be large; the names of the threads once more, in case
            // a thread the recording saw was started after the first snapshot
            WriteThreadNames(path + ".threads.txt");
            LastBytes = new FileInfo(path).Length;
            result = string.Format(CultureInfo.InvariantCulture,
                "allocation trace written: {0} ({1:F1} MB, {2} s)", path, LastBytes / 1048576.0, seconds);
        }
        catch (Exception e)
        {
            LastError = e.GetType().Name + ": " + e.Message;
            result = "allocation trace FAILED: " + LastError;
        }
        finally
        {
            Running = false;
        }
        try { done?.Invoke(result); } catch (Exception) { /* the message is best effort */ }
    }

    /// <summary>"tid name" per line, from what the OS knows about the process's threads.</summary>
    private static void WriteThreadNames(string path)
    {
        try
        {
            if (!OperatingSystem.IsLinux()) return;
            var lines = new List<string>();
            foreach (var dir in Directory.EnumerateDirectories("/proc/self/task"))
            {
                var tid = Path.GetFileName(dir);
                string name;
                try { name = File.ReadAllText(Path.Combine(dir, "comm")).Trim(); }
                catch (Exception) { continue; }
                lines.Add(tid + " " + name);
            }
            File.WriteAllLines(path, lines);
        }
        catch (Exception)
        {
            // names are a convenience; the trace stands without them
        }
    }
}
