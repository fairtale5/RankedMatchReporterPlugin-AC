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
/// 2. Keep only drivers whose Steam ID was in the grid-at-start set.
/// 3. Sort by RacePos, map laps/times/DNF into participant rows.
/// 4. Compare field size and peak window to set counted_for_ranked.
/// 5. Return DTO with new match_id UUID and ISO timestamps.
/// </summary>
public static class MatchReportBuilder
{
    // AssettoServer leaves BestLap at this value until the driver sets a valid lap.
    private const uint InvalidLapSentinel = 999999999;

    public static MatchReportPayload Build(
        RankedMatchReporterConfiguration configuration,
        ACServerConfiguration serverConfiguration,
        SessionState raceSession,
        IReadOnlySet<ulong> gridSteamIdsAtStart,
        DateTime raceStartedAtUtc,
        DateTime raceFinishedAtUtc)
    {
        // Read result rows from the ended race; use empty dict if Results is null.
        var results = raceSession.Results ?? new Dictionary<byte, EntryCarResult>();
        var participants = new List<MatchParticipantPayload>();

        // Filter to grid-at-start Steam IDs, then order by finish position for stable participant list.
        foreach (var result in results.Values
                     .Where(r => r.Guid != 0 && gridSteamIdsAtStart.Contains(r.Guid))
                     .OrderBy(r => r.RacePos == 0 ? uint.MaxValue : r.RacePos))
        {
            // Walk race grid list to find this driver's quali slot index
            // Read raceSession.Grid (start-order list from AssettoServer at race green).
            // Tag each entry with its 0-based slot index; pick the slot where Client.Guid equals result.Guid.
            // FirstOrDefault yields index -1 when the driver is missing from Grid; store null in JSON in that case.
            var gridSlotIndex = raceSession.Grid?
                .Select((car, index) => (car, index))
                .FirstOrDefault(x => x.car.Client?.Guid == result.Guid)
                .index;

            participants.Add(new MatchParticipantPayload
            {
                // result.Guid is Steam ID (ulong); ingest expects decimal string.
                SteamId = result.Guid.ToString(CultureInfo.InvariantCulture),
                Username = result.Name,
                // result.RacePos is server finish order; 0 means unset — use loop order as fallback.
                FinishPosition = result.RacePos == 0 ? participants.Count + 1 : (int)result.RacePos,
                // HasCompletedLastLap false → driver did not complete the final lap before overtime/end.
                Dnf = !result.HasCompletedLastLap,
                GridPosition = gridSlotIndex >= 0 ? gridSlotIndex + 1 : null,
                NumLaps = (int)result.NumLaps,
                BestLapMs = ToLapMs(result.BestLap),
                TotalRaceTimeMs = result.TotalTime > 0 ? (int)result.TotalTime : null
            });
        }

        var fieldSize = participants.Count;
        var inPeakWindow = PeakWindowEvaluator.IsInPeakWindow(
            configuration.PeakWindow,
            raceStartedAtUtc);
        // countedForRanked true only when enough drivers AND (peak check off OR start time inside window).
        var countedForRanked = fieldSize >= configuration.MinimumDriversForRanked
            && (!configuration.PeakWindow.Enabled || inPeakWindow);

        // Track/layout strings come from server cfg, not from the session object.
        var track = serverConfiguration.Server.Track;
        var layout = serverConfiguration.Server.TrackConfig ?? "";

        return new MatchReportPayload
        {
            MatchId = Guid.NewGuid().ToString(),
            LeagueId = configuration.LeagueId,
            ServerId = configuration.ServerId,
            TrackId = track,
            LayoutId = layout,
            StartedAt = raceStartedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            FinishedAt = raceFinishedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            CountedForRanked = countedForRanked,
            Participants = participants
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
