using AssettoServer.Server;
using AssettoServer.Server.Configuration;
using AssettoServer.Shared.Model;
using Serilog;

namespace RankedMatchReporterPlugin;

/// <summary>
/// RankedMatchReporterFeature — session hooks that snapshot grid and report finished races.
///
/// Logic flow:
/// 1. Subscribe to SessionChanged on startup.
/// 2. When next session is Race, store UTC start time and Steam IDs of connected drivers in a HashSet.
/// 3. When previous session was Race and race-over was sent, build payload from Results + snapshot, send via BrainIngestClient.
/// 4. Clear snapshot after each report attempt.
///
/// Deferred (not implemented here): chat ranked-window messages, late-join noclip — see docs/NEXT-STEPS.md.
/// State held between sessions: _gridSteamIdsAtRaceStart (HashSet of Steam IDs), _raceStartedAtUtc (DateTime?).
/// </summary>
public sealed class RankedMatchReporterFeature : IDisposable
{
    private readonly RankedMatchReporterConfiguration _configuration;
    private readonly ACServerConfiguration _serverConfiguration;
    private readonly SessionManager _sessionManager;
    private readonly EntryCarManager _entryCarManager;
    private readonly BrainIngestClient _ingestClient;

    private HashSet<ulong> _gridSteamIdsAtRaceStart = new();
    private DateTime? _raceStartedAtUtc;

    public RankedMatchReporterFeature(
        RankedMatchReporterConfiguration configuration,
        ACServerConfiguration serverConfiguration,
        SessionManager sessionManager,
        EntryCarManager entryCarManager)
    {
        _configuration = configuration;
        _serverConfiguration = serverConfiguration;
        _sessionManager = sessionManager;
        _entryCarManager = entryCarManager;
        _ingestClient = new BrainIngestClient(configuration);

        _sessionManager.SessionChanged += OnSessionChanged;

        Log.Information(
            "RankedMatchReporterPlugin: league={LeagueId} server={ServerId} dryRun={DryRun}",
            configuration.LeagueId,
            configuration.ServerId,
            configuration.DryRun);
    }

    /// <summary>
    /// OnSessionChanged — capture grid at race start; report results when a race session ends.
    /// </summary>
    private void OnSessionChanged(SessionManager sender, SessionChangedEventArgs args)
    {
        // Next session type Race → snapshot who is on the server now (grid-at-start for this race).
        if (args.NextSession.Configuration.Type == SessionType.Race)
            CaptureRaceStartSnapshot();

        // Previous session was not Race (e.g. quali → practice) → no results to report.
        if (args.PreviousSession?.Configuration.Type != SessionType.Race)
            return;

        // Skip aborted races that never broadcast final results to clients.
        // HasSentRaceOverPacket false → session ended before AssettoServer finalized results (timer reset, abort).
        if (!args.PreviousSession.HasSentRaceOverPacket)
        {
            Log.Debug("RankedMatchReporterPlugin: skipped race report (session ended without race over packet)");
            return;
        }

        // Fire report on thread pool; PreviousSession still holds Results dictionary at this moment.
        _ = ReportRaceAsync(args.PreviousSession);
    }

    /// <summary>
    /// CaptureRaceStartSnapshot — store grid Steam IDs and UTC timestamp when Race session begins.
    /// </summary>
    private void CaptureRaceStartSnapshot()
    {
        _raceStartedAtUtc = DateTime.UtcNow;
        // Collect Guid from each connected entry car; empty Guid slots are skipped.
        // Read EntryCarManager.EntryCars; for each car with Client, read Client.Guid (Steam ID).
        // Drop Guid 0 (empty slot); store remaining IDs in _gridSteamIdsAtRaceStart for later Contains() filter.
        _gridSteamIdsAtRaceStart = _entryCarManager.EntryCars
            .Where(car => car.Client != null)
            .Select(car => car.Client!.Guid)
            .Where(guid => guid != 0)
            .ToHashSet();

        Log.Information(
            "RankedMatchReporterPlugin: race start snapshot ({DriverCount} drivers on grid)",
            _gridSteamIdsAtRaceStart.Count);
    }

    /// <summary>
    /// ReportRaceAsync — build ingest payload from ended race session and POST or dry-run log.
    /// </summary>
    private async Task ReportRaceAsync(SessionState raceSession)
    {
        try
        {
            if (_gridSteamIdsAtRaceStart.Count == 0)
            {
                Log.Warning("RankedMatchReporterPlugin: no grid snapshot for ended race");
                return;
            }

            var finishedAt = DateTime.UtcNow;
            var startedAt = _raceStartedAtUtc ?? finishedAt;

            var payload = MatchReportBuilder.Build(
                _configuration,
                _serverConfiguration,
                raceSession,
                _gridSteamIdsAtRaceStart,
                startedAt,
                finishedAt);

            if (payload.Participants.Count == 0)
            {
                Log.Warning("RankedMatchReporterPlugin: no participants in payload");
                return;
            }

            // Optionally skip POST when race did not meet ranked criteria and config says so.
            // payload.CountedForRanked false and ReportUncountedRaces false → do not call ingest at all.
            if (!payload.CountedForRanked && !_configuration.ReportUncountedRaces)
            {
                Log.Information(
                    "RankedMatchReporterPlugin: skipped uncounted race ({ParticipantCount} drivers)",
                    payload.Participants.Count);
                return;
            }

            await _ingestClient.SendAsync(payload, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "RankedMatchReporterPlugin: failed to report race");
        }
        finally
        {
            _gridSteamIdsAtRaceStart.Clear();
            _raceStartedAtUtc = null;
        }
    }

    public void Dispose()
    {
        _sessionManager.SessionChanged -= OnSessionChanged;
        _ingestClient.Dispose();
    }
}
