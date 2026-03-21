namespace RTPTransmitter.Services;

/// <summary>
/// Configuration for tiered storage paths.
/// Bind from appsettings.json section "StoragePaths".
/// </summary>
public sealed class StoragePathOptions
{
    public const string Section = "StoragePaths";

    /// <summary>
    /// Directory where raw audio files are initially written.
    /// This is the "hot" working directory. May be a UNC path.
    /// Default: "Recordings" (relative to app root).
    /// </summary>
    public string ImmediateProcessing { get; set; } = "Recordings";

    /// <summary>
    /// One or more directories (may be UNC paths) where completed recordings
    /// are copied for medium-term retention.
    /// </summary>
    public List<string> MediumTermStorage { get; set; } = [];

    /// <summary>
    /// One or more directories (may be UNC paths) where completed recordings
    /// are copied for long-term / archive retention.
    /// </summary>
    public List<string> LongTermStorage { get; set; } = [];

    /// <summary>
    /// How often (in seconds) the distribution service scans the immediate
    /// processing directory for files to copy. Default: 60.
    /// </summary>
    public int DistributionIntervalSeconds { get; set; } = 60;
}
