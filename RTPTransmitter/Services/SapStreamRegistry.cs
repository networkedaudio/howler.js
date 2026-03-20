using System.Collections.Concurrent;

namespace RTPTransmitter.Services;

/// <summary>
/// Thread-safe registry of streams discovered via SAP/SDP announcements.
/// Registered as a singleton so both the SAP listener and Blazor pages can access it.
/// </summary>
public sealed class SapStreamRegistry
{
    private readonly ConcurrentDictionary<string, DiscoveredStream> _streams = new();

    /// <summary>
    /// Fired when the list of discovered streams changes (add, update, or remove).
    /// </summary>
    public event Action? OnStreamsChanged;

    /// <summary>
    /// Get a snapshot of all currently discovered streams.
    /// </summary>
    public IReadOnlyList<DiscoveredStream> GetAll() =>
        _streams.Values.OrderBy(s => s.Name).ThenBy(s => s.Id).ToList();

    /// <summary>
    /// Get a specific stream by its SAP announcement key.
    /// </summary>
    public DiscoveredStream? Get(string id) =>
        _streams.TryGetValue(id, out var stream) ? stream : null;

    /// <summary>
    /// Add or update a discovered stream. Returns true if a new stream was added.
    /// </summary>
    public bool AddOrUpdate(DiscoveredStream stream)
    {
        bool isNew = false;
        _streams.AddOrUpdate(stream.Id,
            _ =>
            {
                isNew = true;
                return stream;
            },
            (_, existing) =>
            {
                // Preserve first-seen time, update last-seen and other fields
                stream.FirstSeen = existing.FirstSeen;
                stream.LastSeen = DateTimeOffset.UtcNow;
                return stream;
            });

        OnStreamsChanged?.Invoke();
        return isNew;
    }

    /// <summary>
    /// Remove a stream (e.g., on SAP deletion message). Returns true if removed.
    /// </summary>
    public bool Remove(string id)
    {
        if (_streams.TryRemove(id, out _))
        {
            OnStreamsChanged?.Invoke();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Remove streams not seen since the given cutoff time.
    /// </summary>
    public int PurgeExpired(DateTimeOffset cutoff)
    {
        int removed = 0;
        foreach (var kvp in _streams)
        {
            if (kvp.Value.LastSeen < cutoff)
            {
                if (_streams.TryRemove(kvp.Key, out _))
                    removed++;
            }
        }

        if (removed > 0)
            OnStreamsChanged?.Invoke();

        return removed;
    }

    /// <summary>
    /// Number of discovered streams.
    /// </summary>
    public int Count => _streams.Count;
}
