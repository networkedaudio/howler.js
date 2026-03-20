using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Pv;
using RTPTransmitter.Hubs;

namespace RTPTransmitter.Services;

/// <summary>
/// Manages audio capture from local recording devices using PvRecorder.
/// Captured audio is pushed to the SignalR hub in the same interleaved float32
/// format as RTP streams, so the AudioStream page can play it identically.
/// </summary>
public sealed class SoundcardCaptureService : IDisposable
{
    private readonly ILogger<SoundcardCaptureService> _logger;
    private readonly IHubContext<AudioStreamHub> _hubContext;
    private readonly ChannelRecordingService _recordingService;
    private readonly ConcurrentDictionary<string, ActiveCapture> _captures = new();

    /// <summary>PvRecorder fixed sample rate (16 kHz).</summary>
    public const int SampleRate = 16000;

    /// <summary>PvRecorder is always mono.</summary>
    public const int Channels = 1;

    /// <summary>PvRecorder provides 16-bit signed PCM.</summary>
    public const int BitDepth = 16;

    /// <summary>Frame length in samples (~32 ms at 16 kHz).</summary>
    public const int FrameLength = 512;

    public SoundcardCaptureService(
        ILogger<SoundcardCaptureService> logger,
        IHubContext<AudioStreamHub> hubContext,
        ChannelRecordingService recordingService)
    {
        _logger = logger;
        _hubContext = hubContext;
        _recordingService = recordingService;
    }

    /// <summary>
    /// Information about a currently active soundcard capture.
    /// </summary>
    public sealed class ActiveCapture
    {
        public required string StreamId { get; init; }
        public required int DeviceIndex { get; init; }
        public required string DeviceName { get; init; }
        public CancellationTokenSource Cts { get; init; } = new();
        public Task? CaptureTask { get; set; }
        public int ClientCount;
        public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Build the canonical stream ID for a soundcard device.
    /// </summary>
    public static string MakeStreamId(int deviceIndex) => $"soundcard:{deviceIndex}";

    /// <summary>
    /// Enumerate all available recording devices on the system.
    /// </summary>
    public string[] GetAvailableDevices()
    {
        try
        {
            return PvRecorder.GetAvailableDevices();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate recording devices");
            return [];
        }
    }

    /// <summary>
    /// Check if a device is currently being captured.
    /// </summary>
    public bool IsActive(int deviceIndex) =>
        _captures.ContainsKey(MakeStreamId(deviceIndex));

    /// <summary>
    /// Get all currently active captures.
    /// </summary>
    public IReadOnlyList<ActiveCapture> GetActiveCaptures() =>
        _captures.Values.ToList();

    /// <summary>
    /// Start capturing from a recording device. If already active, increments client count.
    /// </summary>
    public ActiveCapture StartCapture(int deviceIndex, string deviceName)
    {
        var streamId = MakeStreamId(deviceIndex);

        var capture = _captures.GetOrAdd(streamId, _ =>
        {
            var c = new ActiveCapture
            {
                StreamId = streamId,
                DeviceIndex = deviceIndex,
                DeviceName = deviceName
            };

            c.CaptureTask = Task.Run(() => RunCapture(c, c.Cts.Token));

            _logger.LogInformation(
                "Started soundcard capture for \"{Name}\" (index {Index})",
                deviceName, deviceIndex);

            return c;
        });

        Interlocked.Increment(ref capture.ClientCount);
        return capture;
    }

    /// <summary>
    /// Stop capturing from a device. Decrements client count;
    /// fully stops when no clients remain.
    /// </summary>
    public async Task StopCapture(int deviceIndex, bool force = false)
    {
        var streamId = MakeStreamId(deviceIndex);
        if (!_captures.TryGetValue(streamId, out var capture))
            return;

        int remaining = Interlocked.Decrement(ref capture.ClientCount);

        if (remaining <= 0 || force)
        {
            if (_captures.TryRemove(streamId, out _))
            {
                _logger.LogInformation(
                    "Stopping soundcard capture for \"{Name}\"", capture.DeviceName);
                capture.Cts.Cancel();

                if (capture.CaptureTask != null)
                {
                    try { await capture.CaptureTask; }
                    catch (OperationCanceledException) { }
                }

                capture.Cts.Dispose();
            }
        }
    }

    /// <summary>
    /// Background capture loop: reads frames from PvRecorder, converts to float32,
    /// and pushes to the SignalR hub group in the same format as RTP streams.
    /// </summary>
    private async Task RunCapture(ActiveCapture capture, CancellationToken ct)
    {
        PvRecorder? recorder = null;
        try
        {
            recorder = PvRecorder.Create(FrameLength, capture.DeviceIndex);
            recorder.Start();

            _logger.LogInformation(
                "Soundcard [{StreamId}] capturing from \"{Name}\" at {SampleRate}Hz",
                capture.StreamId, capture.DeviceName, recorder.SampleRate);

            var sampleBuffer = new List<float>();
            const int framesPerChunk = 10; // ~320 ms chunks, similar to RTP chunking
            int frameCount = 0;
            long totalChunksSent = 0;

            while (!ct.IsCancellationRequested)
            {
                short[] pcmFrame = recorder.Read();

                // Convert 16-bit PCM to normalised float32 (same format as RTP pipeline)
                var floatSamples = new float[pcmFrame.Length];
                for (int i = 0; i < pcmFrame.Length; i++)
                    floatSamples[i] = pcmFrame[i] / 32768f;

                // Feed to recording service (per-channel capture)
                _recordingService.FeedSamples(capture.StreamId, floatSamples, Channels, BitDepth);

                sampleBuffer.AddRange(floatSamples);
                frameCount++;

                if (frameCount >= framesPerChunk)
                {
                    var chunkSamples = sampleBuffer.ToArray();
                    sampleBuffer.Clear();
                    frameCount = 0;

                    var byteArray = new byte[chunkSamples.Length * sizeof(float)];
                    Buffer.BlockCopy(chunkSamples, 0, byteArray, 0, byteArray.Length);
                    var base64 = Convert.ToBase64String(byteArray);

                    await _hubContext.Clients.Group(capture.StreamId)
                        .SendAsync("ReceiveAudioChunk", base64, Channels, ct);

                    totalChunksSent++;

                    if (totalChunksSent == 1)
                    {
                        _logger.LogInformation(
                            "Soundcard [{StreamId}] *** First chunk sent *** ({Samples} samples, {Channels}ch)",
                            capture.StreamId, chunkSamples.Length, Channels);
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Soundcard [{StreamId}] capture error", capture.StreamId);
        }
        finally
        {
            if (recorder != null)
            {
                if (recorder.IsRecording)
                    recorder.Stop();
                recorder.Dispose();
            }
        }
    }

    public void Dispose()
    {
        foreach (var kvp in _captures)
        {
            kvp.Value.Cts.Cancel();
            kvp.Value.Cts.Dispose();
        }
        _captures.Clear();
    }
}
