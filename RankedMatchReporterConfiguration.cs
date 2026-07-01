using AssettoServer.Server.Configuration;
using JetBrains.Annotations;
using YamlDotNet.Serialization;

namespace RankedMatchReporterPlugin;

/// <summary>
/// RankedMatchReporterConfiguration — yaml-loaded settings for plugin_ranked_match_reporter_cfg.yml.
///
/// Logic flow:
/// 1. LeagueId and ServerId are copied into every ingest payload.
/// 2. PeakWindow + MinimumDriversForRanked set counted_for_ranked on the payload (brain still stores uncounted races if ReportUncountedRaces is true).
/// 3. DryRun skips HTTP and logs JSON until serv-brain ingest exists.
/// </summary>
[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public class RankedMatchReporterConfiguration : IValidateConfiguration<RankedMatchReporterConfigurationValidator>
{
    [YamlMember(Description = "Master switch for the plugin")]
    public bool Enabled { get; init; } = true;

    [YamlMember(Description = "League id sent to serv-brain ingest (e.g. gt3_ks)")]
    public string LeagueId { get; init; } = "gt3_ks";

    [YamlMember(Description = "Human label used in rating notice chat (e.g. GT3 Ranked)")]
    public string LeagueDisplayName { get; init; } = "GT3 Ranked";

    [YamlMember(Description = "Private chat with ladder standing and last race rating when a driver joins")]
    public bool SendRatingNoticeOnJoin { get; init; } = true;

    [YamlMember(Description = "Private chat with ladder standing and last race rating at each session start")]
    public bool SendRatingNoticeAtSessionStart { get; init; } = true;

    [YamlMember(Description = "Seconds to wait after a race ends before first rating GET at session start")]
    public int RatingNoticeDelayAfterRaceSeconds { get; init; } = 3;

    [YamlMember(Description = "Max seconds to poll GET until last_race.match_id matches the race just reported")]
    public int RatingNoticeMaxWaitForRaceResultsSeconds { get; init; } = 15;

    [YamlMember(Description = "Seconds between rating GET polls while waiting for processed race results")]
    public int RatingNoticePollIntervalSeconds { get; init; } = 1;

    [YamlMember(Description = "Server id sent to ingest (e.g. ff-circuit-gt3-ks)")]
    public string ServerId { get; init; } = "unknown";

    [YamlMember(Description = "serv-brain ingest URL (POST /v1/races)")]
    public string IngestUrl { get; init; } = "http://127.0.0.1:10000/v1/races";

    [YamlMember(Description = "Bearer or X-Api-Key value for ingest (empty = no auth header)")]
    public string ApiKey { get; init; } = "";

    [YamlMember(Description = "Log JSON payload only; skip HTTP until serv-brain is running")]
    public bool DryRun { get; init; } = true;

    [YamlMember(Description = "Minimum drivers on grid at race start for counted_for_ranked")]
    public int MinimumDriversForRanked { get; init; } = 4;

    [YamlMember(Description = "Omit starters with zero laps from ingest payload so AFK drivers are not ranked (brain unchanged)")]
    public bool ExcludeZeroLapDriversFromRanking { get; init; } = false;

    [YamlMember(Description = "Still POST off-peak races with counted_for_ranked=false")]
    public bool ReportUncountedRaces { get; init; } = true;

    [YamlMember(Description = "Broadcast race-start announcement at green when peak window allows a counted race (MinimumDriversForRanked not checked here)")]
    public bool BroadcastRaceStartAnnouncement { get; init; } = true;

    [YamlMember(Description = "Server chat at race green; one BroadcastChat per line (split on newline)")]
    public string RaceStartAnnouncement { get; init; } =
        "[Ranked Race Starting] Drive safe!\nSTAY TILL END: Abandoning mid-race counts as DNF (last place) and costs a lot of rating.";

    [YamlMember(Description = "Peak window for counted_for_ranked")]
    public PeakWindowConfiguration PeakWindow { get; init; } = new();

    [YamlMember(Description = "Use lap-cap classification for timed races (fixes overtime position drift)")]
    public bool TimedRaceClassificationEnabled { get; init; } = true;

    [YamlMember(Description = "Broadcast one chat line when a driver completes the cap lap count")]
    public bool BroadcastFinishPositionChat { get; init; } = true;

    [YamlMember(Description = "Chat template when a driver is classified; placeholders {Position}, {Name}")]
    public string FinishPositionAnnouncement { get; init; } = "[RANKED FINISH] P{Position} — {Name}";
}

/// <summary>
/// PeakWindowConfiguration — local time window when counted_for_ranked can be true on the ingest payload.
/// </summary>
[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public class PeakWindowConfiguration
{
    [YamlMember(Description = "Apply peak window when setting counted_for_ranked")]
    public bool Enabled { get; init; } = true;

    [YamlMember(Description = "IANA time zone id (e.g. America/Sao_Paulo)")]
    public string TimeZoneId { get; init; } = "America/Sao_Paulo";

    [YamlMember(Description = "Local start time HH:mm inclusive")]
    public string StartLocal { get; init; } = "17:30";

    [YamlMember(Description = "Local end time HH:mm inclusive")]
    public string EndLocal { get; init; } = "22:00";
}
