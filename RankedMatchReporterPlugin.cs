using AssettoServer.Server;
using AssettoServer.Server.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace RankedMatchReporterPlugin;

/// <summary>
/// RankedMatchReporterPlugin — AssettoServer hosted entry; wires yaml config into RankedMatchReporterFeature.
///
/// Logic flow at startup:
/// 1. Read Enabled from yaml; if false, log and skip feature creation.
/// 2. Construct RankedMatchReporterFeature (session hooks + ingest client).
/// 3. ExecuteAsync is idle — all work happens on SessionChanged events inside the feature.
/// </summary>
public class RankedMatchReporterPlugin : BackgroundService
{
    private readonly RankedMatchReporterFeature? _feature;

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

        _feature = new RankedMatchReporterFeature(
            configuration,
            serverConfiguration,
            sessionManager,
            entryCarManager);
    }

    // No background loop — SessionChanged handler in _feature does all work.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;

    public override void Dispose()
    {
        _feature?.Dispose();
        base.Dispose();
    }
}
