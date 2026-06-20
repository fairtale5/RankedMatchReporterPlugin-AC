using AssettoServer.Server;
using AssettoServer.Server.Configuration;
using AssettoServer.Shared.Model;
using RankedMatchReporterPlugin.Models;
using Serilog;

namespace RankedMatchReporterPlugin;

/// <summary>
/// RankedMatchReporterFeature — session hooks that snapshot grid and report finished races.
///
/// Logic flow:
/// 1. Subscribe to SessionChanged on startup.
/// 2. When next session is Race, schedule a snapshot at green flag (StartTime + delay).
/// 3. At green, store Steam ID + username for each grid slot with a connected driver — authoritative starter list.
/// 4. When previous session was Race and race-over was sent, copy that starter list and build payload from Results.
/// 5. Starters missing from Results at race end are reported as DNF at last place; mid-race joiners are never in the starter list.
///
/// Deferred (not implemented here): chat ranked-window messages, late-join noclip, pit-lane gate at green — see docs/NEXT-STEPS.md.
/// State held between sessions: _raceStartersAtGreen, _raceStartedAtUtc (DateTime?).
/// </summary>
public sealed class RankedMatchReporterFeature : IDisposable
{
    /// <summary>Wait this long after session start time before snapshot (lights / clock start).</summary>
    private const int SnapshotDelayAfterSessionStartMs = 1000;

    private readonly RankedMatchReporterConfiguration _configuration;
    private readonly ACServerConfiguration _serverConfiguration;
    private readonly SessionManager _sessionManager;
    private readonly EntryCarManager _entryCarManager;
    private readonly BrainApiClient _brainApi;

    private List<RaceStarterSnapshot> _raceStartersAtGreen = new();
    private DateTime? _raceStartedAtUtc;
    private CancellationTokenSource? _raceStartSnapshotScheduler;

    public RankedMatchReporterFeature(
        RankedMatchReporterConfiguration configuration,
        ACServerConfiguration serverConfiguration,
        SessionManager sessionManager,
        EntryCarManager entryCarManager,
        BrainApiClient brainApi)
    {
        _configuration = configuration;
        _serverConfiguration = serverConfiguration;
        _sessionManager = sessionManager;
        _entryCarManager = entryCarManager;
        _brainApi = brainApi;

        _sessionManager.SessionChanged += OnSessionChanged;

        Log.Information(
            "RankedMatchReporterPlugin: league={LeagueId} server={ServerId} dryRun={DryRun}",
            configuration.LeagueId,
            configuration.ServerId,
            configuration.DryRun);
    }

    /// <summary>
    /// OnSessionChanged — schedule starter snapshot at green; report results when a race session ends.
    /// </summary>
    private void OnSessionChanged(SessionManager sender, SessionChangedEventArgs args)
    {
        // Next session type Race → schedule snapshot at green flag (not at session switch).
        if (args.NextSession.Configuration.Type == SessionType.Race)
            ScheduleRaceStartSnapshot();
        else
            CancelRaceStartSnapshotScheduler();

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

        // Copy starter list now — async report must not read shared fields the next race can overwrite.
        var raceStarters = _raceStartersAtGreen.ToList();
        var raceStartedAt = _raceStartedAtUtc;
        _ = ReportRaceAsync(args.PreviousSession, raceStarters, raceStartedAt);
    }

    /// <summary>
    /// ScheduleRaceStartSnapshot — wait until race green, then capture starter Steam IDs and usernames.
    /// </summary>
    private void ScheduleRaceStartSnapshot()
    {
        CancelRaceStartSnapshotScheduler();

        var raceSession = _sessionManager.CurrentSession;
        long startMs = raceSession.StartTimeMilliseconds;
        long waitUntilSnapshotMs = Math.Max(0L, startMs - _sessionManager.ServerTimeMilliseconds)
            + SnapshotDelayAfterSessionStartMs;

        _raceStartSnapshotScheduler = new CancellationTokenSource();
        var cts = _raceStartSnapshotScheduler;

        Log.Debug(
            "RankedMatchReporterPlugin: starter snapshot scheduled in {DelaySeconds:F1}s (after race green)",
            waitUntilSnapshotMs / 1000.0);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay((int)waitUntilSnapshotMs, cts.Token).ConfigureAwait(false);
                CaptureRaceStartSnapshotAtGreen();
            }
            catch (OperationCanceledException)
            {
                // Session changed before green — discard pending snapshot.
            }
        }, cts.Token);
    }

    /// <summary>
    /// CaptureRaceStartSnapshotAtGreen — read grid slots with a connected driver; store Steam ID and username per starter.
    /// </summary>
    private void CaptureRaceStartSnapshotAtGreen()
    {
        var session = _sessionManager.CurrentSession;
        if (session.Configuration.Type != SessionType.Race)
            return;

        _raceStartedAtUtc = DateTime.UtcNow;

        // Read race grid list; keep slots that have a client with a non-zero Steam ID.
        var gridCars = session.Grid ?? _entryCarManager.EntryCars;
        _raceStartersAtGreen = gridCars
            .Where(car => car.Client?.Guid is ulong guid && guid != 0)
            .Select(car => new RaceStarterSnapshot(car.Client!.Guid, car.Client!.Name ?? ""))
            .ToList();

        Log.Information(
            "RankedMatchReporterPlugin: race start snapshot ({DriverCount} drivers at green)",
            _raceStartersAtGreen.Count);
    }

    /// <summary>
    /// ReportRaceAsync — build ingest payload from ended race session and POST or dry-run log.
    /// </summary>
    private async Task ReportRaceAsync(
        SessionState raceSession,
        IReadOnlyList<RaceStarterSnapshot> raceStartersAtGreen,
        DateTime? raceStartedAtUtc)
    {
        try
        {
            if (raceStartersAtGreen.Count == 0)
            {
                Log.Warning("RankedMatchReporterPlugin: no grid snapshot for ended race");
                return;
            }

            var finishedAt = DateTime.UtcNow;
            var startedAt = raceStartedAtUtc ?? finishedAt;

            var payload = MatchReportBuilder.Build(
                _configuration,
                _serverConfiguration,
                raceSession,
                raceStartersAtGreen,
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

            await _brainApi.SendRaceAsync(payload, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "RankedMatchReporterPlugin: failed to report race");
        }
    }

    private void CancelRaceStartSnapshotScheduler()
    {
        _raceStartSnapshotScheduler?.Cancel();
        _raceStartSnapshotScheduler?.Dispose();
        _raceStartSnapshotScheduler = null;
    }

    public void Dispose()
    {
        CancelRaceStartSnapshotScheduler();
        _sessionManager.SessionChanged -= OnSessionChanged;
    }
}
