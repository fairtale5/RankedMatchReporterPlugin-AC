namespace RankedMatchReporterPlugin.Classification;

/// <summary>
/// TimedRaceClassificationState — mutable per-race classification state.
///
/// Logic flow:
/// 1. Reset at race green.
/// 2. LeaderLapsAtClock set when session clock expires (SessionOverFlag).
/// 3. CapLaps set on first lap crossing above LeaderLapsAtClock.
/// 4. ClassifiedFinishers grows on each cap-lap crossing.
/// 5. FinalizedAtRaceOver set when stragglers are snapshotted at SendSessionOver.
/// </summary>
public sealed class TimedRaceClassificationState
{
    public uint? LeaderLapsAtClock { get; set; }
    public uint? CapLaps { get; set; }
    public bool FinalizedAtRaceOver { get; set; }
    public Dictionary<ulong, ClassifiedFinisherSnapshot> ClassifiedFinishers { get; } = new();
}
