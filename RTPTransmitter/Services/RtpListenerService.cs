using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using RTPTransmitter.Hubs;

namespace RTPTransmitter.Services;

/// <summary>
/// Background service that listens for AES67 RTP packets on a UDP socket,
/// converts the PCM audio to Float32, aggregates packets into chunks, and
/// pushes them to connected browsers via SignalR.
///
/// Multi-channel AES67 streams are sent as interleaved Float32 data with
/// channel count metadata, allowing each browser client to independently
/// select which source channels to monitor and how to map them to output.
/// </summary>
public sealed class RtpListenerService : BackgroundService
{
    private readonly ILogger<RtpListenerService> _logger;
    private readonly IHubContext<AudioStreamHub> _hubContext;
    private readonly RtpListenerOptions _options;

    public RtpListenerService(
        ILogger<RtpListenerService> logger,
        IHubContext<AudioStreamHub> hubContext,
        IOptions<RtpListenerOptions> options)
    {
        _logger = logger;
        _hubContext = hubContext;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "RTP Listener starting on port {Port}, multicast={Multicast}, sampleRate={SampleRate}, channels={Channels}",
            _options.Port, _options.MulticastGroup, _options.SampleRate, _options.Channels);

        using var udpClient = new UdpClient();
        udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udpClient.Client.Bind(new IPEndPoint(IPAddress.Parse(_options.LocalAddress), _options.Port));

        // Join multicast group if configured
        if (!string.IsNullOrWhiteSpace(_options.MulticastGroup))
        {
            var multicastAddress = IPAddress.Parse(_options.MulticastGroup);
            if (!string.IsNullOrWhiteSpace(_options.LocalAddress) && _options.LocalAddress != "0.0.0.0")
            {
                udpClient.JoinMulticastGroup(multicastAddress, IPAddress.Parse(_options.LocalAddress));
            }
            else
            {
                udpClient.JoinMulticastGroup(multicastAddress);
            }
            _logger.LogInformation("Joined multicast group {MulticastGroup}", _options.MulticastGroup);
        }

        var sampleBuffer = new List<float>();
        ushort lastSequence = 0;
        bool firstPacket = true;
        int packetsInChunk = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await udpClient.ReceiveAsync(stoppingToken);
                var data = result.Buffer;

                RtpPacket packet;
                try
                {
                    packet = RtpPacket.Parse(data, data.Length);
                }
                catch (ArgumentException ex)
                {
                    _logger.LogWarning("Invalid RTP packet: {Message}", ex.Message);
                    continue;
                }

                // Check for RTP version 2
                if (packet.Version != 2)
                {
                    _logger.LogDebug("Ignoring non-RTPv2 packet (version={Version})", packet.Version);
                    continue;
                }

                // Detect sequence gaps
                if (!firstPacket)
                {
                    int expectedSeq = (lastSequence + 1) & 0xFFFF;
                    if (packet.SequenceNumber != expectedSeq)
                    {
                        int gap = ((packet.SequenceNumber - lastSequence) + 65536) % 65536;
                        _logger.LogWarning(
                            "RTP sequence gap detected: expected {Expected}, got {Got} (gap={Gap})",
                            expectedSeq, packet.SequenceNumber, gap);
                    }
                }
                firstPacket = false;
                lastSequence = packet.SequenceNumber;

                // Convert payload to float32 samples (preserving interleaved layout)
                float[] samples;
                if (_options.ForceBitDepth == 24)
                {
                    samples = PcmConverter.L24ToFloat32(packet.Payload);
                }
                else if (_options.ForceBitDepth == 16)
                {
                    samples = PcmConverter.L16ToFloat32(packet.Payload);
                }
                else
                {
                    samples = PcmConverter.ConvertPayload(packet.Payload, packet.PayloadType);
                }

                sampleBuffer.AddRange(samples);
                packetsInChunk++;

                // When we've accumulated enough packets, send the chunk
                if (packetsInChunk >= _options.PacketsPerChunk)
                {
                    var chunkSamples = sampleBuffer.ToArray();
                    sampleBuffer.Clear();
                    packetsInChunk = 0;

                    // Encode as base64 for efficient SignalR transport
                    // (Float32Array is 4 bytes per sample)
                    var byteArray = new byte[chunkSamples.Length * sizeof(float)];
                    Buffer.BlockCopy(chunkSamples, 0, byteArray, 0, byteArray.Length);
                    var base64 = Convert.ToBase64String(byteArray);

                    // Send interleaved data with the source channel count so
                    // each browser client can de-interleave and select channels.
                    await _hubContext.Clients.Group("default")
                        .SendAsync("ReceiveAudioChunk", base64, _options.Channels, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing RTP packet");
                await Task.Delay(10, stoppingToken);
            }
        }

        // Leave multicast group on shutdown
        if (!string.IsNullOrWhiteSpace(_options.MulticastGroup))
        {
            try
            {
                udpClient.DropMulticastGroup(IPAddress.Parse(_options.MulticastGroup));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error leaving multicast group");
            }
        }

        _logger.LogInformation("RTP Listener stopped");
    }
}
