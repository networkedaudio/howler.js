namespace RTPTransmitter.Services;

/// <summary>
/// Represents a stream discovered via SAP/SDP announcements.
/// </summary>
public sealed class DiscoveredStream
{
    /// <summary>
    /// Unique key from the SAP announcement (origin:hash).
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Session name from the SDP (s= line).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional session description (i= line).
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Multicast group address to join for receiving RTP.
    /// </summary>
    public string MulticastGroup { get; set; } = string.Empty;

    /// <summary>
    /// Source address from source-filter (for SSM).
    /// </summary>
    public string SourceAddress { get; set; } = string.Empty;

    /// <summary>
    /// UDP port for RTP reception.
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// Audio sample rate in Hz.
    /// </summary>
    public int SampleRate { get; set; } = 48000;

    /// <summary>
    /// Number of audio channels.
    /// </summary>
    public int Channels { get; set; } = 1;

    /// <summary>
    /// PCM bit depth (16 or 24).
    /// </summary>
    public int BitDepth { get; set; }

    /// <summary>
    /// RTP payload type number.
    /// </summary>
    public int PayloadType { get; set; }

    /// <summary>
    /// Codec name (e.g., "L24", "L16").
    /// </summary>
    public string Codec { get; set; } = string.Empty;

    /// <summary>
    /// Packet time in milliseconds.
    /// </summary>
    public double PtimeMs { get; set; }

    /// <summary>
    /// Originating source IP from SAP header.
    /// </summary>
    public string OriginatingSource { get; set; } = string.Empty;

    /// <summary>
    /// SDP origin line.
    /// </summary>
    public string Origin { get; set; } = string.Empty;

    /// <summary>
    /// When this announcement was first seen.
    /// </summary>
    public DateTimeOffset FirstSeen { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When this announcement was last refreshed.
    /// </summary>
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Raw SDP text for reference.
    /// </summary>
    public string RawSdp { get; set; } = string.Empty;
}
