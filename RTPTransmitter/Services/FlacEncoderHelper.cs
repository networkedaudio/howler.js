using CUETools.Codecs;
using CUETools.Codecs.FLAKE;

namespace RTPTransmitter.Services;

/// <summary>
/// Encodes raw big-endian PCM audio (single channel) to FLAC using CUETools FlakeWriter.
/// Embeds a recording timestamp as a Vorbis comment in the FLAC metadata.
/// </summary>
public static class FlacEncoderHelper
{
    /// <summary>
    /// Encode big-endian PCM byte data to a FLAC file on disk.
    /// </summary>
    /// <param name="pcmBytes">Raw PCM bytes in big-endian (AES67 network byte order), single channel.</param>
    /// <param name="sampleRate">Audio sample rate in Hz.</param>
    /// <param name="bitDepth">Bits per sample (16, 24, or 32).</param>
    /// <param name="outputPath">Destination .flac file path.</param>
    /// <param name="recordingTime">Timestamp to embed as a Vorbis comment.</param>
    public static void Encode(
        ReadOnlySpan<byte> pcmBytes,
        int sampleRate,
        int bitDepth,
        string outputPath,
        DateTimeOffset recordingTime)
    {
        int bytesPerSample = bitDepth / 8;
        int totalSamples = pcmBytes.Length / bytesPerSample;

        if (totalSamples == 0)
            return;

        var pcmConfig = new AudioPCMConfig(bitDepth, 1, sampleRate);

        // Convert big-endian PCM to int[,] sample array expected by FlakeWriter
        var samples = ConvertBigEndianToSamples(pcmBytes, totalSamples, bitDepth);

        using var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite);
        using var writer = new FlakeWriter(outputPath, outputStream, pcmConfig);

        writer.CompressionLevel = 5;
        writer.FinalSampleCount = totalSamples;
        writer.Padding = 8192; // leave room for metadata editing

        // Write in blocks
        const int blockSize = 4096;
        var buffer = new AudioBuffer(pcmConfig, blockSize);

        int offset = 0;
        while (offset < totalSamples)
        {
            int count = Math.Min(blockSize, totalSamples - offset);

            // Create a slice of samples for this block
            var blockSamples = new int[count, 1];
            for (int i = 0; i < count; i++)
            {
                blockSamples[i, 0] = samples[offset + i];
            }

            buffer.Prepare(blockSamples, count);
            writer.Write(buffer);
            offset += count;
        }

        writer.Close();

