using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;

namespace Komet.Guard;

/// <summary>
/// Notices when somebody else is on the same methods - and when the engine underneath is not
/// the one this mod was verified against.
///
/// Two ways a Komet patch can stop meaning what it means. Another Harmony mod patches the same
/// engine method: a foreign transpiler rewrites the IL Komet's transpiler expects (and Harmony
/// re-runs every transpiler on the method each time anyone patches it, so Komet's transpiler
/// then executes inside the OTHER mod's patch call), or a foreign prefix that returns bool can
/// cancel the original that Komet's prefix/postfix bracket, or a mod patches Komet's own code.
/// And a forked client (the "Optimum" build of the 02.09. field report) does not patch at all -
/// it ships modified assemblies, and Komet's 1:1 transcriptions (task drain, entity Before loop,
/// minimap upload, entity sync) then silently replace whatever the fork changed in those
/// methods, with no exception and no log line to say so.
///
/// The first case is answered from Harmony's own registry: every patched method in the process
/// with its owners, kinds and priorities. The second from a fingerprint: at build time the verify
/// harness hashes the IL of every engine method Komet patches (opcodes plus resolved operand
/// names, so a rebuild with different metadata tokens hashes the same) into
/// <c>EngineFingerprint.g.cs</c>; at world start the same hash is taken of the live methods.
/// A method that differs is named, once, and stays in the report. Neither check changes
/// behaviour - a collision is reported, never "resolved", because which side should win is
/// not this mod's call.
/// </summary>
public static class PatchGuard
{
    public enum Severity { Info = 0, Medium = 1, High = 2 }

    public sealed class Finding
    {
        /// <summary>Identity for "already reported": target, owner, kind and patch method.</summary>
        public string Key;
        public string Target;
        public string Owner;
        public string Kind;
        public int Priority;
        public bool CanSkipOriginal;
        /// <summary>The target is a method of this mod, not of the engine.</summary>
        public bool OnKometCode;
        /// <summary>What Komet has on the same method ("prefix(cancelling)+postfix", "transpiler").</summary>
        public string Ours;
        /// <summary>Everything Komet has on this method is a measurement bracket (Measure/MeasurementPatches):
        /// timing or allocation reads that change nothing - a foreign patch next to one is information.</summary>
        public bool MeasurementOnly;
        /// <summary>For prefixes: whether the foreign one runs before Komet's.</summary>
        public bool RunsBeforeOurs;
        public Severity Severity;
        public string Why;
    }

    public sealed class Drift
    {
        public string Target;
        public string Expected;
        public string Actual;
    }

    /// <summary>Which Harmony ids are this mod's: the client ("komet"), the server ("komet.server"),
    /// the harness ("komet.verify").</summary>
    public static Func<string, bool> IsOwn = id =>
        id != null && (id == "komet" || id.StartsWith("komet.", StringComparison.Ordinal));

    /// <summary>Warning sink for new findings and for engine drift.</summary>
    public static Action<string> Warn;
    /// <summary>Notification sink for the all-clear lines.</summary>
    public static Action<string> Notify;

    /// <summary>Foreign patches currently present, as of the last <see cref="Scan"/>.</summary>
    public static readonly List<Finding> Findings = new();
    /// <summary>Engine methods whose IL differs from the verified build, as of <see cref="CheckEngine"/>.</summary>
    public static readonly List<Drift> Drifts = new();
    /// <summary>Assemblies whose module id differs from the verified build.</summary>
    public static readonly List<string> ForeignAssemblies = new();

    public static int Scans { get; private set; }
    public static int MethodsChecked { get; private set; }
    public static int MethodsUnverified { get; private set; }
    public static string EngineSummary { get; private set; }
    public static bool EngineChecked { get; private set; }

    private static readonly HashSet<string> reported = new();
    private static readonly Assembly ownAssembly = typeof(PatchGuard).Assembly;

    private static bool HasGeneratedFingerprint()
    {
        return EngineFingerprint.Generated;
    }

