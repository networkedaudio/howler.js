using System.Globalization;
using System.Text;

namespace RTPTransmitter.Services;

/// <summary>
/// Generates waveform SVG images from raw PCM audio data.
/// Produces a single-colour amplitude envelope suitable for web display.
/// </summary>
public static class WaveformRenderer
{
    /// <summary>
    /// Default image width in pixels.
    /// </summary>
    public const int DefaultWidth = 1800;

    /// <summary>
    /// Default image height in pixels.
    /// </summary>
    public const int DefaultHeight = 140;

    /// <summary>
    /// Render a waveform SVG from big-endian PCM byte data (single channel).
    /// </summary>
    /// <param name="pcmBytes">Raw PCM bytes in big-endian (AES67 network byte order).</param>
    /// <param name="bitDepth">Bits per sample (16, 24, or 32).</param>
    /// <param name="width">SVG width in pixels.</param>
    /// <param name="height">SVG height in pixels.</param>
    /// <returns>A complete SVG document as a string.</returns>
    public static string RenderSvg(
        ReadOnlySpan<byte> pcmBytes,
        int bitDepth,
        int width = DefaultWidth,
        int height = DefaultHeight)
    {
        int bytesPerSample = bitDepth / 8;
        int totalSamples = pcmBytes.Length / bytesPerSample;

        if (totalSamples == 0)
            return BuildEmptySvg(width, height);

        // Determine how many samples per pixel column
        int samplesPerColumn = Math.Max(1, totalSamples / width);
        int columns = Math.Min(width, totalSamples);

        var minPeaks = new float[columns];
        var maxPeaks = new float[columns];

        // Compute min/max peaks for each column
        for (int col = 0; col < columns; col++)
        {
            int startSample = col * samplesPerColumn;
            int endSample = Math.Min(startSample + samplesPerColumn, totalSamples);

            float min = float.MaxValue;
            float max = float.MinValue;

            for (int s = startSample; s < endSample; s++)
            {
                float sample = ReadSampleBigEndian(pcmBytes, s * bytesPerSample, bitDepth);
                if (sample < min) min = sample;
                if (sample > max) max = sample;
            }

            minPeaks[col] = min;
            maxPeaks[col] = max;
        }

        return BuildSvg(minPeaks, maxPeaks, columns, width, height);
    }

    /// <summary>
    /// Read a single sample from big-endian PCM data, normalized to [-1.0, 1.0].
    /// </summary>
    private static float ReadSampleBigEndian(ReadOnlySpan<byte> data, int offset, int bitDepth)
    {
        switch (bitDepth)
        {
            case 16:
            {
                int value = (short)((data[offset] << 8) | data[offset + 1]);
                return value / 32768f;
            }
            case 24:
            {
                int value = (data[offset] << 16) | (data[offset + 1] << 8) | data[offset + 2];
                // Sign-extend from 24 bits
                if (value >= 0x800000) value -= 0x1000000;
                return value / 8388608f;
            }
            case 32:
            {
                int value = (data[offset] << 24) | (data[offset + 1] << 16) |
                            (data[offset + 2] << 8) | data[offset + 3];
                return value / 2147483648f;
            }
            default:
                return 0f;
        }
    }

    private static string BuildSvg(
        float[] minPeaks, float[] maxPeaks,
        int columns, int width, int height)
    {
        float midY = height / 2f;
        float halfH = midY - 1; // leave 1px padding top/bottom

        var sb = new StringBuilder(columns * 40 + 512);
        sb.Append(CultureInfo.InvariantCulture,
            $"""
             <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {width} {height}" preserveAspectRatio="none">
             <rect width="{width}" height="{height}" fill="#1e1e2e"/>
             <g fill="#89b4fa">
             """);

        float colWidth = (float)width / columns;

        for (int i = 0; i < columns; i++)
        {
            // Map [-1,1] to pixel Y (inverted: -1 = bottom, 1 = top)
            float yTop = midY - (maxPeaks[i] * halfH);
            float yBot = midY - (minPeaks[i] * halfH);
            float barHeight = Math.Max(0.5f, yBot - yTop);
            float x = i * colWidth;

            sb.Append(CultureInfo.InvariantCulture,
                $"""
                 <rect x="{x:F1}" y="{yTop:F1}" width="{Math.Max(0.5f, colWidth):F1}" height="{barHeight:F1}"/>
                 """);
        }

        sb.AppendLine("</g></svg>");
        return sb.ToString();
    }

    private static string BuildEmptySvg(int width, int height)
    {
        return $"""
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {width} {height}">
                <rect width="{width}" height="{height}" fill="#1e1e2e"/>
                <line x1="0" y1="{height / 2}" x2="{width}" y2="{height / 2}" stroke="#585b70" stroke-width="1"/>
                </svg>
                """;
    }
}
