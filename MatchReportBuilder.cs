using System.Globalization;
using AssettoServer.Server;
using AssettoServer.Server.Configuration;
using AssettoServer.Shared.Model;
using RankedMatchReporterPlugin.Models;

namespace RankedMatchReporterPlugin;

/// <summary>
/// MatchReportBuilder — maps AssettoServer race results (EntryCarResult rows) into a MatchReportPayload
/// for ingest (HTTP POST body sent to serv-brain).
///
/// Terms:
/// - Payload = the structured match report object (later serialized to JSON). Not game jargon — just "the data package" we send.
/// - Ingest = serv-brain's receive endpoint (POST /v1/races) that accepts that JSON and enqueues ranking work.
///
/// Logic flow:
/// 1. Read Results dictionary from the ended race session.
/// 2. Walk the green-flag starter list (one participant row per starter).
/// 3. Match each starter to a Results row by Steam ID; missing row → DNF at last place.
/// 4. When ExcludeZeroLapDriversFromRanking is on, drop rows with num_laps=0 before ingest.
/// 5. Compare ranking field size and peak window to set counted_for_ranked.
/// 6. Return DTO with new match_id (UUID v7) and ISO timestamps.
/// </summary>
public static class MatchReportBuilder
{
    // AssettoServer leaves BestLap at this value until the driver sets a valid lap.
    private const uint InvalidLapSentinel = 999999999;

    public static MatchReportPayload Build(
        RankedMatchReporterConfiguration configuration,
        ACServerConfiguration serverConfiguration,
        SessionState raceSession,
        IReadOnlyList<RaceStarterSnapshot> raceStartersAtGreen,
        DateTime raceStartedAtUtc,
        DateTime raceFinishedAtUtc)
    {
        // Read result rows from the ended race; use empty dict if Results is null.
        var results = raceSession.Results ?? new Dictionary<byte, EntryCarResult>();
        var participants = new List<MatchParticipantPayload>();
        var fieldSize = raceStartersAtGreen.Count;

        // Index Results by Steam ID so we can look up each starter without scanning every slot each time.
        var resultsBySteamId = results.Values
            .Where(r => r.Guid != 0)
            .GroupBy(r => r.Guid)
            .ToDictionary(g => g.Key, g => g.First());

        // Walk green-flag starters only — mid-race joiners never appear in this list.
        foreach (var starter in raceStartersAtGreen)
        {
            if (!resultsBySteamId.TryGetValue(starter.SteamId, out var result))
            {
                // Starter left or slot was reused — no Results row for this Steam ID → last place DNF.
                participants.Add(BuildDisqualifiedParticipant(starter, fieldSize, raceSession, results));
                continue;
            }

            participants.Add(BuildParticipantFromResult(starter, result, fieldSize, raceSession, results));
        }

        // When enabled, send only drivers who completed at least one lap — brain ranks everyone in participants[].
        var rankingParticipants = configuration.ExcludeZeroLapDriversFromRanking
            ? participants.Where(p => p.NumLaps > 0).ToList()
            : participants;

        var rankingFieldSize = rankingParticipants.Count;

        var inPeakWindow = PeakWindowEvaluator.IsInPeakWindow(
            configuration.PeakWindow,
            raceStartedAtUtc);
        // countedForRanked uses drivers in the payload (after zero-lap filter when enabled).
        var countedForRanked = rankingFieldSize >= configuration.MinimumDriversForRanked
            && (!configuration.PeakWindow.Enabled || inPeakWindow);

        // Track/layout strings come from server cfg, not from the session object.
        var track = serverConfiguration.Server.Track;
        var layout = serverConfiguration.Server.TrackConfig ?? "";

        return new MatchReportPayload
        {
            // UUID v7 embeds creation time (sortable, debuggable). ULID would need a text column in Postgres.
            MatchId = Guid.CreateVersion7().ToString(),
            LeagueId = configuration.LeagueId,
            ServerId = configuration.ServerId,
            TrackId = track,
            LayoutId = layout,
            StartedAt = raceStartedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            FinishedAt = raceFinishedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            CountedForRanked = countedForRanked,
            Participants = rankingParticipants
        };
    }

