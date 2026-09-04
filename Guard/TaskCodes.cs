using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;

namespace Komet.Guard;

/// <summary>
/// Gives the main-thread task codes a readable name. The engine labels a packet's handler
/// task "readpacket" plus the numeric packet id, so the report said "readpacket58 0,01 ms
/// (638.134x)" and "laengste 1891,9 ms (readpacket6)" - and both had to be looked up by hand
/// in the engine's Packet_ServerIdEnum (58 = ExchangeBlock, 6 = LevelFinalize). The table is
/// public constants on that class; read once by reflection, so a new packet id in a later
/// game version names itself.
/// </summary>
public static class TaskCodes
{
    private const string Prefix = "readpacket";
    private static readonly ConcurrentDictionary<string, string> Described = new();
    private static Dictionary<int, string> packetNames;

    /// <summary>"readpacket58" -> "readpacket58=ExchangeBlock"; anything else unchanged.</summary>
    public static string Describe(string code)
    {
        if (code == null || !code.StartsWith(Prefix, StringComparison.Ordinal)) return code;
        return Described.GetOrAdd(code, static c =>
        {
            if (!int.TryParse(c.AsSpan(Prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                return c;
            var names = packetNames ??= LoadPacketNames();
            return names.TryGetValue(id, out var name) ? c + "=" + name : c;
        });
    }

    private static Dictionary<int, string> LoadPacketNames()
    {
        var table = new Dictionary<int, string>();
        try
        {
            var type = AccessTools.TypeByName("Packet_ServerIdEnum");
            if (type == null) return table;
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (!f.IsLiteral || f.FieldType != typeof(int)) continue;
                var id = (int)f.GetRawConstantValue();
                // the first name wins when an id is aliased
                table.TryAdd(id, f.Name);
            }
        }
        catch (Exception) { /* no table, codes stay numeric */ }
        return table;
    }

    /// <summary>For the harness: a fresh table on the next call.</summary>
    internal static void Reset()
    {
        Described.Clear();
        packetNames = null;
    }
}
