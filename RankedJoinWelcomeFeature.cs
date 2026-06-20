using AssettoServer.Network.Tcp;
using AssettoServer.Server;
using AssettoServer.Shared.Model;
using Serilog;

namespace RankedMatchReporterPlugin;

/// <summary>
/// RankedJoinWelcomeFeature — private chat with ladder standing and last race rating change.
///
/// Logic flow:
/// 1. If SendRatingNoticeOnJoin, on ClientConnected + FirstUpdateSent, send notice to that driver.
/// 2. If SendRatingNoticeAtSessionStart, on SessionChanged send notice to every connected driver.
/// 3. GET player rating from serv-brain; build two-line message (standing + last race delta).
/// 4. On HTTP failure, send "Ratings currently unavailable".
/// </summary>
public sealed class RankedJoinWelcomeFeature : IDisposable
{
    private const string RatingsUnavailableMessage = "Ratings currently unavailable";

    private readonly RankedMatchReporterConfiguration _configuration;
    private readonly SessionManager _sessionManager;
    private readonly EntryCarManager _entryCarManager;
    private readonly BrainApiClient _brainApi;

    public RankedJoinWelcomeFeature(
        RankedMatchReporterConfiguration configuration,
        SessionManager sessionManager,
        EntryCarManager entryCarManager,
        BrainApiClient brainApi)
    {
        _configuration = configuration;
        _sessionManager = sessionManager;
        _entryCarManager = entryCarManager;
        _brainApi = brainApi;

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
        _ = SendNoticeAsync(client);
    }

    private void OnSessionChanged(SessionManager sender, SessionChangedEventArgs args)
    {
        foreach (var car in _entryCarManager.EntryCars)
        {
            if (car.Client is { HasSentFirstUpdate: true } client)
                _ = SendNoticeAsync(client);
        }
    }

    private async Task SendNoticeAsync(ACTcpClient client)
    {
        try
        {
            var rating = await _brainApi
                .GetPlayerRatingAsync(client.Guid, CancellationToken.None)
                .ConfigureAwait(false);

            var message = rating == null
                ? RatingsUnavailableMessage
                : BuildNoticeMessage(client.Name ?? "Driver", rating);

            client.SendChatMessage(message);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RankedMatchReporterPlugin: rating notice failed for {Name}", client.Name);
            client.SendChatMessage(RatingsUnavailableMessage);
        }
    }

    private string BuildNoticeMessage(string clientName, Models.PlayerRatingResponse rating)
    {
        var leagueLabel = _configuration.LeagueDisplayName;
        var lines = new List<string>();

        if (rating.IsRanked)
        {
            var formattedRating = BrainApiClient.FormatRating(rating.Rating);
            lines.Add($"{clientName} — you're #{rating.LeagueRank} on the {leagueLabel} ladder (rating {formattedRating}).");
        }
        else
        {
            lines.Add($"{clientName} — unranked on the {leagueLabel} ladder.");
        }

        if (rating.LastRace != null)
        {
            var was = BrainApiClient.FormatRating(rating.LastRace.RatingBefore);
            var delta = BrainApiClient.FormatDelta(rating.LastRace.RatingDelta);
            var result = BrainApiClient.FormatRating(rating.LastRace.RatingAfter);
            lines.Add($"Last race results: {was} {delta} = {result}");
        }

        return string.Join("\n", lines);
    }

    public void Dispose()
    {
        if (_configuration.SendRatingNoticeOnJoin)
            _entryCarManager.ClientConnected -= OnClientConnected;

        if (_configuration.SendRatingNoticeAtSessionStart)
            _sessionManager.SessionChanged -= OnSessionChanged;
    }
}
