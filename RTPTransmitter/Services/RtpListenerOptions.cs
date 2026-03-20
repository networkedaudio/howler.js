namespace RTPTransmitter.Services;

/// <summary>
/// Configuration for the RTP listener service.
/// </summary>
public sealed class RtpListenerOptions
{
    public const string Section = "RtpListener";

    /// <summary>
    /// UDP port to listen on for AES67 RTP packets. Default: 5004.
    /// </summary>
    public int Port { get; set; } = 5004;

    /// <summary>
    /// Multicast group address to join (e.g. "239.69.1.1"). 
    /// Leave empty to listen on unicast only.
    /// </summary>
    public string MulticastGroup { get; set; } = string.Empty;

    /// <summary>
    /// Network interface IP to bind for multicast. Leave empty for default.
    /// </summary>
    public string LocalAddress { get; set; } = "0.0.0.0";

    /// <summary>
    /// Expected sample rate of the incoming audio. Default: 48000 (AES67 standard).
    /// </summary>
    public int SampleRate { get; set; } = 48000;

    /// <summary>
    /// Number of audio channels. Default: 1 (mono). Set to 2 for stereo.
    /// </summary>
    public int Channels { get; set; } = 1;

    /// <summary>
    /// Number of RTP packets to accumulate before sending a chunk to the browser.
    /// Larger values = more latency but smoother playback. Default: 10.
    /// </summary>
    public int PacketsPerChunk { get; set; } = 10;

    /// <summary>
    /// Force a specific PCM bit depth (16 or 24). 0 = auto-detect from payload type.
    /// </summary>
    public int ForceBitDepth { get; set; } = 0;
}
