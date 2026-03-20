using System.Net;
using System.Text.RegularExpressions;

namespace RTPTransmitter.Services;

/// <summary>
/// Parses SDP (Session Description Protocol) payloads to extract
/// AES67/RTP audio stream parameters.
/// </summary>
public static partial class SdpParser
{
    /// <summary>
    /// Parse an SDP payload string and extract audio stream parameters.
    /// </summary>
    public static SdpSession? Parse(string sdp)
    {
        if (string.IsNullOrWhiteSpace(sdp))
            return null;

        var session = new SdpSession { RawSdp = sdp };
        string? currentMediaType = null;

        foreach (var rawLine in sdp.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length < 2 || line[1] != '=')
                continue;

            char type = line[0];
            string value = line[2..];

            switch (type)
            {
                case 'v':
                    session.Version = value;
                    break;

                case 'o':
                    session.Origin = value;
                    break;

                case 's':
                    session.SessionName = value;
                    break;

                case 'i':
                    if (currentMediaType == null)
                        session.SessionDescription = value;
                    else
                        session.MediaDescription = value;
                    break;

                case 'c':
                    // c=IN IP4 239.69.1.1/32
                    // c=IN IP4 239.69.1.1/32/2  (multiple addresses)
                    ParseConnectionInfo(value, session);
                    break;

                case 'm':
                    // m=audio 5004 RTP/AVP 96
                    ParseMediaLine(value, session);
                    currentMediaType = session.MediaType;
                    break;

                case 'a':
                    ParseAttribute(value, session);
                    break;
            }
        }

        // Infer bit depth from payload type if not set via rtpmap
        if (session.BitDepth == 0)
        {
            session.BitDepth = session.PayloadType switch
            {
                10 => 16, // L16 stereo
                11 => 16, // L16 mono
                _ => session.BitDepth
            };
        }

        // Infer channels from static payload types
        if (session.Channels == 0)
        {
            session.Channels = session.PayloadType switch
            {
                10 => 2,  // L16/44100/2
                11 => 1,  // L16/44100/1
                _ => session.Channels
            };
        }

        // If no channel labels from fmtp channel-order, try a=label: entries,
        // then fall back to media-level i= line
        if (session.ChannelLabels.Count == 0 && session.MediaLabels.Count > 0)
        {
            session.ChannelLabels = session.MediaLabels;
        }

        if (session.ChannelLabels.Count == 0 && !string.IsNullOrWhiteSpace(session.MediaDescription))
        {
            session.ChannelLabels = ParseMediaDescriptionLabels(session.MediaDescription, session.Channels);
        }

        // Final fallback: try session-level i= line for channel labels
        if (session.ChannelLabels.Count == 0 && !string.IsNullOrWhiteSpace(session.SessionDescription))
        {
            session.ChannelLabels = ParseMediaDescriptionLabels(session.SessionDescription, session.Channels);
        }

