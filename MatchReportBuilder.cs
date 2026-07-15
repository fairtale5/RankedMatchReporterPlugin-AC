using System.Globalization;
using AssettoServer.Server;
using AssettoServer.Server.Configuration;
using AssettoServer.Shared.Model;
using RankedMatchReporterPlugin.Classification;
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
/// 3. Match each starter to a Results row by Steam ID; missing row, zero laps, or disconnect during race → DNF.
/// 4. When ExcludeZeroLapDriversFromRanking is on, drop rows with num_laps=0 before ingest.
/// 5. Renumber finish positions to 1..N with no gaps; all DNFs share the same last rank (tie).
/// 6. Set counted_for_ranked from green-flag starter count (not post-filter payload size) and peak window.
/// 7. Return DTO with new match_id (UUID v7) and ISO timestamps.
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
        DateTime raceFinishedAtUtc,
        IReadOnlySet<ulong> disconnectedSteamIdsDuringRace,
        RaceClassificationResult? timedRaceClassification = null)
    {
        var useClassification = raceSession.Configuration.IsTimedRace
            && timedRaceClassification is { IsUsable: true };

        if (useClassification)
        {
            return BuildFromTimedClassification(
                configuration,
                serverConfiguration,
                raceSession,
                raceStartersAtGreen,
                raceStartedAtUtc,
                raceFinishedAtUtc,
                timedRaceClassification!);
        }

        return BuildLegacy(
            configuration,
            serverConfiguration,
            raceSession,
            raceStartersAtGreen,
            raceStartedAtUtc,
            raceFinishedAtUtc,
            disconnectedSteamIdsDuringRace);
    }

    private static MatchReportPayload BuildFromTimedClassification(
        RankedMatchReporterConfiguration configuration,
        ACServerConfiguration serverConfiguration,
        SessionState raceSession,
        IReadOnlyList<RaceStarterSnapshot> raceStartersAtGreen,
        DateTime raceStartedAtUtc,
        DateTime raceFinishedAtUtc,
        RaceClassificationResult timedRaceClassification)
    {
        var results = raceSession.Results ?? new Dictionary<byte, EntryCarResult>();
        var classificationBySteam = timedRaceClassification.Participants
            .ToDictionary(p => p.SteamId);

        var participants = new List<MatchParticipantPayload>();

        foreach (var starter in raceStartersAtGreen)
        {
            if (!classificationBySteam.TryGetValue(starter.SteamId, out var row))
                continue;

            var gridSlotIndex = ResolveGridSlotIndex(raceSession, results, starter.SteamId);

            participants.Add(new MatchParticipantPayload
            {
                SteamId = starter.SteamId.ToString(CultureInfo.InvariantCulture),
                Username = row.Username.Length > 0 ? row.Username : starter.Username,
                FinishPosition = row.FinishPosition,
                Dnf = row.Dnf,
                GridPosition = gridSlotIndex >= 0 ? gridSlotIndex + 1 : null,
                NumLaps = row.NumLaps,
                BestLapMs = row.BestLapMs,
                TotalRaceTimeMs = row.TotalRaceTimeMs
            });
        }

        var rankingParticipants = configuration.ExcludeZeroLapDriversFromRanking
            ? participants.Where(p => p.NumLaps > 0).ToList()
            : participants;

        return WrapPayload(
            configuration,
            serverConfiguration,
            raceStartedAtUtc,
            raceFinishedAtUtc,
            raceStartersAtGreen.Count,
            rankingParticipants);
    }

    private static MatchReportPayload BuildLegacy(
        RankedMatchReporterConfiguration configuration,
        ACServerConfiguration serverConfiguration,
        SessionState raceSession,
        IReadOnlyList<RaceStarterSnapshot> raceStartersAtGreen,
        DateTime raceStartedAtUtc,
        DateTime raceFinishedAtUtc,
        IReadOnlySet<ulong> disconnectedSteamIdsDuringRace)
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

            var disconnectedDuringRace = disconnectedSteamIdsDuringRace.Contains(starter.SteamId);
            participants.Add(BuildParticipantFromResult(
                starter,
                result,
                fieldSize,
                raceSession,
                results,
                disconnectedDuringRace));
        }

        // When enabled, send only drivers who completed at least one lap — brain ranks everyone in participants[].
        var rankingParticipants = configuration.ExcludeZeroLapDriversFromRanking
            ? participants.Where(p => p.NumLaps > 0).ToList()
            : participants;

        rankingParticipants = NormalizeFinishPositions(rankingParticipants);

        return WrapPayload(
            configuration,
            serverConfiguration,
            raceStartedAtUtc,
            raceFinishedAtUtc,
            raceStartersAtGreen.Count,
            rankingParticipants);
    }

    private static MatchReportPayload WrapPayload(
        RankedMatchReporterConfiguration configuration,
        ACServerConfiguration serverConfiguration,
        DateTime raceStartedAtUtc,
        DateTime raceFinishedAtUtc,
        int greenFlagStarterCount,
        List<MatchParticipantPayload> rankingParticipants)
    {
        var inPeakWindow = PeakWindowEvaluator.IsInPeakWindow(
            configuration.PeakWindow,
            raceStartedAtUtc);
        // Min drivers is decided at green — mid-race leaves / zero-lap drops must not un-count the race.
        var countedForRanked = greenFlagStarterCount >= configuration.MinimumDriversForRanked
            && (!configuration.PeakWindow.Enabled || inPeakWindow);

        var track = serverConfiguration.Server.Track;
        var layout = serverConfiguration.Server.TrackConfig ?? "";

        return new MatchReportPayload
        {
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

    private static int ResolveGridSlotIndex(
        SessionState raceSession,
        Dictionary<byte, EntryCarResult> results,
        ulong steamId)
    {
        var gridSlotIndex = raceSession.Grid?
            .Select((car, index) => (car, index))
            .FirstOrDefault(x => results.TryGetValue(x.car.SessionId, out var slotResult)
                                   && slotResult.Guid == steamId)
            .index;

        return gridSlotIndex ?? -1;
    }

    /// <summary>
    /// BuildParticipantFromResult — map one starter's Results row into a participant payload.
    /// </summary>
    private static MatchParticipantPayload BuildParticipantFromResult(
        RaceStarterSnapshot starter,
        EntryCarResult result,
        int fieldSize,
        SessionState raceSession,
        Dictionary<byte, EntryCarResult> results,
        bool disconnectedDuringRace)
    {
        // Walk Results slot keys to find this driver's quali grid index.
        var gridSlotIndex = raceSession.Grid?
            .Select((car, index) => (car, index))
            .FirstOrDefault(x => results.TryGetValue(x.car.SessionId, out var slotResult)
                                   && slotResult.Guid == starter.SteamId)
            .index;

        var username = result.Name.Length > 0 ? result.Name : starter.Username;
        var numLaps = (int)result.NumLaps;
        var bestLapMs = numLaps > 0 ? ToLapMs(result.BestLap) : null;
        var totalRaceTimeMs = result.TotalTime > 0 ? (int?)result.TotalTime : null;

        // Zero laps or mid-race disconnect → DNF at the back (lap stats kept when the row still exists).
        if (disconnectedDuringRace || result.NumLaps == 0)
        {
            return new MatchParticipantPayload
            {
                SteamId = starter.SteamId.ToString(CultureInfo.InvariantCulture),
                Username = username,
                FinishPosition = fieldSize,
                Dnf = true,
                GridPosition = gridSlotIndex >= 0 ? gridSlotIndex + 1 : null,
                NumLaps = numLaps,
                BestLapMs = bestLapMs,
                TotalRaceTimeMs = totalRaceTimeMs
            };
        }

        // AssettoServer RacePos is 0-based (leader = 0); ingest expects 1-based finish position.
        var finishPosition = (int)result.RacePos + 1;

        return new MatchParticipantPayload
        {
            SteamId = starter.SteamId.ToString(CultureInfo.InvariantCulture),
            Username = username,
            FinishPosition = finishPosition,
            // One or more laps with a classified position — finisher even if overtime ended before the last lap.
            Dnf = false,
            GridPosition = gridSlotIndex >= 0 ? gridSlotIndex + 1 : null,
            NumLaps = numLaps,
            BestLapMs = bestLapMs,
            TotalRaceTimeMs = totalRaceTimeMs
        };
    }

    /// <summary>
    /// NormalizeFinishPositions — sort finishers by laps/time, assign dense ranks 1..N, tie all DNFs at last.
    /// </summary>
    private static List<MatchParticipantPayload> NormalizeFinishPositions(
        IReadOnlyList<MatchParticipantPayload> participants)
    {
        if (participants.Count == 0)
            return [];

        var finishers = participants
            .Where(p => !p.Dnf)
            .OrderByDescending(p => p.NumLaps)
            .ThenBy(p => p.TotalRaceTimeMs ?? int.MaxValue)
            .ThenBy(p => p.BestLapMs ?? int.MaxValue)
            .ThenBy(p => p.FinishPosition)
            .ToList();

        var dnfs = participants.Where(p => p.Dnf).ToList();
        var dnfRank = finishers.Count > 0 ? finishers.Count + 1 : 1;

        var normalized = new List<MatchParticipantPayload>(participants.Count);

        for (var index = 0; index < finishers.Count; index++)
            normalized.Add(WithFinishPosition(finishers[index], index + 1));

        foreach (var dnf in dnfs)
            normalized.Add(WithFinishPosition(dnf, dnfRank));

        return normalized;
    }

    private static MatchParticipantPayload WithFinishPosition(MatchParticipantPayload participant, int finishPosition) =>
        new()
        {
            SteamId = participant.SteamId,
            Username = participant.Username,
            FinishPosition = finishPosition,
            Dnf = participant.Dnf,
            GridPosition = participant.GridPosition,
            NumLaps = participant.NumLaps,
            BestLapMs = participant.BestLapMs,
            TotalRaceTimeMs = participant.TotalRaceTimeMs
        };

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
/// 1. If PeakWindow.Enabled is false, return true (race counts 24/7 by time gate).
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
