using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RTPTransmitter.Services;

namespace RTPTransmitter.Api.Controllers;

/// <summary>
/// Manage channel recordings — list, start, and stop.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(AuthenticationSchemes = ApiKeyAuthHandler.SchemeName)]
[Produces("application/json")]
public class RecordingsController : ControllerBase
{
    private readonly ChannelRecordingService _recordingService;
    private readonly SapStreamRegistry _registry;
    private readonly RtpStreamManager _rtpManager;

    public RecordingsController(
        ChannelRecordingService recordingService,
        SapStreamRegistry registry,
        RtpStreamManager rtpManager)
    {
        _recordingService = recordingService;
        _registry = registry;
        _rtpManager = rtpManager;
    }

    /// <summary>
    /// Get all channels currently being recorded.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<ChannelRecordingDto>), StatusCodes.Status200OK)]
    public IActionResult GetAll()
    {
        var recordings = _recordingService.GetAllRecordingInfo();
        var dtos = recordings.Select(r =>
        {
            var stream = _registry.Get(r.StreamId);
            var label = GetChannelLabel(stream, r.Channel);
            return new ChannelRecordingDto
            {
                StreamSlug = stream != null ? SlugHelper.ToSlug(stream.Name) : r.StreamId,
                StreamId = r.StreamId,
                StreamName = stream?.Name ?? r.StreamId,
                Channel = r.Channel,
                ChannelLabel = label,
                SampleRate = r.SampleRate,
                BitDepth = r.BitDepth,
                VoiceDetectEnabled = r.VoiceDetectEnabled,
                BufferBytes = r.BufferBytes,
                LastPacketTime = r.LastPacketTime
            };
        }).ToList();

        return Ok(dtos);
    }

    /// <summary>
    /// Start recording a channel. If the stream is not already active (RTP listener
    /// not running), this will also start the RTP listener.
    /// </summary>
    [HttpPost("start")]
    [ProducesResponseType(typeof(ChannelRecordingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult Start([FromBody] RecordingRequestDto request)
    {
        var stream = SlugHelper.Resolve(request.Stream, _registry);
        if (stream == null)
            return NotFound(new { error = $"Stream '{request.Stream}' not found." });

        if (request.Channel < 0 || request.Channel >= stream.Channels)
            return BadRequest(new { error = $"Channel {request.Channel} is out of range (0–{stream.Channels - 1})." });

        // Ensure the RTP listener is running so samples flow to the recording service
        if (!_rtpManager.IsActive(stream.Id))
            _rtpManager.StartListening(stream);

        _recordingService.SetChannelRecording(
            stream.Id, request.Channel, enabled: true,
            stream.SampleRate, stream.BitDepth);

        var label = GetChannelLabel(stream, request.Channel);
        var dto = new ChannelRecordingDto
        {
            StreamSlug = SlugHelper.ToSlug(stream.Name),
            StreamId = stream.Id,
            StreamName = stream.Name,
            Channel = request.Channel,
            ChannelLabel = label,
            SampleRate = stream.SampleRate,
            BitDepth = stream.BitDepth,
            VoiceDetectEnabled = false,
            BufferBytes = 0,
            LastPacketTime = DateTimeOffset.UtcNow
        };

        return Ok(dto);
    }

    /// <summary>
    /// Stop recording a channel.
    /// </summary>
    [HttpPost("stop")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Stop([FromBody] RecordingRequestDto request)
    {
        var stream = SlugHelper.Resolve(request.Stream, _registry);
        if (stream == null)
            return NotFound(new { error = $"Stream '{request.Stream}' not found." });

        _recordingService.SetChannelRecording(
            stream.Id, request.Channel, enabled: false);

        return Ok(new { message = $"Recording stopped for '{stream.Name}' channel {request.Channel}." });
    }

    /// <summary>
    /// Enable or disable voice detection on a recording channel.
    /// </summary>
    [HttpPost("voice-detect")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult SetVoiceDetect([FromBody] RecordingRequestDto request, [FromQuery] bool enabled = true)
    {
        var stream = SlugHelper.Resolve(request.Stream, _registry);
        if (stream == null)
            return NotFound(new { error = $"Stream '{request.Stream}' not found." });

        _recordingService.SetChannelVoiceDetect(stream.Id, request.Channel, enabled);

        return Ok(new
        {
            message = $"Voice detection {(enabled ? "enabled" : "disabled")} for '{stream.Name}' channel {request.Channel}."
        });
    }

    private static string GetChannelLabel(DiscoveredStream? stream, int channel)
    {
        if (stream?.ChannelLabels != null && channel < stream.ChannelLabels.Count)
            return stream.ChannelLabels[channel];
        return $"Ch {channel + 1}";
    }
}
