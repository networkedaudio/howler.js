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
    private readonly NetworkInterfaceService _nicService;

    public RtpListenerService(
        ILogger<RtpListenerService> logger,
        IHubContext<AudioStreamHub> hubContext,
        IOptions<RtpListenerOptions> options,
        NetworkInterfaceService nicService)
    {
        _logger = logger;
        _hubContext = hubContext;
        _options = options.Value;
        _nicService = nicService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Prefer runtime NIC selection, then fall back to config
        var localAddress = _nicService.SelectedAddress;
        if (string.IsNullOrWhiteSpace(localAddress) || localAddress == "0.0.0.0")
            localAddress = _options.LocalAddress;

        _logger.LogInformation(
            "RTP Listener starting on port {Port}, multicast={Multicast}, sampleRate={SampleRate}, channels={Channels}",
            _options.Port, _options.MulticastGroup, _options.SampleRate, _options.Channels);

        using var udpClient = new UdpClient();
        udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udpClient.Client.Bind(new IPEndPoint(IPAddress.Parse(localAddress), _options.Port));

        // Join multicast group if configured
        if (!string.IsNullOrWhiteSpace(_options.MulticastGroup))
        {
            var multicastAddress = IPAddress.Parse(_options.MulticastGroup);
            if (!string.IsNullOrWhiteSpace(localAddress) && localAddress != "0.0.0.0")
            {
                udpClient.JoinMulticastGroup(multicastAddress, IPAddress.Parse(localAddress));
            }
            else
            {
                udpClient.JoinMulticastGroup(multicastAddress);
            }
            _logger.LogInformation("Joined multicast group {MulticastGroup}", _options.MulticastGroup);
        }

        _logger.LogInformation(
            "RTP [default] Waiting for packets (forceBitDepth={BitDepth}, packetsPerChunk={PPC})",
            _options.ForceBitDepth, _options.PacketsPerChunk);

        var sampleBuffer = new List<float>();
        ushort lastSequence = 0;
        bool firstPacket = true;
        int packetsInChunk = 0;
        long totalPackets = 0;
        long totalChunksSent = 0;
        long parseErrors = 0;
        long versionMismatches = 0;
        var lastStatsTime = DateTimeOffset.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await udpClient.ReceiveAsync(stoppingToken);
                var data = result.Buffer;
                totalPackets++;

                RtpPacket packet;
                try
                {
                    packet = RtpPacket.Parse(data, data.Length);
                }
                catch (ArgumentException ex)
                {
                    parseErrors++;
                    if (parseErrors <= 5)
                        _logger.LogWarning(
                            "RTP [default] Parse error (#{Count}): {Message} (datagram {Bytes} bytes from {Remote})",
                            parseErrors, ex.Message, data.Length, result.RemoteEndPoint);
                    continue;
                }

                // Check for RTP version 2
                if (packet.Version != 2)
                {
                    versionMismatches++;
                    if (versionMismatches <= 3)
                        _logger.LogWarning(
                            "RTP [default] Non-RTPv2 packet (version={Version}, PT={PT}, {Bytes} bytes from {Remote})",
                            packet.Version, packet.PayloadType, data.Length, result.RemoteEndPoint);
                    continue;
                }

                // Log first packet in detail
                if (firstPacket)
                {
                    _logger.LogInformation(
                        "RTP [default] *** First packet *** from {Remote}: PT={PT}, seq={Seq}, SSRC={Ssrc}, payloadBytes={PayloadLen}, extension={Ext}",
                        result.RemoteEndPoint, packet.PayloadType, packet.SequenceNumber,
                        packet.Ssrc, packet.Payload.Length, packet.Extension);
                }

                // Detect sequence gaps
                if (!firstPacket)
                {
                    int expectedSeq = (lastSequence + 1) & 0xFFFF;
                    if (packet.SequenceNumber != expectedSeq)
                    {
                        int gap = ((packet.SequenceNumber - lastSequence) + 65536) % 65536;
                        _logger.LogWarning(
                            "RTP [default] sequence gap: expected {Expected}, got {Got} (gap={Gap})",
                            expectedSeq, packet.SequenceNumber, gap);
                    }
                }
                firstPacket = false;
                lastSequence = packet.SequenceNumber;

                // Convert payload to float32 samples (preserving interleaved layout)
                float[] samples = PcmConverter.ConvertPayload(packet.Payload, packet.PayloadType, _options.ForceBitDepth);

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

                    totalChunksSent++;

                    if (totalChunksSent == 1)
                    {
                        _logger.LogInformation(
                            "RTP [default] *** First chunk sent *** ({Samples} samples, {Bytes} base64 chars, {Channels}ch)",
                            chunkSamples.Length, base64.Length, _options.Channels);
                    }
                }

                // Periodic stats every 10 seconds
                var now = DateTimeOffset.UtcNow;
                if ((now - lastStatsTime).TotalSeconds >= 10)
                {
                    _logger.LogInformation(
                        "RTP [default] Stats: packets={Packets}, chunksSent={Chunks}, parseErrors={ParseErr}, versionMismatch={VerErr}, bufferSamples={BufLen}",
                        totalPackets, totalChunksSent, parseErrors, versionMismatches, sampleBuffer.Count);
                    lastStatsTime = now;
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
