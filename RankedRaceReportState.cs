namespace RankedMatchReporterPlugin;

/// <summary>
/// RankedRaceReportState — holds the match_id of the race report just built for this server process.
/// Rating notices after a race poll GET until last_race.match_id matches this value.
/// </summary>
public sealed class RankedRaceReportState
{
    private string? _lastReportedMatchId;

    public string? LastReportedMatchId => _lastReportedMatchId;

    public void SetLastReportedMatchId(string matchId) =>
        _lastReportedMatchId = matchId;

    public void ClearLastReportedMatchId() =>
        _lastReportedMatchId = null;
}
