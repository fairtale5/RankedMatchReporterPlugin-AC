using RankedMatchReporterPlugin.Models;

namespace RankedMatchReporterPlugin.Classification;

/// <summary>
/// TimedRaceClassificationEngine — pure cap-lap classification rules (no server hooks).
///
/// Logic flow:
/// 1. TryRecordLapCrossing — cap detection and finish-order assignment on each counted lap.
/// 2. FinalizeAtRaceOver — dedupe stragglers by Steam ID (best progress), place classified
///    finishers first, then stragglers by laps + spline, then DNFs tied at last.
/// 3. Never throw on duplicate Steam IDs — one ranked row per Steam ID.
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

        // Same Steam already locked (e.g. second live slot hits cap): demote behind other classified
        // finishers and keep slower times (blocks multi-client "keep best place/time").
        if (state.ClassifiedFinishers.TryGetValue(steamId, out var existing))
        {
            state.ClassifiedFinishers[steamId] = WithWorseClassifiedOutcome(
                state,
                existing,
                totalRaceTimeMs,
                bestLapMs);
            return default;
        }

        return Classify(state, steamId, username, numLaps, totalRaceTimeMs, bestLapMs);
    }

    /// <summary>
    /// DeduplicateStragglersByBestProgress — one probe per Steam ID; keep most laps then distance.
    /// </summary>
    public static IReadOnlyList<StragglerProbe> DeduplicateStragglersByBestProgress(
        IEnumerable<StragglerProbe> stragglers)
    {
        return stragglers
            .GroupBy(s => s.SteamId)
            .Select(group => group
                .OrderByDescending(s => s.NumLaps + s.NormalizedPosition)
                .ThenBy(s => s.TotalRaceTimeMs ?? int.MaxValue)
                .ThenBy(s => s.BestLapMs ?? int.MaxValue)
                .First())
            .ToList();
    }

    /// <summary>
    /// BuildFromClassifiedOnly — fallback when straggler merge fails; keep locked finishers only.
    /// </summary>
    public static RaceClassificationResult BuildFromClassifiedOnly(TimedRaceClassificationState state)
    {
        if (state.CapLaps == null || state.ClassifiedFinishers.Count == 0)
            return RaceClassificationResult.NotUsed;

        var rows = state.ClassifiedFinishers.Values
            .OrderBy(f => f.FinishPosition)
            .Select((finisher, index) => new ClassificationParticipantRow
            {
                SteamId = finisher.SteamId,
                Username = finisher.Username,
                FinishPosition = index + 1,
                Dnf = false,
                NumLaps = finisher.NumLaps,
                TotalRaceTimeMs = finisher.TotalRaceTimeMs,
                BestLapMs = finisher.BestLapMs
            })
            .ToList();

        return new RaceClassificationResult(true, rows);
    }

    public static RaceClassificationResult FinalizeAtRaceOver(
        TimedRaceClassificationState state,
        IReadOnlyList<RaceStarterSnapshot> starters,
        IReadOnlyList<StragglerProbe> stragglers,
        IReadOnlySet<ulong> disconnectedDuringRace)
    {
        if (state.CapLaps == null || state.FinalizedAtRaceOver)
            return RaceClassificationResult.NotUsed;

        // One probe per Steam — duplicate live slots must not abort finalize.
        var uniqueStragglers = DeduplicateStragglersByBestProgress(stragglers);

        var rows = new List<ClassificationParticipantRow>(starters.Count);

        // Classified finishers first (crossing order); densify 1..N in case of gaps.
        var classifiedOrdered = state.ClassifiedFinishers.Values
            .OrderBy(f => f.FinishPosition)
            .ToList();

        for (var index = 0; index < classifiedOrdered.Count; index++)
        {
            var finisher = classifiedOrdered[index];
            rows.Add(new ClassificationParticipantRow
            {
                SteamId = finisher.SteamId,
                Username = finisher.Username,
                FinishPosition = index + 1,
                Dnf = false,
                NumLaps = finisher.NumLaps,
                TotalRaceTimeMs = finisher.TotalRaceTimeMs,
                BestLapMs = finisher.BestLapMs
            });
        }

        var nextPosition = rows.Count + 1;
        var classifiedSteamIds = state.ClassifiedFinishers.Keys.ToHashSet();

        // Connected stragglers: best progress per Steam, not already classified, not marked disconnected.
        var orderedStragglers = uniqueStragglers
            .Where(s => !classifiedSteamIds.Contains(s.SteamId))
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

        // Index probes by Steam without throwing when a caller still passed duplicates.
        var stragglerBySteam = uniqueStragglers
            .GroupBy(s => s.SteamId)
            .ToDictionary(g => g.Key, g => g.First());

        var placedSteamIds = rows.Select(r => r.SteamId).ToHashSet();
        var uniqueStarters = starters
            .GroupBy(s => s.SteamId)
            .Select(g => g.First())
            .ToList();

        foreach (var starter in uniqueStarters)
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

        // Mark finalized only after a full table exists — a throw before this must allow retry.
        state.FinalizedAtRaceOver = true;
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

    /// <summary>
    /// WithWorseClassifiedOutcome — second cap crossing for the same Steam: sort after every other
    /// classified finisher (densify at race over rewrites 1..N) and keep slower times.
    /// </summary>
    private static ClassifiedFinisherSnapshot WithWorseClassifiedOutcome(
        TimedRaceClassificationState state,
        ClassifiedFinisherSnapshot existing,
        int? totalRaceTimeMs,
        int? bestLapMs)
    {
        var lastClassifiedPlace = state.ClassifiedFinishers.Values.Max(f => f.FinishPosition);
        return new ClassifiedFinisherSnapshot
        {
            SteamId = existing.SteamId,
            Username = existing.Username,
            FinishPosition = lastClassifiedPlace + 1,
            NumLaps = existing.NumLaps,
            TotalRaceTimeMs = MaxNullableTime(existing.TotalRaceTimeMs, totalRaceTimeMs),
            BestLapMs = MaxNullableTime(existing.BestLapMs, bestLapMs)
        };
    }

    private static int? MaxNullableTime(int? a, int? b)
    {
        if (a == null)
            return b;
        if (b == null)
            return a;
        return Math.Max(a.Value, b.Value);
    }
}
