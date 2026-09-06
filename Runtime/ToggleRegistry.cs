using System;
using System.Collections.Generic;
using System.Text;

namespace Komet.Runtime;

/// <summary>
/// One runtime switch, as data.
///
/// Until the window existed there was exactly one way to flip a system - a switch statement in
/// <c>ToggleSystem</c> - and that was fine, because there was exactly one caller. A second
/// caller changes the question: a list of toggles in the GUI would be a second registry, and a
/// second registry is a list that silently stops covering the system added after it. So the
/// switch became this table, the chat command looks its argument up in it, and the window draws
/// the same table. There is one place where a toggle is declared and it is the place that
/// executes it.
///
/// <see cref="Flip"/> returns the very sentence the chat command prints, so the GUI and the
/// chat cannot describe the same flip differently.
/// </summary>
internal sealed class ToggleEntry
{
    /// <summary>The word '.komet toggle' accepts. Never translated - it is an argument.</summary>
    public string Key;

    /// <summary>Which part of the mod this belongs to; the window groups its rows by it.</summary>
    public ToggleGroup Group;

    /// <summary>A short name for the row. English here, translated at the point of display.</summary>
    public string Label;

    /// <summary>Is the system currently doing its thing? What the window's state column reads.</summary>
    public Func<bool> IsOn;

    /// <summary>Flips it and describes the new state, in the words the chat reply has always
    /// used. The full sentence, because half of these carry a caveat that only makes sense
    /// with the value next to it ("... - only takes effect with 'mtt' ON").</summary>
    public Func<string> Flip;

    /// <summary>Null normally; the reason when this toggle cannot be flipped at all on this
    /// machine (no AVX). Checked before <see cref="Flip"/>, and shown in the window as a
    /// greyed row rather than a switch that does nothing.</summary>
    public Func<string> Unavailable;

    /// <summary>True when flipping this changes WHAT is drawn - what safemode switches off in
    /// one go. The window marks those, because a visual artefact is bisected among these and
    /// nowhere else.</summary>
    public bool Visual;
}

/// <summary>The sections the window sorts the toggles into. The order is the order it draws.</summary>
internal enum ToggleGroup
{
    Culling,
    Rendering,
    Shadows,
    Chunks,
    Entities,
    Memory,
    Server,
    Diagnostics,
}

/// <summary>
/// The toggle table, built once per session by <see cref="KometModSystem"/> - it needs the
/// config (every toggle restores what komet.json asked for, not a hardcoded default) and a few
/// instance methods, so it cannot be a static table.
/// </summary>
internal sealed class ToggleRegistry
{
    private readonly List<ToggleEntry> entries = new(48);
    private readonly Dictionary<string, ToggleEntry> byKey = new(48, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ToggleEntry> Entries => entries;

    public ToggleEntry Add(string key, ToggleGroup group, string label, Func<bool> isOn, Func<string> flip,
                           bool visual = false, Func<string> unavailable = null)
    {
        var e = new ToggleEntry
        {
            Key = key, Group = group, Label = label,
            IsOn = isOn, Flip = flip, Unavailable = unavailable, Visual = visual
        };
        // A duplicate key would mean one of the two systems is unreachable from the chat
        // command and drawn twice in the window. Loud, at construction, not in a field report.
        if (byKey.ContainsKey(key)) throw new ArgumentException("duplicate toggle key: " + key);
        byKey[key] = e;
        entries.Add(e);
        return e;
    }

    /// <summary>
    /// The shape most of them have: one bool, flipped, and a sentence saying which way it went.
    /// <paramref name="on"/> and <paramref name="off"/> are the halves the reply appends after
    /// <paramref name="sentence"/> - which defaults to the label, because that is what nearly
    /// all of them said anyway. The toggles that do more than flip a field (restore a configured
    /// value, re-wrap listeners, refuse when a patch is not installed) still call
    /// <see cref="Add"/> directly, and reading the table it is now obvious which those are.
    /// </summary>
    public ToggleEntry AddFlag(string key, ToggleGroup group, string label,
                               Func<bool> isOn, Action<bool> set, string on, string off,
                               bool visual = false, Func<string> unavailable = null,
                               string sentence = null)
    {
        var text = (sentence ?? label) + " ";
        return Add(key, group, label, isOn,
                   () => { var now = !isOn(); set(now); return text + (now ? on : off); },
                   visual, unavailable);
    }

    public ToggleEntry Find(string key)
        => key != null && byKey.TryGetValue(key, out var e) ? e : null;

    /// <summary>Every key, in declaration order, comma separated - what the chat command lists
    /// when it is given a word it does not know. Built from the table, so a system added to it
    /// appears in the help without anyone remembering to add it there.</summary>
    public string KeyList()
    {
        var sb = new StringBuilder(512);
        foreach (var e in entries)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(e.Key);
        }
        return sb.ToString();
    }

    /// <summary>The entries of one group, in declaration order.</summary>
    public IEnumerable<ToggleEntry> InGroup(ToggleGroup group)
    {
        foreach (var e in entries)
            if (e.Group == group) yield return e;
    }

    /// <summary>How many rows a group draws - the window sizes its page from this before it
    /// walks the entries, and counting without allocating an enumerator keeps the refresh path
    /// (four times a second) free of it.</summary>
    public int CountIn(ToggleGroup group)
    {
        var n = 0;
        foreach (var e in entries)
            if (e.Group == group) n++;
        return n;
    }
}
