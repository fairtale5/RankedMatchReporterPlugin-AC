using AssettoServer.Network.Tcp;
using AssettoServer.Server;
using AssettoServer.Shared.Model;
using RankedMatchReporterPlugin.Models;
using Serilog;

namespace RankedMatchReporterPlugin.Classification;

/// <summary>
/// TimedRaceClassificationFeature — lap-cap finish order for timed races (ranked ingest + crossing chat).
///
/// Logic flow:
/// 1. Reset state at race green; track green-flag starter Steam IDs.
/// 2. On ACServer.Update — snapshot LeaderLapsAtClock when clock expires; finalize stragglers at RaceOver.
/// 3. On LapCompleted — assign P1..Pn when drivers hit cap lap count; broadcast one-line finish chat.
/// 4. Expose RaceClassificationResult for MatchReportBuilder at session change.
/// </summary>
public sealed class TimedRaceClassificationFeature : IDisposable
{
    private readonly RankedMatchReporterConfiguration _configuration;
    private readonly SessionManager _sessionManager;
    private readonly EntryCarManager _entryCarManager;
    private readonly ACServer _server;

    private readonly TimedRaceClassificationState _state = new();
    private readonly HashSet<ulong> _starterSteamIds = new();
    private readonly HashSet<ACTcpClient> _lapHandlerClients = new();
    private readonly HashSet<ulong> _disconnectedSteamIdsDuringRace;

    private List<RaceStarterSnapshot> _startersAtGreen = new();

    private RaceClassificationResult _finalResult = RaceClassificationResult.NotUsed;
    private bool _leaderLapsAtClockCaptured;
    private bool _hadRaceOverPacket;

    public TimedRaceClassificationFeature(
        RankedMatchReporterConfiguration configuration,
        SessionManager sessionManager,
        EntryCarManager entryCarManager,
        ACServer server,
        HashSet<ulong> disconnectedSteamIdsDuringRace)
    {
        _configuration = configuration;
        _sessionManager = sessionManager;
        _entryCarManager = entryCarManager;
        _disconnectedSteamIdsDuringRace = disconnectedSteamIdsDuringRace;
        _server = server;

        _server.Update += OnServerUpdate;
        _entryCarManager.ClientConnected += OnClientConnected;
        _entryCarManager.ClientDisconnected += OnClientDisconnected;
    }

    public RaceClassificationResult GetFinalResult() => _finalResult;

    /// <summary>Reset per-race state and attach lap handlers for grid starters.</summary>
    public void OnRaceGreen(IReadOnlyList<RaceStarterSnapshot> starters)
    {
        _state.LeaderLapsAtClock = null;
        _state.CapLaps = null;
        _state.FinalizedAtRaceOver = false;
        _state.ClassifiedFinishers.Clear();
        _finalResult = RaceClassificationResult.NotUsed;
        _leaderLapsAtClockCaptured = false;
        _hadRaceOverPacket = false;

        _starterSteamIds.Clear();
        _startersAtGreen = starters.ToList();
        foreach (var starter in starters)
            _starterSteamIds.Add(starter.SteamId);

        foreach (var car in _entryCarManager.EntryCars)
        {
            if (car.Client != null && _starterSteamIds.Contains(car.Client.Guid))
                AttachLapHandler(car.Client);
        }
    }

    private void OnServerUpdate(ACServer sender, EventArgs args)
    {
        if (!_configuration.TimedRaceClassificationEnabled)
            return;

        var session = _sessionManager.CurrentSession;
        if (session.Configuration.Type != SessionType.Race || !session.Configuration.IsTimedRace)
            return;

        if (session.SessionOverFlag && !_leaderLapsAtClockCaptured)
        {
            _state.LeaderLapsAtClock = session.LeaderLapCount;
            _leaderLapsAtClockCaptured = true;
            Log.Debug(
                "RankedMatchReporterPlugin: leader laps at clock expiry = {LeaderLaps}",
                _state.LeaderLapsAtClock);
        }

        var raceOverNow = session.HasSentRaceOverPacket;
        if (raceOverNow && !_hadRaceOverPacket)
            FinalizeAtRaceOver();

        _hadRaceOverPacket = raceOverNow;
    }

