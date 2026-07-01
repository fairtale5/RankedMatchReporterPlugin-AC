namespace RankedMatchReporterPlugin.Classification;

/// <summary>
/// RaceClassificationResult — immutable finish table for one timed race.
///
/// Logic flow:
/// 1. Built at RaceOver when cap lap count was set and stragglers were snapshotted.
/// 2. Passed to MatchReportBuilder instead of stock RacePos when IsUsable is true.
/// </summary>
public sealed class RaceClassificationResult
{
    public static RaceClassificationResult NotUsed { get; } = new(false, []);

    public bool IsUsable { get; }
    public IReadOnlyList<ClassificationParticipantRow> Participants { get; }

    public RaceClassificationResult(bool isUsable, IReadOnlyList<ClassificationParticipantRow> participants)
    {
        IsUsable = isUsable;
        Participants = participants;
    }
}

/// <summary>
/// ClassificationParticipantRow — one starter's classified outcome for ingest.
/// </summary>
public sealed class ClassificationParticipantRow
{
    public required ulong SteamId { get; init; }
    public required string Username { get; init; }
    public required int FinishPosition { get; init; }
    public required bool Dnf { get; init; }
    public required int NumLaps { get; init; }
    public int? TotalRaceTimeMs { get; init; }
    public int? BestLapMs { get; init; }
}
