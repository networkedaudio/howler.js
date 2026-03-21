using System.Diagnostics;

namespace RTPTransmitter.Services;

/// <summary>
/// Periodically collects cross-platform process and system metrics.
/// Uses <see cref="Process"/> APIs (WorkingSet, TotalProcessorTime)
/// and <see cref="GC"/> APIs which work on Windows, Linux, and macOS.
/// </summary>
public sealed class SystemMonitorService : BackgroundService
{
    private readonly ILogger<SystemMonitorService> _logger;
    private volatile SystemSnapshot _latest = new();

    public SystemMonitorService(ILogger<SystemMonitorService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// The most recent snapshot of system metrics.
    /// </summary>
    public SystemSnapshot Latest => _latest;

    /// <summary>
    /// Raised after each new snapshot is captured (roughly every second).
    /// </summary>
    public event Action? OnSnapshotUpdated;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SystemMonitorService started");

        var process = Process.GetCurrentProcess();
        var lastCpuTime = process.TotalProcessorTime;
        var lastMeasure = Stopwatch.GetTimestamp();

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1_000, stoppingToken);

            try
            {
                process.Refresh();

                var now = Stopwatch.GetTimestamp();
                var elapsed = Stopwatch.GetElapsedTime(lastMeasure, now);
                var cpuUsed = process.TotalProcessorTime - lastCpuTime;
                lastCpuTime = process.TotalProcessorTime;
                lastMeasure = now;

                // CPU% normalised to 0-100 across all logical cores
                var cpuPercent = elapsed.TotalMilliseconds > 0
                    ? cpuUsed.TotalMilliseconds / elapsed.TotalMilliseconds / Environment.ProcessorCount * 100.0
                    : 0;

                var gcInfo = GC.GetGCMemoryInfo();

                _latest = new SystemSnapshot
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    CpuPercent = Math.Round(Math.Clamp(cpuPercent, 0, 100), 1),
                    WorkingSetBytes = process.WorkingSet64,
                    PrivateMemoryBytes = process.PrivateMemorySize64,
                    GcHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
                    GcGen0Collections = GC.CollectionCount(0),
                    GcGen1Collections = GC.CollectionCount(1),
                    GcGen2Collections = GC.CollectionCount(2),
                    GcTotalCommittedBytes = gcInfo.TotalCommittedBytes,
                    ThreadCount = process.Threads.Count,
                    HandleCount = process.HandleCount,
                    Uptime = DateTimeOffset.UtcNow - process.StartTime.ToUniversalTime()
                };

                OnSnapshotUpdated?.Invoke();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Error collecting system metrics");
            }
        }
    }
}

/// <summary>
/// Point-in-time snapshot of process and runtime metrics.
/// </summary>
public sealed class SystemSnapshot
{
    public DateTimeOffset Timestamp { get; init; }
    public double CpuPercent { get; init; }
    public long WorkingSetBytes { get; init; }
    public long PrivateMemoryBytes { get; init; }
    public long GcHeapBytes { get; init; }
    public int GcGen0Collections { get; init; }
    public int GcGen1Collections { get; init; }
    public int GcGen2Collections { get; init; }
    public long GcTotalCommittedBytes { get; init; }
    public int ThreadCount { get; init; }
    public int HandleCount { get; init; }
    public TimeSpan Uptime { get; init; }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
