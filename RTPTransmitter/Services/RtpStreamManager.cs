using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.SignalR;
using RTPTransmitter.Hubs;

namespace RTPTransmitter.Services;

/// <summary>
/// Manages dynamically-started RTP listener sessions. When a user selects a
/// discovered SAP stream, this manager starts listening for RTP on the
/// corresponding multicast group / port and pushes audio to the SignalR group.
///
/// Each active stream gets its own UDP socket and cancellation token.
/// Multiple browser clients can share the same RTP listener.
/// </summary>
public sealed class RtpStreamManager : IDisposable
{
    private readonly ILogger<RtpStreamManager> _logger;
    private readonly IHubContext<AudioStreamHub> _hubContext;
    private readonly ConcurrentDictionary<string, ActiveStream> _activeStreams = new();

    public RtpStreamManager(
        ILogger<RtpStreamManager> logger,
        IHubContext<AudioStreamHub> hubContext)
    {
        _logger = logger;
        _hubContext = hubContext;
    }

    /// <summary>
    /// Information about a currently active RTP listener.
    /// </summary>
    public sealed class ActiveStream
    {
        public required string StreamId { get; init; }
        public required string MulticastGroup { get; init; }
        public required int Port { get; init; }
        public required int SampleRate { get; init; }
        public required int Channels { get; init; }
        public required int BitDepth { get; init; }
        public required string Name { get; init; }
        public CancellationTokenSource Cts { get; init; } = new();
        public Task? ListenerTask { get; set; }
        public int ClientCount;
        public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Get all currently active streams.
    /// </summary>
    public IReadOnlyList<ActiveStream> GetActiveStreams() =>
        _activeStreams.Values.ToList();

    /// <summary>
    /// Check if a stream is currently being listened to.
    /// </summary>
    public bool IsActive(string streamId) =>
        _activeStreams.ContainsKey(streamId);

    /// <summary>
    /// Start listening to an RTP stream. If already active, increments the client count.
    /// Returns the stream info for the SignalR client.
    /// </summary>
    public ActiveStream StartListening(DiscoveredStream discovered)
    {
        var active = _activeStreams.GetOrAdd(discovered.Id, id =>
        {
            var stream = new ActiveStream
            {
                StreamId = id,
                MulticastGroup = discovered.MulticastGroup,
                Port = discovered.Port,
                SampleRate = discovered.SampleRate,
                Channels = discovered.Channels,
                BitDepth = discovered.BitDepth,
                Name = discovered.Name
            };

            stream.ListenerTask = Task.Run(() => RunListener(stream, stream.Cts.Token));

            _logger.LogInformation(
                "Started RTP listener for \"{Name}\" on {Multicast}:{Port} ({BitDepth}bit/{SampleRate}Hz/{Channels}ch)",
                stream.Name, stream.MulticastGroup, stream.Port,
                stream.BitDepth, stream.SampleRate, stream.Channels);

            return stream;
        });

        Interlocked.Increment(ref active.ClientCount);
        return active;
    }

    /// <summary>
    /// Stop listening to an RTP stream. Decrements client count.
    /// Fully stops the listener when no clients remain.
    /// </summary>
    public async Task StopListening(string streamId, bool force = false)
    {
        if (!_activeStreams.TryGetValue(streamId, out var active))
            return;

        int remaining = Interlocked.Decrement(ref active.ClientCount);

        if (remaining <= 0 || force)
        {
            if (_activeStreams.TryRemove(streamId, out _))
            {
                _logger.LogInformation("Stopping RTP listener for \"{Name}\" ({StreamId})", active.Name, streamId);
                active.Cts.Cancel();

                if (active.ListenerTask != null)
                {
                    try { await active.ListenerTask; }
                    catch (OperationCanceledException) { }
                }

                active.Cts.Dispose();
            }
        }
    }

    private async Task RunListener(ActiveStream stream, CancellationToken ct)
    {
        try
        {
            using var udpClient = new UdpClient();
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, stream.Port));

            _logger.LogInformation(
                "RTP [{StreamId}] UDP socket bound to 0.0.0.0:{Port}",
                stream.StreamId, stream.Port);

            if (!string.IsNullOrWhiteSpace(stream.MulticastGroup))
            {
                var multicastAddress = IPAddress.Parse(stream.MulticastGroup);
                udpClient.JoinMulticastGroup(multicastAddress);
                _logger.LogInformation(
                    "RTP [{StreamId}] Joined multicast {Group}:{Port}",
                    stream.StreamId, stream.MulticastGroup, stream.Port);
            }
            else
            {
                _logger.LogWarning(
                    "RTP [{StreamId}] No multicast group configured — listening unicast only on port {Port}",
                    stream.StreamId, stream.Port);
            }

