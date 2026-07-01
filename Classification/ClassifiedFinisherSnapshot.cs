namespace RankedMatchReporterPlugin.Classification;

/// <summary>
/// ClassifiedFinisherSnapshot — frozen row for a driver who completed the cap lap count.
///
/// Logic flow:
/// 1. Written once when the driver crosses with NumLaps == cap.
/// 2. MatchReportBuilder reads these values instead of late EntryCarResult rows.
/// </summary>
public sealed class ClassifiedFinisherSnapshot
{
    public required ulong SteamId { get; init; }
    public required string Username { get; init; }
    public required int FinishPosition { get; init; }
    public required int NumLaps { get; init; }
    public int? TotalRaceTimeMs { get; init; }
    public int? BestLapMs { get; init; }
}
