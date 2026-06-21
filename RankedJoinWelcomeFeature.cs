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

    /// <summary>Fetch current rating and send the same private notice as on join.</summary>
    public Task SendScoreNoticeAsync(ACTcpClient client, CancellationToken cancellationToken = default) =>
        SendNoticeAsync(client, expectedMatchId: null, cancellationToken);

    private async Task SendNoticeAsync(
        ACTcpClient client,
        string? expectedMatchId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var waitResult = await WaitForRatingAsync(client.Guid, expectedMatchId, cancellationToken)
                .ConfigureAwait(false);

            var message = waitResult.Status switch
            {
                RatingWaitStatus.Ready => BuildNoticeMessage(
                    client.Name ?? "Driver",
                    waitResult.Rating!,
                    expectedMatchId),
                RatingWaitStatus.ApiUnavailable => BuildApiUnavailableMessage(expectedMatchId),
                RatingWaitStatus.RaceResultsTimedOut => BuildRaceResultsTimedOutMessage(expectedMatchId),
                _ => BuildApiUnavailableMessage(expectedMatchId)
            };

            client.SendChatMessage(message);
        }
        catch (OperationCanceledException)
        {
            // Session batch cancelled — do not send stale notice.
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RankedMatchReporterPlugin: rating notice failed for {Name}", client.Name);
            client.SendChatMessage(BuildServerErrorMessage());
        }
    }

    private async Task<RatingWaitResult> WaitForRatingAsync(
        ulong steamId,
        string? expectedMatchId,
        CancellationToken cancellationToken)
    {
        if (expectedMatchId == null)
        {
            var rating = await _brainApi.GetPlayerRatingAsync(steamId, cancellationToken).ConfigureAwait(false);
            return rating == null
                ? new RatingWaitResult(RatingWaitStatus.ApiUnavailable, null)
                : new RatingWaitResult(RatingWaitStatus.Ready, rating);
        }

        var deadline = DateTime.UtcNow.AddSeconds(_configuration.RatingNoticeMaxWaitForRaceResultsSeconds);
        var pollMs = _configuration.RatingNoticePollIntervalSeconds * 1000;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rating = await _brainApi.GetPlayerRatingAsync(steamId, cancellationToken).ConfigureAwait(false);
            if (rating == null)
                return new RatingWaitResult(RatingWaitStatus.ApiUnavailable, null);

            if (IsLastRaceForMatch(rating, expectedMatchId))
                return new RatingWaitResult(RatingWaitStatus.Ready, rating);

            if (DateTime.UtcNow >= deadline)
            {
                Log.Warning(
                    "RankedMatchReporterPlugin: timed out waiting for processed race {MatchId} for steam {SteamId}",
                    expectedMatchId,
                    steamId);
                return new RatingWaitResult(RatingWaitStatus.RaceResultsTimedOut, null);
            }

            if (pollMs > 0)
                await Task.Delay(pollMs, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsLastRaceForMatch(Models.PlayerRatingResponse rating, string expectedMatchId) =>
        rating.LastRace != null
        && !string.IsNullOrWhiteSpace(rating.LastRace.MatchId)
        && string.Equals(rating.LastRace.MatchId, expectedMatchId, StringComparison.OrdinalIgnoreCase);

    private string BuildApiUnavailableMessage(string? afterRace) =>
        afterRace != null
            ? "Ratings unavailable — ranked server did not respond after the race. Try /score."
            : "Ratings unavailable — ranked server did not respond. Try /score.";

    private string BuildRaceResultsTimedOutMessage(string? _) =>
        $"Ratings unavailable — results not ready yet (waited {_configuration.RatingNoticeMaxWaitForRaceResultsSeconds}s). Try /score.";

    private static string BuildServerErrorMessage() =>
        "Ratings unavailable — server error. Try /score.";

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
            lines.Add($"{clientName}: Rank: #{rating.LeagueRank}");
            if (TryBuildLastRaceLine(rating, expectedMatchId, out var lastRaceLine))
                lines.Add(lastRaceLine);
        }
        else
        {
            lines.Add($"Hi {clientName}! You don't have a rank in {leagueLabel} yet.");
            lines.Add($"Complete a full race ({minDrivers}+ drivers) to earn your first points.");
        }

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

        line = $"Last race: {resultTag} → {was} {delta} = {result} points";
        return true;
    }

    private enum RatingWaitStatus
    {
        Ready,
        ApiUnavailable,
        RaceResultsTimedOut
    }

    private readonly record struct RatingWaitResult(RatingWaitStatus Status, Models.PlayerRatingResponse? Rating);

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
