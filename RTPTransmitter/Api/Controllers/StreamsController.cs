using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RTPTransmitter.Services;

namespace RTPTransmitter.Api.Controllers;

/// <summary>
/// Discovered SAP/SDP streams.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(AuthenticationSchemes = ApiKeyAuthHandler.SchemeName)]
[Produces("application/json")]
public class StreamsController : ControllerBase
{
    private readonly SapStreamRegistry _registry;
    private readonly RtpStreamManager _rtpManager;

    public StreamsController(SapStreamRegistry registry, RtpStreamManager rtpManager)
    {
        _registry = registry;
        _rtpManager = rtpManager;
    }

    /// <summary>
    /// Get all discovered streams.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<StreamDto>), StatusCodes.Status200OK)]
    public IActionResult GetAll()
    {
        var streams = _registry.GetAll().Select(s => MapToDto(s)).ToList();
        return Ok(streams);
    }

    /// <summary>
    /// Get a single stream by slug or ID.
    /// </summary>
    [HttpGet("{streamRef}")]
    [ProducesResponseType(typeof(StreamDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Get(string streamRef)
    {
        var stream = SlugHelper.Resolve(streamRef, _registry);
        if (stream == null)
            return NotFound(new { error = $"Stream '{streamRef}' not found." });

        return Ok(MapToDto(stream));
    }

    /// <summary>
    /// Get the raw SDP text for a stream.
    /// </summary>
    [HttpGet("{streamRef}/sdp")]
    [Produces("application/sdp", "text/plain")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetSdp(string streamRef)
    {
        var stream = SlugHelper.Resolve(streamRef, _registry);
        if (stream == null)
            return NotFound(new { error = $"Stream '{streamRef}' not found." });

        if (string.IsNullOrWhiteSpace(stream.RawSdp))
            return NotFound(new { error = "No SDP available for this stream." });

        return Content(stream.RawSdp, "application/sdp");
    }

    private StreamDto MapToDto(DiscoveredStream s) => new()
    {
        Slug = SlugHelper.ToSlug(s.Name),
        Id = s.Id,
        Name = s.Name,
        Description = s.Description,
        MulticastGroup = s.MulticastGroup,
        Port = s.Port,
        SampleRate = s.SampleRate,
        Channels = s.Channels,
        BitDepth = s.BitDepth,
        Codec = s.Codec,
        ChannelLabels = s.ChannelLabels,
        IsManual = s.IsManual,
        FirstSeen = s.FirstSeen,
        LastSeen = s.LastSeen,
        IsActive = _rtpManager.IsActive(s.Id)
    };
}
