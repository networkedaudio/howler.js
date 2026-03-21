using CUETools.Codecs;
using CUETools.Codecs.FLAKE;
using NAudio.Lame;
using NAudio.Wave;

namespace RTPTransmitter.Services;

/// <summary>
/// Transcodes a FLAC file to MP3, transferring Vorbis comment metadata to ID3 tags.
/// Uses CUETools FlakeReader for decoding and NAudio.Lame for encoding.
/// </summary>
public static class Mp3TranscodeHelper
{
    /// <summary>
    /// Transcode a FLAC file to MP3. Returns true on success.
    /// On failure the original FLAC file is left intact for retry.
    /// </summary>
    /// <param name="flacPath">Source .flac file path.</param>
    /// <param name="mp3Path">Destination .mp3 file path.</param>
    /// <param name="bitRate">MP3 bit rate in kbps. Default: 192.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <returns>True if transcoding succeeded; false otherwise.</returns>
    public static bool Transcode(
        string flacPath,
        string mp3Path,
        int bitRate = 192,
        ILogger? logger = null)
    {
        try
        {
            // Read Vorbis comments from the FLAC file before decoding
            var vorbisComments = ReadVorbisComments(flacPath);

            // Decode FLAC to raw PCM samples
            using var reader = new FlakeReader(flacPath, null);
            var pcmConfig = reader.PCM;
            long totalSamples = reader.Length;

            // Build NAudio WaveFormat matching the FLAC source
            // NAudio.Lame only supports 16-bit PCM input, so we'll convert if needed
            int outputBits = Math.Min(pcmConfig.BitsPerSample, 16);
            var waveFormat = new WaveFormat(pcmConfig.SampleRate, outputBits, pcmConfig.ChannelCount);

            // Build ID3 tags from Vorbis comments
            var id3 = BuildId3Tags(vorbisComments);

            var config = new LameConfig
            {
                BitRate = bitRate,
                ID3 = id3
            };

            // Encode to MP3
            using var mp3Writer = new LameMP3FileWriter(mp3Path, waveFormat, config);

            const int readBlockSize = 4096;
            var audioBuffer = new AudioBuffer(pcmConfig, readBlockSize);
            var pcmByteBuffer = new byte[readBlockSize * pcmConfig.ChannelCount * (outputBits / 8)];

            while (reader.Remaining > 0)
            {
                int samplesRead = reader.Read(audioBuffer, readBlockSize);
                if (samplesRead == 0)
                    break;

                // Convert int samples to little-endian PCM bytes for NAudio
                int bytesWritten = SamplesToLittleEndianBytes(
                    audioBuffer.Samples, samplesRead, pcmConfig.ChannelCount,
                    pcmConfig.BitsPerSample, outputBits, pcmByteBuffer);

                mp3Writer.Write(pcmByteBuffer, 0, bytesWritten);
            }

            reader.Close();

            logger?.LogInformation(
                "Transcoded FLAC to MP3: {Source} -> {Dest} ({Samples} samples, {BitRate}kbps)",
                flacPath, mp3Path, totalSamples, bitRate);

            return true;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to transcode FLAC to MP3: {Source}", flacPath);

            // Clean up partial MP3 file on failure
            try { if (File.Exists(mp3Path)) File.Delete(mp3Path); } catch { }

            return false;
        }
    }

    /// <summary>
    /// Convert decoded int[,] samples to little-endian PCM bytes.
    /// Handles bit-depth conversion (e.g. 24-bit source to 16-bit output).
    /// </summary>
    private static int SamplesToLittleEndianBytes(
        int[,] samples, int sampleCount, int channels,
        int sourceBits, int outputBits, byte[] output)
    {
        int bytesPerSample = outputBits / 8;
        int shift = sourceBits - outputBits;
        int idx = 0;

        for (int s = 0; s < sampleCount; s++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                int value = samples[s, ch];

                // Shift down if we're reducing bit depth
                if (shift > 0)
                    value >>= shift;

                switch (outputBits)
                {
                    case 16:
                        short s16 = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
                        output[idx] = (byte)(s16 & 0xFF);
                        output[idx + 1] = (byte)((s16 >> 8) & 0xFF);
                        idx += 2;
                        break;
                    case 24:
                        output[idx] = (byte)(value & 0xFF);
                        output[idx + 1] = (byte)((value >> 8) & 0xFF);
                        output[idx + 2] = (byte)((value >> 16) & 0xFF);
                        idx += 3;
                        break;
                    case 32:
                        output[idx] = (byte)(value & 0xFF);
                        output[idx + 1] = (byte)((value >> 8) & 0xFF);
                        output[idx + 2] = (byte)((value >> 16) & 0xFF);
                        output[idx + 3] = (byte)((value >> 24) & 0xFF);
                        idx += 4;
                        break;
                }
            }
        }

