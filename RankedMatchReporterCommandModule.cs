using AssettoServer.Commands;
using AssettoServer.Commands.Attributes;
using Qmmands;

namespace RankedMatchReporterPlugin;

/// <summary>
/// RankedMatchReporterCommandModule — in-game /score command module.
///
/// Logic flow:
/// 1. Player sends /score while connected.
/// 2. Delegate to RankedMatchReporterPlugin.SendScoreNoticeAsync.
/// 3. Same private rating notice as join welcome (GET serv-brain).
/// </summary>
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
