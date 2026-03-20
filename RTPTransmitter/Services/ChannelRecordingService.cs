using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace RTPTransmitter.Services;

/// <summary>
/// Manages per-channel recording of AES67 audio data.
/// Channels are keyed by "{streamId}:{channelIndex}".
/// When a channel is enabled for recording, incoming interleaved PCM samples
/// are de-interleaved and the selected channel's raw bytes are buffered.
/// The buffer is flushed to disk when:
///   - A configurable number of consecutive silent (all-zero) packets arrive
///   - No packets arrive within a timeout window
///   - The buffer exceeds a maximum byte size
/// </summary>
public sealed class ChannelRecordingService : IDisposable
{
    private readonly ILogger<ChannelRecordingService> _logger;
    private readonly RecordingOptions _options;
    private readonly ConcurrentDictionary<string, ChannelRecorder> _recorders = new();
    private readonly Timer _timeoutTimer;

    public ChannelRecordingService(
        ILogger<ChannelRecordingService> logger,
        IOptions<RecordingOptions> options)
    {
        _logger = logger;
        _options = options.Value;

        Directory.CreateDirectory(_options.OutputDirectory);

        // Periodically check for packet-timeout flushes
        _timeoutTimer = new Timer(CheckTimeouts, null, 500, 500);
    }

    /// <summary>
    /// Per-channel recording state.
    /// </summary>
    private sealed class ChannelRecorder
    {
        public required string StreamId { get; init; }
        public required int Channel { get; init; }
        public required int SampleRate { get; init; }
        public required int BitDepth { get; init; }
        public bool VoiceDetectEnabled { get; set; }

        public MemoryStream Buffer { get; } = new();
        public int ConsecutiveSilentPackets { get; set; }
        public DateTimeOffset LastPacketTime { get; set; } = DateTimeOffset.UtcNow;
        public bool HasData => Buffer.Length > 0;
        public readonly object Lock = new();
    }

