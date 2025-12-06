namespace CallTranscription.Functions.Common;

public class FeatureFlagsOptions
{
    public bool EnableTranscription { get; set; } = true;
    public bool EnableSummaries { get; set; } = true;
    public int CleanupRetentionDays { get; set; } = 3;
    public int StaleCallMinutes { get; set; } = 120;
}
