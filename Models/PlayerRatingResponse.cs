using System.Text.Json.Serialization;

namespace RankedMatchReporterPlugin.Models;

/// <summary>
/// PlayerRatingResponse — JSON from GET /v1/players/{steam_id}/rating.
/// </summary>
public sealed class PlayerRatingResponse
{
    [JsonPropertyName("league_id")]
    public string LeagueId { get; init; } = "";

    [JsonPropertyName("steam_id")]
    public string SteamId { get; init; } = "";

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("rating")]
    public int Rating { get; init; }

    [JsonPropertyName("league_rank")]
    public int? LeagueRank { get; init; }

    [JsonPropertyName("players_in_league")]
    public int PlayersInLeague { get; init; }

    [JsonPropertyName("last_race")]
    public LastRaceResult? LastRace { get; init; }

    public bool IsRanked => Rating > 0 && LeagueRank is > 0;
}

/// <summary>
/// LastRaceResult — most recent match in this league for profile chat lines.
/// </summary>
public sealed class LastRaceResult
{
    [JsonPropertyName("finish_position")]
    public int FinishPosition { get; init; }

    [JsonPropertyName("rating_before")]
    public int RatingBefore { get; init; }

    [JsonPropertyName("rating_delta")]
    public int RatingDelta { get; init; }

    [JsonPropertyName("rating_after")]
    public int RatingAfter { get; init; }

    [JsonPropertyName("counted_for_ranked")]
    public bool CountedForRanked { get; init; }
}