    /// <summary>
    /// Enable or disable recording for a specific channel on a stream.
    /// </summary>
    public void SetChannelRecording(string streamId, int channel, bool enabled,
        int sampleRate = 48000, int bitDepth = 24)
    {
        var key = $"{streamId}:{channel}";

        if (enabled)
        {
            _recorders.GetOrAdd(key, _ =>
            {
                _logger.LogInformation(
                    "Recording enabled for stream {StreamId} channel {Channel}",
                    streamId, channel);
                return new ChannelRecorder
                {
                    StreamId = streamId,
                    Channel = channel,
                    SampleRate = sampleRate,
                    BitDepth = bitDepth
                };
            });
        }
        else
        {
            if (_recorders.TryRemove(key, out var recorder))
            {
                _logger.LogInformation(
                    "Recording disabled for stream {StreamId} channel {Channel}",
                    streamId, channel);
                lock (recorder.Lock)
                {
                    if (recorder.HasData)
                        FlushToDisk(recorder);
                    recorder.Buffer.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Enable or disable voice detection for a specific channel.
    /// </summary>
    public void SetChannelVoiceDetect(string streamId, int channel, bool enabled)
    {
        var key = $"{streamId}:{channel}";
        if (_recorders.TryGetValue(key, out var recorder))
        {
            recorder.VoiceDetectEnabled = enabled;
            _logger.LogInformation(
                "Voice detect {State} for stream {StreamId} channel {Channel}",
                enabled ? "enabled" : "disabled", streamId, channel);
        }
    }

    /// <summary>
    /// Get the set of channels currently being recorded for a stream.
    /// Returns channel indices.
    /// </summary>
    public HashSet<int> GetRecordingChannels(string streamId)
    {
        var channels = new HashSet<int>();
        foreach (var kvp in _recorders)
        {
            if (kvp.Value.StreamId == streamId)
                channels.Add(kvp.Value.Channel);
        }
        return channels;
    }

    /// <summary>
    /// Snapshot of a single channel recording for API consumption.
    /// </summary>
    public sealed class RecordingInfo
    {
        public required string StreamId { get; init; }
        public required int Channel { get; init; }
        public required int SampleRate { get; init; }
        public required int BitDepth { get; init; }
        public required bool VoiceDetectEnabled { get; init; }
        public required long BufferBytes { get; init; }
        public required DateTimeOffset LastPacketTime { get; init; }
    }

    /// <summary>
    /// Get a snapshot of all active recordings for API use.
    /// </summary>
    public IReadOnlyList<RecordingInfo> GetAllRecordingInfo()
    {
        var list = new List<RecordingInfo>();
        foreach (var kvp in _recorders)
        {
            var r = kvp.Value;
            lock (r.Lock)
            {
                list.Add(new RecordingInfo
                {
                    StreamId = r.StreamId,
                    Channel = r.Channel,
                    SampleRate = r.SampleRate,
                    BitDepth = r.BitDepth,
                    VoiceDetectEnabled = r.VoiceDetectEnabled,
                    BufferBytes = r.Buffer.Length,
                    LastPacketTime = r.LastPacketTime
                });
            }
        }
        return list;
    }

    /// <summary>
    /// Feed interleaved float32 samples from an RTP packet into the recording buffers.
    /// Called by RtpStreamManager for each packet on streams that have recording channels.
    /// </summary>
    public void FeedSamples(string streamId, float[] interleavedSamples, int totalChannels, int bitDepth)
    {
        // Quick check: any recorders for this stream?
        bool hasRecorders = false;
        foreach (var kvp in _recorders)
        {
            if (kvp.Value.StreamId == streamId)
            {
                hasRecorders = true;
                break;
            }
        }
        if (!hasRecorders) return;

        int framesPerChannel = totalChannels > 0 ? interleavedSamples.Length / totalChannels : 0;
        if (framesPerChannel == 0) return;

        foreach (var kvp in _recorders)
        {
            var recorder = kvp.Value;
            if (recorder.StreamId != streamId) continue;
            if (recorder.Channel < 0 || recorder.Channel >= totalChannels) continue;

            lock (recorder.Lock)
            {
                // Extract this channel's samples from the interleaved buffer
                bool allZero = true;
                var channelBytes = ExtractChannelRaw(
                    interleavedSamples, totalChannels, recorder.Channel,
                    framesPerChannel, bitDepth, ref allZero);

                recorder.LastPacketTime = DateTimeOffset.UtcNow;

                if (allZero)
                {
                    recorder.ConsecutiveSilentPackets++;

                    if (recorder.HasData &&
                        recorder.ConsecutiveSilentPackets >= _options.SilencePacketThreshold)
                    {
                        _logger.LogInformation(
                            "Recording [{StreamId}:ch{Channel}] silence threshold reached ({Count} silent packets), flushing",
                            recorder.StreamId, recorder.Channel, recorder.ConsecutiveSilentPackets);
                        FlushToDisk(recorder);
                    }
                }
                else
                {
                    recorder.ConsecutiveSilentPackets = 0;
                    recorder.Buffer.Write(channelBytes);

                    // Check max buffer size
                    if (recorder.Buffer.Length >= _options.MaxBufferSizeBytes)
                    {
                        _logger.LogInformation(
                            "Recording [{StreamId}:ch{Channel}] max buffer size reached ({Size} bytes), flushing",
                            recorder.StreamId, recorder.Channel, recorder.Buffer.Length);
                        FlushToDisk(recorder);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Extract a single channel from interleaved float32 samples and convert back to raw PCM bytes.
    /// </summary>
    private static byte[] ExtractChannelRaw(
        float[] interleaved, int totalChannels, int channel,
        int framesPerChannel, int bitDepth, ref bool allZero)
    {
        int bytesPerSample = bitDepth / 8;
        var raw = new byte[framesPerChannel * bytesPerSample];

        for (int f = 0; f < framesPerChannel; f++)
        {
            float sample = interleaved[f * totalChannels + channel];
            int offset = f * bytesPerSample;

            switch (bitDepth)
            {
                case 16:
                {
                    short s16 = (short)Math.Clamp(sample * 32768f, short.MinValue, short.MaxValue);
                    if (s16 != 0) allZero = false;
                    // Big-endian (AES67 network byte order)
                    raw[offset] = (byte)(s16 >> 8);
                    raw[offset + 1] = (byte)(s16 & 0xFF);
                    break;
                }
                case 24:
                {
                    int s24 = Math.Clamp((int)(sample * 8388608f), -8388608, 8388607);
                    if (s24 != 0) allZero = false;
                    raw[offset] = (byte)((s24 >> 16) & 0xFF);
                    raw[offset + 1] = (byte)((s24 >> 8) & 0xFF);
                    raw[offset + 2] = (byte)(s24 & 0xFF);
                    break;
                }
                case 32:
                {
                    int s32 = (int)Math.Clamp(sample * 2147483648f, int.MinValue, int.MaxValue);
                    if (s32 != 0) allZero = false;
                    raw[offset] = (byte)((s32 >> 24) & 0xFF);
                    raw[offset + 1] = (byte)((s32 >> 16) & 0xFF);
                    raw[offset + 2] = (byte)((s32 >> 8) & 0xFF);
                    raw[offset + 3] = (byte)(s32 & 0xFF);
                    break;
                }
                default:
                    // Fallback to 24-bit
                    goto case 24;
            }
        }

        return raw;
    }

    /// <summary>
    /// Flush the recorder's buffer to a raw file on disk and reset the buffer.
    /// </summary>
    private void FlushToDisk(ChannelRecorder recorder)
    {
        if (recorder.Buffer.Length == 0) return;

        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
        var safeName = SanitizeFileName(recorder.StreamId);
        var fileName = $"{safeName}_ch{recorder.Channel}_{timestamp}.raw";
        var filePath = Path.Combine(_options.OutputDirectory, fileName);

        try
        {
            using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            recorder.Buffer.Position = 0;
            recorder.Buffer.CopyTo(fileStream);

            _logger.LogInformation(
                "Recording [{StreamId}:ch{Channel}] saved {Bytes} bytes to {File}",
                recorder.StreamId, recorder.Channel, recorder.Buffer.Length, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Recording [{StreamId}:ch{Channel}] failed to write {Bytes} bytes to {File}",
                recorder.StreamId, recorder.Channel, recorder.Buffer.Length, filePath);
        }

        recorder.Buffer.SetLength(0);
        recorder.ConsecutiveSilentPackets = 0;
    }

    /// <summary>
    /// Timer callback: flush any recorder that hasn't received data within the timeout.
    /// </summary>
    private void CheckTimeouts(object? state)
    {
        var cutoff = DateTimeOffset.UtcNow.AddMilliseconds(-_options.NoPacketTimeoutMs);

        foreach (var kvp in _recorders)
        {
            var recorder = kvp.Value;
            lock (recorder.Lock)
            {
                if (recorder.HasData && recorder.LastPacketTime < cutoff)
                {
                    _logger.LogInformation(
                        "Recording [{StreamId}:ch{Channel}] no-packet timeout ({Timeout}ms), flushing",
                        recorder.StreamId, recorder.Channel, _options.NoPacketTimeoutMs);
                    FlushToDisk(recorder);
                }
            }
        }
    }

    private static string SanitizeFileName(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", input.Split(invalid, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    public void Dispose()
    {
        _timeoutTimer.Dispose();

        foreach (var kvp in _recorders)
        {
            lock (kvp.Value.Lock)
            {
                if (kvp.Value.HasData)
                    FlushToDisk(kvp.Value);
                kvp.Value.Buffer.Dispose();
            }
        }
        _recorders.Clear();
    }
}