        return idx;
    }

    /// <summary>
    /// Build ID3 tag data from Vorbis comment key-value pairs.
    /// Maps standard Vorbis fields to ID3 equivalents.
    /// </summary>
    private static ID3TagData BuildId3Tags(Dictionary<string, string> vorbisComments)
    {
        var id3 = new ID3TagData();

        if (vorbisComments.TryGetValue("TITLE", out var title))
            id3.Title = title;

        if (vorbisComments.TryGetValue("ARTIST", out var artist))
            id3.Artist = artist;

        if (vorbisComments.TryGetValue("ALBUM", out var album))
            id3.Album = album;

        if (vorbisComments.TryGetValue("DATE", out var date))
            id3.Year = date;

        if (vorbisComments.TryGetValue("ENCODER", out var encoder))
            id3.Comment = $"Encoder: {encoder}";

        if (vorbisComments.TryGetValue("GENRE", out var genre))
            id3.Genre = genre;

        if (vorbisComments.TryGetValue("TRACKNUMBER", out var track))
            id3.Track = track;

        // Carry any remaining fields as user-defined text
        var mappedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "TITLE", "ARTIST", "ALBUM", "DATE", "ENCODER", "GENRE", "TRACKNUMBER"
        };

        foreach (var kvp in vorbisComments)
        {
            if (!mappedKeys.Contains(kvp.Key))
                id3.UserDefinedText[kvp.Key] = kvp.Value;
        }

        return id3;
    }

    /// <summary>
    /// Read Vorbis comments from a FLAC file by parsing the metadata blocks.
    /// Returns a dictionary of tag key-value pairs.
    /// </summary>
    private static Dictionary<string, string> ReadVorbisComments(string flacPath)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var fs = new FileStream(flacPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        // Verify "fLaC" marker
        var marker = new byte[4];
        if (fs.Read(marker, 0, 4) < 4) return tags;
        if (marker[0] != 0x66 || marker[1] != 0x4C || marker[2] != 0x61 || marker[3] != 0x43)
            return tags;

        // Walk metadata blocks
        while (fs.Position < fs.Length)
        {
            int headerByte = fs.ReadByte();
            if (headerByte < 0) break;

            bool isLast = (headerByte & 0x80) != 0;
            int blockType = headerByte & 0x7F;

            var lenBytes = new byte[3];
            if (fs.Read(lenBytes, 0, 3) < 3) break;
            int blockLength = (lenBytes[0] << 16) | (lenBytes[1] << 8) | lenBytes[2];

            if (blockType == 4) // VORBIS_COMMENT
            {
                var blockData = new byte[blockLength];
                if (fs.Read(blockData, 0, blockLength) < blockLength) break;

                ParseVorbisCommentBlock(blockData, tags);
                return tags;
            }

            // Skip this block
            fs.Position += blockLength;

            if (isLast) break;
        }

        return tags;
    }

    /// <summary>
    /// Parse a Vorbis comment block payload into key-value pairs.
    /// </summary>
    private static void ParseVorbisCommentBlock(byte[] data, Dictionary<string, string> tags)
    {
        if (data.Length < 8) return;

        int offset = 0;

        // Vendor string length (LE32) + vendor string
        uint vendorLen = BitConverter.ToUInt32(data, offset);
        offset += 4 + (int)vendorLen;

        if (offset + 4 > data.Length) return;

        // Comment count (LE32)
        uint commentCount = BitConverter.ToUInt32(data, offset);
        offset += 4;

        for (uint i = 0; i < commentCount && offset + 4 <= data.Length; i++)
        {
            uint commentLen = BitConverter.ToUInt32(data, offset);
            offset += 4;

            if (offset + (int)commentLen > data.Length) break;

            var comment = System.Text.Encoding.UTF8.GetString(data, offset, (int)commentLen);
            offset += (int)commentLen;

            int eqIdx = comment.IndexOf('=');
            if (eqIdx > 0)
            {
                var key = comment[..eqIdx].ToUpperInvariant();
                var value = comment[(eqIdx + 1)..];
                tags[key] = value;
            }
        }
    }
}
