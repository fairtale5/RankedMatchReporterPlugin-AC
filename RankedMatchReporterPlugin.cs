using AssettoServer.Network.Tcp;
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
        var reportState = new RankedRaceReportState();
        _feature = new RankedMatchReporterFeature(
            configuration,
            serverConfiguration,
            sessionManager,
            entryCarManager,
            _brainApi,
            reportState);

        _joinWelcome = new RankedJoinWelcomeFeature(
            configuration,
            sessionManager,
            entryCarManager,
            _brainApi,
            reportState);
    }

    /// <summary>On-demand /score — same private notice as join welcome.</summary>
    public Task SendScoreNoticeAsync(ACTcpClient client, CancellationToken cancellationToken = default)
    {
        if (_joinWelcome == null)
        {
            client.SendChatMessage("Ratings currently unavailable");
            return Task.CompletedTask;
        }

        return _joinWelcome.SendScoreNoticeAsync(client, cancellationToken);
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