    // ---- harmony collisions ----------------------------------------------------------

    /// <summary>
    /// Walks Harmony's registry and rebuilds <see cref="Findings"/>. Returns how many findings
    /// are NEW since the previous scan; each of those is reported once through <see cref="Warn"/>.
    /// A few hundred dictionary lookups - cheap enough for a slow periodic tick, because mods
    /// patch lazily (on first use, on world join) and a one-time scan would miss them.
    /// </summary>
    public static int Scan()
    {
        var current = new List<Finding>();
        foreach (var method in Harmony.GetAllPatchedMethods()) Inspect(current, method);
        return Publish(current);
    }

    // ---- the same scan, in slices ------------------------------------------------------
    //
    // A full scan measured 12,6 ms in the field, on the render thread, and the hitch log named
    // it once the periodic listeners got names of their own. The cost is per METHOD, not in the
    // registry walk: Harmony keeps a patched method's info serialised and GetPatchInfo rebuilds
    // it on every call, so ~150 patched methods are ~150 deserialisations. Nothing about that
    // has to happen in one frame - the guard's job is to notice a lazily applied patch
    // eventually, not within a frame - so the periodic path walks the same list a couple of
    // milliseconds at a time and publishes when it reaches the end.
    //
    // The one-shot callers (world join, '.komet conflicts') keep the whole scan: there the
    // answer is wanted now, and once.

    private static MethodBase[] walking;
    private static int walkIndex;
    private static List<Finding> walkFindings;

    /// <summary>
    /// One slice of a periodic scan, bounded by a time budget. Returns true on the slice that
    /// finished a scan - that is the one that published its findings.
    /// </summary>
    public static bool ScanSlice(double budgetMs)
    {
        if (walking == null)
        {
            var all = new List<MethodBase>();
            foreach (var m in Harmony.GetAllPatchedMethods()) all.Add(m);
            walking = all.ToArray();
            walkIndex = 0;
            walkFindings = new List<Finding>();
        }

        var t0 = Stopwatch.GetTimestamp();
        var budget = (long)(budgetMs * Stopwatch.Frequency / 1000.0);
        while (walkIndex < walking.Length)
        {
            Inspect(walkFindings, walking[walkIndex++]);
            // one timestamp per method against ~80 us of work per method
            if (Stopwatch.GetTimestamp() - t0 >= budget) break;
        }

        if (walkIndex < walking.Length) return false;
        Publish(walkFindings);
        return true;
    }

    /// <summary>Everything one patched method contributes to a scan.</summary>
    private static void Inspect(List<Finding> into, MethodBase method)
    {
        HarmonyLib.Patches info;
        try { info = Harmony.GetPatchInfo(method); }
        catch (Exception) { return; }
        if (info == null) return;

        var onOurCode = method.DeclaringType?.Assembly == ownAssembly;
        var oursPresent = false;
        foreach (var owner in info.Owners) if (IsOwn(owner)) { oursPresent = true; break; }
        if (!onOurCode && !oursPresent) return;

        var ours = DescribeOurs(info, out var ourPrefixCanSkip, out var ourPrefixPriority, out var ourPrefixIndex, out var ourTranspiler, out var measurementOnly);
        Collect(into, method, info.Transpilers, "transpiler", onOurCode, ours, ourPrefixCanSkip, ourTranspiler, ourPrefixPriority, ourPrefixIndex, measurementOnly);
        Collect(into, method, info.Prefixes, "prefix", onOurCode, ours, ourPrefixCanSkip, ourTranspiler, ourPrefixPriority, ourPrefixIndex, measurementOnly);
        Collect(into, method, info.Postfixes, "postfix", onOurCode, ours, ourPrefixCanSkip, ourTranspiler, ourPrefixPriority, ourPrefixIndex, measurementOnly);
        Collect(into, method, info.Finalizers, "finalizer", onOurCode, ours, ourPrefixCanSkip, ourTranspiler, ourPrefixPriority, ourPrefixIndex, measurementOnly);
    }

