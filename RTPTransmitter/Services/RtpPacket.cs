namespace RTPTransmitter.Services;

/// <summary>
/// Represents a parsed RTP packet header and payload per RFC 3550.
/// AES67 uses RTP with L16 or L24 PCM payloads.
/// </summary>
public sealed class RtpPacket
{
    public int Version { get; set; }
    public bool Padding { get; set; }
    public bool Extension { get; set; }
    public int CsrcCount { get; set; }
    public bool Marker { get; set; }
    public int PayloadType { get; set; }
    public ushort SequenceNumber { get; set; }
    public uint Timestamp { get; set; }
    public uint Ssrc { get; set; }
    public byte[] Payload { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Parse an RTP packet from a raw UDP datagram.
    /// </summary>
    public static RtpPacket Parse(byte[] data, int length)
    {
        if (length < 12)
            throw new ArgumentException("RTP packet too short", nameof(length));

        var packet = new RtpPacket
        {
            Version = (data[0] >> 6) & 0x03,
            Padding = ((data[0] >> 5) & 0x01) == 1,
            Extension = ((data[0] >> 4) & 0x01) == 1,
            CsrcCount = data[0] & 0x0F,
            Marker = ((data[1] >> 7) & 0x01) == 1,
            PayloadType = data[1] & 0x7F,
            SequenceNumber = (ushort)((data[2] << 8) | data[3]),
            Timestamp = (uint)((data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7]),
            Ssrc = (uint)((data[8] << 24) | (data[9] << 16) | (data[10] << 8) | data[11])
        };

        int headerLength = 12 + (packet.CsrcCount * 4);

        // Handle RTP header extension (RFC 3550 section 5.3.1)
        if (packet.Extension && headerLength + 4 <= length)
        {
            // Skip extension profile (2 bytes) and read extension length in 32-bit words
            int extensionLength = ((data[headerLength + 2] << 8) | data[headerLength + 3]) * 4;
            headerLength += 4 + extensionLength;
        }

        if (headerLength > length)
            throw new ArgumentException("RTP header extends beyond packet length");

        int payloadLength = length - headerLength;

        // Handle padding
        if (packet.Padding && payloadLength > 0)
        {
            int paddingLength = data[length - 1];
            payloadLength -= paddingLength;
        }

        if (payloadLength > 0)
        {
            packet.Payload = new byte[payloadLength];
            Buffer.BlockCopy(data, headerLength, packet.Payload, 0, payloadLength);
        }

        return packet;
    }
}
