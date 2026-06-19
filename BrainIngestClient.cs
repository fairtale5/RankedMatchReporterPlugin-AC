using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RankedMatchReporterPlugin.Models;
using Serilog;

namespace RankedMatchReporterPlugin;

/// <summary>
/// BrainIngestClient — sends MatchReportPayload to serv-brain over HTTP JSON.
/// BrainIngestClient — serializes MatchReportPayload and POSTs JSON to serv-brain IngestUrl.
///
/// Logic flow:
/// 1. Serialize payload with System.Text.Json (snake_case via JsonPropertyName on DTO).
/// 2. If DryRun, log JSON and return without network call.
/// 3. Else POST to IngestUrl with optional Bearer ApiKey; log success or HTTP error body.
///
/// There is no shared FastFox client library — this class is the transport layer.
/// Contract: serv-db/docs/ranked-system-data-plan.md and Models/MatchReportPayload.cs.
/// </summary>
public sealed class BrainIngestClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = true
    };

    private readonly RankedMatchReporterConfiguration _configuration;
    private readonly HttpClient _httpClient;

    public BrainIngestClient(RankedMatchReporterConfiguration configuration)
    {
        _configuration = configuration;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public async Task SendAsync(MatchReportPayload payload, CancellationToken cancellationToken)
    {
        // Write DTO to JSON string; property names come from [JsonPropertyName] on MatchReportPayload.
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

        if (!string.IsNullOrWhiteSpace(_configuration.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _configuration.ApiKey);

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

    public void Dispose() => _httpClient.Dispose();
}
