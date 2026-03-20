using System.Text.RegularExpressions;
using RTPTransmitter.Services;

namespace RTPTransmitter.Api;

/// <summary>
/// Converts SDP session names into URL-safe slugs and resolves slugs back to streams.
/// </summary>
public static partial class SlugHelper
{
    /// <summary>
    /// Create a URL-safe slug from a stream name.
    /// Example: "Dante-AES67 : 25" → "dante-aes67-25"
    /// </summary>
    public static string ToSlug(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "unknown";

        var slug = name.ToLowerInvariant();
        slug = NonAlphanumericRegex().Replace(slug, "-");
        slug = MultipleDashRegex().Replace(slug, "-").Trim('-');
        return string.IsNullOrEmpty(slug) ? "unknown" : slug;
    }

    /// <summary>
    /// Resolve a stream reference (slug or ID) to a <see cref="DiscoveredStream"/>.
    /// Tries exact ID match first, then slug match.
    /// </summary>
    public static DiscoveredStream? Resolve(string streamRef, SapStreamRegistry registry)
    {
        // Try exact ID match first
        var byId = registry.Get(streamRef);
        if (byId != null) return byId;

        // Try slug match
        foreach (var stream in registry.GetAll())
        {
            if (string.Equals(ToSlug(stream.Name), streamRef, StringComparison.OrdinalIgnoreCase))
                return stream;
        }

        return null;
    }

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlphanumericRegex();

    [GeneratedRegex(@"-{2,}")]
    private static partial Regex MultipleDashRegex();
}