            _logger.LogInformation(
                "RTP [{StreamId}] Waiting for packets (bitDepth={BitDepth}, sampleRate={SampleRate}, channels={Channels})",
                stream.StreamId, stream.BitDepth, stream.SampleRate, stream.Channels);

            var sampleBuffer = new List<float>();
            ushort lastSequence = 0;
            bool firstPacket = true;
            int packetsInChunk = 0;
            const int packetsPerChunk = 10;
            long totalPackets = 0;
            long totalChunksSent = 0;
            long parseErrors = 0;
            long versionMismatches = 0;
            var lastStatsTime = DateTimeOffset.UtcNow;

            while (!ct.IsCancellationRequested)
            {
                var result = await udpClient.ReceiveAsync(ct);
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
                            "RTP [{StreamId}] Parse error (#{Count}): {Message} (datagram {Bytes} bytes from {Remote})",
                            stream.StreamId, parseErrors, ex.Message, data.Length, result.RemoteEndPoint);
                    continue;
                }

                if (packet.Version != 2)
                {
                    versionMismatches++;
                    if (versionMismatches <= 3)
                        _logger.LogWarning(
                            "RTP [{StreamId}] Non-RTPv2 packet (version={Version}, PT={PT}, {Bytes} bytes from {Remote})",
                            stream.StreamId, packet.Version, packet.PayloadType, data.Length, result.RemoteEndPoint);
                    continue;
                }

                // Log first packet in detail
                if (firstPacket)
                {
                    _logger.LogInformation(
                        "RTP [{StreamId}] *** First packet *** from {Remote}: PT={PT}, seq={Seq}, SSRC={Ssrc}, payloadBytes={PayloadLen}, extension={Ext}",
                        stream.StreamId, result.RemoteEndPoint, packet.PayloadType, packet.SequenceNumber,
                        packet.Ssrc, packet.Payload.Length, packet.Extension);
                }

                // Detect sequence gaps
                if (!firstPacket)
                {
                    int expectedSeq = (lastSequence + 1) & 0xFFFF;
                    if (packet.SequenceNumber != expectedSeq)
                    {
                        int gap = ((packet.SequenceNumber - lastSequence) + 65536) % 65536;
                        _logger.LogDebug(
                            "RTP [{Stream}] sequence gap: expected {Expected}, got {Got} (gap={Gap})",
                            stream.StreamId, expectedSeq, packet.SequenceNumber, gap);
                    }
                }
                firstPacket = false;
                lastSequence = packet.SequenceNumber;

                // Convert payload to float32 samples
                float[] samples = PcmConverter.ConvertPayload(packet.Payload, packet.PayloadType, stream.BitDepth);

                sampleBuffer.AddRange(samples);
                packetsInChunk++;

                if (packetsInChunk >= packetsPerChunk)
                {
                    var chunkSamples = sampleBuffer.ToArray();
                    sampleBuffer.Clear();
                    packetsInChunk = 0;

                    var byteArray = new byte[chunkSamples.Length * sizeof(float)];
                    Buffer.BlockCopy(chunkSamples, 0, byteArray, 0, byteArray.Length);
                    var base64 = Convert.ToBase64String(byteArray);

                    await _hubContext.Clients.Group(stream.StreamId)
                        .SendAsync("ReceiveAudioChunk", base64, stream.Channels, ct);

                    totalChunksSent++;

                    if (totalChunksSent == 1)
                    {
                        _logger.LogInformation(
                            "RTP [{StreamId}] *** First chunk sent *** to group \"{Group}\" ({Samples} samples, {Bytes} base64 chars, {Channels}ch)",
                            stream.StreamId, stream.StreamId, chunkSamples.Length, base64.Length, stream.Channels);
                    }
                }

                // Periodic stats every 10 seconds
                var now = DateTimeOffset.UtcNow;
                if ((now - lastStatsTime).TotalSeconds >= 10)
                {
                    _logger.LogInformation(
                        "RTP [{StreamId}] Stats: packets={Packets}, chunksSent={Chunks}, parseErrors={ParseErr}, versionMismatch={VerErr}, bufferSamples={BufLen}",
                        stream.StreamId, totalPackets, totalChunksSent, parseErrors, versionMismatches, sampleBuffer.Count);
                    lastStatsTime = now;
                }
            }

            // Leave multicast
            if (!string.IsNullOrWhiteSpace(stream.MulticastGroup))
            {
                try
                {
                    udpClient.DropMulticastGroup(IPAddress.Parse(stream.MulticastGroup));
                }
                catch { }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RTP listener error for stream {StreamId}", stream.StreamId);
        }

        _logger.LogInformation("RTP listener stopped for \"{Name}\" ({StreamId})", stream.Name, stream.StreamId);
    }

    public void Dispose()
    {
        foreach (var kvp in _activeStreams)
        {
            kvp.Value.Cts.Cancel();
            kvp.Value.Cts.Dispose();
        }
        _activeStreams.Clear();
    }
}
