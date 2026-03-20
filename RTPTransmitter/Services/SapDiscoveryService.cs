using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace RTPTransmitter.Services;

/// <summary>
/// Background service that listens for SAP (Session Announcement Protocol)
/// multicast announcements on 239.255.255.255:9875 (RFC 2974), parses the
/// embedded SDP payloads, and maintains a registry of discovered AES67 streams.
///
/// Restarts its UDP listener automatically when the user selects a different
/// network interface via <see cref="NetworkInterfaceService"/>.
/// </summary>
public sealed class SapDiscoveryService : BackgroundService
{
    private readonly ILogger<SapDiscoveryService> _logger;
    private readonly SapStreamRegistry _registry;
    private readonly SapListenerOptions _options;
    private readonly NetworkInterfaceService _nicService;

    /// <summary>
    /// Signalled when the user picks a different NIC so the listener loop restarts.
    /// </summary>
    private readonly SemaphoreSlim _restartSignal = new(0, 1);

    public SapDiscoveryService(
        ILogger<SapDiscoveryService> logger,
        SapStreamRegistry registry,
        IOptions<SapListenerOptions> options,
        NetworkInterfaceService nicService)
    {
        _logger = logger;
        _registry = registry;
        _options = options.Value;
        _nicService = nicService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("SAP discovery is disabled");
            return;
        }

        _nicService.OnSelectionChanged += OnNicChanged;
        try
        {
            // Start a background timer to purge expired announcements
            _ = PurgeExpiredAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var localAddress = ResolveLocalAddress();

                if (localAddress == "0.0.0.0")
                {
                    _logger.LogInformation("SAP Discovery waiting for NIC selection…");
                    await WaitForRestartOrCancellation(stoppingToken);
                    continue;
                }

                await RunSapListener(localAddress, stoppingToken);
            }
        }
        finally
        {
            _nicService.OnSelectionChanged -= OnNicChanged;
        }

        _logger.LogInformation("SAP Discovery stopped");
    }

    /// <summary>
    /// Resolve the effective local address: prefer the runtime NIC selection,
    /// fall back to appsettings, then 0.0.0.0.
    /// </summary>
    private string ResolveLocalAddress()
    {
        var selected = _nicService.SelectedAddress;
        if (!string.IsNullOrWhiteSpace(selected) && selected != "0.0.0.0")
            return selected;

        if (!string.IsNullOrWhiteSpace(_options.LocalAddress) && _options.LocalAddress != "0.0.0.0")
            return _options.LocalAddress;

        return "0.0.0.0";
    }

    private async Task RunSapListener(string localAddress, CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "SAP Discovery starting on {Group}:{Port} (interface {LocalAddress})",
            _options.MulticastGroup, _options.Port, localAddress);

        using var listenerCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var ct = listenerCts.Token;

        using var udpClient = new UdpClient();
        udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udpClient.Client.Bind(new IPEndPoint(IPAddress.Parse(localAddress), _options.Port));

        // Join SAP multicast group
        var multicastAddress = IPAddress.Parse(_options.MulticastGroup);
        if (localAddress != "0.0.0.0")
        {
            udpClient.JoinMulticastGroup(multicastAddress, IPAddress.Parse(localAddress));
        }
        else
        {
            udpClient.JoinMulticastGroup(multicastAddress);
        }

        _logger.LogInformation("Joined SAP multicast group {Group} on {LocalAddress}", _options.MulticastGroup, localAddress);

        // Wait for either: data arriving, NIC change, or app shutdown
        var restartTask = WaitForRestartOrCancellation(stoppingToken);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // If a NIC change was signalled, break out and restart
                if (restartTask.IsCompleted)
                {
                    _logger.LogInformation("SAP Discovery restarting due to NIC change");
                    break;
                }

                var receiveTask = udpClient.ReceiveAsync(ct).AsTask();
                var completed = await Task.WhenAny(receiveTask, restartTask);

                if (completed == restartTask)
                {
                    _logger.LogInformation("SAP Discovery restarting due to NIC change");
                    listenerCts.Cancel(); // cancel any pending receive
                    break;
                }

                var result = await receiveTask;
                ProcessPacket(result.Buffer, result.Buffer.Length);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error receiving SAP packet");
                await Task.Delay(100, ct);
            }
        }

        // Leave multicast group
        try
        {
            udpClient.DropMulticastGroup(multicastAddress);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error leaving SAP multicast group");
        }
    }

    private void OnNicChanged()
    {
        // Release the semaphore to wake up the listener loop
        try { _restartSignal.Release(); } catch (SemaphoreFullException) { }
    }

    private async Task WaitForRestartOrCancellation(CancellationToken stoppingToken)
    {
        try
        {
            await _restartSignal.WaitAsync(stoppingToken);
        }
        catch (OperationCanceledException) { }
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
            ChannelLabels = sdp.ChannelLabels,
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
        // Check at most every 60s, or more often for very short expiry windows
        var interval = TimeSpan.FromSeconds(Math.Clamp(_options.ExpirySeconds / 3.0, 10, 60));

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
