namespace RTPTransmitter.Services;

/// <summary>
/// Converts raw PCM audio bytes (L16, L24) from AES67 RTP payloads
/// into Float32 samples suitable for Web Audio API playback.
/// AES67 multi-channel PCM is interleaved: for N channels and F frames,
/// the byte layout is [ch0_f0, ch1_f0, ..., chN_f0, ch0_f1, ch1_f1, ...].
/// </summary>
public static class PcmConverter
{
    /// <summary>
    /// Convert L16 (16-bit signed big-endian) PCM to interleaved float32 samples.
    /// The returned array preserves the interleaved channel layout.
    /// </summary>
    public static float[] L16ToFloat32(byte[] payload)
    {
        int bytesPerSample = 2;
        int totalSamples = payload.Length / bytesPerSample;
        var result = new float[totalSamples];

        for (int i = 0; i < totalSamples; i++)
        {
            int offset = i * bytesPerSample;
            short sample = (short)((payload[offset] << 8) | payload[offset + 1]);
            result[i] = sample / 32768f;
        }

        return result;
    }

    /// <summary>
    /// Convert L24 (24-bit signed big-endian) PCM to interleaved float32 samples.
    /// The returned array preserves the interleaved channel layout.
    /// </summary>
    public static float[] L24ToFloat32(byte[] payload)
    {
        int bytesPerSample = 3;
        int totalSamples = payload.Length / bytesPerSample;
        var result = new float[totalSamples];

        for (int i = 0; i < totalSamples; i++)
        {
            int offset = i * bytesPerSample;
            int sample = (payload[offset] << 16) | (payload[offset + 1] << 8) | payload[offset + 2];
            if ((sample & 0x800000) != 0)
                sample |= unchecked((int)0xFF000000);
            result[i] = sample / 8388608f;
        }

        return result;
    }

    /// <summary>
    /// Detect the PCM format from the RTP payload type and convert accordingly.
    /// AES67 payload types:
    ///   PT 96+ = dynamic (commonly L24 or L16)
    ///   PT 10  = L16 stereo 44.1kHz
    ///   PT 11  = L16 mono 44.1kHz
    /// For dynamic payload types, we default to L24 (most common in AES67).
    /// </summary>
    public static float[] ConvertPayload(byte[] payload, int payloadType)
    {
        return payloadType switch
        {
            10 or 11 => L16ToFloat32(payload),
            >= 96 => L24ToFloat32(payload),
            _ => L16ToFloat32(payload)
        };
    }

    /// <summary>
    /// De-interleave a flat interleaved sample array into per-channel arrays.
    /// Input: [ch0_f0, ch1_f0, ..., chN_f0, ch0_f1, ch1_f1, ...]
    /// Output: float[channelIndex][frameIndex]
    /// </summary>
    public static float[][] DeInterleave(float[] interleaved, int channels)
    {
        if (channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(channels));

        int framesPerChannel = interleaved.Length / channels;
        var result = new float[channels][];

        for (int ch = 0; ch < channels; ch++)
        {
            result[ch] = new float[framesPerChannel];
            for (int f = 0; f < framesPerChannel; f++)
            {
                result[ch][f] = interleaved[f * channels + ch];
            }
        }

        return result;
    }

    /// <summary>
    /// Extract specific channels from an interleaved sample array and
    /// re-interleave them in the given order. Useful for server-side
    /// channel selection when bandwidth is a concern.
    /// </summary>
    /// <param name="interleaved">Source interleaved samples.</param>
    /// <param name="sourceChannels">Total channel count in the source.</param>
    /// <param name="selectedChannels">Zero-based indices of channels to extract, in output order.</param>
    /// <returns>Interleaved float array containing only the selected channels.</returns>
    public static float[] ExtractChannels(float[] interleaved, int sourceChannels, int[] selectedChannels)
    {
        if (sourceChannels <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceChannels));
        if (selectedChannels.Length == 0)
            throw new ArgumentException("At least one channel must be selected", nameof(selectedChannels));

        int frames = interleaved.Length / sourceChannels;
        int outChannels = selectedChannels.Length;
        var result = new float[frames * outChannels];

        for (int f = 0; f < frames; f++)
        {
            int srcBase = f * sourceChannels;
            int dstBase = f * outChannels;
            for (int i = 0; i < outChannels; i++)
            {
                int srcCh = selectedChannels[i];
                result[dstBase + i] = (srcCh >= 0 && srcCh < sourceChannels)
                    ? interleaved[srcBase + srcCh]
                    : 0f;
            }
        }

        return result;
    }
}