    /// <summary>Swaps a finished scan in and reports what is new about it. A slice in progress
    /// is dropped: whatever published is newer than whatever it had walked so far.</summary>
    private static int Publish(List<Finding> current)
    {
        walking = null;
        walkFindings = null;

        Findings.Clear();
        Findings.AddRange(current);
        Scans++;

        var fresh = 0;
        foreach (var f in current)
        {
            if (!reported.Add(f.Key)) continue;
            fresh++;
            // information goes to the notification log; a warning is for something to decide
            if (f.Severity == Severity.Info) Notify?.Invoke(Format(f));
            else Warn?.Invoke(Format(f));
        }
        return fresh;
    }

    private static string DescribeOurs(HarmonyLib.Patches info, out bool prefixCanSkip, out int prefixPriority, out int prefixIndex, out bool transpiler, out bool measurementOnly)
    {
        prefixCanSkip = false; transpiler = false;
        var own = 0; var measuring = 0;
        void Count(Patch p) { own++; if (p.PatchMethod?.DeclaringType == typeof(Komet.Measure.MeasurementPatches)) measuring++; }
        prefixPriority = int.MinValue; prefixIndex = int.MaxValue;
        var parts = new List<string>(3);
        foreach (var p in info.Prefixes)
        {
            if (!IsOwn(p.owner)) continue;
            Count(p);
            var skip = p.PatchMethod?.ReturnType == typeof(bool);
            prefixCanSkip |= skip;
            // the prefix of ours that runs first is the one a foreign prefix is ordered against
            if (p.priority > prefixPriority || (p.priority == prefixPriority && p.index < prefixIndex))
            {
                prefixPriority = p.priority;
                prefixIndex = p.index;
            }
            parts.Add(skip ? "prefix(cancelling)" : "prefix");
        }
        var post = false; var fin = false;
        foreach (var p in info.Postfixes) if (IsOwn(p.owner)) { Count(p); post = true; }
        foreach (var p in info.Transpilers) if (IsOwn(p.owner)) { Count(p); transpiler = true; }
        foreach (var p in info.Finalizers) if (IsOwn(p.owner)) { Count(p); fin = true; }
        if (post) parts.Add("postfix");
        if (transpiler) parts.Add("transpiler");
        if (fin) parts.Add("finalizer");
        measurementOnly = own > 0 && measuring == own;
        return parts.Count == 0 ? "-" : string.Join("+", parts);
    }

    private static void Collect(List<Finding> into, MethodBase method, IEnumerable<Patch> patches, string kind,
                                bool onOurCode, string ours, bool ourPrefixCanSkip, bool ourTranspiler,
                                int ourPrefixPriority, int ourPrefixIndex, bool measurementOnly = false)
    {
        foreach (var p in patches)
        {
            if (IsOwn(p.owner)) continue;
            var canSkip = kind == "prefix" && p.PatchMethod?.ReturnType == typeof(bool);
            var f = new Finding
            {
                Target = ShortName(method),
                Owner = p.owner ?? "?",
                Kind = canSkip ? "prefix(cancelling)" : kind,
                Priority = p.priority,
                CanSkipOriginal = canSkip,
                OnKometCode = onOurCode,
                Ours = ours,
                MeasurementOnly = measurementOnly,
                RunsBeforeOurs = kind == "prefix" && ourPrefixPriority != int.MinValue
                                 && (p.priority > ourPrefixPriority || (p.priority == ourPrefixPriority && p.index < ourPrefixIndex)),
            };
            f.Key = KeyOf(method) + "|" + f.Owner + "|" + kind + "|" + (p.PatchMethod != null ? KeyOf(p.PatchMethod) : "?");
            Classify(f, ourPrefixCanSkip, ourTranspiler);
            into.Add(f);
        }
    }

