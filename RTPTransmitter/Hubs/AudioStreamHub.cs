using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using RTPTransmitter.Services;

namespace RTPTransmitter.Hubs;

/// <summary>
/// SignalR hub that streams audio chunks from the RTP listener to connected browsers.
/// The browser connects to this hub and receives "ReceiveAudioChunk" messages
/// containing interleaved Float32 PCM samples (base64) plus the source channel count.
/// Each client independently de-interleaves and selects which channels to play.
///
/// Supports both the static "default" stream (from appsettings) and dynamically
/// started streams from SAP discovery via RtpStreamManager.
/// </summary>
public sealed class AudioStreamHub : Hub
{
    private readonly ILogger<AudioStreamHub> _logger;
    private readonly RtpListenerOptions _options;
    private readonly RtpStreamManager _streamManager;
    private readonly ChannelRecordingService _recordingService;
    private readonly SoundcardCaptureService _soundcardService;

    public AudioStreamHub(
        ILogger<AudioStreamHub> logger,
        IOptions<RtpListenerOptions> options,
        RtpStreamManager streamManager,
        ChannelRecordingService recordingService,
        SoundcardCaptureService soundcardService)
    {
        _logger = logger;
        _options = options.Value;
        _streamManager = streamManager;
        _recordingService = recordingService;
        _soundcardService = soundcardService;
    }

    public override Task OnConnectedAsync()
    {
        _logger.LogInformation("Audio client connected: {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Audio client disconnected: {ConnectionId}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Called by the browser to join the audio stream group.
    /// Returns stream metadata including the total source channel count
    /// so the client knows what channels are available for selection.
    /// For the "default" stream, uses static config. For SAP-discovered
    /// streams, reads from the active stream manager.
    /// </summary>
    public async Task<StreamInfo> JoinAudioStream(string streamId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, streamId);

        int sampleRate;
        int channels;

        // Check if this is a soundcard stream
        var soundcardCapture = _soundcardService.GetActiveCaptures()
            .FirstOrDefault(c => c.StreamId == streamId);

        if (soundcardCapture != null)
        {
            sampleRate = SoundcardCaptureService.SampleRate;
            channels = SoundcardCaptureService.Channels;
        }
        else
        {
            // Check if this is a dynamically-started SAP stream
            var activeStreams = _streamManager.GetActiveStreams();
            var active = activeStreams.FirstOrDefault(s => s.StreamId == streamId);

            if (active != null)
            {
                sampleRate = active.SampleRate;
                channels = active.Channels;
            }
            else
            {
                // Fall back to static config (the "default" stream)
                sampleRate = _options.SampleRate;
                channels = _options.Channels;
            }
        }

        _logger.LogInformation(
            "Client {ConnectionId} joined stream {StreamId} ({Channels} channels)",
            Context.ConnectionId, streamId, channels);

        return new StreamInfo
        {
            SampleRate = sampleRate,
            SourceChannels = channels,
            StreamId = streamId
        };
    }

    /// <summary>
    /// Called by the browser to enable/disable recording for a channel.
    /// </summary>
    public void SetChannelRecording(string streamId, int channel, bool enabled)
    {
        // Resolve stream parameters (sample rate, bit depth)
        int sampleRate = _options.SampleRate;
        int bitDepth = 24;

        var activeStreams = _streamManager.GetActiveStreams();
        var active = activeStreams.FirstOrDefault(s => s.StreamId == streamId);
        if (active != null)
        {
            sampleRate = active.SampleRate;
            bitDepth = active.BitDepth;
        }

        _recordingService.SetChannelRecording(streamId, channel, enabled, sampleRate, bitDepth);
        _logger.LogInformation(
            "Client {ConnectionId} set recording {State} for stream {StreamId} channel {Channel}",
            Context.ConnectionId, enabled ? "ON" : "OFF", streamId, channel);
    }

    /// <summary>
    /// Called by the browser to enable/disable voice detection for a channel.
    /// </summary>
    public void SetChannelVoiceDetect(string streamId, int channel, bool enabled)
    {
        _recordingService.SetChannelVoiceDetect(streamId, channel, enabled);
        _logger.LogInformation(
            "Client {ConnectionId} set voice detect {State} for stream {StreamId} channel {Channel}",
            Context.ConnectionId, enabled ? "ON" : "OFF", streamId, channel);
    }

    /// <summary>
    /// Called by the browser to leave the audio stream group.
    /// </summary>
    public async Task LeaveAudioStream(string streamId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, streamId);
        _logger.LogInformation("Client {ConnectionId} left stream {StreamId}", Context.ConnectionId, streamId);
    }
}

/// <summary>
/// Metadata returned to the browser when joining a stream,
/// describing the available channels and format.
/// </summary>
public sealed class StreamInfo
{
    public int SampleRate { get; set; }
    public int SourceChannels { get; set; }
    public string StreamId { get; set; } = string.Empty;
}
