using AssettoServer.Commands;
using AssettoServer.Commands.Attributes;
using Qmmands;

namespace RankedMatchReporterPlugin;

/// <summary>In-game /score — private ranked notice on demand (same text as join welcome).</summary>
public class RankedMatchReporterCommandModule : ACModuleBase
{
    private readonly RankedMatchReporterPlugin _plugin;

    public RankedMatchReporterCommandModule(RankedMatchReporterPlugin plugin)
    {
        _plugin = plugin;
    }

    [Command("score"), RequireConnectedPlayer]
    public async Task Score()
    {
        var client = Client;
        if (client == null)
            return;

        await _plugin.SendScoreNoticeAsync(client).ConfigureAwait(false);
    }
}