    /// <summary>The rule: what a foreign patch of this kind does to what Komet has there.</summary>
    internal static void Classify(Finding f, bool ourPrefixCanSkip, bool ourTranspiler)
    {
        if (f.OnKometCode)
        {
            f.Severity = Severity.High;
            f.Why = "foreign patch on komet code: what this method does is no longer komet's decision";
            return;
        }
        switch (f.Kind)
        {
            case "transpiler":
                if (ourPrefixCanSkip)
                {
                    // The transcription case: komets prefix returns false, so the original -
                    // and every transpiler on it - never runs. The other mod's change is gone
                    // without an exception and without a log line of its own. This is the
                    // shape behind "entities are invisible since I installed this mod".
                    f.Severity = Severity.High;
                    f.Why = "komet's prefix replaces the original, this IL rewrite never runs";
                    return;
                }
                f.Severity = ourTranspiler ? Severity.High : Severity.Medium;
                f.Why = ourTranspiler
                    ? "both rewrite the same IL; harmony re-runs komet's transpiler on every patch call of the other mod, and a changed shape throws in there"
                    : "the IL under komet's prefix/postfix is no longer vanilla";
                return;
            case "prefix(cancelling)":
                if (ourPrefixCanSkip)
                {
                    f.Severity = Severity.High;
                    f.Why = (f.RunsBeforeOurs ? "runs BEFORE komet's prefix" : "runs AFTER komet's prefix")
                            + " - both can cancel the original, priority decides whose version runs";
                }
                else
                {
                    f.Severity = Severity.Medium;
                    f.Why = "can skip the original; komet's measurement then books a call that never happened";
                }
                return;
            case "prefix":
                // harmony stops calling prefixes as soon as one returns false, so a foreign
                // prefix ordered behind komet's cancelling prefix does not run at all
                if (ourPrefixCanSkip && !f.RunsBeforeOurs)
                {
                    f.Severity = Severity.High;
                    f.Why = "runs AFTER komet's cancelling prefix - harmony does not call it at all then";
                    return;
                }
                f.Severity = Severity.Info;
                f.Why = f.RunsBeforeOurs ? "runs before komet's prefix, does not cancel" : "does not cancel";
                if (f.MeasurementOnly) f.Why += MeasurementNote;
                return;
            default:
                // postfixes still run when a prefix cancels - but on the result of komets
                // transcription, not on the one the original would have produced
                if (ourPrefixCanSkip)
                {
                    f.Severity = Severity.Medium;
                    f.Why = "komet's prefix replaces the original; this " + f.Kind
                            + " sees the result of the transcription, not the original's";
                    return;
                }
                f.Severity = Severity.Info;
                f.Why = "runs after the original, independently of komet";
                if (f.MeasurementOnly) f.Why += MeasurementNote;
                return;
        }
    }

    private const string MeasurementNote = " - komet only measures here (time/allocation), nothing to decide";

    internal static string Format(Finding f)
        => string.Format(CultureInfo.InvariantCulture,
            "patch collision {0}: {1} - '{2}' {3} (prio {4}) next to {5}: {6}",
            f.Severity.ToString().ToUpperInvariant(), f.Target, f.Owner, f.Kind, f.Priority,
            f.MeasurementOnly ? "komet's measurement bracket (" + f.Ours + ")" : "komet " + f.Ours, f.Why);

    public static int CountAt(Severity s)
    {
        var n = 0;
        foreach (var f in Findings) if (f.Severity == s) n++;
        return n;
    }

    // ---- engine fingerprint ----------------------------------------------------------

    /// <summary>Stable identity of a method across assembly builds: type, name, parameter types.</summary>
    public static string KeyOf(MethodBase m)
    {
        var sb = new StringBuilder(96);
        sb.Append(m.DeclaringType?.FullName ?? "?").Append("::").Append(m.Name).Append('(');
        var ps = m.GetParameters();
        for (var i = 0; i < ps.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(ps[i].ParameterType.Name);
        }
        return sb.Append(')').ToString();
    }

