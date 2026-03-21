using Microsoft.Extensions.Options;

namespace RTPTransmitter.Services;

/// <summary>
/// Background service that periodically scans the Immediate Processing directory
/// and distributes recordings to configured storage tiers:
///   - Medium-Term: copies .flac and .svg files as-is.
///   - Long-Term: transcodes .flac to .mp3 (transferring metadata) and copies .svg.
/// If a FLAC-to-MP3 transcode fails the source FLAC is left intact for retry.
/// </summary>
public sealed class FileDistributionService : BackgroundService
{
    private readonly IOptionsMonitor<StoragePathOptions> _pathOptions;
    private readonly ILogger<FileDistributionService> _logger;

    public FileDistributionService(
        IOptionsMonitor<StoragePathOptions> pathOptions,
        ILogger<FileDistributionService> logger)
    {
        _pathOptions = pathOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("FileDistributionService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            var options = _pathOptions.CurrentValue;
            var intervalMs = options.DistributionIntervalSeconds * 1000;

            await Task.Delay(intervalMs, stoppingToken);

            try
            {
                DistributeFiles(options);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during file distribution cycle");
            }
        }
    }

    /// <summary>
    /// Scan the immediate processing directory and distribute recordings.
    /// </summary>
    private void DistributeFiles(StoragePathOptions options)
    {
        var sourceDir = options.ImmediateProcessing;
        if (!Directory.Exists(sourceDir))
            return;

        var hasMedium = options.MediumTermStorage.Count > 0;
        var hasLong = options.LongTermStorage.Count > 0;

        if (!hasMedium && !hasLong)
            return;

        // Ensure all target directories exist
        foreach (var dir in options.MediumTermStorage.Concat(options.LongTermStorage))
        {
            EnsureDirectory(dir);
        }

        // Use .flac files as the primary manifest of recordings to distribute.
        // Each .flac may have a matching .svg alongside it.
        var flacFiles = Directory.GetFiles(sourceDir, "*.flac");
        if (flacFiles.Length == 0)
            return;

        _logger.LogInformation(
            "FileDistribution: found {Count} recording(s) to distribute", flacFiles.Length);

        foreach (var flacPath in flacFiles)
        {
            var baseName = Path.GetFileNameWithoutExtension(flacPath);
            var svgPath = Path.Combine(sourceDir, baseName + ".svg");
            bool svgExists = File.Exists(svgPath);

            // --- Medium-Term: copy FLAC + SVG as-is ---
            if (hasMedium)
            {
                DistributeToMediumTerm(flacPath, svgPath, svgExists, options.MediumTermStorage);
            }

            // --- Long-Term: transcode FLAC → MP3, copy SVG ---
            if (hasLong)
            {
                DistributeToLongTerm(flacPath, baseName, svgPath, svgExists, options.LongTermStorage);
            }
        }
    }

    /// <summary>
    /// Copy FLAC and SVG files to all medium-term storage destinations.
    /// </summary>
    private void DistributeToMediumTerm(
        string flacPath, string svgPath, bool svgExists,
        List<string> destinations)
    {
        var flacName = Path.GetFileName(flacPath);
        var svgName = Path.GetFileName(svgPath);

        foreach (var dest in destinations)
        {
            CopyIfNotExists(flacPath, flacName, dest);

            if (svgExists)
                CopyIfNotExists(svgPath, svgName, dest);
        }
    }

    /// <summary>
    /// Transcode FLAC to MP3 and copy to all long-term storage destinations.
    /// If transcoding fails, the source FLAC is preserved for retry on the next cycle.
    /// The SVG waveform is copied as-is.
    /// </summary>
    private void DistributeToLongTerm(
        string flacPath, string baseName, string svgPath, bool svgExists,
        List<string> destinations)
    {
        var mp3Name = baseName + ".mp3";
        var svgName = baseName + ".svg";

        foreach (var dest in destinations)
        {
            var destMp3Path = Path.Combine(dest, mp3Name);

            // Skip if the MP3 already exists at this destination
            if (!File.Exists(destMp3Path))
            {
                // Transcode to a temp file in the destination, then rename on success
                var tempPath = destMp3Path + ".tmp";

                try
                {
                    bool success = Mp3TranscodeHelper.Transcode(flacPath, tempPath, logger: _logger);

                    if (success && File.Exists(tempPath))
                    {
                        File.Move(tempPath, destMp3Path, overwrite: false);
                        _logger.LogInformation(
                            "FileDistribution: transcoded {Flac} -> {Mp3}",
                            Path.GetFileName(flacPath), destMp3Path);
                    }
                    else
                    {
                        // Transcode failed — leave the source FLAC intact for retry
                        _logger.LogWarning(
                            "FileDistribution: transcode failed for {Flac} to {Dest}, will retry next cycle",
                            Path.GetFileName(flacPath), dest);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "FileDistribution: transcode error for {Flac} to {Dest}",
                        Path.GetFileName(flacPath), dest);

                    // Clean up temp file on failure
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                }
            }

            // Copy SVG as-is
            if (svgExists)
                CopyIfNotExists(svgPath, svgName, dest);
        }
    }

    /// <summary>
    /// Copy a file to a destination directory if it doesn't already exist there.
    /// Returns true on success or if the file already exists at the destination.
    /// </summary>
    private bool CopyIfNotExists(string sourceFilePath, string fileName, string destDir)
    {
        var destPath = Path.Combine(destDir, fileName);

        if (File.Exists(destPath))
            return true;

        try
        {
            File.Copy(sourceFilePath, destPath, overwrite: false);
            _logger.LogInformation(
                "FileDistribution: copied {File} to {Destination}", fileName, destDir);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "FileDistribution: failed to copy {File} to {Destination}", fileName, destDir);
            return false;
        }
    }

    private void EnsureDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "FileDistribution: failed to create directory {Path}", path);
        }
    }
}
