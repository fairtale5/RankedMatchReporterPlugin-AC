using AssettoServer.Server;
using AssettoServer.Server.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace RankedMatchReporterPlugin;

/// <summary>
/// RankedMatchReporterPlugin — AssettoServer hosted entry; wires yaml config into ranked features.
///
/// Logic flow at startup:
/// 1. Read Enabled from yaml; if false, log and skip feature creation.
/// 2. Construct BrainApiClient (shared HTTP to serv-brain).
/// 3. Construct RankedMatchReporterFeature (session hooks + race ingest) and optional RankedJoinWelcomeFeature.
/// 4. ExecuteAsync is idle — work happens on session and connect events inside the features.
/// </summary>
public class RankedMatchReporterPlugin : BackgroundService
{
    private readonly BrainApiClient? _brainApi;
    private readonly RankedMatchReporterFeature? _feature;
    private readonly RankedJoinWelcomeFeature? _joinWelcome;

    public RankedMatchReporterPlugin(
        RankedMatchReporterConfiguration configuration,
        ACServerConfiguration serverConfiguration,
        SessionManager sessionManager,
        EntryCarManager entryCarManager)
    {
        if (!configuration.Enabled)
        {
            Log.Information("RankedMatchReporterPlugin: disabled in config");
            return;
        }

        _brainApi = new BrainApiClient(configuration);
        _feature = new RankedMatchReporterFeature(
            configuration,
            serverConfiguration,
            sessionManager,
            entryCarManager,
            _brainApi);

        if (configuration.SendRatingNoticeOnJoin || configuration.SendRatingNoticeAtSessionStart)
        {
            _joinWelcome = new RankedJoinWelcomeFeature(
                configuration,
                sessionManager,
                entryCarManager,
                _brainApi);
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;

    public override void Dispose()
    {
        _feature?.Dispose();
        _joinWelcome?.Dispose();
        _brainApi?.Dispose();
        base.Dispose();
    }
}
