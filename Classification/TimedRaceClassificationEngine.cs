using RankedMatchReporterPlugin.Models;

namespace RankedMatchReporterPlugin.Classification;

/// <summary>
/// TimedRaceClassificationEngine — pure cap-lap classification rules (no server hooks).
///
/// Logic flow:
/// 1. TryRecordLapCrossing — cap detection and finish-order assignment on each counted lap.
/// 2. FinalizeAtRaceOver — sort stragglers by NumLaps + spline; merge DNFs tied at last.
/// </summary>
public static class TimedRaceClassificationEngine
{
    private const uint InvalidLapSentinel = 999999999;

    public readonly record struct LapCrossingOutcome(bool NewlyClassified, int FinishPosition);

    public readonly record struct StragglerProbe(
        ulong SteamId,
        string Username,
        int NumLaps,
        float NormalizedPosition,
        int? TotalRaceTimeMs,
        int? BestLapMs);

    public static LapCrossingOutcome TryRecordLapCrossing(
        TimedRaceClassificationState state,
        ulong steamId,
        string username,
        int numLaps,
        int? totalRaceTimeMs,
        int? bestLapMs)
    {
        if (state.FinalizedAtRaceOver)
            return default;

        if (state.ClassifiedFinishers.ContainsKey(steamId))
            return default;

        if (state.LeaderLapsAtClock == null)
            return default;

        var leaderLapsAtClock = (int)state.LeaderLapsAtClock.Value;

        if (state.CapLaps == null)
        {
            if (numLaps <= leaderLapsAtClock)
                return default;

            state.CapLaps = (uint)numLaps;
            return Classify(state, steamId, username, numLaps, totalRaceTimeMs, bestLapMs);
        }

        var cap = (int)state.CapLaps.Value;

        if (numLaps > cap)
            return default;

        if (numLaps < cap)
            return default;

        return Classify(state, steamId, username, numLaps, totalRaceTimeMs, bestLapMs);
    }

    public static RaceClassificationResult FinalizeAtRaceOver(
        TimedRaceClassificationState state,
        IReadOnlyList<RaceStarterSnapshot> starters,
        IReadOnlyList<StragglerProbe> stragglers,
        IReadOnlySet<ulong> disconnectedDuringRace)
    {
        if (state.CapLaps == null || state.FinalizedAtRaceOver)
            return RaceClassificationResult.NotUsed;

        state.FinalizedAtRaceOver = true;

        var rows = new List<ClassificationParticipantRow>(starters.Count);
        var classifiedCount = state.ClassifiedFinishers.Count;

        foreach (var finisher in state.ClassifiedFinishers.Values.OrderBy(f => f.FinishPosition))
        {
            rows.Add(new ClassificationParticipantRow
            {
                SteamId = finisher.SteamId,
                Username = finisher.Username,
                FinishPosition = finisher.FinishPosition,
                Dnf = false,
                NumLaps = finisher.NumLaps,
                TotalRaceTimeMs = finisher.TotalRaceTimeMs,
                BestLapMs = finisher.BestLapMs
            });
        }

        var nextPosition = classifiedCount + 1;

        var orderedStragglers = stragglers
            .Where(s => !state.ClassifiedFinishers.ContainsKey(s.SteamId))
            .Where(s => !disconnectedDuringRace.Contains(s.SteamId))
            .OrderByDescending(s => s.NumLaps + s.NormalizedPosition)
            .ThenBy(s => s.TotalRaceTimeMs ?? int.MaxValue)
            .ThenBy(s => s.BestLapMs ?? int.MaxValue)
            .ToList();

        foreach (var straggler in orderedStragglers)
        {
            rows.Add(new ClassificationParticipantRow
            {
                SteamId = straggler.SteamId,
                Username = straggler.Username,
                FinishPosition = nextPosition++,
                Dnf = false,
                NumLaps = straggler.NumLaps,
                TotalRaceTimeMs = straggler.TotalRaceTimeMs,
                BestLapMs = straggler.BestLapMs
            });
        }

        var placedSteamIds = rows.Select(r => r.SteamId).ToHashSet();
        var stragglerBySteam = stragglers.ToDictionary(s => s.SteamId);

        foreach (var starter in starters)
        {
            if (placedSteamIds.Contains(starter.SteamId))
                continue;

            var hasProbe = stragglerBySteam.TryGetValue(starter.SteamId, out var probe);
            rows.Add(new ClassificationParticipantRow
            {
                SteamId = starter.SteamId,
                Username = hasProbe && probe.Username.Length > 0 ? probe.Username : starter.Username,
                FinishPosition = 0,
                Dnf = true,
                NumLaps = hasProbe ? probe.NumLaps : 0,
                TotalRaceTimeMs = hasProbe ? probe.TotalRaceTimeMs : null,
                BestLapMs = hasProbe ? probe.BestLapMs : null
            });
        }

        var finishers = rows.Where(r => !r.Dnf).OrderBy(r => r.FinishPosition).ToList();
        var dnfs = rows.Where(r => r.Dnf).ToList();
        var tiedDnfRank = finishers.Count > 0 ? finishers.Max(r => r.FinishPosition) + 1 : 1;

        var normalized = new List<ClassificationParticipantRow>(rows.Count);
        normalized.AddRange(finishers);
        foreach (var dnf in dnfs)
        {
            normalized.Add(new ClassificationParticipantRow
            {
                SteamId = dnf.SteamId,
                Username = dnf.Username,
                FinishPosition = tiedDnfRank,
                Dnf = true,
                NumLaps = dnf.NumLaps,
                TotalRaceTimeMs = dnf.TotalRaceTimeMs,
                BestLapMs = dnf.BestLapMs
            });
        }

        return new RaceClassificationResult(true, normalized);
    }

    public static int? ToLapMs(uint lapTime)
    {
        if (lapTime == 0 || lapTime >= InvalidLapSentinel)
            return null;

        return (int)lapTime;
    }

    private static LapCrossingOutcome Classify(
        TimedRaceClassificationState state,
        ulong steamId,
        string username,
        int numLaps,
        int? totalRaceTimeMs,
        int? bestLapMs)
    {
        var position = state.ClassifiedFinishers.Count + 1;
        state.ClassifiedFinishers[steamId] = new ClassifiedFinisherSnapshot
        {
            SteamId = steamId,
            Username = username,
            FinishPosition = position,
            NumLaps = numLaps,
            TotalRaceTimeMs = totalRaceTimeMs,
            BestLapMs = bestLapMs
        };

        return new LapCrossingOutcome(true, position);
    }
}