        return session;
    }

    private static void ParseConnectionInfo(string value, SdpSession session)
    {
        // IN IP4 239.69.1.1/32
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3)
        {
            var addrPart = parts[2].Split('/')[0];
            if (IPAddress.TryParse(addrPart, out var addr))
            {
                session.MulticastAddress = addr.ToString();
            }
        }
    }

    private static void ParseMediaLine(string value, SdpSession session)
    {
        // audio 5004 RTP/AVP 96
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 4)
        {
            session.MediaType = parts[0];
            if (int.TryParse(parts[1], out int port))
                session.Port = port;
            session.Protocol = parts[2];
            if (int.TryParse(parts[3], out int pt))
                session.PayloadType = pt;
        }
    }

    private static void ParseAttribute(string value, SdpSession session)
    {
        // a=rtpmap:96 L24/48000/8
        // a=ptime:1
        // a=recvonly
        // a=source-filter: incl IN IP4 239.69.1.1 192.168.1.100

        if (value.StartsWith("rtpmap:"))
        {
            ParseRtpMap(value[7..], session);
        }
        else if (value.StartsWith("ptime:"))
        {
            if (double.TryParse(value[6..], System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double ptime))
            {
                session.PtimeMs = ptime;
            }
        }
        else if (value.StartsWith("source-filter:"))
        {
            // source-filter: incl IN IP4 <dest> <src>
            var parts = value[14..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 5)
                session.SourceAddress = parts[4];
        }
        else if (value.StartsWith("fmtp:"))
        {
            ParseFmtp(value[5..], session);
        }
        else if (value.StartsWith("label:"))
        {
            ParseLabel(value[6..], session);
        }
        else if (value == "recvonly")
        {
            session.Direction = "recvonly";
        }
        else if (value == "sendonly")
        {
            session.Direction = "sendonly";
        }
        else if (value == "sendrecv")
        {
            session.Direction = "sendrecv";
        }
    }

    private static void ParseRtpMap(string value, SdpSession session)
    {
        // 96 L24/48000/8
        // 96 L16/48000/2
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return;

        var codecParts = parts[1].Split('/');
        session.Codec = codecParts[0];

        if (codecParts.Length >= 2 && int.TryParse(codecParts[1], out int sampleRate))
            session.SampleRate = sampleRate;

        if (codecParts.Length >= 3 && int.TryParse(codecParts[2], out int channels))
            session.Channels = channels;
        else if (session.Channels == 0)
            session.Channels = 1; // default per RTP spec

        // Infer bit depth from codec name
        session.BitDepth = session.Codec.ToUpperInvariant() switch
        {
            "L32" => 32,
            "L24" => 24,
            "L16" => 16,
            "L8" => 8,
            _ => session.BitDepth
        };
    }

    /// <summary>
    /// Parse a=fmtp lines. The main item of interest is channel-order
    /// per SMPTE ST 2110-30, e.g.:
    ///   a=fmtp:96 channel-order=SMPTE2110.(ST,M,M,M,M,M,M,M)
    ///   a=fmtp:96 channel-order=SMPTE2110.(51,LFE)
    /// The grouping symbols inside the parentheses are defined by
    /// SMPTE ST 2110-30 Table 1 (ST, M, DM, etc.).
    /// </summary>
    private static void ParseFmtp(string value, SdpSession session)
    {
        // value example: "96 channel-order=SMPTE2110.(ST,M,M,M,M,M,M,M)"
        var match = ChannelOrderRegex().Match(value);
        if (match.Success)
        {
            var inner = match.Groups[1].Value; // e.g. "ST,M,M,M,M,M,M,M"
            var labels = ExpandChannelOrder(inner);
            if (labels.Count > 0)
                session.ChannelLabels = labels;
        }
    }

    /// <summary>
    /// Expand SMPTE ST 2110-30 channel grouping symbols into per-channel labels.
    /// </summary>
    private static List<string> ExpandChannelOrder(string inner)
    {
        var labels = new List<string>();
        // Split by comma, but groups can also be separated by spaces
        var tokens = inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var token in tokens)
        {
            var t = token.Trim();
            switch (t.ToUpperInvariant())
            {
                // Mono
                case "M":
                    labels.Add("M");
                    break;

                // Dual Mono
                case "DM":
                    labels.Add("M1");
                    labels.Add("M2");
                    break;

                // Standard Stereo
                case "ST":
                    labels.Add("L");
                    labels.Add("R");
                    break;

                // Left-Total / Right-Total (matrix stereo)
                case "LT/RT" or "LTRT":
                    labels.Add("Lt");
                    labels.Add("Rt");
                    break;

                // 5.1 Surround (per SMPTE ST 2110-30 / 377-4)
                case "51":
                    labels.Add("L");
                    labels.Add("R");
                    labels.Add("C");
                    labels.Add("LFE");
                    labels.Add("Ls");
                    labels.Add("Rs");
                    break;

                // 7.1 Surround
                case "71":
                    labels.Add("L");
                    labels.Add("R");
                    labels.Add("C");
                    labels.Add("LFE");
                    labels.Add("Lss");
                    labels.Add("Rss");
                    labels.Add("Lrs");
                    labels.Add("Rrs");
                    break;

                // 22.2 (NHK) — simplified
                case "222":
                    for (int i = 1; i <= 24; i++)
                        labels.Add($"22.2-{i}");
                    break;

                // Standard Definition groups from ST 2110-30 Table 1
                case "SGRP":
                    labels.Add("SGRP");
                    break;

                // Undefined / single-channel symbol — use as-is
                default:
                    labels.Add(t);
                    break;
            }
        }

        return labels;
    }

    /// <summary>
    /// If no channel-order or a=label: was found, attempt to extract labels from
    /// the i= line (some devices put "Ch1,Ch2,Ch3..." or "Mic 1; Mic 2" there).
    /// Also tries slash and pipe separators.
    /// Handles common prefix patterns like "8 channels: 01, 02, ..." by stripping
    /// the prefix before splitting.
    /// </summary>
    public static List<string> ParseMediaDescriptionLabels(string description, int channelCount)
    {
        if (string.IsNullOrWhiteSpace(description) || channelCount <= 0)
            return [];

        // Strip common "N channels:" prefix first, then try the raw string as fallback
        var stripped = StripChannelPrefix(description);
        var candidates = stripped != description
            ? new[] { stripped, description }
            : new[] { description };

        char[] separators = [',', ';', '|', '/'];

        foreach (var candidate in candidates)
        {
            foreach (var sep in separators)
            {
                var parts = candidate.Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == channelCount)
                    return [.. parts];
            }
        }

        return [];
    }

    /// <summary>
    /// Strip common channel-count prefixes from i= lines.
    /// Patterns: "N channels: ...", "N ch: ...", "channels: ..."
    /// </summary>
    private static string StripChannelPrefix(string value)
    {
        var match = ChannelPrefixRegex().Match(value);
        if (match.Success)
            return value[match.Length..].Trim();
        return value;
    }

    [GeneratedRegex(@"^(\d+\s+)?(channels?|ch)\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelPrefixRegex();

    /// <summary>
    /// Parse a=label: attributes (RFC 4574).
    /// Dante devices commonly emit one a=label: per channel:
    ///   a=label:1 Mic 1
    ///   a=label:2 Mic 2
    /// Some emit a single comma-separated line:
    ///   a=label:Mic 1,Mic 2,Mic 3
    /// </summary>
    private static void ParseLabel(string value, SdpSession session)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return;

        // If it contains commas, treat as a comma-separated list
        if (trimmed.Contains(','))
        {
            var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var p in parts)
                session.MediaLabels.Add(p);
        }
        else
        {
            // Single label — may have a leading channel number: "1 Mic 1"
            // Strip optional leading digits + space
            var spaceIdx = trimmed.IndexOf(' ');
            if (spaceIdx > 0 && int.TryParse(trimmed[..spaceIdx], out _))
                session.MediaLabels.Add(trimmed[(spaceIdx + 1)..].Trim());
            else
                session.MediaLabels.Add(trimmed);
        }
    }

    [GeneratedRegex(@"channel-order=SMPTE2110\.\(([^)]+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelOrderRegex();
}

/// <summary>
/// Represents a parsed SDP session with audio stream parameters.
/// </summary>
public sealed class SdpSession
{
    public string Version { get; set; } = "0";
    public string Origin { get; set; } = string.Empty;
    public string SessionName { get; set; } = string.Empty;
    public string SessionDescription { get; set; } = string.Empty;
    public string MulticastAddress { get; set; } = string.Empty;
    public string SourceAddress { get; set; } = string.Empty;
    public string MediaType { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Protocol { get; set; } = string.Empty;
    public int PayloadType { get; set; }
    public string Codec { get; set; } = string.Empty;
    public int SampleRate { get; set; } = 48000;
    public int Channels { get; set; }
    public int BitDepth { get; set; }
    public double PtimeMs { get; set; }
    public string Direction { get; set; } = string.Empty;
    public string RawSdp { get; set; } = string.Empty;

    /// <summary>
    /// Per-channel labels extracted from SDP, in channel order.
    /// Sources: SMPTE ST 2110-30 channel-order in a=fmtp,
    /// a=label: (RFC 4574), or media-level i= lines from some implementations.
    /// Empty list if no labels were found.
    /// </summary>
    public List<string> ChannelLabels { get; set; } = [];

    /// <summary>
    /// Raw a=label: values collected during media section parsing.
    /// </summary>
    internal List<string> MediaLabels { get; set; } = [];

    /// <summary>
    /// Media-level description (i= line under m= section).
    /// Some devices put comma-separated channel names here.
    /// </summary>
    public string MediaDescription { get; set; } = string.Empty;

    /// <summary>
    /// Whether this looks like a valid audio stream we can listen to.
    /// </summary>
    public bool IsValidAudioStream =>
        MediaType == "audio" &&
        Port > 0 &&
        !string.IsNullOrEmpty(MulticastAddress) &&
        SampleRate > 0 &&
        Channels > 0;
}
