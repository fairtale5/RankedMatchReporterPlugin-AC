using System.Text.Json.Serialization;

namespace RankedMatchReporterPlugin.Models;

/// <summary>
/// MatchReportPayload — JSON body for serv-brain POST /v1/races.
///
/// Logic flow:
/// 1. MatchReportBuilder fills this DTO from SessionState.Results and yaml configuration.
/// 2. BrainIngestClient serializes with System.Text.Json using snake_case property names below.
/// 3. serv-brain ingest reads the same JSON shape (see serv-db/docs/ranked-system-data-plan.md § Ingest payload).
/// </summary>
public sealed class MatchReportPayload
{
    [JsonPropertyName("match_id")]
    public required string MatchId { get; init; }

    [JsonPropertyName("league_id")]
    public required string LeagueId { get; init; }

    [JsonPropertyName("server_id")]
    public required string ServerId { get; init; }

    [JsonPropertyName("track_id")]
    public required string TrackId { get; init; }

    [JsonPropertyName("layout_id")]
    public required string LayoutId { get; init; }

    [JsonPropertyName("started_at")]
    public required string StartedAt { get; init; }

    [JsonPropertyName("finished_at")]
    public required string FinishedAt { get; init; }

    [JsonPropertyName("counted_for_ranked")]
    public required bool CountedForRanked { get; init; }

    [JsonPropertyName("participants")]
    public required List<MatchParticipantPayload> Participants { get; init; }
}

/// <summary>
/// MatchParticipantPayload — one driver inside participants[] in the ingest JSON.
///
/// Logic flow:
/// MatchReportBuilder sets each field from one EntryCarResult row (Guid, Name, RacePos, laps, times).
/// </summary>
public sealed class MatchParticipantPayload
{
    [JsonPropertyName("steam_id")]
    public required string SteamId { get; init; }

    [JsonPropertyName("username")]
    public required string Username { get; init; }

    [JsonPropertyName("finish_position")]
    public required int FinishPosition { get; init; }

    [JsonPropertyName("dnf")]
    public required bool Dnf { get; init; }

    [JsonPropertyName("grid_position")]
    public int? GridPosition { get; init; }

    [JsonPropertyName("num_laps")]
    public required int NumLaps { get; init; }

    [JsonPropertyName("best_lap_ms")]
    public int? BestLapMs { get; init; }

    [JsonPropertyName("total_race_time_ms")]
    public int? TotalRaceTimeMs { get; init; }
}