    private void FinalizeAtRaceOver()
    {
        if (_starterSteamIds.Count == 0 || _state.CapLaps == null)
        {
            Log.Debug("RankedMatchReporterPlugin: timed classification skipped at race over (no cap set)");
            return;
        }

        var session = _sessionManager.CurrentSession;
        var results = session.Results;
        if (results == null)
            return;

        var stragglers = new List<TimedRaceClassificationEngine.StragglerProbe>();

        foreach (var car in _entryCarManager.EntryCars)
        {
            var client = car.Client;
            if (client == null || !_starterSteamIds.Contains(client.Guid))
                continue;

            if (_state.ClassifiedFinishers.ContainsKey(client.Guid))
                continue;

            if (_disconnectedSteamIdsDuringRace.Contains(client.Guid))
                continue;

            if (!results.TryGetValue(car.SessionId, out var result))
                continue;

            var username = result.Name.Length > 0 ? result.Name : client.Name ?? "";
            stragglers.Add(new TimedRaceClassificationEngine.StragglerProbe(
                client.Guid,
                username,
                (int)result.NumLaps,
                car.Status.NormalizedPosition,
                result.TotalTime > 0 ? (int)result.TotalTime : null,
                TimedRaceClassificationEngine.ToLapMs(result.BestLap)));
        }

        // Index result rows by Steam id for lookup when the car slot no longer holds a client.
        var resultsBySteamId = results.Values
            .Where(r => r.Guid != 0)
            .GroupBy(r => r.Guid)
            .ToDictionary(g => g.Key, g => g.First());

        // Walk starters who left the race; add each finished-a-lap abandoner as a back-of-grid probe.
        foreach (var starter in _startersAtGreen)
        {
            if (_state.ClassifiedFinishers.ContainsKey(starter.SteamId))
                continue;

            if (!_disconnectedSteamIdsDuringRace.Contains(starter.SteamId))
                continue;

            // Read the driver's last result row; skip drivers with zero laps so AFK players are not punished.
            if (!resultsBySteamId.TryGetValue(starter.SteamId, out var result) || result.NumLaps < 1)
                continue;

            // Force laps to 1 and distance to 0 so the abandoner counts but sorts behind every driver still on track.
            var username = result.Name.Length > 0 ? result.Name : starter.Username;
            stragglers.Add(new TimedRaceClassificationEngine.StragglerProbe(
                starter.SteamId,
                username,
                1,
                0f,
                null,
                null));
        }

        var starters = _startersAtGreen;

        _finalResult = TimedRaceClassificationEngine.FinalizeAtRaceOver(
            _state,
            starters,
            stragglers,
            _disconnectedSteamIdsDuringRace);

        Log.Information(
            "RankedMatchReporterPlugin: timed classification finalized at race over (cap={CapLaps}, classified={Classified}, total={Total})",
            _state.CapLaps,
            _state.ClassifiedFinishers.Count,
            _finalResult.Participants.Count);
    }

    private void OnClientConnected(ACTcpClient sender, EventArgs args)
    {
        if (_sessionManager.CurrentSession.Configuration.Type != SessionType.Race)
            return;

        if (_starterSteamIds.Contains(sender.Guid))
            AttachLapHandler(sender);
    }

    private void OnClientDisconnected(ACTcpClient sender, EventArgs args) =>
        DetachLapHandler(sender);

    private void AttachLapHandler(ACTcpClient client)
    {
        if (!_lapHandlerClients.Add(client))
            return;

        client.LapCompleted += OnLapCompleted;
    }

    private void DetachLapHandler(ACTcpClient client)
    {
        if (!_lapHandlerClients.Remove(client))
            return;

        client.LapCompleted -= OnLapCompleted;
    }

    private void OnLapCompleted(ACTcpClient client, LapCompletedEventArgs args)
    {
        if (!_configuration.TimedRaceClassificationEnabled)
            return;

        var session = _sessionManager.CurrentSession;
        if (session.Configuration.Type != SessionType.Race || !session.Configuration.IsTimedRace)
            return;

        if (!_starterSteamIds.Contains(client.Guid))
            return;

        var results = session.Results;
        if (results == null || !results.TryGetValue(client.SessionId, out var result))
            return;

        var outcome = TimedRaceClassificationEngine.TryRecordLapCrossing(
            _state,
            client.Guid,
            result.Name.Length > 0 ? result.Name : client.Name ?? "",
            (int)result.NumLaps,
            result.TotalTime > 0 ? (int)result.TotalTime : null,
            TimedRaceClassificationEngine.ToLapMs(result.BestLap));

        if (outcome.NewlyClassified)
            MaybeBroadcastFinishChat(outcome.FinishPosition, result.Name.Length > 0 ? result.Name : client.Name ?? "");
    }

    private void MaybeBroadcastFinishChat(int finishPosition, string driverName)
    {
        if (!_configuration.BroadcastFinishPositionChat)
            return;

        var template = _configuration.FinishPositionAnnouncement;
        if (string.IsNullOrWhiteSpace(template))
            return;

        var message = template
            .Replace("{Position}", finishPosition.ToString(), StringComparison.Ordinal)
            .Replace("{Name}", driverName, StringComparison.Ordinal);

        _entryCarManager.BroadcastChat(message);
    }

    public void Dispose()
    {
        _server.Update -= OnServerUpdate;

        foreach (var client in _lapHandlerClients.ToList())
            DetachLapHandler(client);

        _entryCarManager.ClientConnected -= OnClientConnected;
        _entryCarManager.ClientDisconnected -= OnClientDisconnected;
    }
}
