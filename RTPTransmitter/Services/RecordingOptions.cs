namespace RTPTransmitter.Services;

/// <summary>
/// Configuration for the channel recording feature.
/// Bind from appsettings.json section "Recording".
/// </summary>
public sealed class RecordingOptions
{
    public const string Section = "Recording";

    /// <summary>
    /// Number of consecutive silent (all-zero) packets on a channel before the
    /// current buffer is flushed to disk. Default: 100.
    /// </summary>
    public int SilencePacketThreshold { get; set; } = 100;

    /// <summary>
    /// Milliseconds of no packets received before the current buffer is flushed.
    /// Default: 1000 (1 second).
    /// </summary>
    public int NoPacketTimeoutMs { get; set; } = 1000;

    /// <summary>
    /// Maximum buffer size in bytes before forcing a flush to disk.
    /// Default: 10485760 (10 MB).
    /// </summary>
    public long MaxBufferSizeBytes { get; set; } = 10 * 1024 * 1024;
}
