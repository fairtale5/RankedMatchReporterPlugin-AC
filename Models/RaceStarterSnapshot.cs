namespace RankedMatchReporterPlugin.Models;

/// <summary>
/// RaceStarterSnapshot — one driver captured at race green.
///
/// Logic flow:
/// 1. RankedMatchReporterFeature fills this at green flag from grid slots with a connected client.
/// 2. MatchReportBuilder walks this list at race end — one participant row per Steam ID.
/// 3. Username is stored here so disconnects still have a name when Results no longer has the row.
/// 4. Callers collapse duplicate Steam IDs before use (two live slots = one starter).
/// </summary>
public readonly record struct RaceStarterSnapshot(ulong SteamId, string Username);
