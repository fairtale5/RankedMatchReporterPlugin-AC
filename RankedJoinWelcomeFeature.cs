using AssettoServer.Network.Tcp;
using AssettoServer.Server;
using AssettoServer.Shared.Model;
using Serilog;

namespace RankedMatchReporterPlugin;

/// <summary>
/// RankedJoinWelcomeFeature — private chat with rank, rating, and last race delta.
///
/// Logic flow:
/// 1. On join: GET rating immediately and send notice.
/// 2. On session start after a race: wait configured delay, poll GET until last_race.match_id matches the report just sent, then send notice.
/// 3. On other session starts: GET immediately.
/// </summary>
public sealed class RankedJoinWelcomeFeature : IDisposable
{
    private const string RatingsUnavailableMessage = "Ratings currently unavailable";

    private readonly RankedMatchReporterConfiguration _configuration;
    private readonly SessionManager _sessionManager;
    private readonly EntryCarManager _entryCarManager;
    private readonly BrainApiClient _brainApi;
    private readonly RankedRaceReportState _reportState;
    private CancellationTokenSource? _sessionNoticeCts;

    public RankedJoinWelcomeFeature(
        RankedMatchReporterConfiguration configuration,
        SessionManager sessionManager,
        EntryCarManager entryCarManager,
        BrainApiClient brainApi,
        RankedRaceReportState reportState)
    {
        _configuration = configuration;
        _sessionManager = sessionManager;
        _entryCarManager = entryCarManager;
        _brainApi = brainApi;
        _reportState = reportState;

        if (_configuration.SendRatingNoticeOnJoin)
            _entryCarManager.ClientConnected += OnClientConnected;

        if (_configuration.SendRatingNoticeAtSessionStart)
            _sessionManager.SessionChanged += OnSessionChanged;
    }

    private void OnClientConnected(ACTcpClient client, EventArgs args) =>
        client.FirstUpdateSent += OnClientFirstUpdateSent;

    private void OnClientFirstUpdateSent(ACTcpClient client, EventArgs args)
    {
        client.FirstUpdateSent -= OnClientFirstUpdateSent;
        _ = SendNoticeAsync(client, expectedMatchId: null);
    }

    private void OnSessionChanged(SessionManager sender, SessionChangedEventArgs args)
    {
        CancelPendingSessionNotices();

        var clients = _entryCarManager.EntryCars
            .Select(car => car.Client)
            .Where(client => client is { HasSentFirstUpdate: true })
            .Cast<ACTcpClient>()
            .ToList();

        if (clients.Count == 0)
            return;

        var expectedMatchId = args.PreviousSession?.Configuration.Type == SessionType.Race
                              && args.PreviousSession.HasSentRaceOverPacket
            ? _reportState.LastReportedMatchId
            : null;

        if (expectedMatchId != null)
        {
            _sessionNoticeCts = new CancellationTokenSource();
            var token = _sessionNoticeCts.Token;
            _ = SendSessionNoticesAfterRaceAsync(clients, expectedMatchId, token);
            return;
        }

        foreach (var client in clients)
            _ = SendNoticeAsync(client, expectedMatchId: null);
    }

    private async Task SendSessionNoticesAfterRaceAsync(
        IReadOnlyList<ACTcpClient> clients,
        string expectedMatchId,
        CancellationToken cancellationToken)
    {
        try
        {
            var delayMs = _configuration.RatingNoticeDelayAfterRaceSeconds * 1000;
            if (delayMs > 0)
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);

            foreach (var client in clients)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                await SendNoticeAsync(client, expectedMatchId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // New session started before notices finished — drop pending batch.
        }
    }

