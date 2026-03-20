using System.Net;
using System.Text;

namespace RTPTransmitter.Services;

/// <summary>
/// Represents a parsed SAP (Session Announcement Protocol) packet per RFC 2974.
/// SAP is used to announce multicast sessions via SDP payloads.
/// 
/// Header format (first 4 bytes + originating source):
///   V (3 bits)  = Version (1)
///   A (1 bit)   = Address type (0=IPv4, 1=IPv6)
///   R (1 bit)   = Reserved
///   T (1 bit)   = Message type (0=announcement, 1=deletion)
///   E (1 bit)   = Encryption (0=not encrypted)
///   C (1 bit)   = Compressed (0=not compressed)
///   Auth Length  (8 bits) = number of 32-bit words of auth data
///   Msg ID Hash  (16 bits) = hash for identifying announcements
///   Originating Source (32 bits for IPv4, 128 for IPv6)
///   [Optional auth data]
///   Payload type string (null-terminated, e.g. "application/sdp")
///   SDP payload
/// </summary>
public sealed class SapPacket
{
    public int Version { get; set; }
    public bool IsIpv6 { get; set; }
    public bool IsDeletion { get; set; }
    public bool IsEncrypted { get; set; }
    public bool IsCompressed { get; set; }
    public int AuthLength { get; set; }
    public ushort MessageIdHash { get; set; }
    public IPAddress OriginatingSource { get; set; } = IPAddress.None;
    public string PayloadType { get; set; } = "application/sdp";
    public string Payload { get; set; } = string.Empty;

    /// <summary>
    /// Parse a SAP packet from raw UDP data.
    /// </summary>
    public static SapPacket? Parse(byte[] data, int length)
    {
        if (length < 4)
            return null;

        var packet = new SapPacket
        {
            Version = (data[0] >> 5) & 0x07,
            IsIpv6 = ((data[0] >> 4) & 0x01) == 1,
            IsDeletion = ((data[0] >> 2) & 0x01) == 1,
            IsEncrypted = ((data[0] >> 1) & 0x01) == 1,
            IsCompressed = (data[0] & 0x01) == 1,
            AuthLength = data[1],
            MessageIdHash = (ushort)((data[2] << 8) | data[3])
        };

        // Only support SAP version 1
        if (packet.Version != 1)
            return null;

        // We don't handle encrypted or compressed packets
        if (packet.IsEncrypted || packet.IsCompressed)
            return null;

        int offset = 4;

        // Read originating source address
        if (packet.IsIpv6)
        {
            if (length < offset + 16)
                return null;
            var addrBytes = new byte[16];
            Array.Copy(data, offset, addrBytes, 0, 16);
            packet.OriginatingSource = new IPAddress(addrBytes);
            offset += 16;
        }
        else
        {
            if (length < offset + 4)
                return null;
            var addrBytes = new byte[4];
            Array.Copy(data, offset, addrBytes, 0, 4);
            packet.OriginatingSource = new IPAddress(addrBytes);
            offset += 4;
        }

        // Skip authentication data
        offset += packet.AuthLength * 4;

        if (offset >= length)
            return null;

        // Read optional payload type string (null-terminated)
        // If the first byte looks like text, read until null terminator
        if (data[offset] != 'v' || (offset + 1 < length && data[offset + 1] != '='))
        {
            // There's a payload type string before the SDP
            int ptStart = offset;
            while (offset < length && data[offset] != 0)
                offset++;

            if (offset > ptStart)
                packet.PayloadType = Encoding.ASCII.GetString(data, ptStart, offset - ptStart);

            // Skip the null terminator
            if (offset < length && data[offset] == 0)
                offset++;
        }

        // Remainder is the SDP payload
        if (offset < length)
            packet.Payload = Encoding.UTF8.GetString(data, offset, length - offset);

        return packet;
    }

    /// <summary>
    /// Unique key for this announcement (origin + hash).
    /// </summary>
    public string AnnouncementKey => $"{OriginatingSource}:{MessageIdHash}";
}
