namespace RTPTransmitter.Api;

/// <summary>
/// A discovered SAP/SDP stream as returned by the API.
/// Uses a human-readable <see cref="Slug"/> derived from the SDP session name.
/// </summary>
public sealed class StreamDto
{
    /// <summary>
    /// URL-safe slug derived from the SDP session name (s= line).
    /// Use this value to reference the stream in other API calls.
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>
    /// Internal stream ID (SAP origin:hash key).
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable session name from the SDP.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional session description (i= line).
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Multicast group address.
    /// </summary>
    public string MulticastGroup { get; set; } = string.Empty;

    /// <summary>
    /// UDP port.
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// Audio sample rate in Hz.
    /// </summary>
    public int SampleRate { get; set; }

    /// <summary>
    /// Number of audio channels.
    /// </summary>
    public int Channels { get; set; }

    /// <summary>
    /// PCM bit depth (16, 24, 32).
    /// </summary>
    public int BitDepth { get; set; }

    /// <summary>
    /// Codec name (e.g. "L24").
    /// </summary>
    public string Codec { get; set; } = string.Empty;

    /// <summary>
    /// Per-channel labels (from SMPTE 2110-30 / SDP).
    /// </summary>
    public List<string> ChannelLabels { get; set; } = [];

    /// <summary>
    /// Whether the stream was manually added (SDP upload) vs SAP-discovered.
    /// </summary>
    public bool IsManual { get; set; }

    /// <summary>
    /// When the stream was first discovered.
    /// </summary>
    public DateTimeOffset FirstSeen { get; set; }

    /// <summary>
    /// When the stream was last seen / refreshed.
    /// </summary>
    public DateTimeOffset LastSeen { get; set; }

    /// <summary>
    /// Whether this stream currently has an active RTP listener.
    /// </summary>
    public bool IsActive { get; set; }
}

/// <summary>
/// Information about a single channel that is currently being recorded.
/// </summary>
public sealed class ChannelRecordingDto
{
    public string StreamSlug { get; set; } = string.Empty;
    public string StreamId { get; set; } = string.Empty;
    public string StreamName { get; set; } = string.Empty;
    public int Channel { get; set; }
    public string ChannelLabel { get; set; } = string.Empty;
    public int SampleRate { get; set; }
    public int BitDepth { get; set; }
    public bool VoiceDetectEnabled { get; set; }
    public long BufferBytes { get; set; }
    public DateTimeOffset LastPacketTime { get; set; }
}

/// <summary>
/// Recording settings (mirrors <see cref="Services.RecordingOptions"/>).
/// </summary>
public sealed class RecordingSettingsDto
{
    public int SilencePacketThreshold { get; set; } = 100;
    public int NoPacketTimeoutMs { get; set; } = 1000;
    public long MaxBufferSizeBytes { get; set; } = 10 * 1024 * 1024;
}

/// <summary>
/// Tiered storage path settings (mirrors <see cref="Services.StoragePathOptions"/>).
/// </summary>
public sealed class StoragePathsDto
{
    public string ImmediateProcessing { get; set; } = "Recordings";
    public List<string> MediumTermStorage { get; set; } = [];
    public List<string> LongTermStorage { get; set; } = [];
    public int DistributionIntervalSeconds { get; set; } = 60;
}

/// <summary>
/// Request body to start or stop recording on a channel.
/// </summary>
public sealed class RecordingRequestDto
{
    /// <summary>
    /// Stream slug (human-readable name) or internal stream ID.
    /// </summary>
    public string Stream { get; set; } = string.Empty;

    /// <summary>
    /// Zero-based channel index.
    /// </summary>
    public int Channel { get; set; }
}