    /// <summary>
    /// BuildParticipantFromResult — map one starter's Results row into a participant payload.
    /// </summary>
    private static MatchParticipantPayload BuildParticipantFromResult(
        RaceStarterSnapshot starter,
        EntryCarResult result,
        int fieldSize,
        SessionState raceSession,
        Dictionary<byte, EntryCarResult> results)
    {
        // Walk Results slot keys to find this driver's quali grid index.
        var gridSlotIndex = raceSession.Grid?
            .Select((car, index) => (car, index))
            .FirstOrDefault(x => results.TryGetValue(x.car.SessionId, out var slotResult)
                                   && slotResult.Guid == starter.SteamId)
            .index;

        // No laps and no result row activity → treat as disqualified at last place (same as missing row).
        if (result.NumLaps == 0)
        {
            return new MatchParticipantPayload
            {
                SteamId = starter.SteamId.ToString(CultureInfo.InvariantCulture),
                Username = result.Name.Length > 0 ? result.Name : starter.Username,
                FinishPosition = fieldSize,
                Dnf = true,
                GridPosition = gridSlotIndex >= 0 ? gridSlotIndex + 1 : null,
                NumLaps = 0,
                BestLapMs = null,
                TotalRaceTimeMs = null
            };
        }

        // AssettoServer RacePos is 0-based (leader = 0); ingest expects 1-based finish position.
        var finishPosition = (int)result.RacePos + 1;

        return new MatchParticipantPayload
        {
            SteamId = starter.SteamId.ToString(CultureInfo.InvariantCulture),
            Username = result.Name.Length > 0 ? result.Name : starter.Username,
            FinishPosition = finishPosition,
            // HasCompletedLastLap false → driver did not complete the final lap before overtime/end.
            Dnf = !result.HasCompletedLastLap,
            GridPosition = gridSlotIndex >= 0 ? gridSlotIndex + 1 : null,
            NumLaps = (int)result.NumLaps,
            BestLapMs = ToLapMs(result.BestLap),
            TotalRaceTimeMs = result.TotalTime > 0 ? (int)result.TotalTime : null
        };
    }

    /// <summary>
    /// BuildDisqualifiedParticipant — starter with no Results row; last place, DNF.
    /// </summary>
    private static MatchParticipantPayload BuildDisqualifiedParticipant(
        RaceStarterSnapshot starter,
        int fieldSize,
        SessionState raceSession,
        Dictionary<byte, EntryCarResult> results)
    {
        var gridSlotIndex = raceSession.Grid?
            .Select((car, index) => (car, index))
            .FirstOrDefault(x => results.TryGetValue(x.car.SessionId, out var slotResult)
                                   && slotResult.Guid == starter.SteamId)
            .index;

        return new MatchParticipantPayload
        {
            SteamId = starter.SteamId.ToString(CultureInfo.InvariantCulture),
            Username = starter.Username,
            FinishPosition = fieldSize,
            Dnf = true,
            GridPosition = gridSlotIndex >= 0 ? gridSlotIndex + 1 : null,
            NumLaps = 0,
            BestLapMs = null,
            TotalRaceTimeMs = null
        };
    }

    /// <summary>
    /// ToLapMs — returns null when AssettoServer still has the invalid-lap sentinel in BestLap.
    /// </summary>
    private static int? ToLapMs(uint lapTime)
    {
        // Skip sentinel and zero — omit best_lap_ms in JSON when no valid lap exists.
        if (lapTime == 0 || lapTime >= InvalidLapSentinel)
            return null;

        return (int)lapTime;
    }
}

/// <summary>
/// PeakWindowEvaluator — checks whether a UTC instant falls inside the configured local time window.
///
/// Logic flow:
/// 1. If PeakWindow.Enabled is false, return true (no time gate).
/// 2. Convert instantUtc to local time using TimeZoneId.
/// 3. Parse StartLocal and EndLocal as TimeSpan.
/// 4. Compare local TimeOfDay to the window; if start &gt; end, treat window as overnight (wraps midnight).
/// </summary>
public static class PeakWindowEvaluator
{
    public static bool IsInPeakWindow(PeakWindowConfiguration config, DateTime instantUtc)
    {
        if (!config.Enabled)
            return true;

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(config.TimeZoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(instantUtc, timeZone);
        var start = TimeSpan.Parse(config.StartLocal, CultureInfo.InvariantCulture);
        var end = TimeSpan.Parse(config.EndLocal, CultureInfo.InvariantCulture);
        var timeOfDay = local.TimeOfDay;

        if (start <= end)
            return timeOfDay >= start && timeOfDay <= end;

        // start &gt; end: window wraps midnight — match if timeOfDay is after start OR before end.
        return timeOfDay >= start || timeOfDay <= end;
    }
}
