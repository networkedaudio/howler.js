namespace RTPTransmitter.Services;

/// <summary>
/// Configuration for the SAP discovery listener.
/// </summary>
public sealed class SapListenerOptions
{
    public const string Section = "SapListener";

    /// <summary>
    /// SAP multicast group address. Default: 239.255.255.255 (RFC 2974).
    /// </summary>
    public string MulticastGroup { get; set; } = "239.255.255.255";

    /// <summary>
    /// SAP port. Default: 9875 (RFC 2974).
    /// </summary>
    public int Port { get; set; } = 9875;

    /// <summary>
    /// Network interface IP to bind for multicast reception.
    /// </summary>
    public string LocalAddress { get; set; } = "0.0.0.0";

    /// <summary>
    /// Whether SAP discovery is enabled. Default: true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Timeout in seconds after which a stream not re-announced is purged.
    /// SAP announcements are typically repeated every 300s. Default: 900 (15 min).
    /// </summary>
    public int ExpirySeconds { get; set; } = 900;
}