    private async Task SendNoticeAsync(
        ACTcpClient client,
        string? expectedMatchId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var rating = await WaitForRatingAsync(client.Guid, expectedMatchId, cancellationToken)
                .ConfigureAwait(false);

            var message = rating == null
                ? RatingsUnavailableMessage
                : BuildNoticeMessage(client.Name ?? "Driver", rating, expectedMatchId);

            client.SendChatMessage(message);
        }
        catch (OperationCanceledException)
        {
            // Session batch cancelled — do not send stale notice.
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RankedMatchReporterPlugin: rating notice failed for {Name}", client.Name);
            client.SendChatMessage(RatingsUnavailableMessage);
        }
    }

    private async Task<Models.PlayerRatingResponse?> WaitForRatingAsync(
        ulong steamId,
        string? expectedMatchId,
        CancellationToken cancellationToken)
    {
        if (expectedMatchId == null)
            return await _brainApi.GetPlayerRatingAsync(steamId, cancellationToken).ConfigureAwait(false);

        var deadline = DateTime.UtcNow.AddSeconds(_configuration.RatingNoticeMaxWaitForRaceResultsSeconds);
        var pollMs = _configuration.RatingNoticePollIntervalSeconds * 1000;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rating = await _brainApi.GetPlayerRatingAsync(steamId, cancellationToken).ConfigureAwait(false);
            if (rating == null)
                return null;

            if (rating.LastRace != null
                && string.Equals(rating.LastRace.MatchId, expectedMatchId, StringComparison.OrdinalIgnoreCase))
            {
                return rating;
            }

            if (DateTime.UtcNow >= deadline)
            {
                Log.Warning(
                    "RankedMatchReporterPlugin: timed out waiting for processed race {MatchId} for steam {SteamId}",
                    expectedMatchId,
                    steamId);
                return rating;
            }

            if (pollMs > 0)
                await Task.Delay(pollMs, cancellationToken).ConfigureAwait(false);
        }
    }

    private string BuildNoticeMessage(
        string clientName,
        Models.PlayerRatingResponse rating,
        string? expectedMatchId)
    {
        var leagueLabel = _configuration.LeagueDisplayName;
        var minDrivers = _configuration.MinimumDriversForRanked;
        var lines = new List<string>();

        if (rating.IsRanked)
        {
            var formattedRating = BrainApiClient.FormatRating(rating.Rating);
            lines.Add($"{clientName}: Rank: #{rating.LeagueRank} | Rating {formattedRating}.");
        }
        else
        {
            lines.Add($"Hi {clientName}! You don't have a rank in {leagueLabel} yet.");
            lines.Add($"Complete a full race ({minDrivers}+ drivers) to earn your first points.");
        }

        if (TryBuildLastRaceLine(rating, expectedMatchId, out var lastRaceLine))
            lines.Add(lastRaceLine);

        return string.Join("\n", lines);
    }

    private static bool TryBuildLastRaceLine(
        Models.PlayerRatingResponse rating,
        string? expectedMatchId,
        out string line)
    {
        line = "";

        if (rating.LastRace == null)
            return false;

        if (expectedMatchId != null
            && !string.Equals(rating.LastRace.MatchId, expectedMatchId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var was = BrainApiClient.FormatRating(rating.LastRace.RatingBefore);
        var delta = BrainApiClient.FormatDelta(rating.LastRace.RatingDelta);
        var result = BrainApiClient.FormatRating(rating.LastRace.RatingAfter);
        var resultTag = rating.LastRace.Dnf
            ? "[abandoned]"
            : $"[P{rating.LastRace.FinishPosition}]";

        line = $"Last race results: {resultTag} -> {was} {delta} = {result}";
        return true;
    }

    private void CancelPendingSessionNotices()
    {
        _sessionNoticeCts?.Cancel();
        _sessionNoticeCts?.Dispose();
        _sessionNoticeCts = null;
    }

    public void Dispose()
    {
        CancelPendingSessionNotices();

        if (_configuration.SendRatingNoticeOnJoin)
            _entryCarManager.ClientConnected -= OnClientConnected;

        if (_configuration.SendRatingNoticeAtSessionStart)
            _sessionManager.SessionChanged -= OnSessionChanged;
    }
}
