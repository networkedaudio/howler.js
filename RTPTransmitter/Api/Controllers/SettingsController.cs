using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RTPTransmitter.Services;

namespace RTPTransmitter.Api.Controllers;

/// <summary>
/// Read and update recording settings in appsettings.json.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(AuthenticationSchemes = ApiKeyAuthHandler.SchemeName)]
[Produces("application/json")]
public class SettingsController : ControllerBase
{
    private readonly IOptionsMonitor<RecordingOptions> _recordingOptions;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(
        IOptionsMonitor<RecordingOptions> recordingOptions,
        IWebHostEnvironment env,
        ILogger<SettingsController> logger)
    {
        _recordingOptions = recordingOptions;
        _env = env;
        _logger = logger;
    }

    /// <summary>
    /// Get the current recording settings.
    /// </summary>
    [HttpGet("recording")]
    [ProducesResponseType(typeof(RecordingSettingsDto), StatusCodes.Status200OK)]
    public IActionResult GetRecordingSettings()
    {
        var opts = _recordingOptions.CurrentValue;
        return Ok(new RecordingSettingsDto
        {
            OutputDirectory = opts.OutputDirectory,
            SilencePacketThreshold = opts.SilencePacketThreshold,
            NoPacketTimeoutMs = opts.NoPacketTimeoutMs,
            MaxBufferSizeBytes = opts.MaxBufferSizeBytes
        });
    }

    /// <summary>
    /// Update recording settings. Changes are persisted to appsettings.json and
    /// take effect on the next options reload cycle.
    /// </summary>
    [HttpPut("recording")]
    [ProducesResponseType(typeof(RecordingSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateRecordingSettings([FromBody] RecordingSettingsDto dto)
    {
        // Validate
        if (dto.SilencePacketThreshold < 1)
            return BadRequest(new { error = "SilencePacketThreshold must be >= 1." });
        if (dto.NoPacketTimeoutMs < 100)
            return BadRequest(new { error = "NoPacketTimeoutMs must be >= 100." });
        if (dto.MaxBufferSizeBytes < 1024)
            return BadRequest(new { error = "MaxBufferSizeBytes must be >= 1024." });

        var appSettingsPath = Path.Combine(_env.ContentRootPath, "appsettings.json");

        try
        {
            var json = await System.IO.File.ReadAllTextAsync(appSettingsPath);
            var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            }) as JsonObject;

            if (root == null)
                return StatusCode(500, new { error = "Failed to parse appsettings.json." });

            var recording = root[RecordingOptions.Section] as JsonObject ?? new JsonObject();
            recording["OutputDirectory"] = dto.OutputDirectory;
            recording["SilencePacketThreshold"] = dto.SilencePacketThreshold;
            recording["NoPacketTimeoutMs"] = dto.NoPacketTimeoutMs;
            recording["MaxBufferSizeBytes"] = dto.MaxBufferSizeBytes;
            root[RecordingOptions.Section] = recording;

            var options = new JsonSerializerOptions { WriteIndented = true };
            await System.IO.File.WriteAllTextAsync(appSettingsPath, root.ToJsonString(options));

            _logger.LogInformation("Recording settings updated via API: {@Settings}", dto);

            return Ok(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update appsettings.json via API");
            return StatusCode(500, new { error = "Failed to write settings." });
        }
    }
}
