using AssettoServer.Server.Plugin;
using Autofac;
using Microsoft.Extensions.Hosting;

namespace RankedMatchReporterPlugin;

/// <summary>
/// RankedMatchReporterModule — AssettoServer plugin module; registers DI types and default yaml schema.
///
/// Logic flow:
/// 1. Server loads EnablePlugins entry RankedMatchReporterPlugin → this module runs.
/// 2. Load() registers RankedMatchReporterPlugin as a single hosted service instance.
/// 3. ReferenceConfiguration supplies default field values used for yaml schema / reference file generation.
/// </summary>
public class RankedMatchReporterModule : AssettoServerModule<RankedMatchReporterConfiguration>
{
    public override RankedMatchReporterConfiguration ReferenceConfiguration => new();

    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<RankedMatchReporterPlugin>().AsSelf().As<IHostedService>().SingleInstance();
    }
}
