using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace RTPTransmitter.Services;

/// <summary>
/// Background service that listens for SAP (Session Announcement Protocol)
/// multicast announcements on 239.255.255.255:9875 (RFC 2974), parses the
/// embedded SDP payloads, and maintains a registry of discovered AES67 streams.
/// </summary>
public sealed class SapDiscoveryService : BackgroundService
{
    private readonly ILogger<SapDiscoveryService> _logger;
    private readonly SapStreamRegistry _registry;
    private readonly SapListenerOptions _options;

    public SapDiscoveryService(
        ILogger<SapDiscoveryService> logger,
        SapStreamRegistry registry,
        IOptions<SapListenerOptions> options)
    {
        _logger = logger;
        _registry = registry;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("SAP discovery is disabled");
            return;
        }

        _logger.LogInformation(
            "SAP Discovery starting on {Group}:{Port}",
            _options.MulticastGroup, _options.Port);

        using var udpClient = new UdpClient();
        udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udpClient.Client.Bind(new IPEndPoint(IPAddress.Parse(_options.LocalAddress), _options.Port));

        // Join SAP multicast group
        var multicastAddress = IPAddress.Parse(_options.MulticastGroup);
        if (!string.IsNullOrWhiteSpace(_options.LocalAddress) && _options.LocalAddress != "0.0.0.0")
        {
            udpClient.JoinMulticastGroup(multicastAddress, IPAddress.Parse(_options.LocalAddress));
        }
        else
        {
            udpClient.JoinMulticastGroup(multicastAddress);
        }

        _logger.LogInformation("Joined SAP multicast group {Group}", _options.MulticastGroup);

        // Start a background timer to purge expired announcements
        _ = PurgeExpiredAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await udpClient.ReceiveAsync(stoppingToken);
                ProcessPacket(result.Buffer, result.Buffer.Length);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error receiving SAP packet");
                await Task.Delay(100, stoppingToken);
            }
        }

        // Leave multicast group on shutdown
        try
        {
            udpClient.DropMulticastGroup(multicastAddress);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error leaving SAP multicast group");
        }

        _logger.LogInformation("SAP Discovery stopped");
    }

    private void ProcessPacket(byte[] data, int length)
    {
        var sapPacket = SapPacket.Parse(data, length);
        if (sapPacket == null)
        {
            _logger.LogDebug("Received unparseable SAP packet ({Length} bytes)", length);
            return;
        }

        // Handle deletion
        if (sapPacket.IsDeletion)
        {
            if (_registry.Remove(sapPacket.AnnouncementKey))
            {
                _logger.LogInformation("SAP deletion: removed stream {Key}", sapPacket.AnnouncementKey);
            }
            return;
        }

        // Parse SDP payload
        var sdp = SdpParser.Parse(sapPacket.Payload);
        if (sdp == null || !sdp.IsValidAudioStream)
        {
            _logger.LogDebug(
                "SAP announcement {Key} has no valid audio stream (media={Media}, port={Port}, addr={Addr})",
                sapPacket.AnnouncementKey, sdp?.MediaType, sdp?.Port, sdp?.MulticastAddress);
            return;
        }

        var stream = new DiscoveredStream
        {
            Id = sapPacket.AnnouncementKey,
            Name = sdp.SessionName,
            Description = sdp.SessionDescription,
            MulticastGroup = sdp.MulticastAddress,
            SourceAddress = sdp.SourceAddress,
            Port = sdp.Port,
            SampleRate = sdp.SampleRate,
            Channels = sdp.Channels,
            BitDepth = sdp.BitDepth,
            PayloadType = sdp.PayloadType,
            Codec = sdp.Codec,
            PtimeMs = sdp.PtimeMs,
            OriginatingSource = sapPacket.OriginatingSource.ToString(),
            Origin = sdp.Origin,
            RawSdp = sdp.RawSdp,
            LastSeen = DateTimeOffset.UtcNow
        };

        bool isNew = _registry.AddOrUpdate(stream);
        if (isNew)
        {
            _logger.LogInformation(
                "SAP: discovered new stream \"{Name}\" at {Multicast}:{Port} ({Codec}/{SampleRate}/{Channels}ch)",
                stream.Name, stream.MulticastGroup, stream.Port,
                stream.Codec, stream.SampleRate, stream.Channels);
        }
        else
        {
            _logger.LogDebug("SAP: refreshed stream \"{Name}\" ({Key})", stream.Name, stream.Id);
        }
    }

    private async Task PurgeExpiredAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(60, _options.ExpirySeconds / 3));

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(interval, stoppingToken);

            var cutoff = DateTimeOffset.UtcNow.AddSeconds(-_options.ExpirySeconds);
            int purged = _registry.PurgeExpired(cutoff);

            if (purged > 0)
            {
                _logger.LogInformation("SAP: purged {Count} expired stream(s)", purged);
            }
        }
    }
}
