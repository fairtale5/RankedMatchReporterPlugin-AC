using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RankedMatchReporterPlugin.Models;
using Serilog;

namespace RankedMatchReporterPlugin;

/// <summary>
/// BrainApiClient — HTTP client for serv-brain ingest (POST) and rating reads (GET).
///
/// Logic flow:
/// 1. Serialize MatchReportPayload and POST to IngestUrl (snake_case via JsonPropertyName on DTOs).
/// 2. GET /v1/players/{steam_id}/rating?league_id=… for join welcome and other read paths.
/// 3. If DryRun, log race JSON and skip POST only — GET still runs when brain is up.
/// 4. Optional Bearer ApiKey on every request.
/// </summary>
public sealed class BrainApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly CultureInfo RatingFormatCulture = CreateRatingFormatCulture();

    private static CultureInfo CreateRatingFormatCulture()
    {
        try
        {
            return CultureInfo.GetCultureInfo("pt-BR");
        }
        catch (CultureNotFoundException)
        {
            Log.Warning(
                "RankedMatchReporterPlugin: pt-BR culture not installed on this host; using dot thousands separator");
            var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            culture.NumberFormat.NumberGroupSeparator = ".";
            culture.NumberFormat.NumberDecimalSeparator = ",";
            return culture;
        }
    }

    private readonly RankedMatchReporterConfiguration _configuration;
    private readonly HttpClient _httpClient;

    public BrainApiClient(RankedMatchReporterConfiguration configuration)
    {
        _configuration = configuration;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public async Task SendRaceAsync(MatchReportPayload payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);

        if (_configuration.DryRun)
        {
            Log.Information("RankedMatchReporterPlugin dry-run payload:\n{Payload}", json);
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _configuration.IngestUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        ApplyAuthHeader(request);

        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Log.Error(
                "RankedMatchReporterPlugin ingest failed: {StatusCode} {Body}",
                (int)response.StatusCode,
                body);
            return;
        }

        Log.Information(
            "RankedMatchReporterPlugin posted match {MatchId} ({ParticipantCount} drivers, counted={Counted})",
            payload.MatchId,
            payload.Participants.Count,
            payload.CountedForRanked);
    }

    public async Task<PlayerRatingResponse?> GetPlayerRatingAsync(
        ulong steamId,
        CancellationToken cancellationToken)
    {
        var url =
            $"{GetBrainRootUrl()}/v1/players/{steamId.ToString(CultureInfo.InvariantCulture)}/rating?league_id={Uri.EscapeDataString(_configuration.LeagueId)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuthHeader(request);

        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Log.Warning(
                "RankedMatchReporterPlugin rating query failed: {StatusCode} {Body}",
                (int)response.StatusCode,
                body);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<PlayerRatingResponse>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public static string FormatRating(int rating) =>
        rating.ToString("N0", RatingFormatCulture);

    public static string FormatDelta(int delta) =>
        delta >= 0
            ? $"+{delta.ToString(CultureInfo.InvariantCulture)}"
            : delta.ToString(CultureInfo.InvariantCulture);

    private void ApplyAuthHeader(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_configuration.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _configuration.ApiKey);
    }

    private string GetBrainRootUrl()
    {
        var ingestUri = new Uri(_configuration.IngestUrl);
        return $"{ingestUri.Scheme}://{ingestUri.Authority}";
    }

    public void Dispose() => _httpClient.Dispose();
}