        // Embed the recording timestamp as a Vorbis comment in the FLAC file
        EmbedVorbisComment(outputPath, recordingTime);
    }

    /// <summary>
    /// Convert big-endian PCM byte data to signed integer samples.
    /// </summary>
    private static int[] ConvertBigEndianToSamples(ReadOnlySpan<byte> data, int totalSamples, int bitDepth)
    {
        int bytesPerSample = bitDepth / 8;
        var samples = new int[totalSamples];

        for (int i = 0; i < totalSamples; i++)
        {
            int offset = i * bytesPerSample;
            switch (bitDepth)
            {
                case 16:
                    samples[i] = (short)((data[offset] << 8) | data[offset + 1]);
                    break;
                case 24:
                {
                    int val = (data[offset] << 16) | (data[offset + 1] << 8) | data[offset + 2];
                    if (val >= 0x800000) val -= 0x1000000;
                    samples[i] = val;
                    break;
                }
                case 32:
                    samples[i] = (data[offset] << 24) | (data[offset + 1] << 16) |
                                 (data[offset + 2] << 8) | data[offset + 3];
                    break;
            }
        }

        return samples;
    }

    /// <summary>
    /// Write a minimal Vorbis comment block into the FLAC file's padding area.
    /// FLAC files use Vorbis comments (metadata block type 4) for tags.
    /// We replace the first PADDING block with a VORBIS_COMMENT block.
    /// </summary>
    private static void EmbedVorbisComment(string flacPath, DateTimeOffset recordingTime)
    {
        // Build Vorbis comment payload
        var tags = new Dictionary<string, string>
        {
            ["DATE"] = recordingTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["ENCODER"] = "RTPTransmitter",
            ["TITLE"] = $"Recording {recordingTime:yyyy-MM-dd HH:mm:ss} UTC"
        };

        byte[] commentBlock = BuildVorbisCommentPayload(tags);

        using var fs = new FileStream(flacPath, FileMode.Open, FileAccess.ReadWrite);

        // Skip "fLaC" marker
        if (fs.Length < 4) return;
        fs.Position = 4;

        // Walk metadata blocks looking for PADDING
        while (fs.Position < fs.Length)
        {
            long blockHeaderPos = fs.Position;

            int headerByte = fs.ReadByte();
            if (headerByte < 0) break;

            bool isLast = (headerByte & 0x80) != 0;
            int blockType = headerByte & 0x7F;

            // Read 3-byte big-endian length
            var lenBytes = new byte[3];
            if (fs.Read(lenBytes, 0, 3) < 3) break;
            int blockLength = (lenBytes[0] << 16) | (lenBytes[1] << 8) | lenBytes[2];

            if (blockType == 1 && blockLength >= commentBlock.Length + 4)
            {
                // Found PADDING block large enough — replace with VORBIS_COMMENT + remaining padding
                int commentTotalSize = commentBlock.Length;
                int remainingPadding = blockLength - commentTotalSize - 4; // 4 bytes for the new padding block header

                fs.Position = blockHeaderPos;

                if (remainingPadding >= 0)
                {
                    // Write VORBIS_COMMENT block header (type 4, not last)
                    fs.WriteByte(0x04); // type=4, not last
                    fs.WriteByte((byte)((commentTotalSize >> 16) & 0xFF));
                    fs.WriteByte((byte)((commentTotalSize >> 8) & 0xFF));
                    fs.WriteByte((byte)(commentTotalSize & 0xFF));

                    // Write comment payload
                    fs.Write(commentBlock);

                    // Write remaining PADDING block header (type 1, last if original was last)
                    fs.WriteByte((byte)((isLast ? 0x80 : 0x00) | 0x01));
                    fs.WriteByte((byte)((remainingPadding >> 16) & 0xFF));
                    fs.WriteByte((byte)((remainingPadding >> 8) & 0xFF));
                    fs.WriteByte((byte)(remainingPadding & 0xFF));

                    // Zero out the remaining padding
                    if (remainingPadding > 0)
                    {
                        fs.Write(new byte[remainingPadding]);
                    }
                }
                else
                {
                    // Exact fit or close enough — mark as last if needed
                    byte typeByte = (byte)((isLast ? 0x80 : 0x00) | 0x04);
                    fs.WriteByte(typeByte);
                    fs.WriteByte((byte)((commentTotalSize >> 16) & 0xFF));
                    fs.WriteByte((byte)((commentTotalSize >> 8) & 0xFF));
                    fs.WriteByte((byte)(commentTotalSize & 0xFF));
                    fs.Write(commentBlock);

                    // Zero-fill whatever is left of the original block
                    int leftover = blockLength - commentTotalSize;
                    if (leftover > 0) fs.Write(new byte[leftover]);
                }

                return;
            }

            // Skip this block's data
            fs.Position = blockHeaderPos + 4 + blockLength;

            if (isLast) break;
        }
    }

    /// <summary>
    /// Build a Vorbis comment payload (without the metadata block header).
    /// Format: vendor string length (LE32) + vendor string + comment count (LE32) + comments.
    /// </summary>
    private static byte[] BuildVorbisCommentPayload(Dictionary<string, string> tags)
    {
        const string vendor = "RTPTransmitter";

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // Vendor string (little-endian length + UTF-8 bytes)
        var vendorBytes = System.Text.Encoding.UTF8.GetBytes(vendor);
        bw.Write((uint)vendorBytes.Length);
        bw.Write(vendorBytes);

        // Comment count
        bw.Write((uint)tags.Count);

        foreach (var kvp in tags)
        {
            var comment = System.Text.Encoding.UTF8.GetBytes($"{kvp.Key}={kvp.Value}");
            bw.Write((uint)comment.Length);
            bw.Write(comment);
        }

        bw.Flush();
        return ms.ToArray();
    }
}