    /// <summary>Type.Method for log lines - the namespace is noise there.</summary>
    public static string ShortName(MethodBase m)
        => (m.DeclaringType?.Name ?? "?") + "." + m.Name;

    /// <summary>
    /// FNV-1a over the instruction stream as Harmony reads it from metadata: opcode names plus
    /// resolved operands (member names, not tokens; branch targets as offsets; literals as
    /// text). Harmony patches at the native level and leaves the IL untouched, so this reads
    /// the same on a patched method as on a pristine one. Null for a method without IL.
    /// </summary>
    public static string FingerprintOf(MethodBase m)
    {
        IEnumerable<KeyValuePair<OpCode, object>> body;
        try { body = PatchProcessor.ReadMethodBody(m); }
        catch (Exception) { return null; }

        const ulong prime = 1099511628211UL;
        var h = 14695981039346656037UL;
        void Mix(string s)
        {
            if (s != null) foreach (var c in s) { h ^= c; h *= prime; }
            h ^= 0x1F; h *= prime;
        }
        try
        {
            foreach (var ins in body)
            {
                Mix(ins.Key.Name);
                Mix(Describe(ins.Value));
            }
        }
        catch (Exception) { return null; }
        return h.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static FieldInfo ilOffsetField;

    internal static string Describe(object operand)
    {
        switch (operand)
        {
            case null: return "";
            case MethodBase mb: return KeyOf(mb);
            case FieldInfo fi: return (fi.DeclaringType?.FullName ?? "?") + "::" + fi.Name;
            case Type t: return t.FullName ?? t.Name;
            case string s: return "\"" + s + "\"";
            case Label l: return "L" + l.GetHashCode().ToString(CultureInfo.InvariantCulture);
            case LocalBuilder lb: return "V" + lb.LocalIndex + ":" + (lb.LocalType?.FullName ?? "?");
            case LocalVariableInfo lv: return "V" + lv.LocalIndex + ":" + (lv.LocalType?.FullName ?? "?");
            case ParameterInfo pi: return "P" + pi.Position;
            case IFormattable f: return f.ToString(null, CultureInfo.InvariantCulture);
            case Array arr:
            {
                var sb = new StringBuilder("[");
                foreach (var e in arr) sb.Append(Describe(e)).Append(';');
                return sb.Append(']').ToString();
            }
        }
        // Harmony's ILInstruction: branch targets resolve to the target instruction, whose
        // offset is the stable part (a re-emit with the same code has the same offsets)
        var type = operand.GetType();
        if (type.Name == "ILInstruction")
        {
            ilOffsetField ??= type.GetField("offset", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (ilOffsetField != null) return "->" + ilOffsetField.GetValue(operand);
        }
        return type.Name;
    }

    /// <summary>
    /// Compares the live engine against the build the verify harness hashed: assembly module
    /// ids first (the whole-build answer), then every method Komet has patched that the table
    /// knows (the answer that matters for the transcriptions). Fills <see cref="Drifts"/>,
    /// <see cref="ForeignAssemblies"/> and <see cref="EngineSummary"/>; the summary goes to
    /// <see cref="Warn"/> when a patched method differs and to <see cref="Notify"/> otherwise.
    /// </summary>
    public static void CheckEngine(string liveVersionText)
    {
        Drifts.Clear();
        ForeignAssemblies.Clear();
        MethodsChecked = MethodsUnverified = 0;
        EngineChecked = true;

        if (!HasGeneratedFingerprint())
        {
            EngineSummary = "engine: " + liveVersionText + " - no fingerprint compiled in (./build.sh fingerprint)";
            Notify?.Invoke(EngineSummary);
            return;
        }

        var loaded = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = a.GetName().Name;
            if (name != null && !loaded.ContainsKey(name)) loaded[name] = a;
        }
        foreach (var (name, mvid) in EngineFingerprint.Assemblies)
        {
            if (!loaded.TryGetValue(name, out var a)) continue; // not loaded yet (server-only dll on a pure client)
            if (!string.Equals(a.ManifestModule.ModuleVersionId.ToString("D"), mvid, StringComparison.OrdinalIgnoreCase))
                ForeignAssemblies.Add(name);
        }

        var expected = new Dictionary<string, string>(EngineFingerprint.Methods.Length, StringComparer.Ordinal);
        foreach (var (key, hash) in EngineFingerprint.Methods) expected[key] = hash;

        foreach (var method in Harmony.GetAllPatchedMethods())
        {
            if (method.DeclaringType?.Assembly == ownAssembly) continue;
            HarmonyLib.Patches info;
            try { info = Harmony.GetPatchInfo(method); }
            catch (Exception) { continue; }
            if (info == null) continue;
            var ours = false;
            foreach (var owner in info.Owners) if (IsOwn(owner)) { ours = true; break; }
            if (!ours) continue;

            var key = KeyOf(method);
            if (!expected.TryGetValue(key, out var want)) { MethodsUnverified++; continue; }
            var have = FingerprintOf(method);
            MethodsChecked++;
            if (have != null && have != want)
                Drifts.Add(new Drift { Target = ShortName(method), Expected = want, Actual = have });
        }

        var sb = new StringBuilder(256);
        sb.Append("engine: ").Append(liveVersionText);
        if (ForeignAssemblies.Count == 0)
            sb.Append(" - assemblies wie verifiziert (").Append(EngineFingerprint.GameVersion).Append(')');
        else
            sb.Append(" - ").Append(string.Join(", ", ForeignAssemblies))
              .Append(" weichen vom verifizierten build ").Append(EngineFingerprint.GameVersion).Append(" ab");
        if (Drifts.Count == 0)
        {
            sb.Append(", all ").Append(MethodsChecked).Append(" gepatchten methoden unveraendert");
            if (ForeignAssemblies.Count > 0) sb.Append(" (komets patches treffen bekannten code)");
        }
        else
        {
            sb.Append("; ").Append(Drifts.Count).Append(" von ").Append(MethodsChecked).Append(" gepatchten methoden VERAENDERT: ");
            for (var i = 0; i < Drifts.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Drifts[i].Target);
            }
            sb.Append(" - komet's transpilers and 1:1 transcriptions there run against foreign code, which is not verified");
        }
        if (MethodsUnverified > 0) sb.Append(" (").Append(MethodsUnverified).Append(" without a fingerprint)");
        EngineSummary = sb.ToString();
        if (Drifts.Count > 0) Warn?.Invoke(EngineSummary);
        else Notify?.Invoke(EngineSummary);
    }

    // ---- report ----------------------------------------------------------------------

    /// <summary>One-line state for the report header, plus one line per finding.</summary>
    public static string ReportLines()
    {
        var sb = new StringBuilder(256);
        if (EngineChecked && EngineSummary != null) sb.Append(EngineSummary).Append('\n');
        if (Scans == 0)
        {
            sb.Append("patch collisions: not checked yet\n");
            return sb.ToString();
        }
        if (Findings.Count == 0)
        {
            sb.Append("patch collisions: none (foreign patches on komet's methods or komet's own code)\n");
            return sb.ToString();
        }
        sb.AppendFormat(CultureInfo.InvariantCulture, "patch collisions: {0} ({1} high, {2} medium, {3} info)\n",
            Findings.Count, CountAt(Severity.High), CountAt(Severity.Medium), CountAt(Severity.Info));
        foreach (var f in Findings) sb.Append("  ").Append(Format(f)).Append('\n');
        return sb.ToString();
    }

    public static void Reset()
    {
        Findings.Clear();
        Drifts.Clear();
        ForeignAssemblies.Clear();
        reported.Clear();
        Scans = 0;
        MethodsChecked = MethodsUnverified = 0;
        EngineSummary = null;
        EngineChecked = false;
    }
}
