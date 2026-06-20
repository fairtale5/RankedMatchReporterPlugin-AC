using FluentValidation;

namespace RankedMatchReporterPlugin;

/// <summary>
/// RankedMatchReporterConfigurationValidator — rejects invalid yaml before the server accepts players.
///
/// Logic flow:
/// At config load, FluentValidation runs each RuleFor against yaml values; startup fails when any rule fails.
/// </summary>
public class RankedMatchReporterConfigurationValidator : AbstractValidator<RankedMatchReporterConfiguration>
{
    public RankedMatchReporterConfigurationValidator()
    {
        RuleFor(c => c.LeagueId).NotEmpty();
        RuleFor(c => c.LeagueDisplayName).NotEmpty();
        RuleFor(c => c.ServerId).NotEmpty();
        RuleFor(c => c.IngestUrl).NotEmpty();
        RuleFor(c => c.MinimumDriversForRanked).GreaterThan(0);
        RuleFor(c => c.PeakWindow.TimeZoneId).NotEmpty();
    }
}
